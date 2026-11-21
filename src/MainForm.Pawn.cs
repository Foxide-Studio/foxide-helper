using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>
    /// Sensors read through the PawnIO driver: CPU (MSR / SMN), motherboard Super I/O or Dell SMM (fans, temperatures,
    /// fan control) and memory module temperature (SMBus). Opened lazily on the sensor worker thread, polled together
    /// with HwSensorsLive; the UI only reads the last snapshots.
    /// </summary>
    public partial class MainForm
    {
        PawnCpu pawnCpu; PawnBoard pawnBoard; PawnMemory pawnMem;
        bool pawnOpened;
        int pawnTick;
        string pawnCpuWhy, pawnBoardWhy, pawnMemWhy;
        volatile List<CpuSensor> pawnCpuValues = new List<CpuSensor>();
        volatile List<BoardFan> pawnFans = new List<BoardFan>();
        volatile List<BoardValue> pawnBoardValues = new List<BoardValue>();
        volatile List<DimmTemp> pawnDimms = new List<DimmTemp>();
        readonly object pawnSync = new object();

        bool PawnOn { get { return Settings.UsePawnIo && pawnOpened; } }

        /// <summary>Called from the sensor worker (StartHwPoller) after hw.Poll().</summary>
        void PollPawn()
        {
            if (!Settings.UsePawnIo || Demo.On) return;
            lock (pawnSync)
            {
                if (!pawnOpened)
                {
                    if (!PawnIo.Available) return;
                    pawnOpened = true;
                    pawnCpu = PawnCpu.Open(out pawnCpuWhy);
                    pawnBoard = PawnBoard.Open(out pawnBoardWhy);
                    pawnMem = PawnMemory.Open(out pawnMemWhy);
                    CpuSensors.ExternalTemp = () => { var c = pawnCpu; return c != null && Settings.UsePawnIo ? c.PackageTemp : null; };
                }
                try { if (pawnCpu != null) pawnCpuValues = pawnCpu.Read() ?? new List<CpuSensor>(); } catch { }
                try { if (pawnBoard != null) { pawnBoard.Update(); pawnFans = new List<BoardFan>(pawnBoard.Fans ?? new List<BoardFan>()); pawnBoardValues = new List<BoardValue>(pawnBoard.Values ?? new List<BoardValue>()); } } catch { }
                try { if (pawnMem != null && pawnTick % 3 == 0) pawnDimms = pawnMem.Read() ?? new List<DimmTemp>(); } catch { }
                pawnTick++;
            }
        }

        /// <summary>Close everything (fans back to automatic) — on exit, when the toggle is switched off or after install.</summary>
        void ResetPawnSensors()
        {
            lock (pawnSync)
            {
                try { if (pawnBoard != null) { pawnBoard.RestoreAll(); pawnBoard.Dispose(); } } catch { }
                try { if (pawnCpu != null) pawnCpu.Dispose(); } catch { }
                try { if (pawnMem != null) pawnMem.Dispose(); } catch { }
                pawnBoard = null; pawnCpu = null; pawnMem = null; pawnOpened = false;
                pawnCpuValues = new List<CpuSensor>(); pawnFans = new List<BoardFan>(); pawnBoardValues = new List<BoardValue>(); pawnDimms = new List<DimmTemp>();
                CpuSensors.ExternalTemp = null;
            }
        }

        double? PawnCpuTemp { get { var c = pawnCpu; return PawnOn && c != null ? c.PackageTemp : null; } }
        double? PawnCpuPower { get { var c = pawnCpu; return PawnOn && c != null ? c.PackagePower : null; } }
        double? PawnRamTemp { get { var d = pawnDimms; return PawnOn && d.Count > 0 ? d.Max(x => x.TempC) : (double?)null; } }

        bool PawnSetFan(int index, int percent, out string why)
        {
            why = null;
            lock (pawnSync)
            {
                if (pawnBoard == null) { why = L.T("Датчики платы недоступны."); return false; }
                return percent <= 0 ? pawnBoard.RestoreFan(index, out why) : pawnBoard.SetFanPercent(index, percent, out why);
            }
        }

        // ---------------------------------------------------------------- visible cards
        /// <summary>Live CPU sensors from the driver: temperatures (package, cores, CCD), power and core clocks.</summary>
        Control PawnCpuCard()
        {
            var card = Card(L.T("Датчики процессора"), L.T("Через драйвер PawnIO: регистры процессора, обновление каждые 2 секунды"));
            var gT = new TileGrid { MinTile = 150, TileH = 86 };
            var gP = new TileGrid { MinTile = 150, TileH = 86 };
            var gC = new TileGrid { MinTile = 150, TileH = 86 };
            var lT = new TextBlock(L.T("Температура"), TStyle.Bold) { Single = true };
            var lP = new TextBlock(L.T("Мощность"), TStyle.Bold) { Single = true };
            var lC = new TextBlock(L.T("Частота ядер"), TStyle.Bold) { Single = true };
            var wait = new TextBlock(L.T("Опрашиваю датчики…"), TStyle.Muted);
            card.Controls.Add(wait);
            card.Controls.Add(lT); card.Controls.Add(gT);
            card.Controls.Add(lP); card.Controls.Add(gP);
            card.Controls.Add(lC); card.Controls.Add(gC);
            lT.Visible = lP.Visible = lC.Visible = false;
            var tiles = new Dictionary<string, StatTile>();
            Action refresh = () =>
            {
                var list = pawnCpuValues;
                if (!PawnOn || list == null || list.Count == 0) return;
                wait.Visible = false;
                foreach (var s in list)
                {
                    string key = s.Kind + "|" + s.Name;
                    StatTile t;
                    if (!tiles.TryGetValue(key, out t))
                    {
                        t = new StatTile(s.Name, "—");
                        tiles[key] = t;
                        (s.Kind == "temp" ? gT : s.Kind == "power" ? gP : gC).Controls.Add(t);
                    }
                    double v = s.Value;
                    if (s.Kind == "temp") { t.Set(v.ToString("0") + " °C", null); t.ValueColor = TempColor(v, 80, 90); }
                    else if (s.Kind == "power") t.Set(v.ToString(v < 10 ? "0.0" : "0") + " " + L.T("Вт"), null);
                    else t.Set(v.ToString("0") + " " + L.T("МГц"), null);
                }
                lT.Visible = gT.Controls.Count > 0; lP.Visible = gP.Controls.Count > 0; lC.Visible = gC.Controls.Count > 0;
                gT.PerformLayout(); gP.PerformLayout(); gC.PerformLayout();
                card.PerformLayout(); if (page != null) page.PerformLayout();
            };
            var timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += (s, e) => { if (Visible) refresh(); };
            timer.Start();
            card.Disposed += (s, e) => timer.Dispose();
            StartHwPoller();
            refresh();
            return card;
        }

        /// <summary>Offer to install the driver (Home and Processor pages) — null when installed, switched off or dismissed.</summary>
        Control PawnOfferCard()
        {
            if (Demo.On || !Settings.UsePawnIo || Settings.PawnOfferOff) return null;
            if (PawnIo.Installed && Environment.GetEnvironmentVariable("FH_FAKE_NOPAWN") != "1") return null;
            var card = PawnCard();
            var off = new FlatButton(L.T("Не предлагать"), BtnKind.Ghost, "");
            off.Click += (s, e) => { Settings.PawnOfferOff = true; Settings.Save(); card.Visible = false; if (page != null) page.PerformLayout(); };
            card.Controls.Add(new Row { MinHeight = 38 }.Add(off, 200).Add(new Panel(), -1));
            return card;
        }

        // ---------------------------------------------------------------- Settings → driver
        static string PawnVersion()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                    return k == null ? null : k.GetValue("DisplayVersion") as string;
            }
            catch { return null; }
        }

        static string PawnUninstallCommand()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                    return k == null ? null : k.GetValue("UninstallString") as string;
            }
            catch { return null; }
        }

        Control SettingsPawnCard()
        {
            var card = Card(L.T("Расширенные датчики (драйвер PawnIO)"));
            string problem = PawnIo.Problem(), ver = PawnVersion();
            card.Controls.Add(new TextBlock(problem == null ? L.T("Драйвер работает.") + (ver != null ? " " + L.F("Версия {0}.", ver) : "") : problem, TStyle.Bold));
            card.Controls.Add(new TextBlock(L.T("Драйвер даёт точные температуры и мощность процессора, обороты и управление кулерами платы и температуру памяти. Его используют и другие программы мониторинга (LibreHardwareMonitor, FanControl)."), TStyle.Small));
            var tg = new Toggle(L.T("Использовать драйвер PawnIO"), Settings.UsePawnIo);
            tg.CheckedChanged += (s, e) => { Settings.UsePawnIo = tg.Checked; Settings.Save(); ResetPawnSensors(); };
            card.Controls.Add(tg);
            var row = new Row { MinHeight = 38 };
            if (!PawnIo.Installed)
            {
                var install = new FlatButton(L.T("Установить драйвер PawnIO"), BtnKind.Primary, "\uE896");
                install.Click += async (s, e) =>
                {
                    if (pawnInstalling) return;
                    pawnInstalling = true; install.Enabled = false;
                    Telemetry.Count("action.pawnio_install");
                    string err = await System.Threading.Tasks.Task.Run(() => PawnIo.Install(null));
                    pawnInstalling = false;
                    if (err != null) FxMessage.Show(this, err, L.T("Драйвер PawnIO"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ResetPawnSensors();
                    if (section == "settings") BuildSettings();
                };
                row.Add(install, 260);
            }
            else
            {
                var remove = new FlatButton(L.T("Удалить драйвер PawnIO"), BtnKind.Danger, "\uE74D");
                remove.Click += async (s, e) =>
                {
                    if (!FxMessage.Confirm(this, L.T("Удалить драйвер PawnIO"),
                        L.T("Драйвер будет удалён из системы. Точные температуры процессора и памяти и кулеры платы станут недоступны — и в других программах, которые им пользуются (LibreHardwareMonitor, FanControl). Продолжить?"),
                        L.T("Удалить"), true)) return;
                    string cmd = PawnUninstallCommand();
                    if (string.IsNullOrEmpty(cmd)) { FxMessage.Show(this, L.T("Не найдена программа удаления PawnIO. Удалите драйвер в «Приложениях» Windows."), L.T("Драйвер PawnIO"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    remove.Enabled = false;
                    Telemetry.Count("action.pawnio_uninstall");
                    ResetPawnSensors();                       // fans back to auto, handles closed before the driver goes away
                    string err = await System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            string exe = cmd, args = "";
                            if (cmd.StartsWith("\"")) { int q = cmd.IndexOf('"', 1); exe = cmd.Substring(1, q - 1); args = cmd.Substring(q + 1).Trim(); }
                            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true }))
                                p.WaitForExit();
                            return PawnIo.Installed ? L.T("Драйвер не удалён — возможно, удаление отменено или нужна перезагрузка.") : null;
                        }
                        catch (Exception ex) { return ex.Message; }
                    });
                    if (err != null) FxMessage.Show(this, err, L.T("Драйвер PawnIO"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    if (section == "settings") BuildSettings();
                };
                row.Add(remove, 260);
            }
            var more = new FlatButton(L.T("Что такое PawnIO"), BtnKind.Ghost, "\uE946");
            more.Click += (s, e) => OpenUrl("https://pawnio.eu");
            row.Add(more, 200).Add(new Panel(), -1);
            card.Controls.Add(row);
            return card;
        }
    }
}
