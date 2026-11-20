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
                    trayBatteryLine = Settings.TrayBattery && BatteryService.HasBattery ? BatteryService.TrayLine() : null;
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
            hw = null;
        }

        /// <summary>Tray menu lines: temperatures and battery (only values that exist).</summary>
        void AddTraySensorItems()
        {
            int added = 0;
            if (Settings.TraySensors && hw != null)
                foreach (var kv in hw.TrayLines())
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
            page.Controls.Add(new Banner(BannerKind.Info, L.T("Что видно без драйвера"),
                L.T("Foxide Helper не устанавливает драйверы. Обороты и управление доступны у вентиляторов видеокарт NVIDIA (через драйвер NVIDIA, если он это разрешает) и частично AMD. Кулеры материнской платы и корпуса, температура памяти и ядер процессора в Windows без драйвера недоступны — для них нужны программы производителя платы.")));

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
            EndPage();

            var tiles = new Dictionary<string, StatTile>();
            var fanRows = new Dictionary<string, KeyValuePair<TextBlock, ThemedCombo>>();
            bool built = false;
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
                cpu.Set(h.CpuTempC.HasValue ? h.CpuTempC.Value.ToString("0") + " °C" : "—",
                    h.CpuTempC.HasValue ? (h.CpuTempSource == "APU" ? L.T("датчик графики APU") : L.T("датчик ACPI")) : (h.CpuTempNote ?? L.T("датчик недоступен")));
                cpu.ValueColor = TempColor(h.CpuTempC, 80, 90);
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
                ram.Set(h.RamTempC.HasValue ? h.RamTempC.Value.ToString("0") + " °C" : "—", h.RamTempC.HasValue ? null : (h.RamTempNote ?? L.T("датчик недоступен")));
                ram.ValueColor = TempColor(h.RamTempC, 60, 75);
                zones.Text = h.CpuZones.Count > 0 ? L.T("Термозоны ACPI:") + " " + string.Join("   ·   ", h.CpuZones.Select(z => z.Name + " " + z.TempC.ToString("0") + " °C").ToArray()) : "";
                zones.Invalidate();

                // fans
                if (!built)
                {
                    built = true;
                    fstack.Controls.Clear();
                    if (h.Fans.Count == 0)
                        fstack.Controls.Add(new TextBlock(L.T("Windows не сообщает ни об одном вентиляторе. Обороты кулеров платы и корпуса без драйвера недоступны."), TStyle.Muted));
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
                                bool ok = p == 0 ? hh.ResetGpuFan(ff.GpuIndex, ff.FanIndex, out why) : hh.SetGpuFanPercent(ff.GpuIndex, ff.FanIndex, p, out why);
                                if (!ok) FxMessage.Show(this, L.T("Драйвер не позволил изменить скорость вентилятора.") + (string.IsNullOrEmpty(why) ? "" : "\n\n" + why), L.T("Охлаждение"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                            };
                            row.Add(mode, 170);
                        }
                        fstack.Controls.Add(row);
                        fanRows[key] = new KeyValuePair<TextBlock, ThemedCombo>(val, mode);
                    }
                    if (h.Fans.Any(x => x.CanControl))
                        fstack.Controls.Add(new TextBlock(L.T("Ручная скорость действует, пока открыта программа: при выходе вентиляторы возвращаются в автоматический режим. Не ставьте низкую скорость под нагрузкой."), TStyle.Small));
                    Theme.Apply(fstack);
                    fcard.PerformLayout(); page.PerformLayout();
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
    }
}
