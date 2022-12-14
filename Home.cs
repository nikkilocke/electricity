using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeFirstWebFramework;
using Markdig.Syntax.Inlines;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Asn1.Cms;
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
            return new DataTableForm(this, typeof(DataDisplay), false, "Name", "OffPeakRate", "PeakRate", "StandingCharge", "ShiftWeeklyUnitsToOffPeak", "PercentageOffPeak", "TotalCost", "AnnualCost", "MonthlyCost") {
                Select = "/home/view"
            };
        }

        public JObjectEnumerable ListListing() {
            return Database.Query("SELECT * FROM DataDisplay ORDER BY Name");
        }

        public Form View(int id) {
            if(id > 0)
                InsertMenuOptions(new MenuOption("Copy", "/home/view?dup=y&id=" + id));
            if (id <= 0 || !Database.TryGet(id, out DataDisplay display)) {
                display = new DataDisplay() {
                    OffPeakStart = ((Settings)Settings).OffPeakStart,
                    OffPeakEnd = ((Settings)Settings).OffPeakEnd,
                    OffPeakRate = ((Settings)Settings).OffPeakRate,
                    PeakRate = ((Settings)Settings).PeakRate,
                    StandingCharge = ((Settings)Settings).StandingCharge,
                    PeriodStart = DateTime.Today.AddYears(-1),
                    PeriodEnd = DateTime.Today
                };
            }
            if (GetParameters["dup"] == "y") {
                display.idDataDisplay = null;
                display.Name += " (copy)";
            }
            Form form = new Form(this, typeof(DataDisplay)) {
                Data = display
            };
            return form;
        }

        public AjaxReturn ViewSave(DataDisplay json) {
            json.OffPeakUsage = json.PeakUsage = 0;
            DateTime first = DateTime.MaxValue;
            DateTime last = DateTime.MinValue;
            foreach(Data d in Database.Query<Data>($@"SELECT *
FROM Data
WHERE Period >= {Database.Quote(json.PeriodStart)}
AND Period < {Database.Quote(json.PeriodEnd)}
")) {
                if (d.InPeakTime(json.OffPeakStart, json.OffPeakEnd))
                    json.PeakUsage += d.Value;
                else
                    json.OffPeakUsage += d.Value;
                if (d.Period < first)
                    first = d.Period.Date;
                if (d.Period > last)
                    last = d.Period.Date;
            }
            if(last != DateTime.MaxValue) {
                json.PeriodStart = first;
                json.PeriodEnd = last;
            }
            json.Days = (int)(json.PeriodEnd - json.PeriodStart).TotalDays;
            decimal shift = json.ShiftWeeklyUnitsToOffPeak * json.Days / 7m;
            json.PeakUsage -= shift;
            json.OffPeakUsage += shift;
            json.TotalUsage = json.OffPeakUsage + json.PeakUsage;
            json.PercentageOffPeak = json.TotalUsage > 0 ? (decimal)json.OffPeakUsage / (decimal)json.TotalUsage : 0;
            json.PeakCost = json.PeakUsage * json.PeakRate / 100;
            json.OffPeakCost = json.OffPeakUsage * json.OffPeakRate / 100;
            json.StandingCost = json.Days * json.StandingCharge / 100;
            json.TotalCost = json.OffPeakCost + json.PeakCost + json.StandingCost;
            json.AnnualUsage = json.TotalUsage * 365 / json.Days;
            json.AnnualCost = json.TotalCost * 365 / json.Days;
            json.MonthlyCost = Math.Round(json.AnnualCost / 12, 2);
            AjaxReturn r = SaveRecord(json);
            if(r.error == null)
                r.redirect = "/home/view?id=" + json.idDataDisplay;
            return r;
        }

        public DumbForm Import() {
            DumbForm form = new DumbForm(this, true);
            form.Add(new FieldAttribute("type", "file", "data", "UploadData", "attributes", "class='autoUpload'"));
            form.Data = new JObject();
            return form;
        }

        public void ImportSave(UploadedFile UploadData) {
            new BatchJob(this, "/", delegate () {
                Database.BeginTransaction();
                int lineno = 0;
                foreach(string line in UploadData.Content.Split('\n')) {
                    if (lineno++ == 0 || string.IsNullOrWhiteSpace(line))
                        continue;
                    string []vals = line.Split(',');
                    Utils.Check(vals.Length == 2, $"{lineno}:No comma:'$line'");
                    Data data = new Data() {
                        Period = DateTime.Parse(vals[0].Replace("\"", "")),
                        Value = Decimal.Parse(vals[1].Replace("\"", ""))
                    };
                    System.Diagnostics.Debug.WriteLine(data.ToString());
                    Database.Update(data);
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
        public decimal OffPeakStart;
        public decimal OffPeakEnd;
        public decimal OffPeakRate;
        public decimal PeakRate;
        public decimal StandingCharge;
        public DateTime PeriodStart;
        public DateTime PeriodEnd;
        public int ShiftWeeklyUnitsToOffPeak;
        [ReadOnly]
        public int Days;
        [ReadOnly]
        public decimal TotalUsage;
        [ReadOnly]
        public decimal PeakUsage;
        [ReadOnly]
        public decimal OffPeakUsage;
        [ReadOnly]
        public decimal PercentageOffPeak;
        [ReadOnly]
        public decimal AnnualUsage;
        [ReadOnly]
        public decimal PeakCost;
        [ReadOnly]
        public decimal OffPeakCost;
        [ReadOnly]
        public decimal StandingCost;
        [ReadOnly]
        public decimal TotalCost;
        [ReadOnly]
        public decimal AnnualCost;
        [ReadOnly]
        public decimal MonthlyCost;
    }

    [Table]
    public class Data : JsonObject {
        [Primary(AutoIncrement = false)]
        public DateTime Period;
        public decimal Value;
        public bool InPeakTime(decimal start, decimal end) {
            decimal time = Period.Hour + Period.Minute / 100;
            return start < end ? time < start || time > end : time <= start && time >= end;
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
