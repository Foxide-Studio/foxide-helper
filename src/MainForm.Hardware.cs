using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>Graphics card info, the "Cooling" section and the shared background sensor poller (tray menu).</summary>
    public partial class MainForm
    {
        HwSensorsLive hw;                 // created lazily on a worker thread; Poll() runs every few seconds
        System.Threading.Timer hwTimer;
        string trayBatteryLine;
        BatteryInfo hwBattery;
        int hwPolling;

        /// <summary>Start background polling (sensors for the tray menu and the Cooling page). Cheap after the first poll.</summary>
        void StartHwPoller()
        {
            if (hwTimer != null) return;
            hwTimer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.Exchange(ref hwPolling, 1) == 1) return;
                try
                {
                    if (hw == null) hw = new HwSensorsLive();
                    hw.Poll();
                    PollPawn();
                    trayBatteryLine = Settings.TrayBattery && BatteryService.HasBattery ? BatteryService.TrayLine() : null;
                    if (BatteryService.HasBattery) hwBattery = BatteryService.Combined(BatteryService.Query());
                }
                catch { }
                finally { Interlocked.Exchange(ref hwPolling, 0); }
            }, null, 1500, 4000);
        }

        void StopHwPoller()
        {
            try { if (hwTimer != null) hwTimer.Dispose(); } catch { }
            hwTimer = null;
            try { if (hw != null) hw.Dispose(); } catch { }   // also returns GPU fans to automatic mode
            ResetPawnSensors();                                  // and motherboard fans
            hw = null;
        }

        /// <summary>Tray menu lines: temperatures and battery (only values that exist).</summary>
        void AddTraySensorItems()
        {
            int added = 0;
            if (Settings.TraySensors && hw != null)
                foreach (var kv in PawnTrayLines(hw.TrayLines()))
                {
                    trayMenu.Items.Add(new ToolStripMenuItem(kv.Key + "   " + kv.Value, null, (s, e) => { ShowFromTray(); SelectSection("cooling"); }));
                    added++;
                }
            if (Settings.TrayBattery && trayBatteryLine != null)
            {
                trayMenu.Items.Add(new ToolStripMenuItem(trayBatteryLine, null, (s, e) => { ShowFromTray(); SelectSection("battery"); }));
                added++;
            }
            if (added > 0) trayMenu.Items.Add(new ToolStripSeparator());
        }

        // ---------------------------------------------------------------- graphics card info
        void BuildGpuInfo()
        {
            var list = HwInfo.Gpus();
            if (list.Count == 0)
            {
                page.Controls.Add(new Banner(BannerKind.Info, L.T("Видеокарта не определена"), L.T("Windows не сообщила о видеоадаптерах.")));
                return;
            }
            var first = list[0];
            gpuInfoName = first.Name + (first.VramBytes > 0 ? "  ·  " + first.VramText : "");
            if (hSub.Text != gpuInfoName && section == "gpu") { hSub.Text = gpuInfoName; hSub.Invalidate(); }
            foreach (var g in list)
            {
                string kind = g.Integrated ? L.T("встроенная") : L.T("дискретная");
                var card = Card(g.Name, g.Vendor + "  ·  " + kind);
                var grid = new TileGrid { MinTile = 170, TileH = 92 };
                grid.Controls.Add(new StatTile(L.T("Видеопамять"), g.VramBytes > 0 ? g.VramText : "—", g.Integrated ? L.T("выделено из ОЗУ") : null));
                grid.Controls.Add(new StatTile(L.T("Драйвер"), !string.IsNullOrEmpty(g.VendorDriver) ? g.VendorDriver : (g.DriverVersion ?? "—"),
                    g.DriverDate.HasValue ? L.F("от {0}", g.DriverDate.Value.ToString("d")) : (string.IsNullOrEmpty(g.VendorDriver) ? null : g.DriverVersion)));
                if (g.Width.HasValue && g.Height.HasValue)
                    grid.Controls.Add(new StatTile(L.T("Экран"), g.Width + "×" + g.Height, g.RefreshHz.HasValue ? g.RefreshHz + " " + L.T("Гц") : null));
                if (!string.IsNullOrEmpty(g.PcieText)) grid.Controls.Add(new StatTile(L.T("Шина"), g.PcieText));
                if (g.PowerLimitW.HasValue)
                    grid.Controls.Add(new StatTile(L.T("Лимит мощности"), g.PowerLimitW.Value.ToString("0") + " " + L.T("Вт"),
                        g.PowerMaxW.HasValue ? L.F("максимум {0} Вт", g.PowerMaxW.Value.ToString("0")) : null));
                if (g.MaxCoreMHz.HasValue)
                    grid.Controls.Add(new StatTile(L.T("Макс. частота"), g.MaxCoreMHz + " " + L.T("МГц"), g.MaxMemMHz.HasValue ? L.F("память {0} МГц", g.MaxMemMHz) : null));
                if (!string.IsNullOrEmpty(g.Vbios)) grid.Controls.Add(new StatTile("VBIOS", g.Vbios));
                grid.Controls.Add(new StatTile(L.T("Идентификатор"), string.Format("{0:X4}:{1:X4}", g.VendorId, g.DeviceId), "PCI VEN:DEV"));
                card.Controls.Add(grid);
                page.Controls.Add(card);
            }
        }

        // ---------------------------------------------------------------- cooling
        void BuildCooling()
        {
            BeginPage();
            StartHwPoller();
            page.Controls.Add(PawnCard());

            if (PawnIo.Available && Settings.UsePawnIo) page.Controls.Add(PawnCpuCard());
            var tcard = Card(L.T("Температуры"));
            var tgrid = new TileGrid { MinTile = 170, TileH = 92 };
            tcard.Controls.Add(tgrid);
            var zones = new TextBlock("", TStyle.Small);
            tcard.Controls.Add(zones);
            page.Controls.Add(tcard);

            var fcard = Card(L.T("Вентиляторы"));
            var fstack = new Stack { Pad = 0, Gap = 8 };
            fcard.Controls.Add(fstack);
            page.Controls.Add(fcard);
            var wait = new TextBlock(L.T("Опрашиваю датчики…"), TStyle.Muted);
            fstack.Controls.Add(wait);
            page.Controls.Add(AllSensorsCard("cooling", CoolingSensorRows));
            EndPage();

            var tiles = new Dictionary<string, StatTile>();
            var fanRows = new Dictionary<string, KeyValuePair<TextBlock, ThemedCombo>>();
            bool built = false;
            int boardFanCount = 0;
            var ui = new System.Windows.Forms.Timer { Interval = 2000 };
            Action refresh = null;
            Func<string, string, StatTile> tile = (key, title) =>
            {
                StatTile t;
                if (!tiles.TryGetValue(key, out t)) { t = new StatTile(title, "—"); tiles[key] = t; tgrid.Controls.Add(t); tgrid.PerformLayout(); }
                return t;
            };
            refresh = () =>
            {
                var h = hw;
                if (h == null || h.LastPoll == DateTime.MinValue) return;
                // temperatures
                var cpu = tile("cpu", L.T("Процессор"));
                double? pct = PawnCpuTemp, ppw = PawnCpuPower;
                if (pct.HasValue)
                {
                    cpu.Set(pct.Value.ToString("0") + " °C", L.T("датчик процессора") + (ppw.HasValue ? " · " + ppw.Value.ToString("0.0") + " " + L.T("Вт") : ""));
                    cpu.ValueColor = TempColor(pct, 80, 90);
                }
                else
                {
                    cpu.Set(h.CpuTempC.HasValue ? h.CpuTempC.Value.ToString("0") + " °C" : "—",
                        h.CpuTempC.HasValue ? (h.CpuTempSource == "APU" ? L.T("датчик графики APU") : L.T("датчик ACPI")) : (h.CpuTempNote ?? L.T("датчик недоступен")));
                    cpu.ValueColor = TempColor(h.CpuTempC, 80, 90);
                }
                foreach (var g in h.Gpus)
                {
                    var gt = tile("gpu" + g.Index, h.Gpus.Count > 1 ? L.F("Видеокарта {0}", g.Index + 1) : L.T("Видеокарта"));
                    var sub = new List<string>();
                    if (g.LoadPct.HasValue) sub.Add(L.F("загрузка {0} %", g.LoadPct.Value.ToString("0")));
                    if (g.PowerW.HasValue) sub.Add(g.PowerW.Value.ToString("0") + " " + L.T("Вт"));
                    gt.Set(g.TempC.HasValue ? g.TempC.Value.ToString("0") + " °C" : "—", sub.Count > 0 ? string.Join(" · ", sub.ToArray()) : g.Name);
                    gt.ValueColor = TempColor(g.TempC, 80, 88);
                }
                var ram = tile("ram", L.T("Оперативная память"));
                double? prt = PawnRamTemp;
                if (prt.HasValue)
                {
                    var dl = pawnDimms;
                    ram.Set(prt.Value.ToString("0") + " °C", dl.Count > 1 ? L.F("макс. из {0} модулей", dl.Count) : L.T("датчик модуля памяти"));
                    ram.ValueColor = TempColor(prt, 60, 75);
                }
                else
                {
                    ram.Set(h.RamTempC.HasValue ? h.RamTempC.Value.ToString("0") + " °C" : "—", h.RamTempC.HasValue ? null : (PawnOn ? L.T("у модулей нет датчика") : (h.RamTempNote ?? L.T("датчик недоступен"))));
                    ram.ValueColor = TempColor(h.RamTempC, 60, 75);
                }
                foreach (var bv in pawnBoardValues.Where(x => x.Kind == "temp"))
                {
                    var bt = tile("board|" + bv.Name, L.T("Плата:") + " " + bv.Name);
                    double v = bv.Value;
                    bt.Set(v.ToString("0") + " °C", pawnBoard != null ? pawnBoard.ChipName : null);
                    bt.ValueColor = TempColor(v, 70, 85);
                }
                var zl = new List<string>();
                var cores = pawnCpuValues.Where(x => x.Kind == "temp").ToList();
                if (false && cores.Count > 1) zl.Add(L.T("Процессор:") + " " + string.Join(" · ", cores.Select(c => c.Name + " " + c.Value.ToString("0") + " °C").ToArray()));
                if (PawnOn && pawnDimms.Count > 0) zl.Add(L.T("Память:") + " " + string.Join(" · ", pawnDimms.Select(d => d.Name + " " + d.TempC.ToString("0") + " °C").ToArray()));
                if (h.CpuZones.Count > 0) zl.Add(L.T("Термозоны ACPI:") + " " + string.Join("   ·   ", h.CpuZones.Select(z => z.Name + " " + z.TempC.ToString("0") + " °C").ToArray()));
                zones.Text = string.Join("\n", zl.ToArray());
                zones.Invalidate();

                // fans
                var bfans = PawnOn ? pawnFans : new List<BoardFan>();
                if (built && bfans.Count != boardFanCount) { built = false; fanRows.Clear(); }
                if (!built)
                {
                    boardFanCount = bfans.Count;
                    built = true;
                    fstack.Controls.Clear();
                    foreach (var bf in bfans)
                    {
                        var name = new TextBlock(bf.Name, TStyle.Bold) { Single = true };
                        var val = new TextBlock("", TStyle.Muted) { Single = true };
                        ThemedCombo mode = null;
                        var row = new Row { MinHeight = 38 }.Add(name, 240).Add(val, -1);
                        if (bf.CanControl)
                        {
                            int[] pcts = { 0, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
                            mode = new ThemedCombo(pcts.Select(p => p == 0 ? L.T("автоматически") : p + " %").ToArray()) { SelectedIndex = 0 };
                            int idx = bf.Index;
                            mode.SelectedIndexChanged += (s2, e2) =>
                            {
                                int p = pcts[mode.SelectedIndex]; string why;
                                Telemetry.Count(p == 0 ? "action.board_fan_auto" : "action.board_fan_manual");
                                if (!PawnSetFan(idx, p, out why))
                                    FxMessage.Show(this, L.T("Не удалось изменить скорость вентилятора.") + (string.IsNullOrEmpty(why) ? "" : "\n\n" + why), L.T("Охлаждение"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                            };
                            row.Add(mode, 170);
                        }
                        fstack.Controls.Add(row);
                        fanRows["board|" + bf.Index] = new KeyValuePair<TextBlock, ThemedCombo>(val, mode);
                    }
                    if (h.Fans.Count == 0 && bfans.Count == 0)
                        fstack.Controls.Add(new TextBlock(PawnOn ? L.T("Ни плата, ни видеокарта не сообщают о вентиляторах. На ноутбуках и мини-ПК кулерами обычно управляет встроенный контроллер, который их скорость не показывает.") + (string.IsNullOrEmpty(pawnBoardWhy) ? "" : Environment.NewLine + pawnBoardWhy) : L.T("Windows не сообщает ни об одном вентиляторе. Обороты кулеров платы и корпуса без драйвера недоступны."), TStyle.Muted));
                    foreach (var f in h.Fans)
                    {
                        string key = f.Source + "|" + f.GpuIndex + "|" + f.FanIndex + "|" + f.Name;
                        var name = new TextBlock(f.Name, TStyle.Bold) { Single = true };
                        var val = new TextBlock("", TStyle.Muted) { Single = true };
                        ThemedCombo mode = null;
                        var row = new Row { MinHeight = 38 }.Add(name, 240).Add(val, -1);
                        if (f.CanControl)
                        {
                            int[] pcts = { 0, 30, 40, 50, 60, 70, 80, 90, 100 };
                            mode = new ThemedCombo(pcts.Select(p => p == 0 ? L.T("автоматически") : p + " %").ToArray()) { SelectedIndex = 0 };
                            var ff = f;
                            mode.SelectedIndexChanged += (s, e) =>
                            {
                                var hh = hw; if (hh == null) return;
                                string why; int p = pcts[mode.SelectedIndex];
                                Telemetry.Count(p == 0 ? "action.fan_auto" : "action.fan_manual");
                                bool ok = p == 0 ? hh.ResetGpuFan(ff.GpuIndex, ff.FanIndex, out why) : hh.SetGpuFanPercent(ff.GpuIndex, ff.FanIndex, p, out why);
                                if (!ok) FxMessage.Show(this, L.T("Драйвер не позволил изменить скорость вентилятора.") + (string.IsNullOrEmpty(why) ? "" : "\n\n" + why), L.T("Охлаждение"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                            };
                            row.Add(mode, 170);
                        }
                        fstack.Controls.Add(row);
                        fanRows[key] = new KeyValuePair<TextBlock, ThemedCombo>(val, mode);
                    }
                    if (h.Fans.Any(x => x.CanControl) || bfans.Any(x => x.CanControl))
                        fstack.Controls.Add(new TextBlock(L.T("Ручная скорость действует, пока открыта программа: при выходе вентиляторы возвращаются в автоматический режим. Не ставьте низкую скорость под нагрузкой."), TStyle.Small));
                    Theme.Apply(fstack);
                    fcard.PerformLayout(); page.PerformLayout();
                }
                foreach (var bf in bfans)
                {
                    KeyValuePair<TextBlock, ThemedCombo> r;
                    if (!fanRows.TryGetValue("board|" + bf.Index, out r)) continue;
                    var parts = new List<string> { pawnBoard != null ? pawnBoard.ChipName : L.T("плата") };
                    if (bf.Rpm.HasValue) parts.Add(L.F("{0} об/мин", bf.Rpm.Value.ToString("N0")));
                    if (bf.Percent.HasValue) parts.Add(bf.Percent.Value.ToString("0") + " %");
                    if (bf.Manual) parts.Add(L.T("вручную"));
                    r.Key.Text = string.Join("  ·  ", parts.ToArray());
                    r.Key.Invalidate();
                }
                foreach (var f in h.Fans)
                {
                    string key = f.Source + "|" + f.GpuIndex + "|" + f.FanIndex + "|" + f.Name;
                    KeyValuePair<TextBlock, ThemedCombo> r;
                    if (!fanRows.TryGetValue(key, out r)) continue;
                    var parts = new List<string> { f.Source };
                    if (f.Rpm.HasValue) parts.Add(L.F("{0} об/мин", f.Rpm.Value.ToString("N0")));
                    if (f.Percent.HasValue) parts.Add(f.Percent.Value.ToString("0") + " %");
                    if (!f.Rpm.HasValue && !f.Percent.HasValue) parts.Add(L.T("обороты не сообщаются"));
                    if (!string.IsNullOrEmpty(f.Status)) parts.Add(f.Status);
                    r.Key.Text = string.Join("  ·  ", parts.ToArray());
                    r.Key.Invalidate();
                }
            };
            ui.Tick += (s, e) => { if (section == "cooling") refresh(); };
            ui.Start();
            tcard.Disposed += (s, e) => ui.Dispose();
            refresh();
        }

        static Func<System.Drawing.Color> TempColor(double? t, double warn, double bad)
        {
            return () => !t.HasValue ? Theme.P.Text : t >= bad ? Theme.P.Danger : t >= warn ? Theme.P.Warning : Theme.P.Success;
        }
    

        // ---------------------------------------------------------------- PawnIO driver card
        bool pawnInstalling;

        /// <summary>State of the PawnIO driver and the install button (shown on Cooling, CPU and GPU pages).</summary>
        Control PawnCard()
        {
            string problem = Environment.GetEnvironmentVariable("FH_FAKE_NOPAWN") == "1" ? L.T("Драйвер PawnIO не установлен.") : PawnIo.Problem();
            if (problem == null)
            {
                var ok = Card(L.T("Расширенные датчики"), L.T("Драйвер PawnIO работает: точные температуры процессора, кулеры и датчики платы, температура памяти (где есть датчик)."));
                var tg = new Toggle(L.T("Использовать драйвер PawnIO"), Settings.UsePawnIo);
                tg.CheckedChanged += (s, e) => { Settings.UsePawnIo = tg.Checked; Settings.Save(); ResetPawnSensors(); };
                ok.Controls.Add(tg);
                return ok;
            }
            var card = Card(L.T("Расширенные датчики"));
            card.Controls.Add(new TextBlock(L.T("Без драйвера Windows показывает только датчики видеокарт и ACPI. Для точных температур ядер процессора, оборотов и управления кулерами материнской платы и температуры памяти нужен драйвер PawnIO — открытый драйвер с цифровой подписью, его используют LibreHardwareMonitor и FanControl."), TStyle.Muted));
            card.Controls.Add(new TextBlock(problem, TStyle.Bold));
            if (!PawnIo.Installed || Environment.GetEnvironmentVariable("FH_FAKE_NOPAWN") == "1")
            {
                var status = new TextBlock(L.T("Foxide Helper скачает официальный установщик с GitHub, проверит контрольную сумму и подпись и запустит его. Удалить драйвер можно в «Приложениях» Windows."), TStyle.Small);
                var install = new FlatButton(L.T("Установить драйвер PawnIO"), BtnKind.Primary, "\uE896");
                var more = new FlatButton(L.T("Что такое PawnIO"), BtnKind.Ghost, "\uE946");
                more.Click += (s, e) => OpenUrl("https://pawnio.eu");
                install.Click += async (s, e) =>
                {
                    if (pawnInstalling) return;
                    pawnInstalling = true; install.Enabled = false;
                    Telemetry.Count("action.pawnio_install");
                    string err = await Task.Run(() => PawnIo.Install(m => BeginInvoke(new Action(() => { status.Text = m; status.Invalidate(); }))));
                    pawnInstalling = false; install.Enabled = true;
                    if (err != null) FxMessage.Show(this, err, L.T("Драйвер PawnIO"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ResetPawnSensors();
                    if (Array.IndexOf(SystemSections, section) >= 0) SelectSection(section);
                };
                card.Controls.Add(new Row { MinHeight = 38 }.Add(install, 260).Add(more, 200).Add(new Panel(), -1));
                card.Controls.Add(status);
            }
            return card;
        }
    

        /// <summary>Tray lines with CPU / RAM replaced by PawnIO readings when they exist.</summary>
        List<KeyValuePair<string, string>> PawnTrayLines(List<KeyValuePair<string, string>> lines)
        {
            var res = new List<KeyValuePair<string, string>>(lines ?? new List<KeyValuePair<string, string>>());
            double? c = PawnCpuTemp, r = PawnRamTemp;
            if (c.HasValue)
            {
                res.RemoveAll(x => x.Key == "CPU");
                res.Insert(0, new KeyValuePair<string, string>("CPU", c.Value.ToString("0") + " °C"));
            }
            if (r.HasValue)
            {
                res.RemoveAll(x => x.Key == "RAM");
                res.Add(new KeyValuePair<string, string>("RAM", r.Value.ToString("0") + " °C"));
            }
            var fans = PawnOn ? pawnFans.Where(f => f.Rpm.HasValue && f.Rpm.Value > 0).ToList() : new List<BoardFan>();
            if (fans.Count > 0)
                res.Add(new KeyValuePair<string, string>(L.T("Кулеры"), string.Join(" · ", fans.Take(3).Select(f => f.Rpm.Value.ToString("N0")).ToArray()) + " " + L.T("об/мин")));
            return res;
        }
    }
}
