using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>"Battery" section: health / wear of every battery and a discharge test under load.</summary>
    public partial class MainForm
    {
        BatteryTest batTest;

        void BuildBattery()
        {
            BeginPage();
            var loading = new TextBlock(L.T("Читаю сведения об аккумуляторе…"), TStyle.Muted);
            page.Controls.Add(loading);
            EndPage();
            Task.Run(() => BatteryService.Query()).ContinueWith(t => BeginInvoke(new Action(() =>
            {
                if (section != "battery") return;
                BuildBatteryPage(t.IsFaulted ? new List<BatteryInfo>() : t.Result);
            })));
        }

        static string Wh(long mwh) { return mwh > 0 ? (mwh / 1000.0).ToString("0.0") + " " + L.T("Вт·ч") : "—"; }

        void BuildBatteryPage(List<BatteryInfo> list)
        {
            BeginPage();
            if (list.Count == 0)
            {
                page.Controls.Add(new Banner(BannerKind.Info, L.T("Аккумулятор не найден"),
                    L.T("Windows не сообщает о батарее: это настольный компьютер или драйвер аккумулятора не установлен.")));
                EndPage();
                return;
            }
            foreach (var b in list) page.Controls.Add(BatteryCard(b, list.Count > 1));

            var tray = new Toggle(L.T("Показывать заряд и здоровье аккумулятора в меню значка в трее"), Settings.TrayBattery);
            tray.CheckedChanged += (s, e) => { Settings.TrayBattery = tray.Checked; Settings.Save(); };
            var tc = Card(L.T("Значок в трее"));
            tc.Controls.Add(tray);
            page.Controls.Add(tc);

            BuildBatteryTest(BatteryService.Combined(list));
            EndPage();
        }

        Control BatteryCard(BatteryInfo b, bool several)
        {
            string title = several ? L.F("Аккумулятор {0}", b.Index + 1) : L.T("Здоровье аккумулятора");
            var card = Card(title, string.Join("  ·  ", new[] { b.Manufacturer, b.Name, b.Chemistry }.Where(x => !string.IsNullOrEmpty(x)).ToArray()));
            var grid = new TileGrid { MinTile = 160, TileH = 92 };
            var tHealth = new StatTile(L.T("Здоровье"), b.HealthPercent.HasValue ? b.HealthPercent.Value.ToString("0") + " %" : "—", b.HealthText);
            tHealth.ValueColor = () => b.Health == BatteryHealth.Good ? Theme.P.Success : b.Health == BatteryHealth.Fair ? Theme.P.Warning : b.Health == BatteryHealth.Poor ? Theme.P.Danger : Theme.P.Text;
            var tCharge = new StatTile(L.T("Заряд"), b.Percent.HasValue ? b.Percent.Value.ToString("0") + " %" : "—", b.StatusText);
            var tCap = new StatTile(L.T("Ёмкость сейчас"), Wh(b.FullMWh), b.DesignMWh > 0 ? L.F("заводская {0}", Wh(b.DesignMWh)) : null);
            var tWear = new StatTile(L.T("Износ"), b.WearPercent.HasValue ? b.WearPercent.Value.ToString("0") + " %" : "—", L.T("потеря ёмкости от заводской"));
            var tCycles = new StatTile(L.T("Циклы заряда"), b.CycleCount.HasValue ? b.CycleCount.Value.ToString("N0") : "—", b.CycleCount.HasValue ? null : L.T("контроллер не сообщает"));
            var tRate = new StatTile(L.T("Мощность"), b.RateW.HasValue ? Math.Abs(b.RateW.Value).ToString("0.0") + " " + L.T("Вт") : "—",
                b.RateMW.HasValue ? (b.RateMW.Value > 0 ? L.T("заряжается") : b.RateMW.Value < 0 ? L.T("разряжается") : L.T("не заряжается")) : null);
            var tVolt = new StatTile(L.T("Напряжение"), b.VoltageMV.HasValue ? (b.VoltageMV.Value / 1000.0).ToString("0.00") + " " + L.T("В") : "—");
            var tTemp = new StatTile(L.T("Температура"), b.TemperatureC.HasValue ? b.TemperatureC.Value.ToString("0") + " °C" : "—", b.TemperatureC.HasValue ? null : L.T("датчик недоступен"));
            var tLeft = new StatTile(L.T("Осталось"), b.EstimatedMinutes.HasValue && b.EstimatedMinutes.Value > 0 ? Dur(TimeSpan.FromMinutes(b.EstimatedMinutes.Value)) : "—", L.T("оценка Windows"));
            grid.Controls.AddRange(new Control[] { tHealth, tCharge, tCap, tWear, tCycles, tRate, tVolt, tTemp, tLeft });
            card.Controls.Add(grid);
            var info = new List<string>();
            if (!string.IsNullOrEmpty(b.Serial)) info.Add(L.T("Серийный номер:") + " " + b.Serial);
            if (!string.IsNullOrEmpty(b.ManufactureDate)) info.Add(L.T("Дата производства:") + " " + b.ManufactureDate);
            if (b.Relative) info.Add(L.T("Контроллер сообщает ёмкость в относительных единицах — ватт-часы и мощность недоступны."));
            if (info.Count > 0) card.Controls.Add(new TextBlock(string.Join("   ·   ", info.ToArray()), TStyle.Small));
            card.Controls.Add(new TextBlock(L.T("Здоровье — отношение текущей полной ёмкости к заводской. Выше 80 % — хорошо, 60–80 % — аккумулятор заметно изношен, ниже 60 % — стоит заменить."), TStyle.Small));
            return card;
        }

        void BuildBatteryTest(BatteryInfo bat)
        {
            var card = Card(L.T("Проверка разрядом"), L.T("Аккумулятор разряжается под нагрузкой, программа измеряет мощность, падение напряжения и реальную ёмкость"));
            var cTime = new ThemedCombo(L.T("10 минут"), L.T("30 минут"), L.T("1 час"), L.T("до порога заряда"));
            int[] mins = { 10, 30, 60, 0 };
            cTime.SelectedIndex = 1;
            var cStop = new ThemedCombo("50 %", "30 %", "20 %", "10 %");
            int[] stops = { 50, 30, 20, 10 };
            cStop.SelectedIndex = 2;
            var cLoad = new ThemedCombo(L.T("без нагрузки (простой)"), L.T("процессор"), L.T("процессор и видеокарта"));
            cLoad.SelectedIndex = 1;
            card.Controls.Add(FormRow(L.T("Длительность"), cTime, 190));
            card.Controls.Add(FormRow(L.T("Остановить при заряде"), cStop, 190));
            card.Controls.Add(FormRow(L.T("Нагрузка"), cLoad, 190));
            var start = new FlatButton(L.T("Запустить"), BtnKind.Primary, "");
            var stop = new FlatButton(L.T("Остановить"), BtnKind.Secondary) { Enabled = false };
            card.Controls.Add(new Row { MinHeight = 38 }.Add(start, 160).Add(stop, 130).Add(new Panel(), -1));
            var prog = new ProgressLine();
            var status = new TextBlock(L.T("Отключите зарядку, чтобы начать проверку."), TStyle.Muted) { Single = true };
            card.Controls.Add(prog);
            card.Controls.Add(status);

            var grid = new TileGrid { MinTile = 150, TileH = 92 };
            var tW = new StatTile(L.T("Средняя мощность"), "—");
            var tPeak = new StatTile(L.T("Пиковая мощность"), "—");
            var tUsed = new StatTile(L.T("Израсходовано"), "—");
            var tRun = new StatTile(L.T("Время работы"), "—", L.T("при такой нагрузке"));
            var tCap = new StatTile(L.T("Реальная ёмкость"), "—");
            var tSag = new StatTile(L.T("Просадка напряжения"), "—");
            grid.Controls.AddRange(new Control[] { tW, tPeak, tUsed, tRun, tCap, tSag });
            card.Controls.Add(grid);
            var chart = new TempChart { Window = TimeSpan.FromMinutes(30), Unit = " %", Axis = "%", Warn = double.NaN, Bad = double.NaN };
            card.Controls.Add(new TextBlock(L.T("Заряд во время проверки"), TStyle.Bold) { Single = true });
            card.Controls.Add(chart);
            var verdict = new TextBlock("", TStyle.Body);
            card.Controls.Add(verdict);
            page.Controls.Add(card);

            DateTime t0 = DateTime.Now;
            Action<BatteryTestResult> showResult = r =>
            {
                tW.Set(r.AvgW.ToString("0.0") + " " + L.T("Вт"), null);
                tPeak.Set(r.PeakW.ToString("0.0") + " " + L.T("Вт"), null);
                tUsed.Set(r.PercentUsed.ToString("0.0") + " %", r.MWhUsed > 0 ? Wh(r.MWhUsed) : null);
                tRun.Set(r.RuntimeHours.HasValue ? Dur(TimeSpan.FromHours(r.RuntimeHours.Value)) : "—", L.T("при такой нагрузке"));
                tCap.Set(r.MeasuredCapacityMWh.HasValue ? Wh(r.MeasuredCapacityMWh.Value) : "—",
                    r.CapacityDeltaPercent.HasValue ? L.F("{0} % к заявленной", (r.CapacityDeltaPercent.Value >= 0 ? "+" : "") + r.CapacityDeltaPercent.Value.ToString("0.0")) : L.T("нужно израсходовать от 3 %"));
                tSag.Set(r.VoltageSagMV.HasValue ? r.VoltageSagMV.Value.ToString("N0") + " " + L.T("мВ") : "—", null);
                verdict.Text = r.Verdict ?? "";
                verdict.Invalidate(); card.PerformLayout();
            };
            Action<BatterySample> addPoint = smp =>
            {
                chart.Points.Add(new TempPoint { T = t0.AddSeconds(smp.Seconds), V = smp.Percent });
                chart.Invalidate();
            };

            Action<bool> running = on =>
            {
                start.Enabled = !on; stop.Enabled = on; cTime.Enabled = cStop.Enabled = cLoad.Enabled = on ? false : true;
                prog.Busy = on;
                StressBusy(on);
            };

            start.Click += (o, e) =>
            {
                if (batTest != null && batTest.Running) return;
                string why = BatteryTest.CheckPreconditions(stops[cStop.SelectedIndex]);
                if (why != null && !Demo.On) { FxMessage.Show(this, why, L.T("Проверка разрядом"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                var opt = new BatteryTestOptions { Minutes = mins[cTime.SelectedIndex], StopPercent = stops[cStop.SelectedIndex], Load = (BatteryLoad)cLoad.SelectedIndex };
                chart.Window = TimeSpan.FromMinutes(opt.Minutes > 0 ? opt.Minutes : 120);
                chart.Points.Clear(); t0 = DateTime.Now; verdict.Text = "";
                var test = new BatteryTest();
                test.SampleAdded += smp => BeginInvoke(new Action(() =>
                {
                    addPoint(smp);
                    status.Text = L.F("Идёт проверка…  {0}  ·  заряд {1} %  ·  {2} Вт", Dur(TimeSpan.FromSeconds(smp.Seconds)), smp.Percent.ToString("0.0"), smp.DischargeW.ToString("0.0"));
                    if (opt.Minutes > 0) prog.Value = Math.Min(1, smp.Seconds / (opt.Minutes * 60.0));
                }));
                test.Finished += r => BeginInvoke(new Action(() =>
                {
                    if (gpuLoadForBattery != null) { gpuLoadForBattery(); gpuLoadForBattery = null; }
                    running(false);
                    status.Text = r.ReasonText + "  ·  " + Dur(TimeSpan.FromSeconds(r.Seconds));
                    showResult(r);
                }));
                string err;
                if (!test.Start(opt, out err)) { FxMessage.Show(this, err, L.T("Проверка разрядом"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                batTest = test;
                Telemetry.Count("action.battery_test_" + (opt.Load == BatteryLoad.None ? "idle" : opt.Load == BatteryLoad.Cpu ? "cpu" : "cpu_gpu"));
                if (opt.Load == BatteryLoad.CpuGpu) StartGpuLoadForBattery();
                running(true);
                status.Text = L.T("Запуск…");
            };
            stop.Click += (o, e) => { if (batTest != null) batTest.Cancel(); };
            card.Disposed += (o, e) => { if (batTest != null && batTest.Running) batTest.Cancel(); };

            if (Demo.On)
            {
                var r = BatteryTest.DemoResult();
                t0 = DateTime.Now.AddSeconds(-r.Seconds);
                foreach (var smp in r.Samples) addPoint(smp);
                status.Text = r.ReasonText + "  ·  " + Dur(TimeSpan.FromSeconds(r.Seconds));
                showResult(r);
            }
        }

        // GPU load for the "CPU + GPU" battery test: a hidden-from-taskbar window with the OpenGL stress view.
        Action gpuLoadForBattery;
        void StartGpuLoadForBattery()
        {
            try
            {
                var f = new Form { Text = L.T("Нагрузка на видеокарту"), Width = Theme.Dp(480), Height = Theme.Dp(320), ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent };
                var view = new GpuStressView { Dock = DockStyle.Fill };
                f.Controls.Add(view);
                f.Show(this);
                view.StartRender();
                gpuLoadForBattery = () => { try { view.StopRender(); f.Close(); f.Dispose(); } catch { } };
            }
            catch { gpuLoadForBattery = null; }
        }
    }
}
