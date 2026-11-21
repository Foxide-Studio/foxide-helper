using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FoxideDisk
{
    /// <summary>"Home": every module as a widget with its current state; a click opens the module.</summary>
    public partial class MainForm
    {
        string cpuNameCache;

        static string CpuName()
        {
            if (Demo.On) return Demo.Cpu().Info.Name;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    var v = k == null ? null : k.GetValue("ProcessorNameString") as string;
                    if (!string.IsNullOrEmpty(v)) return v.Trim();
                }
            }
            catch { }
            return L.T("Процессор");
        }

        StatTile HomeTile(TileGrid grid, string title, string glyph, string target)
        {
            var t = new StatTile(title, "—", null, glyph) { Cursor = Cursors.Hand };
            t.Click += (s, e) => { Telemetry.Count("home." + target); SelectSection(target); };
            grid.Controls.Add(t);
            return t;
        }

        void BuildHome()
        {
            BeginPage();
            StartHwPoller();
            if (cpuNameCache == null) cpuNameCache = CpuName();
            if (ramInfo == null && !ramLoadingHome)
            {
                ramLoadingHome = true;
                Task.Run(() => RamService.Load()).ContinueWith(t => BeginInvoke(new Action(() =>
                {
                    if (ramInfo == null) ramInfo = t.IsFaulted ? new RamInfo() : t.Result;
                    if (section == "home" && !busy) BuildHome();
                })));
            }

            var offer = PawnOfferCard(); if (offer != null) page.Controls.Add(offer);
            var sys = Card(L.T("Компьютер"));
            var g1 = new TileGrid { MinTile = 230, TileH = 104 };
            var tDisks = HomeTile(g1, L.T("Диски"), "", "all");
            var tHot = HomeTile(g1, L.T("Самый горячий диск"), "", "all");
            var tRam = HomeTile(g1, L.T("Оперативная память"), "", "ram");
            var tCpu = HomeTile(g1, L.T("Процессор"), "", "cpu");
            var tGpu = HomeTile(g1, L.T("Видеокарта"), "", "gpu");
            StatTile tBat = BatteryService.HasBattery ? HomeTile(g1, L.T("Аккумулятор"), "", "battery") : null;
            var tFan = HomeTile(g1, L.T("Охлаждение"), "", "cooling");
            sys.Controls.Add(g1);
            page.Controls.Add(sys);
            page.Controls.Add(HomeSensorsCard());

            var app = Card(L.T("Программа"));
            var g2 = new TileGrid { MinTile = 230, TileH = 104 };
            var tBg = HomeTile(g2, L.T("Фоновый режим"), "", "settings");
            var tUpd = HomeTile(g2, L.T("Обновления"), "", "settings");
            var tStat = HomeTile(g2, L.T("Статистика"), "", "settings");
            app.Controls.Add(g2);
            page.Controls.Add(app);
            EndPage();

            Action refresh = () =>
            {
                // disks
                var ds = disks.Where(x => IsShown(x)).ToList();
                if (ds.Count == 0) tDisks.Set(loading ? L.T("поиск…") : "0", null);
                else
                {
                    int bad = ds.Count(x => x.Health == HealthState.Bad), warn = ds.Count(x => x.Health == HealthState.Warning);
                    tDisks.Set(L.F("{0} шт.", ds.Count), bad > 0 ? L.F("неисправны: {0}", bad) : warn > 0 ? L.F("требуют внимания: {0}", warn) : L.T("все исправны"));
                    tDisks.ValueColor = () => bad > 0 ? Theme.P.Danger : warn > 0 ? Theme.P.Warning : Theme.P.Success;
                    var hot = ds.Where(x => x.TempC.HasValue).OrderByDescending(x => x.TempC.Value).FirstOrDefault();
                    if (hot != null)
                    {
                        long tc = hot.TempC.Value; int lim = hot.IsRotating ? Settings.AlertHdd : Settings.AlertSsd;
                        tHot.Set(tc + " °C", hot.Model);
                        tHot.ValueColor = () => tc >= lim ? Theme.P.Danger : tc >= lim - 8 ? Theme.P.Warning : Theme.P.Success;
                    }
                    else tHot.Set("—", L.T("датчики недоступны"));
                }
                // memory
                var st = RamService.Status();
                tRam.Set(st.Load + " %", L.F("занято {0} из {1}", DiskService.FormatSize(st.Total - st.Available), DiskService.FormatSize(st.Total)));
                int load = st.Load;
                tRam.ValueColor = () => load >= 90 ? Theme.P.Danger : load >= 75 ? Theme.P.Warning : Theme.P.Text;
                // sensors
                var h = hw;
                double? ct = PawnCpuTemp ?? (h != null ? h.CpuTempC : null);
                tCpu.Set(ct.HasValue ? ct.Value.ToString("0") + " °C" : "—", cpuNameCache);
                tCpu.ValueColor = TempColor(ct, 80, 90);
                var gl = h != null ? h.Gpus.FirstOrDefault() : null;
                var ga = HwInfo.Gpus().FirstOrDefault();
                double? gt = gl != null ? gl.TempC : null;
                tGpu.Set(gt.HasValue ? gt.Value.ToString("0") + " °C" : (ga != null && ga.VramBytes > 0 ? ga.VramText : "—"), ga != null ? ga.Name : L.T("не определена"));
                tGpu.ValueColor = TempColor(gt, 80, 88);
                if (tBat != null)
                {
                    var b = hwBattery;
                    if (b != null)
                    {
                        tBat.Set(b.Percent.HasValue ? b.Percent.Value.ToString("0") + " %" : "—",
                            (b.HealthPercent.HasValue ? L.F("здоровье {0} %", b.HealthPercent.Value.ToString("0")) + " · " : "") + b.StatusText);
                        tBat.ValueColor = () => b.Health == BatteryHealth.Poor ? Theme.P.Danger : b.Health == BatteryHealth.Fair ? Theme.P.Warning : Theme.P.Text;
                    }
                }
                if (h != null && h.LastPoll != DateTime.MinValue)
                {
                    var withSpeed = h.Fans.Where(f => f.Rpm.HasValue || f.Percent.HasValue).ToList();
                    var bf = PawnOn ? pawnFans.Where(f => f.Rpm.HasValue && f.Rpm.Value > 0).ToList() : new List<BoardFan>();
                    foreach (var b in bf) withSpeed.Insert(0, new FanInfo { Name = b.Name, Rpm = b.Rpm });
                    int total = h.Fans.Count + (PawnOn ? pawnFans.Count : 0);
                    tFan.Set(total == 0 ? "—" : L.F("{0} шт.", total),
                        withSpeed.Count > 0 ? string.Join(" · ", withSpeed.Take(2).Select(f => f.Rpm.HasValue ? L.F("{0} об/мин", f.Rpm.Value.ToString("N0")) : f.Percent.Value.ToString("0") + " %").ToArray())
                                            : L.T("Windows не сообщает о вентиляторах"));
                }
                else tFan.Set("…", L.T("Опрашиваю датчики…"));
                // program
                tBg.Set(Settings.TempAlerts ? L.T("включён") : L.T("выключен"), Settings.TempAlerts ? L.T("уведомления о перегреве дисков") : L.T("уведомления выключены"));
                tUpd.Set(Updater.CurrentText, updLast ?? (Settings.CheckUpdates ? L.T("проверка при запуске") : L.T("проверка выключена")));
                tStat.Set(Settings.SendStats ? L.T("включена") : L.T("выключена"), L.T("анонимная статистика и отчёты о сбоях"));
            };
            refresh();
            var timer = new Timer { Interval = 3000 };
            timer.Tick += (s, e) => { if (section == "home" && Visible) refresh(); };
            timer.Start();
            sys.Disposed += (s, e) => timer.Dispose();
        }

        bool ramLoadingHome;
    }
}
