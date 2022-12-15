﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeFirstWebFramework;
using Markdig.Syntax.Inlines;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Pkcs;
using static System.Net.Mime.MediaTypeNames;

namespace Electricity {
    public class Home : AppModule {

        protected override void Init() {
            base.Init();
            InsertMenuOptions(
                new MenuOption("List Scenarios", "/home/list"),
                new MenuOption("New Scenario", "/home/view?id=0"),
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

        public HeaderDetailForm View(int id) {
            if(id > 0)
                InsertMenuOptions(new MenuOption("Copy", "/home/view?dup=y&id=" + id));
            if (id <= 0 || !Database.TryGet(id, out DataDisplay display)) {
                display = new DataDisplay() {
                    Rate = ((Settings)Settings).PeakRate,
                    StandingCharge = ((Settings)Settings).StandingCharge,
                    PeriodStart = DateTime.Today.AddYears(-1),
                    PeriodEnd = DateTime.Today
                };
                if(display.Rate != ((Settings)Settings).OffPeakRate)
                    display.Rates = new List<RatePeriod>(new RatePeriod[] {
                        new RatePeriod() {
                            Start = ((Settings)Settings).OffPeakStart,
                            End = ((Settings)Settings).OffPeakEnd,
                            Rate = ((Settings)Settings).OffPeakRate
                        }
                    });
            }
            if (GetParameters["dup"] == "y") {
                display.idDataDisplay = null;
                display.Name += " (copy)";
            }
            HeaderDetailForm form = new HeaderDetailForm(this, typeof(DataDisplay), typeof(RatePeriod)) {
                Data = new JObject().AddRange("header", display, "detail", display.Rates)
            };
            form.Detail.Options["addRows"] = true;
            form.Detail.Options["deleteRows"] = true;
            return form;
        }

        public AjaxReturn ViewSave(JObject json) {
            DataDisplay header = json["header"].To<DataDisplay>();
            header.Rates = new List<RatePeriod>(json["detail"].To<List<RatePeriod>>()
                .Where(rp => rp.Start != rp.End && rp.Rate != 0));
            header.Recalc(Database);
            AjaxReturn r = SaveRecord(header);
            if(r.error == null)
                r.redirect = "/home/view?id=" + header.idDataDisplay;
            return r;
        }

        public void Recalc() {
            new BatchJob(this, "/home/list", delegate () {
                List<DataDisplay> data = Database.Query<DataDisplay>("SELECT * FROM DataDisplay").ToList();
                Batch.Records = data.Count;
                foreach (DataDisplay d in data) {
                    Batch.Status = d.Name;
                    d.Recalc(Database);
                    Batch.Record++;
                    SaveRecord(d);
                }
            });
        }

        public DumbForm Import() {
            DumbForm form = new DumbForm(this, true);
            form.Add(new FieldAttribute("type", "file", "data", "UploadData", "attributes", "class='autoUpload'"));
            form.Data = new JObject();
            return form;
        }

        public void ImportSave(UploadedFile UploadData) {
            // TODO: check if times are UTC or local, and adjust accordingly
            new BatchJob(this, "/", delegate () {
                Database.BeginTransaction();
                int time = -1, units = -1;
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
                        }
                        Utils.Check(time >= 0, $"{Batch.Record}:Time of reading heading not found:'$line'");
                        Utils.Check(time >= 0 && units >= 0, $"{Batch.Record}:Consumption heading not found:'$line'");
                        continue;
                    }
                    Data d = new Data() {
                        Period = DateTime.Parse(vals[time].Replace("\"", "")),
                        Value = Convert.ToDecimal(Double.Parse(vals[units].Replace("\"", "")))
                    };
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
                if(current == null || d.Period > current.End.AddMinutes(30)) {
                    current = new CheckResult() { Start = d.Period, End = d.Period };
                    results.Add(current);
                } else {
                    current.End = d.Period;
                }
            }
            ListForm form = new ListForm(this, typeof(CheckResult));
            form.Data = results;
            return form;
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
        [ReadOnly]
        public int Days;
        [ReadOnly]
        public decimal TotalUsage;
        [ReadOnly]
        public decimal PeakUsage;
        [ReadOnly]
        public decimal AnnualUsage;
        [ReadOnly]
        public decimal PeakCost;
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
            PeakUsage = 0;
            DateTime first = DateTime.MaxValue;
            DateTime last = DateTime.MinValue;
            foreach (RatePeriod rate in rates) {
                rate.Units = 0;
                rate.Cost = 0;
            }
            foreach (Data d in db.Query<Data>($@"SELECT *
FROM Data
WHERE Period >= {db.Quote(PeriodStart)}
AND Period < {db.Quote(PeriodEnd)}
")) {
                decimal time = d.Period.Hour + d.Period.Minute / 100;
                d.RateIndex = -1;
                for (int i = 0; i < rates.Count; i++) {
                    RatePeriod rate = rates[i];
                    if (rate.Matches(time)) {
                        d.RateIndex = i;
                        rate.Units += d.Value;
                        break;
                    }
                }
                if (d.RateIndex == -1)
                    PeakUsage += d.Value;
                if (d.Period < first)
                    first = d.Period.Date;
                if (d.Period > last)
                    last = d.Period.Date.AddDays(1);
            }
            if (last != DateTime.MaxValue) {
                PeriodStart = first;
                PeriodEnd = last;
            }
            Days = (int)(PeriodEnd - PeriodStart).TotalDays;
            StandingCost = Days * StandingCharge / 100;
            TotalUsage = PeakUsage;
            TotalCost = StandingCost;
            foreach (RatePeriod rate in rates) {
                TotalUsage += rate.Units;
                decimal shift = Math.Round(rate.ShiftWeeklyUnitsHere * Days / 7m, 0);
                PeakUsage -= shift;
                rate.Units += shift;
                rate.Cost = rate.Units * rate.Rate / 100;
                TotalCost += rate.Cost;
            }
            foreach (RatePeriod rate in rates) {
                rate.Percentage = TotalUsage > 0 ? (decimal)rate.Units / (decimal)TotalUsage : 0;
            }
            PeakCost = PeakUsage * Rate / 100;
            TotalCost += PeakCost;
            AnnualUsage = TotalUsage * 365 / Days;
            AnnualCost = TotalCost * 365 / Days;
            MonthlyCost = Math.Round(AnnualCost / 12, 2);
            Rates = rates;
        }
    }

    [Table]
    public class Data : JsonObject {
        [Primary(AutoIncrement = false)]
        public DateTime Period;
        public decimal Value;
        [DoNotStore]
        public int RateIndex;
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
        public decimal Units;
        public decimal Cost;
        public decimal Percentage;
        public bool Matches(decimal time) {
            return Start < End ? time > Start && time <= End : Start > End  ? time > End || time <= Start : false;
        }
    }

    [Table]
    public class Settings : CodeFirstWebFramework.Settings {
        public decimal OffPeakStart;
        public decimal OffPeakEnd;
        public decimal OffPeakRate;
        public decimal PeakRate;
        public decimal StandingCharge;
    }
}
