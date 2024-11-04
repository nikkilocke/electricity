using System;
using System.Collections.Generic;
using System.Linq;
using CodeFirstWebFramework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Text;
using System.Globalization;

namespace Electricity {
    public class Home : AppModule {

        protected override void Init() {
            base.Init();
            InsertMenuOptions(
                new MenuOption("List Scenarios", "/home/list"),
                new MenuOption("New Scenario", "/home/view?id=0"),
				new MenuOption("Download From Octopus", "/home/downloadfromoctopus"),
				new MenuOption("Download From Hildebrand", "/home/downloadfromhildebrand"),
                new MenuOption("Import", "/home/import"),
                new MenuOption("Check For Missing Data", "/home/check"),
                new MenuOption("Settings", "/admin/editsettings")
                );
        }

        public override void Default() {
            Redirect("/home/list");
        }

        public DataTableForm List() {
            InsertMenuOptions(new MenuOption("Recalculate All", "/home/recalc"));
            return new DataTableForm(this, typeof(DataDisplay), false, "Name", "Rate", "StandingCharge", "TotalCost", "AnnualCost", "MonthlyCost") {
                Select = "/home/view"
            };
        }

        public JObjectEnumerable ListListing() {
            return Database.Query("SELECT * FROM DataDisplay ORDER BY AnnualCost, Name");
        }

        public MultiDetailForm View(int id) {
			if (id <= 0 || !Database.TryGet(id, out DataDisplay display)) {
                display = new DataDisplay() {
                    Rate = ((Settings)Settings).PeakRate,
                    StandingCharge = ((Settings)Settings).StandingCharge,
                    PeriodStart = DateTime.Today.AddYears(-1),
                    PeriodEnd = DateTime.Today
                };
                if (display.Rate != ((Settings)Settings).OffPeakRate)
                    display.Rates = new List<RatePeriod>(new RatePeriod[] {
                        new RatePeriod() {
                            Start = ((Settings)Settings).OffPeakStart,
                            End = ((Settings)Settings).OffPeakEnd,
                            Rate = ((Settings)Settings).OffPeakRate,
                            Battery = BatteryUse.Charge
                        }
                    });
            } else {
				InsertMenuOptions(new MenuOption("Copy", "/home/view?dup=y&id=" + id));
				InsertMenuOptions(new MenuOption("Data", $"/home/showdata?start={display.PeriodStart.ToString("yyyy-MM-dd")}&end={display.PeriodEnd.ToString("yyyy-MM-dd")}&{FromHere}"));
			}
			if (GetParameters["dup"] == "y") {
                display.idDataDisplay = null;
                display.Name += " (copy)";
            }
            MultiDetailForm form = new MultiDetailForm(this, typeof(DataDisplay), new Type[] { typeof(RatePeriod), typeof(RatePeriod) }) {
                Data = new JObject().AddRange("header", display, "detail", new List<RatePeriod>[] { display.Rates, display.Summary })
            };
            form.Details[1].Remove("Start", "End", "ShiftWeeklyUnitsHere", "Battery");
			form.Details[1]["Rate"].Type = "decimal";
			form.CanDelete = display.idDataDisplay != null;
            form.Details[0].Options["addRows"] = true;
            form.Details[0].Options["deleteRows"] = true;
			form.Details[0].Options["sortable"] = true;
			return form;
        }

        public AjaxReturn ViewSave(JObject json) {
            DataDisplay header = json["header"].To<DataDisplay>();
            if(header.BatteryStorage > 0) {
                Utils.Check(header.MaxChargeRate > 0, "Max Charge Rate must be > 0");
				Utils.Check(header.MaxDischargeRate > 0, "Max Discharge Rate must be > 0");
				Utils.Check(header.Efficiency > 0 && header.Efficiency <= 100, "Efficience must be > 0 and <= 100%");
            }
            JArray details = (JArray)json["detail"];
			header.Rates = new List<RatePeriod>(details[0].To<List<RatePeriod>>()
                .Where(rp => rp.Start != rp.End && rp.Rate != 0));
            header.Recalc(Database);
            AjaxReturn r = SaveRecord(header);
            if (r.error == null)
                r.redirect = "/home/view?id=" + header.idDataDisplay;
            return r;
        }

        public AjaxReturn ViewDelete(int id) {
            return DeleteRecord("DataDisplay", id);
        }

        public DataTableForm ShowData(DateTime start, DateTime end) {
            DataTableForm form = new DataTableForm(this, typeof(Data));
            return form;
        }

        public JObjectEnumerable ShowDataListing(DateTime start, DateTime end) {
            return Database.Query($@"SELECT * FROM Data 
WHERE Period > {Database.Quote(start)}
AND Period <= {Database.Quote(end.AddDays(1))}
ORDER BY Period");
        }


		public Form Recalc() {
            return new Form(this, typeof(DataDisplay), true, "PeriodStart", "PeriodEnd") {
                Data = new DataDisplay() {
                    PeriodStart = DateTime.Today.AddYears(-1),
                    PeriodEnd = DateTime.Today
                }
            };
        }

        public AjaxReturn RecalcSave(DataDisplay json) {
            new BatchJob(this, "/home/list", delegate () {
                List<DataDisplay> data = Database.Query<DataDisplay>("SELECT * FROM DataDisplay").ToList();
                Batch.Records = data.Count;
                foreach (DataDisplay d in data) {
                    Batch.Status = d.Name;
                    d.PeriodStart = json.PeriodStart;
                    d.PeriodEnd = json.PeriodEnd;
                    d.Recalc(Database);
                    Batch.Record++;
                    SaveRecord(d);
                }
            });
            return new AjaxReturn() { redirect = "/admin/batch?id=" + Batch.Id };
        }

        public DumbForm Import() {
            DumbForm form = new DumbForm(this, true);
            form.Add(new FieldAttribute("type", "file", "data", "UploadData", "attributes", "class='autoUpload'"));
            form.Data = new JObject();
            return form;
        }

        public void ImportSave(UploadedFile UploadData) {
            // TODO: check if times are UTC or local, and adjust accordingly
            new BatchJob(this, "/home/list", delegate () {
                Database.BeginTransaction();
                int time = -1, units = -1, start = -1;
                string[] data = UploadData.Content.Split('\n');
                Batch.Records = data.Length;
                foreach (string line in data) {
                    Batch.Record++;
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    string[] vals = line.Split(',');
                    Utils.Check(vals.Length >= 2, $"{Batch.Record}:No comma:'$line'");
                    if (units == -1) {
                        // header row
                        for (int i = 0; i < vals.Length; i++) {
                            if (vals[i].Contains("Consumption"))
                                units = i;
                            else if (vals[i].Contains("time") || vals[i].Contains("End"))
                                time = i;
							else if (vals[i].Contains("Start"))
								start = i;
						}
						Utils.Check(time >= 0, $"{Batch.Record}:Time of reading heading not found:'$line'");
                        Utils.Check(time >= 0 && units >= 0, $"{Batch.Record}:Consumption heading not found:'$line'");
                        continue;
                    }
                    Data d = new Data() {
                        Period = DateTime.Parse(vals[time].Replace("\"", "")),
                        Value = Convert.ToDouble(Double.Parse(vals[units].Replace("\"", "")))
                    };
                    d.Start = start >= 0 ? DateTime.Parse(vals[start].Replace("\"", "")) : d.Period.AddMinutes(-30);

                    Database.Update(d);
                }
                Database.Commit();
            });
        }

        public class CheckResult : JsonObject {
            [Field(Type = "dateTime")]
            public DateTime Start;
            [Field(Type = "dateTime")]
            public DateTime End;
        }

        public ListForm Check() {
            List<CheckResult> results = new List<CheckResult>();
            CheckResult current = null;
            foreach (Data d in Database.Query<Data>($@"SELECT * FROM Data ORDER BY Period")) {
                if (d.Start == DateTime.MinValue)
                    d.Start = d.Period.AddMinutes(-30);
                if (current == null || d.Start > current.End) {
                    current = new CheckResult() { Start = d.Period, End = d.Period };
                    results.Add(current);
                } else {
                    current.End = d.Period;
                }
            }
            ListForm form = new ListForm(this, typeof(CheckResult), false);
            form.Data = results;
            return form;
        }

        public Form DownloadFromHildebrand() {
            return new Form(this, typeof(DownloadRequest)) {
                Data = new DownloadRequest() {
                    Start = DateTime.Now.AddDays(-10),
                    End = DateTime.Now,
                    HildebrandLogin = ((Settings)Settings).HildebrandLogin,
                    HildebrandPassword = ((Settings)Settings).HildebrandPassword
                }
            };
        }

        HttpClient client;
        static DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);

        public AjaxReturn DownloadFromHildebrandSave(DownloadRequest json) {
            ((Settings)Settings).HildebrandLogin = json.HildebrandLogin;
            ((Settings)Settings).HildebrandPassword = json.HildebrandPassword;
            Database.Update(Settings);
            new AsyncBatchJob(this, "/home/list", async delegate () {
                using (client = new HttpClient()) {
                    Batch.Status = "Logging in";
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    JObject headers = new JObject().AddRange(
//                        "Content-Type", "application/json",
                        "applicationId", "b0f1b774-a586-4f72-9edd-27ead8aa7a8d"
                        );
                    // Login
                    JObject j = (JObject) await send(HttpMethod.Post, "https://api.glowmarkt.com/api/v0-1/auth",
                        headers, new JObject().AddRange(
                            "username", json.HildebrandLogin,
                            "password", json.HildebrandPassword
                            ));
                    Batch.Status = "Getting entities";
                    headers["token"] = j["token"];
                    JArray entities = (JArray)await send(HttpMethod.Get, "https://api.glowmarkt.com/api/v0-1/virtualentity", headers, null);
                    JObject resource = (JObject)((JArray)entities[0]["resources"]).First(r => r["name"].ToString().Contains("consumption"));
                    Batch.Status = "Getting data";
                    j = (JObject)await send(HttpMethod.Get, $"https://api.glowmarkt.com/api/v0-1/resource/{resource["resourceId"]}/readings?from={json.Start:s}&to={json.End:s}&period=PT30M&function=sum", headers, null);
                    JArray data = (JArray)j["data"];
                    Database.BeginTransaction();
                    Batch.Records = data.Count;
                    foreach (JArray line in data) {
                        Batch.Record++;
                        Data d = new Data() {
                            Period = epoch.AddSeconds(line[0].ToObject<int>()),
                            Value = line[1].ToObject<double>()
                        };
                        Database.Update(d);
                    }
                    Database.Commit();
                }
            });
            return new AjaxReturn() { redirect = "/admin/batch?id=" + Batch.Id };
        }

		public Form DownloadFromOctopus() {
			return new Form(this, typeof(OctopusDownloadRequest)) {
				Data = new OctopusDownloadRequest() {
					Start = DateTime.Now.AddDays(-10),
					End = DateTime.Now,
					OctopusAccountNumber = ((Settings)Settings).OctopusAccountNumber,
					OctopusApiKey = ((Settings)Settings).OctopusApiKey,
                    MeterMpan = ((Settings)Settings).MeterMpan,
                    MeterSerialNumber = ((Settings)Settings).MeterSerialNumber
				}
			};
		}

        string octopusDate(DateTime d) {
            return d.ToUniversalTime().ToString("yyyy-MM-ddThh:mm:ssZ");
        }

		public AjaxReturn DownloadFromOctopusSave(OctopusDownloadRequest json) {
			((Settings)Settings).OctopusAccountNumber = json.OctopusAccountNumber;
			((Settings)Settings).OctopusApiKey = json.OctopusApiKey;
			((Settings)Settings).MeterMpan = json.MeterMpan;
			((Settings)Settings).MeterSerialNumber = json.MeterSerialNumber;
			Database.Update(Settings);
			new AsyncBatchJob(this, "/home/list", async delegate () {
				using (client = new HttpClient()) {
					Batch.Status = "Downloading data";
					client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
					JObject headers = new JObject().AddRange(
						"Authorization", "Basic " + Convert.ToBase64String(Encoding.GetBytes(json.OctopusApiKey + ":"))
						);
                    // Login
                    JObject j = (JObject)await send(HttpMethod.Get,
$"https://api.octopus.energy/v1/electricity-meter-points/{json.MeterMpan}/meters/{json.MeterSerialNumber}/consumption/"
+ "?order_by=period"
+ "&page_size=25000&"
+ "period_from=" + octopusDate(json.Start)
+ "&period_to=" + octopusDate(json.End),
                        headers, null);
                    JArray data = (JArray)j["results"];
					Batch.Status = "Analysing results";
					Database.BeginTransaction();
					Batch.Records = data.Count;
					foreach (JObject line in data) {
						Batch.Record++;
                        Data d = new Data() {
							Start = line.AsDate("interval_start"),
							Period = line.AsDate("interval_end"),
                            Value = line.AsDouble("consumption")
						};
						Database.Update(d);
					}
					Database.Commit();
				}
			});
			return new AjaxReturn() { redirect = "/admin/batch?id=" + Batch.Id };
		}

		async Task<JToken> send(HttpMethod method, string uri, JObject headers, JObject postParameters) {
            using (var message = new HttpRequestMessage(method, uri)) {
                foreach (KeyValuePair<string, JToken> h in headers)
                    message.Headers.Add(h.Key, h.Value.ToString());
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                message.Headers.Add("User-Agent", "Electricity");
                if (postParameters != null)
                    message.Content = new StringContent(postParameters.ToJson(), System.Text.Encoding.UTF8, "application/json");
                using (HttpResponseMessage result = await client.SendAsync(message)) {
                    string data = await result.Content.ReadAsStringAsync();
                    result.EnsureSuccessStatusCode();
                    return JToken.Parse(data);
                }
            }
        }
    }

    [Table]
    public class DataDisplay : JsonObject {
        [Primary]
        public int? idDataDisplay;
        [Unique("Name")]
        public string Name;
        public decimal Rate;
        public decimal StandingCharge;
        public DateTime PeriodStart;
        public DateTime PeriodEnd;
        [Field(Postamble = "kWh")]
        public decimal BatteryStorage;
        [Field(Postamble = "kW")]
        public decimal MaxChargeRate;
		[Field(Postamble = "kW")]
		public decimal MaxDischargeRate;
		[Field(Postamble = "%")]
		public decimal Efficiency;
        public BatteryUse MainRateBattery = BatteryUse.Use;
        [Field(Visible = false)]
        [Length(0)]
        public string RateData;
        [DoNotStore]
        [JsonIgnore]
        public List<RatePeriod> Rates {
            get {
                if (string.IsNullOrWhiteSpace(RateData))
                    return new List<RatePeriod>();
                else
                    return JArray.Parse(RateData).To<List<RatePeriod>>();
            }
            set {
                RateData = value.ToJToken().ToString();
            }
        }
		[Field(Visible = false)]
		[Length(0)]
		public string SummaryData;
		[DoNotStore]
		[JsonIgnore]
		public List<RatePeriod> Summary {
			get {
				if (string.IsNullOrWhiteSpace(SummaryData))
					return new List<RatePeriod>();
				else
					return JArray.Parse(SummaryData).To<List<RatePeriod>>();
			}
			set {
				SummaryData = value.ToJToken().ToString();
			}
		}
		[ReadOnly]
        public int Days;
        [ReadOnly]
        public decimal TotalUsage;
		[ReadOnly]
        public decimal AnnualUsage;
        [ReadOnly]
        public decimal StandingCost;
        [ReadOnly]
        public decimal TotalCost;
        [ReadOnly]
        public decimal AnnualCost;
        [ReadOnly]
        public decimal MonthlyCost;

        public void Recalc(Database db) {
            List<RatePeriod> rates = Rates;
            DateTime first = DateTime.MaxValue;
            DateTime last = DateTime.MinValue;
            foreach (RatePeriod rate in rates) {
                rate.Units = 0;
                rate.Cost = 0;
                rate.BatteryChargedUnits = 0;
                rate.BatteryUsedUnits = 0;
            }
            RatePeriod standardRate = new RatePeriod() { Rate = Rate, Battery = MainRateBattery };
            decimal BatteryCharge = 0;

            foreach (Data d in db.Query<Data>($@"SELECT *
FROM Data
WHERE Period > {db.Quote(PeriodStart)}
AND Period <= {db.Quote(PeriodEnd.AddDays(1))}
ORDER BY Period
")) {
                decimal time = d.Period.Hour + d.Period.Minute / (decimal)100;
                d.RateIndex = -1;
                for (int i = 0; i < rates.Count; i++) {
                    RatePeriod rate = rates[i];
                    if (rate.Matches(time)) {
                        d.RateIndex = i;
                        updateRate(rate, ref d.Value, ref BatteryCharge);
                        break;
                    }
                }
                if (d.RateIndex == -1)
                    updateRate(standardRate, ref d.Value, ref BatteryCharge);
                if (d.Period < first)
                    first = d.Period.Date;
                if (d.Period > last)
                    last = d.Period.Date;
            }
            if (last != DateTime.MaxValue) {
                PeriodStart = first.Date;
                PeriodEnd = last.AddSeconds(-1).Date;
            }
            Days = (int)(PeriodEnd - PeriodStart).TotalDays;
            StandingCost = Days * StandingCharge / 100;
            List<RatePeriod> summary = new List<RatePeriod> {
				standardRate
			};
            double totalUsage = standardRate.Units;
            TotalCost = StandingCost;
            foreach (RatePeriod rate in rates) {
				totalUsage += rate.Units;
                decimal shift = Math.Min(Math.Round(rate.ShiftWeeklyUnitsHere * Days / 7m, 0), (decimal)standardRate.Units);
				standardRate.Units -= (double)shift;
                rate.Units += (double)shift;
                rate.Cost = (decimal)(rate.Units * (double)rate.Rate / 100);
                TotalCost += rate.Cost;
                RatePeriod p = summary.FirstOrDefault(s => s.Rate == rate.Rate);
                if(p == null) {
                    p = new RatePeriod();
                    p.CopyFrom(rate);
                    summary.Add(p);
                } else {
                    p.Units += rate.Units;
                    p.Cost += rate.Cost;
                    p.BatteryChargedUnits += rate.BatteryChargedUnits;
                    p.BatteryUsedUnits += rate.BatteryUsedUnits;
                }
            }
			standardRate.Cost = (decimal)standardRate.Units * standardRate.Rate / 100;
			TotalCost += standardRate.Cost;
			foreach (RatePeriod rate in rates) {
                rate.Percentage = TotalUsage > 0 ? (decimal)rate.Units / (decimal)TotalUsage : 0;
            }
			foreach (RatePeriod rate in summary) {
				rate.Percentage = TotalUsage > 0 ? (decimal)rate.Units / (decimal)TotalUsage : 0;
			}
			TotalUsage = (decimal)totalUsage;
			AnnualUsage = TotalUsage * 365 / Days;
            AnnualCost = TotalCost * 365 / Days;
            MonthlyCost = Math.Round(AnnualCost / 12, 2);
            Rates = rates;
            Summary = summary;
        }

        void DebugRates(List<RatePeriod> rates, RatePeriod standardRate) {
            List<RatePeriod> all = new List<RatePeriod>(rates);
            all.Add(standardRate);
            decimal total = 0;
            foreach (RatePeriod rate in all) {
				System.Diagnostics.Debug.WriteLine($"{rate.Rate} {rate.Battery} c={rate.BatteryChargedUnits} u={rate.BatteryUsedUnits}");
                total += rate.BatteryChargedUnits - rate.BatteryUsedUnits;
			}
			System.Diagnostics.Debug.WriteLine($"t={total}");
		}

		void updateRate(RatePeriod rate, ref double value, ref decimal BatteryCharge) {
			if (BatteryStorage > 0) {
				switch (rate.Battery) {
					case BatteryUse.Charge:
						decimal freeCapacity = BatteryStorage - BatteryCharge;
						if (freeCapacity > 0) {
							decimal amountToCharge = Math.Min(freeCapacity, MaxChargeRate / 2);
//                            System.Diagnostics.Debug.WriteLine($"{rate.Rate} charging {amountToCharge} from {BatteryCharge} capacity {freeCapacity}");
							BatteryCharge += amountToCharge;
							rate.BatteryChargedUnits += amountToCharge;
							value += (double)(amountToCharge * 100 / Efficiency);
						}
						break;
					case BatteryUse.Use:
						decimal availableCapacity = Math.Min(BatteryCharge, MaxDischargeRate / 2);
						if (availableCapacity > 0) {
							decimal amountToUse = Math.Min(availableCapacity, (decimal)value);
//							System.Diagnostics.Debug.WriteLine($"{rate.Rate} using {amountToUse} from {BatteryCharge} capacity {availableCapacity}");
							BatteryCharge -= amountToUse;
							value -= (double)amountToUse;
							rate.BatteryUsedUnits += amountToUse;
						}
						break;
				}
                Utils.Check(BatteryCharge >= 0, "Battery charge negative");
				Utils.Check(BatteryCharge <= BatteryStorage, "Battery overcharged");
			}
			rate.Units += value;
		}

	}

    [Writeable]
    public class DownloadRequest : JsonObject {
        public DateTime Start;
        public DateTime End;
        public string HildebrandLogin;
        public string HildebrandPassword;
    }

	[Writeable]
	public class OctopusDownloadRequest : JsonObject {
		public DateTime Start;
		public DateTime End;
		public string OctopusAccountNumber;
		public string OctopusApiKey;
		public string MeterMpan;
		public string MeterSerialNumber;
	}

	[Table]
    public class Data : JsonObject {
		[Field(Type = "dateTime")]
		public DateTime Start;
		[Primary(AutoIncrement = false)]
		[Field(Type="dateTime")]
        public DateTime Period;
		public double Value;
        [DoNotStore]
        public int RateIndex;
    }

    public enum BatteryUse {
        None,
        Charge,
        Use
    }

    public class RatePeriod : JsonObject {
        [Writeable]
        public decimal Start;
        [Writeable]
        public decimal End;
        [Writeable]
        public decimal Rate;
        [Writeable]
        public int ShiftWeeklyUnitsHere;
		[Writeable]
		public BatteryUse Battery = BatteryUse.Use;
		public double Units;
        public decimal Cost;
        public decimal Percentage;
        public decimal BatteryChargedUnits;
		public decimal BatteryUsedUnits;
		public bool Matches(decimal time) {
            return Start < End ? time > Start && time <= End :
                Start > End  ? time > Start || time <= End 
                : false;
        }
    }

    [Table]
    public class Settings : CodeFirstWebFramework.Settings {
        public decimal OffPeakStart;
        public decimal OffPeakEnd;
        public decimal OffPeakRate;
        public decimal PeakRate;
        public decimal StandingCharge;
        public string HildebrandLogin;
        public string HildebrandPassword;
        public string OctopusAccountNumber;
        public string OctopusApiKey;
        public string MeterMpan;
        public string MeterSerialNumber;
    }
}
