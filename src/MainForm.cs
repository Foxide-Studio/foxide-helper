using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FoxideDisk
{
    public partial class MainForm : Form
    {
        // layout
        RolePanel sidebar, listPanel, content, headerPanel;
        Stack navStack, navBottom, listTop, listStack, header, page;
        TextBlock listTitle, hTitle, hSub;
        Row tabsRow;
        FlatButton btnRefresh, btnCollapse;
        TextBlock sectionsLabel, adminLabel, systemLabel;
        static readonly string[] SystemSections = { "home", "settings", "ram", "cpu", "gpu", "battery", "cooling" };
        bool DiskSection { get { return Array.IndexOf(SystemSections, section) < 0; } }
        readonly ToolTip tip = new ToolTip();

        // live temperature
        readonly Dictionary<string, List<TempPoint>> tempHistory = new Dictionary<string, List<TempPoint>>();
        readonly Dictionary<string, int> tempFails = new Dictionary<string, int>();
        readonly System.Windows.Forms.Timer tempTimer = new System.Windows.Forms.Timer();
        bool tempReading;
        TempChart liveChart;
        TextBlock liveStats;
        string liveSig;
        readonly Dictionary<string, FlatButton> nav = new Dictionary<string, FlatButton>();
        readonly List<FlatButton> tabs = new List<FlatButton>();

        // state
        List<DiskInfo> disks = new List<DiskInfo>();
        DiskInfo sel;
        string section = "all";
        int tab;
        bool busy, loading;
        string loadError;
        readonly Dictionary<string, SpeedResult> speedResults = new Dictionary<string, SpeedResult>();
        readonly Dictionary<string, string> wipeLogs = new Dictionary<string, string>();
        CancellationTokenSource speedCts;
        readonly System.Windows.Forms.Timer devTimer = new System.Windows.Forms.Timer { Interval = 1500 };

        public event EventHandler DisksLoaded;

        static readonly string[] SectionIds = { "all", "ssd", "hdd", "usb", "other" };
        static readonly string[] SectionNames = { L.T("Все диски"), "SSD", "HDD", L.T("USB и карты памяти"), L.T("Другие") };
        static readonly string[] SectionGlyphs = { "", "", "", "", "" };
        static readonly string[] TabIds = { "overview", "health", "speed", "wipe" };
        static readonly string[] TabNames = { L.T("Обзор"), L.T("Здоровье"), L.T("Скорость"), L.T("Очистка") };
        static readonly string[] TabGlyphs = { "", "", "", "" };

        public MainForm()
        {
            Text = "Foxide Helper";
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body;
            Size = new Size(Theme.Dp(1200), Theme.Dp(780));
            MinimumSize = new Size(Theme.Dp(980), Theme.Dp(620));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildShell();
            Theme.Apply(this);
            SystemEvents.UserPreferenceChanged += OnUserPrefChanged;
            devTimer.Tick += (s, e) => { devTimer.Stop(); if (!busy) RefreshDisks(); };
            tempTimer.Interval = Settings.TempIntervalSec * 1000;
            tempTimer.Tick += (s, e) => PollTemp();
        }

        // ---------------------------------------------------------------- shell
        void BuildShell()
        {
            // content (fill) first, then docked-left panels
            content = new RolePanel(() => Theme.P.Back) { Dock = DockStyle.Fill };
            page = new Stack { Dock = DockStyle.Fill, Pad = 24, Gap = 14 };
            page.AutoScrollEnabled = true;
            headerPanel = new RolePanel(() => Theme.P.Back) { Dock = DockStyle.Top, BorderBottom = true };
            header = new Stack { Dock = DockStyle.Fill, Pad = 0, Gap = 2 };
            hTitle = new TextBlock("", TStyle.Title) { Single = true };
            hSub = new TextBlock("", TStyle.Subtitle) { Single = true };
            tabsRow = new Row { Gap = 4, MinHeight = 44 };
            for (int i = 0; i < TabNames.Length; i++)
            {
                var b = new FlatButton(TabNames[i], BtnKind.Tab, TabGlyphs[i]) { Height = Theme.Dp(44) };
                int idx = i;
                b.Click += (s, e) => SelectTab(idx);
                tabs.Add(b);
                tabsRow.Add(b, (int)Math.Ceiling(b.PreferredWidth() / Theme.Scale));
            }
            tabsRow.Add(new Panel(), -1);
            header.Controls.Add(new Panel { Height = Theme.Dp(14) });
            header.Controls.Add(hTitle);
            header.Controls.Add(hSub);
            header.Controls.Add(new Panel { Height = Theme.Dp(6) });
            header.Controls.Add(tabsRow);
            headerPanel.Padding = new Padding(Theme.Dp(24), 0, Theme.Dp(24), 0);
            headerPanel.Controls.Add(header);
            content.Controls.Add(page);
            content.Controls.Add(headerPanel);
            Controls.Add(content);

            // disk list
            listPanel = new RolePanel(() => Theme.P.ListBack) { Dock = DockStyle.Left, Width = Theme.Dp(330), BorderRight = true };
            listStack = new Stack { Dock = DockStyle.Fill, Pad = 12, Gap = 8, Bg = () => Theme.P.ListBack };
            listStack.AutoScrollEnabled = true;
            listTop = new Stack { Dock = DockStyle.Top, Pad = 14, Height = Theme.Dp(68), Bg = () => Theme.P.ListBack };
            listTitle = new TextBlock(L.T("Все диски"), TStyle.Section) { Single = true };
            btnRefresh = new FlatButton("", BtnKind.Ghost, "");
            btnRefresh.Click += (s, e) => RefreshDisks();
            tip.SetToolTip(btnRefresh, L.T("Обновить список (F5)"));
            listTop.Controls.Add(new Row { MinHeight = 38 }.Add(listTitle, -1).Add(btnRefresh, 40));
            listPanel.Controls.Add(listStack);
            listPanel.Controls.Add(listTop);
            Controls.Add(listPanel);

            // sidebar
            sidebar = new RolePanel(() => Theme.P.Sidebar) { Dock = DockStyle.Left, Width = Theme.Dp(232), BorderRight = true };
            navStack = new Stack { Dock = DockStyle.Fill, Pad = 10, Gap = 2, Bg = () => Theme.P.Sidebar };
            navStack.Controls.Add(new LogoHeader());
            btnCollapse = new FlatButton(L.T("Свернуть панель"), BtnKind.Nav, "");
            btnCollapse.Click += (s, e) => SetSidebarCollapsed(!Settings.SidebarCollapsed, true);
            navStack.Controls.Add(btnCollapse);
            navStack.Controls.Add(new Panel { Height = Theme.Dp(6) });
            AddSysNav("home", L.T("Главная"), "\uE80F");
            navStack.Controls.Add(new Panel { Height = Theme.Dp(10) });
            sectionsLabel = new TextBlock("  " + L.T("РАЗДЕЛЫ"), TStyle.Small) { Single = true };
            navStack.Controls.Add(sectionsLabel);
            for (int i = 0; i < SectionIds.Length; i++)
            {
                var b = new FlatButton(SectionNames[i], BtnKind.Nav, SectionGlyphs[i]);
                string id = SectionIds[i];
                b.Click += (s, e) => SelectSection(id);
                nav[id] = b;
                navStack.Controls.Add(b);
            }
            navStack.Controls.Add(new Panel { Height = Theme.Dp(10) });
            systemLabel = new TextBlock("  " + L.T("СИСТЕМА"), TStyle.Small) { Single = true };
            navStack.Controls.Add(systemLabel);
            var bRam = new FlatButton(L.T("Оперативная память"), BtnKind.Nav, "");
            bRam.Click += (s, e) => SelectSection("ram");
            nav["ram"] = bRam;
            navStack.Controls.Add(bRam);
            AddSysNav("cpu", L.T("Процессор"), "\uE950");
            AddSysNav("gpu", L.T("Видеокарта"), "\uE7F4");
            if (BatteryService.HasBattery) AddSysNav("battery", L.T("Аккумулятор"), "\uEBA7");
            AddSysNav("cooling", L.T("Охлаждение"), "\uE9CA");
            navBottom = new Stack { Dock = DockStyle.Bottom, Pad = 10, Gap = 6, Bg = () => Theme.P.Sidebar };
            bPatreon = new FlatButton(L.T("Поддержать на Patreon"), BtnKind.Nav, "");
            bPatreon.Click += (s, e) => { Telemetry.Count("action.patreon_sidebar"); OpenUrl(PatreonUrl); };
            navBottom.Controls.Add(bPatreon);
            var bs = new FlatButton(L.T("Настройки"), BtnKind.Nav, "");
            bs.Click += (s, e) => SelectSection("settings");
            nav["settings"] = bs;
            navBottom.Controls.Add(bs);
            adminLabel = new TextBlock("  " + (Program.IsAdmin ? L.T("Права администратора: да") : L.T("Без прав администратора — очистка недоступна")), TStyle.Small);
            navBottom.Controls.Add(adminLabel);
            sidebar.Controls.Add(navStack);
            sidebar.Controls.Add(navBottom);
            Controls.Add(sidebar);
            SetSidebarCollapsed(Settings.SidebarCollapsed, false);

            UpdateHeaderHeight();
            content.Resize += (s, e) => UpdateHeaderHeight();
        }

        FlatButton bPatreon;

        void AddSysNav(string id, string text, string glyph)
        {
            var b = new FlatButton(text, BtnKind.Nav, glyph);
            b.Click += (s, e) => SelectSection(id);
            nav[id] = b;
            navStack.Controls.Add(b);
        }
        public const string PatreonUrl = "https://www.patreon.com/cw/FoxideStudio";
        public const string RepoUrl = "https://github.com/" + Updater.Repo;

        void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { FxMessage.Show(this, L.T("Не удалось открыть ссылку:") + " " + url + "\n\n" + ex.Message, "Foxide Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void SetSidebarCollapsed(bool collapsed, bool save)
        {
            Settings.SidebarCollapsed = collapsed;
            if (save) Settings.Save();
            SuspendLayout();
            sidebar.Width = Theme.Dp(collapsed ? 64 : 232);
            sectionsLabel.Visible = !collapsed;
            systemLabel.Visible = !collapsed;
            adminLabel.Visible = !collapsed;
            btnCollapse.Glyph = collapsed ? "" : "";
            btnCollapse.Text = collapsed ? L.T("Развернуть панель") : L.T("Свернуть панель");
            foreach (var b in nav.Values.Concat(new[] { btnCollapse, bPatreon }))
            {
                b.IconOnly = collapsed;
                tip.SetToolTip(b, collapsed ? b.Text : null);
            }
            navBottom.Height = navBottom.HeightFor(sidebar.Width);
            ResumeLayout(true);
            navStack.PerformLayout();
            navBottom.PerformLayout();
            UpdateHeaderHeight();
            sidebar.Invalidate(true);
        }

        void UpdateHeaderHeight()
        {
            int w = Math.Max(100, content.ClientSize.Width - Theme.Dp(48));
            headerPanel.Height = header.HeightFor(w) + 1;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        // ---------------------------------------------------------------- start / tray / background
        public bool StartHidden;
        bool started, exiting, trayHintShown, alertPolling;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        ToolStripMenuItem trayAlertsItem;
        readonly System.Windows.Forms.Timer alertTimer = new System.Windows.Forms.Timer();
        readonly Dictionary<string, DateTime> alertSent = new Dictionary<string, DateTime>();
        string lastAlertSig;

        protected override void SetVisibleCore(bool value)
        {
            if (StartHidden && !IsHandleCreated)
            {
                CreateHandle();
                value = false;
                BeginInvoke(new Action(StartUp));
            }
            base.SetVisibleCore(value);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartUp();
        }

        void StartUp()
        {
            if (started) return;
            started = true;
            SelectSection(Demo.On && Environment.GetEnvironmentVariable("FH_SHOT_START") == "disks" ? "all" : "home", false);
            InitTray();
            RefreshDisks();
            if (Settings.LiveTemp && !StartHidden) tempTimer.Start();
            alertTimer.Interval = Settings.AlertIntervalSec * 1000;
            alertTimer.Tick += (s, e) => PollAlerts();
            alertTimer.Start();
            DisksLoaded += FirstAlertPoll;
            if (Program.IsAdmin) Task.Run(() => Autostart.MigrateOldTask());
            if (Settings.CheckUpdates)
            {
                var t = new System.Windows.Forms.Timer { Interval = 8000 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); CheckForUpdates(false); };
                t.Start();
            }
            Telemetry.Context = () =>
            {
                var ds = disks;   // the list object is replaced, not modified, on refresh
                var gpus = HwInfo.Gpus();
                return new Dictionary<string, object>
                {
                    { "disks_ssd", ds.Count(x => x.Kind == DiskKind.SSD) }, { "disks_nvme", ds.Count(x => x.Kind == DiskKind.SSD && x.BusType == 17) },
                    { "disks_hdd", ds.Count(x => x.Kind == DiskKind.HDD) }, { "disks_usb", ds.Count(x => x.Kind == DiskKind.Removable) },
                    { "gpu", gpus.Count > 0 ? gpus[0].Vendor.ToLowerInvariant() : "none" },
                    { "gpu_count", gpus.Count }, { "battery", BatteryService.HasBattery },
                    { "ram_gb", ramInfo != null ? (int)Math.Round(ramInfo.Installed / 1073741824.0) : 0 },
                };
            };
            Telemetry.Start();
            if (!Demo.On) StartHwPoller();
            if (Demo.On && Environment.GetEnvironmentVariable("FH_SHOT_BANNER") == "1")
                ShowUpdateBanner(new UpdateInfo { Version = "1.2.0", Notes = "" });
        }

        // ---------------------------------------------------------------- updates (GitHub Releases)
        bool updBusy;
        TextBlock updStatus;
        string updLast;

        void SetUpdStatus(string text)
        {
            updLast = text;
            if (updStatus == null || updStatus.IsDisposed) return;
            updStatus.Text = text;
            if (updStatus.Parent != null) updStatus.Parent.PerformLayout();
            updStatus.Invalidate();
        }

        /// <summary>manual = the button in Settings: report "up to date" and errors, ignore "skip this version".</summary>
        async void CheckForUpdates(bool manual)
        {
            if (updBusy) return;
            updBusy = true;
            SetUpdStatus(L.T("Проверяю обновления…"));
            UpdateInfo u = null; string err = null;
            try { u = await Task.Run(() => Updater.Check()); }
            catch (Exception ex) { err = ex.Message; }
            updBusy = false;
            SetUpdStatus(err != null ? L.T("Не удалось проверить обновления:") + " " + err
                : u == null ? L.F("Установлена последняя версия ({0}).", Updater.CurrentText)
                : L.F("Доступна версия {0}.", u.Version));
            if (err != null) { if (manual) FxMessage.Show(this, L.T("Не удалось проверить обновления.") + "\n\n" + err, L.T("Обновления"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (u == null) { if (manual) FxMessage.Show(this, L.F("У вас последняя версия Foxide Helper ({0}).", Updater.CurrentText), L.T("Обновления"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!manual)
            {
                if (u.Version == Settings.SkipVersion) return;
                ShowUpdateBanner(u);
                if (!Visible && tray != null)
                {
                    trayBalloonUpdate = true;
                    tray.ShowBalloonTip(10000, L.T("Доступно обновление Foxide Helper"), L.F("Версия {0}. Нажмите, чтобы открыть программу и обновить.", u.Version), ToolTipIcon.Info);
                }
                return;
            }
            HideUpdateBanner();
            OfferUpdate(u);
        }

        // ---- полоса «Доступна новая версия» над шапкой ----
        RolePanel updBanner;
        bool trayBalloonUpdate;

        void ShowUpdateBanner(UpdateInfo u)
        {
            HideUpdateBanner();
            updBanner = new RolePanel(() => Theme.Mix(Theme.P.Back, Theme.P.Accent, .16f)) { Dock = DockStyle.Top, Height = Theme.Dp(58), BorderBottom = true };
            updBanner.Padding = new Padding(Theme.Dp(24), Theme.Dp(10), Theme.Dp(24), Theme.Dp(10));
            var text = new TextBlock(L.F("Доступна новая версия Foxide Helper {0} (у вас {1}).", u.Version, Updater.CurrentText), TStyle.Bold) { Single = true };
            var bUpd = new FlatButton(L.T("Обновить"), BtnKind.Primary, "");
            bUpd.Click += (s, e) => { HideUpdateBanner(); OfferUpdate(u); };
            var bLater = new FlatButton(L.T("Позже"), BtnKind.Secondary, "");
            bLater.Click += (s, e) => HideUpdateBanner();
            var bOff = new FlatButton(L.T("Не проверять обновления"), BtnKind.Ghost, "");
            bOff.Click += (s, e) =>
            {
                Settings.CheckUpdates = false; Settings.Save();
                text.Text = L.T("Проверка обновлений отключена. Включить снова: «Настройки» → «Обновления».");
                text.Invalidate();
                bUpd.Visible = bLater.Visible = bOff.Visible = false;
                var t = new System.Windows.Forms.Timer { Interval = 6000 };
                t.Tick += (s2, e2) => { t.Stop(); t.Dispose(); HideUpdateBanner(); };
                t.Start();
                if (section == "settings") BuildSettings();
            };
            var row = new Row { Dock = DockStyle.Fill, MinHeight = 38 };
            row.Add(text, -1).Add(bUpd, 130).Add(bLater, 110).Add(bOff, 220);
            updBanner.Controls.Add(row);
            content.Controls.Add(updBanner);
            Theme.Apply(updBanner);
            content.PerformLayout();
        }

        void HideUpdateBanner()
        {
            if (updBanner == null) return;
            var b = updBanner; updBanner = null;
            content.Controls.Remove(b);
            b.Dispose();
            content.PerformLayout();
        }

        async void OfferUpdate(UpdateInfo u)
        {
            string text = L.F("Доступна новая версия Foxide Helper: {0} (у вас {1}).", u.Version, Updater.CurrentText) +
                (u.Notes.Length > 0 ? "\n\n" + L.T("Что нового:") + "\n" + (u.Notes.Length > 1500 ? u.Notes.Substring(0, 1500) + "…" : u.Notes) : "") +
                "\n\n" + L.T("Скачать и установить сейчас? Программа перезапустится.") +
                "\n\n" + L.T("Если отказаться, напоминания об этой версии не будет — её можно установить в «Настройках» → «Обновления».");
            if (!FxMessage.Confirm(this, L.T("Обновление"), text, L.T("Обновить"), false))
            {
                Settings.SkipVersion = u.Version; Settings.Save();
                return;
            }
            if (busy)
            {
                FxMessage.Show(this, L.T("Дождитесь завершения текущей операции и повторите обновление в «Настройках» → «Обновления»."), L.T("Обновления"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (updBusy) return;
            updBusy = true;
            string file = null, err = null;
            try
            {
                file = await Task.Run(() => Updater.Download(u, p => BeginInvoke(new Action(() => SetUpdStatus(L.F("Скачиваю версию {0}… {1}%", u.Version, p)))),
                    System.Threading.CancellationToken.None));
            }
            catch (Exception ex) { err = ex.Message; }
            updBusy = false;
            if (err == null)
            {
                Settings.SkipVersion = ""; Settings.Save();
                Telemetry.Count("action.update_install");
                try { Updater.Install(file); }
                catch (Exception ex) { err = L.T("Не удалось заменить файл программы:") + " " + ex.Message + "\n\n" + L.T("Скачайте новую версию вручную со страницы релизов на GitHub."); }
            }
            if (err != null)
            {
                SetUpdStatus(L.T("Обновление не установлено."));
                FxMessage.Show(this, err, L.T("Обновления"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                Updater.Cleanup();
                return;
            }
            ExitApp();
        }

        void FirstAlertPoll(object sender, EventArgs e)
        {
            DisksLoaded -= FirstAlertPoll;
            PollAlerts();
        }

        bool TrayWanted { get { return Settings.MinimizeToTray || Settings.TempAlerts || StartHidden; } }

        class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.P.Surface; } }
            public override Color ImageMarginGradientBegin { get { return Theme.P.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.P.Surface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.P.Surface; } }
            public override Color MenuBorder { get { return Theme.P.Border; } }
            public override Color MenuItemBorder { get { return Theme.P.Accent; } }
            public override Color MenuItemSelected { get { return Theme.Mix(Theme.P.Surface, Theme.P.Accent, .2f); } }
            public override Color MenuItemSelectedGradientBegin { get { return MenuItemSelected; } }
            public override Color MenuItemSelectedGradientEnd { get { return MenuItemSelected; } }
            public override Color SeparatorDark { get { return Theme.P.Border; } }
            public override Color SeparatorLight { get { return Theme.P.Surface; } }
        }

        void InitTray()
        {
            if (tray != null) { tray.Visible = TrayWanted; return; }
            trayMenu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new MenuColors()), Font = Theme.Body };
            trayMenu.Opening += (s, e) => BuildTrayMenu();
            tray = new NotifyIcon { Text = "Foxide Helper", ContextMenuStrip = trayMenu };
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            tray.BalloonTipClicked += (s, e) =>
            {
                ShowFromTray();
                if (trayBalloonUpdate) { trayBalloonUpdate = false; return; }
                var d = disks.FirstOrDefault(x => x.Signature == lastAlertSig);
                if (d != null)
                {
                    if (!DiskSection || !InSection(d, section)) SelectSection("all");
                    BuildList(); ShowDisk(d); SelectTab(1);
                }
            };
            tray.Visible = TrayWanted;
        }

        void BuildTrayMenu()
        {
            trayMenu.Items.Clear();
            var open = new ToolStripMenuItem(L.T("Открыть Foxide Helper"), null, (s, e) => ShowFromTray()) { Font = Theme.Bold };
            trayMenu.Items.Add(open);
            trayMenu.Items.Add(new ToolStripSeparator());
            AddTraySensorItems();
            foreach (var d in disks.Where(x => IsShown(x)))
            {
                string t = d.TempC.HasValue ? d.TempC + " °C" : "—";
                string h = d.HealthPercent.HasValue ? "  ·  " + d.HealthPercent + " %" : "";
                var dd = d;
                trayMenu.Items.Add(new ToolStripMenuItem(t + h + "   " + d.Model, null, (s, e) =>
                {
                    ShowFromTray();
                    if (!DiskSection) SelectSection("all");
                    BuildList(); ShowDisk(dd);
                }));
            }
            trayMenu.Items.Add(new ToolStripSeparator());
            trayAlertsItem = new ToolStripMenuItem(L.T("Уведомлять о перегреве"), null, (s, e) =>
            {
                Settings.TempAlerts = !Settings.TempAlerts; Settings.Save();
                if (section == "settings" && Visible) BuildSettings();
            }) { Checked = Settings.TempAlerts };
            trayMenu.Items.Add(trayAlertsItem);
            trayMenu.Items.Add(new ToolStripMenuItem(L.T("Выход"), null, (s, e) => ExitApp()));
            foreach (ToolStripItem i in trayMenu.Items) i.ForeColor = Theme.P.Text;
        }

        public void ShowFromTray()
        {
            StartHidden = false;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            if (Settings.LiveTemp) tempTimer.Start();
            if (!started) StartUp();
        }

        void HideToTray()
        {
            Hide();
            tempTimer.Stop();
            if (tray != null) tray.Visible = true;
            if (!trayHintShown && tray != null)
            {
                trayHintShown = true;
                trayBalloonUpdate = false;
                tray.ShowBalloonTip(5000, L.T("Foxide Helper работает в фоне"),
                    Settings.TempAlerts ? L.T("Температура дисков проверяется, при перегреве появится уведомление. Выход — через значок в трее.") : L.T("Выход — через значок в трее."),
                    ToolTipIcon.Info);
            }
        }

        public void ExitApp()
        {
            exiting = true;
            Close();
        }

        void PollAlerts()
        {
            if (Demo.On || !Settings.TempAlerts || alertPolling || busy || loading) return;
            alertTimer.Interval = Settings.AlertIntervalSec * 1000;
            var list = disks.Where(d => d.Kind != DiskKind.Virtual && (Settings.AlertPollHdd || !d.IsRotating) && (d.TempC.HasValue || d.Smart != null)).ToList();
            if (list.Count == 0) return;
            alertPolling = true;
            Task.Run(() => list.Select(d => new KeyValuePair<DiskInfo, long?>(d, SmartReader.ReadTemp(d))).ToList()).ContinueWith(t =>
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        alertPolling = false;
                        if (t.IsFaulted) return;
                        long max = 0;
                        foreach (var kv in t.Result)
                        {
                            if (!kv.Value.HasValue) continue;
                            var d = kv.Key; long v = kv.Value.Value;
                            max = Math.Max(max, v);
                            var h = History(d.Signature);
                            if (h.Count == 0 || (DateTime.Now - h[h.Count - 1].T).TotalSeconds >= 1)
                                h.Add(new TempPoint { T = DateTime.Now, V = v });
                            h.RemoveAll(p => p.T < DateTime.Now.AddMinutes(-30));
                            if (d.TempC != v) { d.TempC = v; InvalidateCard(d); }
                            CheckAlert(d, v);
                        }
                        if (tray != null)
                        {
                            string txt = "Foxide Helper" + (max > 0 ? L.F(" — макс. {0} °C", max) : "");
                            tray.Text = txt.Length > 63 ? txt.Substring(0, 63) : txt;
                        }
                    }));
                }
                catch { alertPolling = false; }
            });
        }

        void InvalidateCard(DiskInfo d)
        {
            foreach (Control c in listStack.Controls) { var card = c as DiskCard; if (card != null && card.Disk == d) card.Invalidate(); }
        }

        void CheckAlert(DiskInfo d, long v)
        {
            if (!Settings.TempAlerts) return;
            int limit = d.IsRotating ? Settings.AlertHdd : Settings.AlertSsd;
            DateTime last;
            bool sent = alertSent.TryGetValue(d.Signature, out last);
            if (v >= limit)
            {
                if (sent && (DateTime.Now - last).TotalMinutes < 15) return;   // do not spam: once per 15 min per disk
                alertSent[d.Signature] = DateTime.Now;
                Notify(d, v, limit);
            }
            else if (sent && v <= limit - 3) alertSent.Remove(d.Signature);   // re-arm after cooling down
        }

        void Notify(DiskInfo d, long v, int limit)
        {
            InitTray();
            tray.Visible = true;
            lastAlertSig = d.Signature;
            trayBalloonUpdate = false;
            tray.ShowBalloonTip(15000, L.F("Перегрев диска: {0} °C", v),
                L.F("{0} (диск {1}{2}) нагрелся до {3} °C — порог {4} °C. Проверьте охлаждение.", d.Model, d.Number, d.Letters.Length > 0 ? ", " + d.Letters : "", v, limit),
                ToolTipIcon.Warning);
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F5 && !busy) { RefreshDisks(); e.Handled = true; }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (Demo.On) Demo.FixMinMax(ref m);
            const int WM_DEVICECHANGE = 0x0219;
            if (m.Msg == WM_DEVICECHANGE && Settings.AutoRefresh)
            {
                int ev = m.WParam.ToInt32();
                if (ev == 0x8000 || ev == 0x8004 || ev == 0x0007) { devTimer.Stop(); devTimer.Start(); }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && Settings.MinimizeToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (busy && speedCts == null)
            {
                FxMessage.Show(this, L.T("Идёт очистка диска. Дождитесь завершения операции."), "Foxide Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                exiting = false;
                return;
            }
            if (speedCts != null) speedCts.Cancel();
            base.OnFormClosing(e);
            if (!e.Cancel && tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (!e.Cancel) StopHwPoller();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { SystemEvents.UserPreferenceChanged -= OnUserPrefChanged; devTimer.Dispose(); tempTimer.Dispose(); alertTimer.Dispose(); if (tray != null) tray.Dispose(); }
            base.Dispose(disposing);
        }

        void OnUserPrefChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (Settings.ThemeMode == 0 && e.Category == UserPreferenceCategory.General)
                BeginInvoke(new Action(() => SetTheme(0)));
        }

        public void SetTheme(int mode)
        {
            Theme.Set(mode);
            Theme.Apply(this);
            Theme.ApplyTitleBar(this);
            // nudge the non-client area so the title bar repaints
            if (WindowState == FormWindowState.Normal) { Width += 1; Width -= 1; }
        }

        // ---------------------------------------------------------------- data
        public async void RefreshDisks()
        {
            if (loading || busy) return;
            loading = true;
            btnRefresh.Enabled = false;
            loadError = null;
            if (disks.Count == 0 && DiskSection) { BuildList(); ShowDisk(null); }
            try
            {
                disks = await Task.Run(() => Demo.On ? Demo.Disks() : DiskService.Load());
                if (Demo.On) Demo.Seed(disks, tempHistory, speedResults);
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
                disks = new List<DiskInfo>();
            }
            finally
            {
                loading = false;
                btnRefresh.Enabled = !busy;
            }

            string keep = sel != null ? sel.Signature : null;
            int keepNum = sel != null ? sel.Number : -1;
            sel = null;
            UpdateCounts();
            if (DiskSection)
            {
                var vis = Filtered();
                sel = vis.FirstOrDefault(d => d.Signature == keep) ?? vis.FirstOrDefault(d => d.Number == keepNum) ?? vis.FirstOrDefault();
                BuildList();
                ShowDisk(sel);
            }
            if (DisksLoaded != null) DisksLoaded(this, EventArgs.Empty);
        }

        bool IsShown(DiskInfo d) { return Settings.ShowVirtual || d.Kind != DiskKind.Virtual; }

        bool InSection(DiskInfo d, string s)
        {
            switch (s)
            {
                case "ssd": return d.Kind == DiskKind.SSD || (d.Kind == DiskKind.Removable && d.Rpm == 1 && d.MediaType == 4);
                case "hdd": return d.Kind == DiskKind.HDD || d.Rpm > 1;
                case "usb": return d.Kind == DiskKind.Removable;
                case "other": return d.Kind == DiskKind.Virtual || d.Kind == DiskKind.Other;
                default: return true;
            }
        }

        List<DiskInfo> Filtered() { return disks.Where(d => IsShown(d) && InSection(d, section)).ToList(); }

        void UpdateCounts()
        {
            foreach (var id in SectionIds)
            {
                int n = disks.Count(d => IsShown(d) && InSection(d, id));
                nav[id].Badge = n.ToString(CultureInfo.InvariantCulture);
                nav[id].Invalidate();
            }
        }

        // ---------------------------------------------------------------- navigation
        public void SelectSection(string id, bool rebuild = true)
        {
            if (busy) return;
            if (started) Telemetry.Count("section." + id);
            section = id;
            foreach (var kv in nav) kv.Value.Selected = kv.Key == id;
            if (id == "stress") id = section = "cpu";       // old name of the section
            bool settings = id == "settings", ram = id == "ram", sys = Array.IndexOf(SystemSections, id) >= 0;
            listPanel.Visible = !sys;
            tabsRow.Visible = !sys;
            if (id == "cpu" || id == "gpu")
            {
                hTitle.Text = id == "cpu" ? L.T("Процессор") : L.T("Видеокарта");
                hSub.Text = id == "cpu" ? (cpuInfoName ?? L.T("Сведения, датчики и стресс-тест процессора"))
                                        : (gpuInfoName ?? L.T("Сведения, датчики и стресс-тест видеокарты"));
                UpdateHeaderHeight();
                BuildStress(id);
                return;
            }
            if (id == "home")
            {
                hTitle.Text = L.T("Главная");
                hSub.Text = L.T("Состояние компьютера и программы");
                UpdateHeaderHeight();
                BuildHome();
                return;
            }
            if (id == "battery")
            {
                hTitle.Text = L.T("Аккумулятор");
                hSub.Text = L.T("Здоровье, износ и проверка разрядом");
                UpdateHeaderHeight();
                BuildBattery();
                return;
            }
            if (id == "cooling")
            {
                hTitle.Text = L.T("Охлаждение");
                hSub.Text = L.T("Вентиляторы и температуры");
                UpdateHeaderHeight();
                BuildCooling();
                return;
            }
            if (ram)
            {
                hTitle.Text = L.T("Оперативная память");
                hSub.Text = ramInfo != null && ramInfo.Modules.Count > 0 ? ramInfo.Summary : L.T("Сведения о модулях, загрузка и проверка на ошибки");
                UpdateHeaderHeight();
                BuildRam();
                return;
            }
            if (settings)
            {
                hTitle.Text = L.T("Настройки");
                hSub.Text = L.T("Оформление, тест скорости и параметры форматирования");
                UpdateHeaderHeight();
                BuildSettings();
                return;
            }
            int i = Array.IndexOf(SectionIds, id);
            listTitle.Text = SectionNames[Math.Max(0, i)];
            if (!rebuild) return;
            var vis = Filtered();
            if (sel == null || !vis.Contains(sel)) sel = vis.FirstOrDefault();
            BuildList();
            ShowDisk(sel);
        }

        public void SelectTab(int i)
        {
            if (busy) return;
            if (started && tab != i) Telemetry.Count("tab." + TabIds[Math.Max(0, Math.Min(TabIds.Length - 1, i))]);
            tab = i;
            for (int k = 0; k < tabs.Count; k++) tabs[k].Selected = k == i;
            BuildPage();
        }

        void BuildList()
        {
            listStack.SuspendLayout();
            listStack.ClearAll();
            var vis = Filtered();
            if (loading && disks.Count == 0)
                listStack.Controls.Add(new TextBlock(L.T("Загрузка…"), TStyle.Muted));
            else if (vis.Count == 0)
                listStack.Controls.Add(new TextBlock(loadError != null ? L.T("Не удалось получить список дисков:") + "\n" + loadError : L.T("В этом разделе нет дисков."), TStyle.Muted));
            foreach (var d in vis)
            {
                var card = new DiskCard(d) { Selected = d == sel };
                var dd = d;
                card.Click += (s, e) => { if (!busy) ShowDisk(dd); };
                if (card.CanEject)
                {
                    card.EjectClicked += (s, e) => EjectDisk(card);
                    tip.SetToolTip(card, L.T("Нажмите на иконку, чтобы безопасно извлечь диск"));
                }
                listStack.Controls.Add(card);
            }
            Theme.Apply(listStack);
            listStack.ResumeLayout(true);
        }

        public void ShowDisk(DiskInfo d)
        {
            sel = d;
            foreach (Control c in listStack.Controls)
            {
                var card = c as DiskCard;
                if (card != null) card.Selected = card.Disk == d;
            }
            if (d == null)
            {
                hTitle.Text = loading ? L.T("Загрузка…") : L.T("Диск не выбран");
                hSub.Text = loading ? "" : L.T("Выберите диск в списке слева");
                tabsRow.Visible = false;
            }
            else
            {
                hTitle.Text = d.Model.Length > 0 ? d.Model : L.F("Диск {0}", d.Number);
                hSub.Text = L.F("Диск {0}", d.Number) + "  ·  " + DiskService.FormatSize(d.Size) + "  ·  " + d.KindName +
                            (d.HealthPercent.HasValue ? "  ·  " + L.F("здоровье {0} %", d.HealthPercent) : "") +
                            (d.TempC.HasValue ? "  ·  " + d.TempC + " °C" : "") +
                            (d.Rpm > 1 ? "  ·  " + d.RpmText : "") + "  ·  " + d.BusName +
                            "  ·  " + d.StyleName + (d.Letters.Length > 0 ? "  ·  " + d.Letters : "");
                tabsRow.Visible = true;
            }
            for (int k = 0; k < tabs.Count; k++) tabs[k].Selected = k == tab;
            UpdateHeaderHeight();
            BuildPage();
            if (d != null) BeginInvoke(new Action(PollTemp));
        }

        void BeginPage()
        {
            page.SuspendLayout();
            page.ClearAll();
        }

        void EndPage()
        {
            Theme.Apply(page);
            page.ResumeLayout(true);
            page.PerformLayout();
        }

        void BuildPage()
        {
            BeginPage();
            if (sel == null)
            {
                if (!loading)
                    page.Controls.Add(new Banner(BannerKind.Info, L.T("Нет выбранного диска"),
                        loadError != null ? L.T("Ошибка чтения дисков:") + " " + loadError : L.T("Выберите диск в списке слева или нажмите «Обновить» (F5).")));
                EndPage();
                return;
            }
            switch (tab)
            {
                case 1: BuildHealth(sel); break;
                case 2: BuildSpeed(sel); break;
                case 3: BuildWipe(sel); break;
                default: BuildOverview(sel); break;
            }
            EndPage();
        }

        static Stack Card(string title, string subtitle = null)
        {
            var s = new Stack { Card = true, Pad = 18, Gap = 10 };
            if (title != null) s.Controls.Add(new TextBlock(title, TStyle.Section) { Single = true });
            if (subtitle != null) s.Controls.Add(new TextBlock(subtitle, TStyle.Muted));
            return s;
        }

        /// <summary>Card whose body can be folded; the state is remembered per key. Subtitle stays visible as a summary.</summary>
        Stack FoldCard(string key, string title, string subtitle = null)
        {
            var s = new Stack { Card = true, Pad = 18, Gap = 10 };
            var h = new CardHeader(title) { Collapsed = Settings.CollapsedCards.Contains(key) };
            s.Controls.Add(h);
            if (subtitle != null) s.Controls.Add(new TextBlock(subtitle, TStyle.Muted) { Tag = "keep" });
            Func<Control, bool> body = c => c != h && !"keep".Equals(c.Tag);
            s.ControlAdded += (o, e) => { if (h.Collapsed && body(e.Control)) e.Control.Visible = false; };
            h.Click += (o, e) =>
            {
                h.Collapsed = !h.Collapsed;
                if (h.Collapsed) Settings.CollapsedCards.Add(key); else Settings.CollapsedCards.Remove(key);
                Settings.Save();
                foreach (Control c in s.Controls) if (body(c)) c.Visible = !h.Collapsed;
                page.PerformLayout();
                page.Invalidate(true);
            };
            return s;
        }

        static Row FormRow(string label, Control ctl, int labelDp = 190)
        {
            return new Row { MinHeight = 36 }.Add(new TextBlock(label, TStyle.Body) { Single = true }, labelDp).Add(ctl, -1);
        }

        static string N(long v) { return v.ToString("N0", CultureInfo.CurrentCulture); }

        // ---------------------------------------------------------------- Overview
        void BuildOverview(DiskInfo d)
        {
            if (d.VolumesSize > 0)
            {
                var usage = FoldCard("overview.usage", L.T("Занятое пространство"),
                    L.F("Занято {0} из {1} ({2} %)  ·  свободно {3}", DiskService.FormatSize(d.VolumesUsed), DiskService.FormatSize(d.VolumesSize),
                    (int)Math.Round(d.UsedFraction * 100), DiskService.FormatSize(d.VolumesSize - d.VolumesUsed)));
                var uv = new UsageView(d);
                uv.OpenClicked += letter => OpenVolume(letter);
                usage.Controls.Add(uv);
                page.Controls.Add(usage);
            }
            else if (d.Partitions.Count == 0 || d.Partitions.All(p => p.IsService))
                page.Controls.Add(new Banner(BannerKind.Info, L.T("На диске нет томов с файловой системой"),
                    L.T("Занятое место показать нельзя. Создайте раздел на вкладке «Очистка».")));

            var info = FoldCard("overview.info", L.T("Сведения о диске"));
            var kv = new KVGrid();
            kv.Add(L.T("Модель"), d.Model);
            kv.Add(L.T("Тип"), d.KindName);
            kv.Add(L.T("Интерфейс"), d.BusName);
            kv.Add(L.T("Объём"), DiskService.FormatSize(d.Size) + "  (" + L.F("{0} байт", N((long)d.Size)) + ")");
            kv.Add(L.T("Серийный номер"), d.Serial);
            kv.Add(L.T("Прошивка"), d.Firmware);
            kv.Add(L.T("Стиль разделов"), d.StyleName);
            kv.Add(L.T("Сектор (лог. / физ.)"), d.LogicalSector > 0 ? L.F("{0} / {1} Б", d.LogicalSector, d.PhysicalSector) : "");
            kv.Add(L.T("Скорость вращения"), d.Rpm > 1 ? d.RpmText + (d.RpmSource != null && d.RpmSource != "Windows" ? "  (" + d.RpmSource + ")" : "")
                : d.Rpm == 1 ? L.T("нет (твердотельный)") : L.T("не определена"));
            kv.Add(L.T("Номер диска"), d.Number.ToString(CultureInfo.InvariantCulture));
            kv.Add(L.T("Состояние"), (d.IsOffline ? L.T("Не в сети") : L.T("В сети")) + (d.IsReadOnly ? L.T(", только чтение") : ""));
            kv.Add(L.T("Роль"), d.IsProtected ? L.T("Системный диск Windows") : L.T("Обычный диск"));
            info.Controls.Add(kv);
            page.Controls.Add(info);

            var parts = FoldCard("overview.parts", L.T("Разделы"), d.Partitions.Count == 0 ? null :
                L.F("{0} шт.  ·  служебных: {1}  ·  не распределено: {2}", d.Partitions.Count, d.Partitions.Count(p => p.IsService),
                DiskService.FormatSize(d.Size > (ulong)d.Partitions.Sum(p => (double)p.Size) ? d.Size - (ulong)d.Partitions.Sum(p => (double)p.Size) : 0)));
            parts.Controls.Add(new PartitionBar(d));
            var ptable = new PartitionTable(d) { Actions = true };
            ptable.ActionClicked += (p, action) => PartitionAction(d, p, action);
            parts.Controls.Add(ptable);
            if (d.Partitions.Any(p => p.IsService) && !d.IsProtected)
            {
                var go = new FlatButton(L.T("Удалить все разделы…"), BtnKind.Secondary, "");
                go.Click += (s, e) => SelectTab(3);
                parts.Controls.Add(new Row().Add(go, 220).Add(new Panel(), -1));
            }
            page.Controls.Add(parts);
        }

        // ---------------------------------------------------------------- Health
        void BuildHealth(DiskInfo d)
        {
            string body;
            BannerKind k;
            switch (d.Health)
            {
                case HealthState.Good: k = BannerKind.Success; body = L.T("Windows и SMART не сообщают о проблемах."); break;
                case HealthState.Warning: k = BannerKind.Warning; body = L.T("Есть признаки, на которые стоит обратить внимание."); break;
                case HealthState.Bad: k = BannerKind.Danger; body = L.T("Диск может скоро выйти из строя. Сделайте резервную копию данных."); break;
                default: k = BannerKind.Info; body = L.T("Диск не передаёт сведения о состоянии (часто так у USB-флешек и карт памяти)."); break;
            }
            if (d.HealthNotes.Count > 0) body += "\n• " + string.Join("\n• ", d.HealthNotes);
            GC.KeepAlive(k);
            if (d.HealthPercent.HasValue && d.HealthBasis != null) body += "\n" + L.F("Здоровье рассчитано {0}.", d.HealthBasis);
            page.Controls.Add(new HealthGauge(d, d.Health == HealthState.Unknown ? L.T("Нет данных о состоянии") : L.T("Состояние:") + " " + DiskService.HealthName(d.Health), body));
            if (Settings.LiveTemp) AddLiveTempCard(d);

            var r = d.Rel ?? new Reliability();
            var grid = new TileGrid();
            int tLimit = d.Kind == DiskKind.HDD ? 55 : 70;
            var sm = d.Smart;
            long t = r.Temperature ?? 0;
            if (t <= 0 && sm != null && sm.Temperature.HasValue) t = sm.Temperature.Value;
            var temp = new StatTile(L.T("Температура"), t > 0 ? t + " °C" : "—",
                (r.TemperatureMax ?? 0) > 0 ? L.F("максимум: {0} °C", r.TemperatureMax) : null);
            if (t > tLimit) temp.ValueColor = () => Theme.P.Warning;
            grid.Controls.Add(temp);

            if (d.Kind == DiskKind.SSD || (r.Wear ?? 0) > 0)
            {
                long w = r.Wear ?? (sm != null && sm.Wear.HasValue ? sm.Wear.Value : -1);
                var wear = new StatTile(L.T("Износ SSD"), w >= 0 ? w + " %" : "—", w >= 0 ? L.F("ресурс: осталось ≈{0} %", 100 - w) : null);
                if (w >= 90) wear.ValueColor = () => Theme.P.Warning;
                grid.Controls.Add(wear);
            }
            long poh = r.PowerOnHours ?? 0;
            if (poh <= 0 && sm != null && sm.PowerOnHours.HasValue) poh = sm.PowerOnHours.Value;
            grid.Controls.Add(new StatTile(L.T("Наработка"), poh > 0 ? L.F("{0} ч", N(poh)) : "—", poh > 0 ? L.F("≈ {0} дн.", N(poh / 24)) : null));
            // values the drive reports only through SMART (NVMe log / ATA attributes)
            Func<string, int, string> sv = (name, attr) =>
            {
                if (sm == null) return null;
                var row = sm.Rows.FirstOrDefault(x => (attr >= 0 && x.AttrId == attr) || (attr < 0 && (x.Name == name || x.Name == L.T(name))));
                return row == null ? null : row.Value;
            };
            string cycles = (r.StartStopCycles ?? 0) > 0 ? N(r.StartStopCycles.Value) : (sv("Циклы включения", sm != null && sm.Nvme ? -1 : 0x0C) ?? "—");
            string unsafeOff = sv("Небезопасные отключения", -1);
            grid.Controls.Add(new StatTile(L.T("Циклы включения"), cycles,
                (r.LoadUnloadCycles ?? 0) > 0 ? L.F("парковок головок: {0}", N(r.LoadUnloadCycles.Value))
                : unsafeOff != null ? L.F("небезопасных отключений: {0}", unsafeOff) : null));
            if (sm != null && sm.Nvme)
            {
                var me = new StatTile(L.T("Ошибки носителя"), N(sm.MediaErrors), L.T("ошибки целостности данных NVMe"));
                me.ValueColor = () => sm.MediaErrors > 0 ? Theme.P.Danger : Theme.P.Success;
                grid.Controls.Add(me);
                string written = sv("Записано данных", -1), read = sv("Прочитано данных", -1);
                if (written != null) grid.Controls.Add(new StatTile(L.T("Записано за всё время"), written, read != null ? L.F("прочитано: {0}", read) : null));
            }

            var re = new StatTile(L.T("Ошибки чтения"), r.ReadErrors.HasValue ? N(r.ReadErrors.Value) : "—",
                r.ReadErrorsUncorrected.HasValue ? L.F("неисправленных: {0}", N(r.ReadErrorsUncorrected.Value)) : null);
            if ((r.ReadErrorsUncorrected ?? 0) > 0) re.ValueColor = () => Theme.P.Danger;
            grid.Controls.Add(re);
            var we = new StatTile(L.T("Ошибки записи"), r.WriteErrors.HasValue ? N(r.WriteErrors.Value) : "—",
                r.WriteErrorsUncorrected.HasValue ? L.F("неисправленных: {0}", N(r.WriteErrorsUncorrected.Value)) : null);
            if ((r.WriteErrorsUncorrected ?? 0) > 0) we.ValueColor = () => Theme.P.Danger;
            grid.Controls.Add(we);

            grid.Controls.Add(new StatTile(L.T("Макс. задержка"), (r.ReadLatencyMax ?? 0) > 0 ? L.F("{0} мс", N(r.ReadLatencyMax.Value)) : "—",
                (r.WriteLatencyMax ?? 0) > 0 ? L.F("запись: {0} мс", N(r.WriteLatencyMax.Value)) : L.T("чтение")));

            var smart = new StatTile(L.T("Прогноз SMART"),
                d.PredictFailure.HasValue ? (d.PredictFailure.Value ? L.T("Отказ") : L.T("Норма")) : "—",
                d.PredictFailure.HasValue ? "MSStorageDriver_FailurePredict" : L.T("диск не поддерживает"));
            if (d.PredictFailure == true) smart.ValueColor = () => Theme.P.Danger;
            else if (d.PredictFailure == false) smart.ValueColor = () => Theme.P.Success;
            grid.Controls.Add(smart);

            string hs = d.HealthStatus == 0 ? L.T("Исправен") : d.HealthStatus == 1 ? L.T("Предупр.") : d.HealthStatus == 2 ? L.T("Неисправен") : "—";
            var win = new StatTile(L.T("Оценка Windows"), hs, "Storage HealthStatus");
            win.ValueColor = () => d.HealthStatus == 0 ? Theme.P.Success : d.HealthStatus == 1 ? Theme.P.Warning : d.HealthStatus == 2 ? Theme.P.Danger : Theme.P.Text;
            grid.Controls.Add(win);
            page.Controls.Add(grid);

            if (sm != null && sm.Rows.Count > 0)
            {
                int bad = sm.Count(SmartState.Bad), warn = sm.Count(SmartState.Warning);
                string summary = L.F("Источник: {0}  ·  показателей: {1}", sm.Source, sm.Rows.Count) + "  ·  " +
                    (bad + warn == 0 ? L.T("отклонений от нормы нет") : L.F("отклонений: {0}", bad + warn) + (bad > 0 ? L.F(" (критичных: {0})", bad) : ""));
                var card = FoldCard("health.smart", L.T("Расширенные данные по датчикам и показателям"), summary);
                var table = new SmartTable(sm);
                var only = new Toggle(L.T("Показывать только отклонения от нормы"), false);
                only.CheckedChanged += (s, e) => { table.OnlyIssues = only.Checked; page.PerformLayout(); };
                card.Controls.Add(only);
                card.Controls.Add(table);
                card.Controls.Add(new TextBlock(L.T("Норма — ожидаемое значение для исправного диска. «Тек. / Худш. / Порог» — нормализованные значения SMART: атрибут считается критичным, когда текущее значение опускается до порога производителя. «Инфо» — счётчики без нормы."), TStyle.Small));
                page.Controls.Add(card);
            }
            else
                page.Controls.Add(new Banner(BannerKind.Info, L.T("Расширенные данные недоступны"),
                    Program.IsAdmin ? L.T("Диск или его USB-контроллер не передаёт SMART.") : L.T("Для чтения SMART запустите программу от имени администратора.")));

            page.Controls.Add(new TextBlock(L.T("Данные получены из Windows Storage Management (счётчики надёжности) и SMART. Если диск не передаёт какой-то показатель, вместо него стоит «—». Для чтения счётчиков нужны права администратора."), TStyle.Muted));
            var upd = new FlatButton(L.T("Обновить данные"), BtnKind.Secondary, "");
            upd.Click += (s, e) => RefreshDisks();
            page.Controls.Add(new Row().Add(upd, 190).Add(new Panel(), -1));
        }

        // ---------------------------------------------------------------- Speed
        static readonly int[] TestSizes = { 256, 512, 1024, 2048, 4096 };
        static readonly int[] TestSecs = { 3, 5, 10, 20 };

        static ThemedCombo SizeCombo()
        {
            var c = new ThemedCombo(TestSizes.Select(v => DiskService.FormatSize((ulong)v << 20)).ToArray());
            c.SelectedIndex = Math.Max(0, Array.IndexOf(TestSizes, Settings.TestSizeMB));
            c.SelectedIndexChanged += (s, e) => { Settings.TestSizeMB = TestSizes[c.SelectedIndex]; Settings.Save(); };
            return c;
        }

        static ThemedCombo SecsCombo()
        {
            var c = new ThemedCombo(TestSecs.Select(v => L.F("{0} с на каждый", v)).ToArray());
            c.SelectedIndex = Math.Max(0, Array.IndexOf(TestSecs, Settings.RandomSeconds));
            c.SelectedIndexChanged += (s, e) => { Settings.RandomSeconds = TestSecs[c.SelectedIndex]; Settings.Save(); };
            return c;
        }

        void BuildSpeed(DiskInfo d)
        {
            var vols = d.Partitions.Where(p => p.Letter != '\0' && p.FileSystem.Length > 0).ToList();
            var card = Card(L.T("Тест скорости"),
                L.T("Измеряет реальную скорость в обход кэша Windows: последовательные операции блоками 1 МБ и случайные блоками 4 КБ. На выбранном томе временно создаётся тестовый файл, после теста он удаляется — ваши данные не затрагиваются. Больший файл точнее показывает скорость HDD и SSD с большим кэшем, но тест идёт дольше."));
            page.Controls.Add(card);

            SpeedResult prev;
            speedResults.TryGetValue(d.Signature, out prev);

            if (vols.Count == 0)
            {
                page.Controls.Add(new Banner(BannerKind.Warning, L.T("Нет доступного тома"),
                    L.T("Для теста нужен раздел с буквой диска и файловой системой. Отформатируйте диск на вкладке «Очистка» или назначьте букву в «Управлении дисками».")));
                return;
            }

            var combo = new ThemedCombo();
            foreach (var v in vols)
                combo.Items.Add(v.Letter + ":  " + (v.Label.Length > 0 ? v.Label : L.T("Без метки")) + "  (" + v.FileSystem + ", " + L.F("свободно {0}", DiskService.FormatSize(v.Free)) + ")");
            combo.SelectedIndex = 0;
            var start = new FlatButton(L.T("Запустить тест"), BtnKind.Primary, "");
            var stop = new FlatButton(L.T("Остановить"), BtnKind.Secondary) { Enabled = false };
            var cSize = SizeCombo();
            var cSecs = SecsCombo();
            card.Controls.Add(FormRow(L.T("Том для теста"), combo, 140));
            card.Controls.Add(new Row { MinHeight = 36 }
                .Add(new TextBlock(L.T("Размер файла"), TStyle.Body) { Single = true }, 140).Add(cSize, -1)
                .Add(new Panel(), 8)
                .Add(new TextBlock(L.T("Тесты 4K"), TStyle.Body) { Single = true }, 80).Add(cSecs, -1));
            card.Controls.Add(new Row { MinHeight = 38 }.Add(start, 180).Add(stop, 130).Add(new Panel(), -1));

            var prog = new ProgressLine();
            var status = new TextBlock(prev != null ? L.F("Последний тест: {0}, {1} МБ, {2}", prev.Volume, prev.SizeMB, prev.When.ToString("HH:mm:ss")) : L.T("Готов к тесту"), TStyle.Muted) { Single = true };
            card.Controls.Add(prog);
            card.Controls.Add(status);

            var grid = new TileGrid { MinTile = 200, TileH = 104 };
            var tSR = new StatTile(L.T("Последовательное чтение"), "—", L.T("блок 1 МБ, очередь 1"), "");
            var tSW = new StatTile(L.T("Последовательная запись"), "—", L.T("блок 1 МБ, очередь 1"), "");
            var tRR = new StatTile(L.T("Случайное чтение 4K"), "—", L.T("очередь 1"), "");
            var tRW = new StatTile(L.T("Случайная запись 4K"), "—", L.T("очередь 1"), "");
            grid.Controls.AddRange(new Control[] { tSR, tSW, tRR, tRW });
            page.Controls.Add(grid);
            page.Controls.Add(new TextBlock(L.T("Скорость указана в МБ/с (1 МБ = 1 000 000 байт), как в большинстве тестов накопителей. Ориентиры: HDD — 80–250 МБ/с последовательно, SATA SSD — 450–560 МБ/с, NVMe — от 1 500 МБ/с."), TStyle.Muted));

            Action<SpeedResult> show = res =>
            {
                tSR.Set(L.F("{0} МБ/с", res.SeqRead.ToString("N1")), L.T("блок 1 МБ, очередь 1"));
                tSW.Set(L.F("{0} МБ/с", res.SeqWrite.ToString("N1")), L.T("блок 1 МБ, очередь 1"));
                tRR.Set(L.F("{0} МБ/с", res.RndRead.ToString("N2")), N((long)res.RndReadIops) + " IOPS");
                tRW.Set(L.F("{0} МБ/с", res.RndWrite.ToString("N2")), N((long)res.RndWriteIops) + " IOPS");
            };
            if (prev != null) { show(prev); prog.Value = 1; }

            stop.Click += (s, e) => { if (speedCts != null) { speedCts.Cancel(); status.Text = L.T("Останавливаю…"); } };
            start.Click += async (s, e) =>
            {
                if (busy) return;
                char letter = vols[Math.Max(0, combo.SelectedIndex)].Letter;
                var disk = d;
                SetBusy(true);
                start.Enabled = false; stop.Enabled = true; combo.Enabled = cSize.Enabled = cSecs.Enabled = false;
                foreach (var t in new[] { tSR, tSW, tRR, tRW }) t.Set("—", t.Sub);
                prog.Value = 0;
                speedCts = new CancellationTokenSource();
                var token = speedCts.Token;
                var clock = Stopwatch.StartNew();
                long last = -1000;
                Action<string, double> report = (phase, f) =>
                {
                    long now = clock.ElapsedMilliseconds;
                    if (now - last < 80 && f < 1) return;
                    last = now;
                    BeginInvoke(new Action(() =>
                    {
                        if (prog.IsDisposed) return;
                        prog.Value = f;
                        status.Text = phase + "…  " + (int)(f * 100) + " %";
                    }));
                };
                try
                {
                    int sizeMb = Settings.TestSizeMB, secs = Settings.RandomSeconds;
                    Telemetry.Count("action.speed_test");
                    var res = await Task.Run(() => SpeedTest.Run(letter, sizeMb, secs, report, token));
                    speedResults[disk.Signature] = res;
                    show(res);
                    prog.Value = 1;
                    status.Text = L.F("Готово: {0}, файл {1} МБ, {2}", res.Volume, res.SizeMB, res.When.ToString("HH:mm:ss"));
                }
                catch (OperationCanceledException) { status.Text = L.T("Тест остановлен."); prog.Value = 0; }
                catch (Exception ex) { status.Text = L.T("Ошибка:") + " " + ex.Message; prog.Value = 0; }
                finally
                {
                    speedCts = null;
                    SetBusy(false);
                    if (!start.IsDisposed) { start.Enabled = true; stop.Enabled = false; combo.Enabled = cSize.Enabled = cSecs.Enabled = true; }
                }
            };
        }

        // ---------------------------------------------------------------- Wipe
        void BuildWipe(DiskInfo d)
        {
            if (d.IsProtected)
            {
                page.Controls.Add(new Banner(BannerKind.Warning, L.T("Системный диск защищён"),
                    L.T("На этом диске запущена Windows, поэтому очищать его из работающей системы нельзя. Чтобы переразметить системный диск, загрузитесь с установочной флешки Windows.")));
                return;
            }
            if (!Program.IsAdmin)
            {
                page.Controls.Add(new Banner(BannerKind.Warning, L.T("Нужны права администратора"),
                    L.T("Запустите Foxide Helper от имени администратора, чтобы очищать и форматировать диски.")));
                return;
            }

            page.Controls.Add(new Banner(BannerKind.Danger, L.F("Полная очистка диска {0}", d.Number),
                L.T("Будут удалены ВСЕ разделы, включая скрытые служебные (EFI, MSR, восстановление, OEM), которые не удаляются в «Управлении дисками». Затем можно сразу создать один или несколько разделов со своими буквами. Данные восстановить будет нельзя.")));

            var what = FoldCard("wipe.parts", L.T("Что будет удалено"),
                L.F("{0} разд. · {1}", d.Partitions.Count, string.Join(", ", d.Partitions.Select(p => p.Letter != '\0' ? p.Letter + ":" : p.Type))));
            what.Controls.Add(new PartitionBar(d));
            what.Controls.Add(new PartitionTable(d));
            page.Controls.Add(what);

            // ---- plan model
            var plan = new WipeOptions { Style = Settings.DefaultStyle == "MBR" ? "MBR" : "GPT", Quick = Settings.QuickFormat };
            plan.Parts[0].FileSystem = Settings.DefaultFs;
            plan.Parts[0].Label = d.Kind == DiskKind.Removable ? "USB" : "Data";
            var busyLetters = Wiper.BusyLetters(d);
            var freeLetters = "DEFGHIJKLMNOPQRSTUVWXYZ".Where(c => !busyLetters.Contains(c)).ToList();
            long usable = Wiper.UsableMB(d, "GPT");

            var opts = Card(L.T("Новая разметка"));
            var cStyle = new ThemedCombo(L.T("GPT — современные ПК, до 128 разделов"), L.T("MBR — старые ПК с BIOS, до 4 разделов"));
            cStyle.SelectedIndex = plan.Style == "MBR" ? 1 : 0;
            var tgQuick = new Toggle(L.T("Быстрое форматирование"), Settings.QuickFormat);
            var tgZero = new Toggle(L.T("Перезаписать весь диск нулями (clean all) — очень долго, перед продажей или передачей диска"), false);
            var tgEmpty = new Toggle(L.T("Не создавать разделы — оставить диск пустым"), false);
            opts.Controls.AddRange(new Control[] { FormRow(L.T("Стиль разделов"), cStyle), tgQuick, tgZero, tgEmpty });
            page.Controls.Add(opts);

            var layout = Card(L.T("Разделы на диске"), L.T("Последний раздел получает всё оставшееся место. Буква «Авто» — Windows назначит свободную сама."));
            var previewDisk = new DiskInfo { Size = d.Size };
            var preview = new PartitionBar(previewDisk);
            var editor = new Stack { Gap = 6 };
            var add = new FlatButton(L.T("Добавить раздел"), BtnKind.Secondary, "");
            var split = new FlatButton(L.T("Разделить поровну"), BtnKind.Ghost, "");
            var err = new TextBlock("", TStyle.Body) { ColorFn = () => Theme.P.Danger };
            layout.Controls.Add(preview);
            layout.Controls.Add(editor);
            layout.Controls.Add(new Row { MinHeight = 38 }.Add(add, 200).Add(split, 200).Add(new Panel(), -1));
            layout.Controls.Add(err);
            var fatNote = new Banner(BannerKind.Info, L.T("FAT32 больше 32 ГБ"),
                L.T("Windows так форматировать не умеет, поэтому эти разделы отформатирует встроенный форматировщик Foxide Helper (всегда быстрое форматирование). Такие диски читают старые ПК, PS3, Xbox 360, Wii, автомагнитолы и ТВ. Ограничение FAT32: один файл — не больше 4 ГБ. Для консолей и старых ПК выбирайте стиль разделов MBR."), "") { Visible = false };
            layout.Controls.Add(fatNote);
            page.Controls.Add(layout);

            var go = new FlatButton(L.F("Стереть и отформатировать диск {0}", d.Number), BtnKind.Danger, "");
            page.Controls.Add(new Row { MinHeight = 40 }.Add(go, 330).Add(new Panel(), -1));

            var logCard = Card(L.T("Журнал"));
            string prevLog;
            wipeLogs.TryGetValue(d.Signature, out prevLog);
            var log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
                BorderStyle = BorderStyle.None, Height = Theme.Dp(220), WordWrap = true,
                Text = prevLog ?? L.T("Здесь появится вывод diskpart.")
            };
            logCard.Controls.Add(log);
            page.Controls.Add(logCard);

            TextBlock restLabel = null;
            var sliders = new List<KeyValuePair<Slider, int>>();
            Func<int, long> maxFor = idx =>
            {
                long others = 0;
                for (int k = 0; k < plan.Parts.Count - 1; k++) if (k != idx) others += plan.Parts[k].SizeMB;
                return Math.Max(Wiper.MinPartMB, Wiper.UsableMB(d, plan.Style) - others - Wiper.MinPartMB);
            };
            Action changed = null;
            Action rebuildEditor = null;

            Func<WipeOptions> read = () =>
            {
                plan.Style = cStyle.SelectedIndex == 1 ? "MBR" : "GPT";
                plan.Quick = tgQuick.Checked;
                plan.ZeroFill = tgZero.Checked;
                plan.NoPartition = tgEmpty.Checked;
                return plan;
            };

            changed = () =>
            {
                read();
                long rest = Wiper.RestMB(d, plan);
                if (restLabel != null && !restLabel.IsDisposed)
                    restLabel.Text = rest > 0 ? L.F("остаток ≈ {0}", DiskService.FormatSize((ulong)rest << 20)) : L.T("не хватает места");
                foreach (var sp in sliders)
                {
                    if (sp.Key.IsDisposed || sp.Value >= plan.Parts.Count) continue;
                    sp.Key.Maximum = maxFor(sp.Value);
                    sp.Key.SetSilently(plan.Parts[sp.Value].SizeMB);
                }

                // preview of the future layout
                var list = new List<PartInfo>();
                ulong off = 1UL << 20;
                if (plan.Style == "GPT")
                {
                    list.Add(new PartInfo { Number = 0, Offset = off, Size = 16UL << 20, Type = L.T("Зарезервировано (MSR)"), IsService = true });
                    off += 16UL << 20;
                }
                if (!plan.NoPartition)
                    for (int i = 0; i < plan.Parts.Count; i++)
                    {
                        var p = plan.Parts[i];
                        long mb = i == plan.Parts.Count - 1 ? Math.Max(0, rest) : p.SizeMB;
                        list.Add(new PartInfo { Number = i + 1, Offset = off, Size = (ulong)mb << 20, Letter = p.Letter, FileSystem = p.FileSystem, Type = L.T("Основной раздел") });
                        off += (ulong)mb << 20;
                    }
                previewDisk.Partitions = list;
                preview.Invalidate();

                bool parts = !plan.NoPartition;
                editor.Enabled = add.Enabled = split.Enabled = tgQuick.Enabled = parts;
                add.Enabled = parts && plan.Parts.Count < (plan.Style == "MBR" ? 4 : 26);
                fatNote.Visible = parts && Enumerable.Range(0, plan.Parts.Count).Any(i => Wiper.IsBigFat(d, plan, i));
                string msg = Wiper.Validate(d, plan, busyLetters) ?? "";
                err.Text = msg;
                err.Visible = msg.Length > 0;
                go.Enabled = msg.Length == 0;
                go.Text = !parts ? L.F("Стереть диск {0}", d.Number)
                    : plan.Parts.Count > 1 ? L.F("Стереть и создать {0} разд. на диске {1}", plan.Parts.Count, d.Number)
                    : L.F("Стереть и отформатировать диск {0}", d.Number);
                page.PerformLayout();
            };

            rebuildEditor = () =>
            {
                editor.SuspendLayout();
                editor.ClearAll();
                sliders.Clear();
                restLabel = null;
                Func<string, TextBlock> head = t => new TextBlock(t, TStyle.Small) { Single = true };
                editor.Controls.Add(new Row { MinHeight = 18, Gap = 6 }
                    .Add(head("№"), 22).Add(head(L.T("Размер")), 132).Add(head(L.T("ФС")), 92)
                    .Add(head(L.T("Метка тома")), -1).Add(head(L.T("Буква")), 80).Add(head(""), 34));

                for (int i = 0; i < plan.Parts.Count; i++)
                {
                    var p = plan.Parts[i];
                    int idx = i;
                    bool last = i == plan.Parts.Count - 1;
                    var row = new Row { MinHeight = 34, Gap = 6 };
                    Slider slider = null;
                    row.Add(new TextBlock((i + 1) + ".", TStyle.Bold) { Single = true }, 22);
                    if (last)
                    {
                        restLabel = new TextBlock("", TStyle.Muted) { Single = true };
                        row.Add(restLabel, 132);
                    }
                    else
                    {
                        var num = new NumericUpDown
                        {
                            DecimalPlaces = 1, Increment = 1, Minimum = 0.1m, ThousandsSeparator = true,
                            Maximum = Math.Max(1, usable / 1024m), BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body,
                            TextAlign = HorizontalAlignment.Right,
                        };
                        num.Value = Math.Min(num.Maximum, Math.Max(num.Minimum, Math.Round(p.SizeMB / 1024m, 1)));
                        slider = new Slider { Minimum = Wiper.MinPartMB, Step = 1024 };
                        slider.Maximum = maxFor(idx);
                        slider.SetSilently(p.SizeMB);
                        bool syncing = false;
                        var sl = slider;
                        num.ValueChanged += (s, e) =>
                        {
                            if (syncing) return;
                            p.SizeMB = (long)Math.Round(num.Value * 1024m);
                            changed();
                        };
                        sl.ValueChanged += (s, e) =>
                        {
                            p.SizeMB = sl.Value;
                            syncing = true;
                            num.Value = Math.Min(num.Maximum, Math.Max(num.Minimum, Math.Round(sl.Value / 1024m, 1)));
                            syncing = false;
                            changed();
                        };
                        sliders.Add(new KeyValuePair<Slider, int>(sl, idx));
                        row.Add(num, 100).Add(new TextBlock(L.T("ГБ"), TStyle.Muted) { Single = true }, 26);
                    }
                    var fs = new ThemedCombo("NTFS", "exFAT", "FAT32");
                    fs.SelectedIndex = Math.Max(0, fs.Items.IndexOf(p.FileSystem));
                    fs.SelectedIndexChanged += (s, e) => { p.FileSystem = (string)fs.SelectedItem; changed(); };
                    var label = new TextBox { Text = p.Label, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body };
                    label.TextChanged += (s, e) => { p.Label = label.Text; };
                    var letter = new ThemedCombo(new[] { L.T("Авто") }.Concat(freeLetters.Select(c => c + ":")).ToArray());
                    int li = p.Letter == '\0' ? 0 : freeLetters.IndexOf(p.Letter) + 1;
                    letter.SelectedIndex = Math.Max(0, li);
                    letter.SelectedIndexChanged += (s, e) =>
                    {
                        p.Letter = letter.SelectedIndex <= 0 ? '\0' : freeLetters[letter.SelectedIndex - 1];
                        changed();
                    };
                    var del = new FlatButton("", BtnKind.Ghost, "") { Enabled = plan.Parts.Count > 1 };
                    tip.SetToolTip(del, L.T("Удалить раздел из плана"));
                    del.Click += (s, e) =>
                    {
                        if (plan.Parts.Count <= 1) return;
                        plan.Parts.RemoveAt(idx);
                        BeginInvoke(new Action(() => { rebuildEditor(); changed(); }));
                    };
                    row.Add(fs, 92).Add(label, -1).Add(letter, 80).Add(del, 34);
                    editor.Controls.Add(row);
                    if (slider != null)
                        editor.Controls.Add(new Row { MinHeight = 26, Gap = 6 }.Add(new Panel(), 22).Add(slider, -1).Add(new Panel(), 34));
                }
                Theme.Apply(editor);
                editor.ResumeLayout(true);
            };

            add.Click += (s, e) =>
            {
                long rest = Wiper.RestMB(d, read());
                var lastP = plan.Parts[plan.Parts.Count - 1];
                lastP.SizeMB = Math.Max(Wiper.MinPartMB, rest / 2 / 1024 * 1024);
                plan.Parts.Add(new PartPlan { FileSystem = lastP.FileSystem, Label = "Data" + (plan.Parts.Count + 1) });
                rebuildEditor(); changed();
            };
            split.Click += (s, e) =>
            {
                read();
                int n = plan.Parts.Count;
                long each = Wiper.UsableMB(d, plan.Style) / n / 1024 * 1024;
                for (int i = 0; i < n - 1; i++) plan.Parts[i].SizeMB = Math.Max(Wiper.MinPartMB, each);
                rebuildEditor(); changed();
            };
            cStyle.SelectedIndexChanged += (s, e) => changed();
            tgEmpty.CheckedChanged += (s, e) => changed();
            rebuildEditor();
            changed();

            go.Click += async (s, e) =>
            {
                if (busy || !go.Enabled) return;
                var o = read();
                using (var dlg = new ConfirmDialog(d, o))
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                SetBusy(true);
                go.Enabled = false;
                log.Text = "";
                var sig = d.Signature;

                // progress card pinned at the top of the page
                var progCard = Card(null);
                var progTitle = new TextBlock(L.T("Подготовка…"), TStyle.Section) { Single = true };
                var progBar = new ProgressLine { Height = Theme.Dp(12), Busy = true };
                var progInfo = new TextBlock("", TStyle.Muted) { Single = true };
                progCard.Controls.Add(progTitle);
                progCard.Controls.Add(progBar);
                progCard.Controls.Add(progInfo);
                page.Controls.Add(progCard);
                page.Controls.SetChildIndex(progCard, 0);
                Theme.Apply(progCard);
                page.AutoScrollPosition = new Point(0, 0);
                page.PerformLayout();
                var clock = Stopwatch.StartNew();
                Action<string, int, int, double, bool> progress = (title, k, n, f, indet) => BeginInvoke(new Action(() =>
                {
                    if (progBar.IsDisposed) return;
                    progTitle.Text = title;
                    progBar.Value = f;
                    progBar.Busy = indet;
                    var el = clock.Elapsed;
                    progInfo.Text = L.F("Шаг {0} из {1}   ·   выполнено {2} %   ·   прошло {3}", k + 1, n, (int)Math.Round(f * 100),
                                    (el.TotalHours >= 1 ? L.F("{0} ч ", (int)el.TotalHours) : "") + L.F("{0} мин {1} с", el.Minutes, el.Seconds));
                }));
                Action<string> append = line => BeginInvoke(new Action(() =>
                {
                    string l = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine;
                    string cur;
                    wipeLogs.TryGetValue(sig, out cur);
                    wipeLogs[sig] = (cur ?? "") + l;
                    if (!log.IsDisposed) log.AppendText(l);
                }));
                wipeLogs[sig] = "";
                append(L.F("Начинаю очистку диска {0} ({1})…", d.Number, d.Model));
                int code = -1;
                string error = null;
                try
                {
                    code = await Task.Run(() =>
                    {
                        Telemetry.Count("action.wipe");
                        var fresh = Wiper.Reverify(d);
                        return Wiper.Run(fresh, o, append, progress);
                    });
                }
                catch (Exception ex) { error = ex.Message; }
                bool okRun = error == null && code == 0;
                BeginInvoke(new Action(() =>
                {
                    if (progBar.IsDisposed) return;
                    progBar.Busy = false;
                    progBar.Value = okRun ? 1 : progBar.Value;
                    progTitle.Text = okRun ? L.T("Готово") : L.T("Ошибка");
                    progTitle.ColorFn = okRun ? (Func<Color>)(() => Theme.P.Success) : () => Theme.P.Danger;
                }));

                append(error != null ? L.T("ОШИБКА:") + " " + error
                    : code == 0 ? (o.NoPartition ? L.T("Готово. Диск очищен.") : L.T("Готово. Диск очищен и отформатирован."))
                    : L.F("diskpart завершился с кодом {0}. Подробности — в журнале выше.", code));
                await Task.Delay(300);
                SetBusy(false);
                if (error == null && code == 0)
                    FxMessage.Show(this, o.NoPartition ? L.F("Диск {0} успешно очищен.", d.Number) : L.F("Диск {0} успешно очищен. Создано разделов: {1}.", d.Number, o.Parts.Count),
                        "Foxide Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    FxMessage.Show(this, error ?? L.F("diskpart вернул код ошибки {0}. Подробности — в журнале.", code),
                        "Foxide Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshDisks();
            };
        }

        // ---------------------------------------------------------------- live temperature
        List<TempPoint> History(string sig)
        {
            List<TempPoint> l;
            if (!tempHistory.TryGetValue(sig, out l)) tempHistory[sig] = l = new List<TempPoint>();
            return l;
        }

        void PollTemp()
        {
            if (Demo.On || !Settings.LiveTemp || !Visible || tempReading || busy || loading || sel == null || sel.Kind == DiskKind.Virtual) return;
            var d = sel;
            tempReading = true;
            Task.Run(() => SmartReader.ReadTemp(d)).ContinueWith(t =>
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        tempReading = false;
                        long? v = t.IsFaulted ? null : t.Result;
                        int fails; tempFails.TryGetValue(d.Signature, out fails);
                        if (v.HasValue)
                        {
                            tempFails[d.Signature] = 0;
                            var h = History(d.Signature);
                            h.Add(new TempPoint { T = DateTime.Now, V = v.Value });
                            h.RemoveAll(p => p.T < DateTime.Now.AddMinutes(-30));
                            CheckAlert(d, v.Value);
                            if (d.TempC != v)
                            {
                                d.TempC = v;
                                foreach (Control c in listStack.Controls) { var card = c as DiskCard; if (card != null && card.Disk == d) card.Invalidate(); }
                            }
                        }
                        else tempFails[d.Signature] = fails + 1;
                        UpdateLive(d);
                    }));
                }
                catch { tempReading = false; }
            });
        }

        void UpdateLive(DiskInfo d)
        {
            if (liveChart == null || liveChart.IsDisposed || liveSig != d.Signature) return;
            var h = History(d.Signature);
            liveChart.Points = h;
            liveChart.Invalidate();
            int fails; tempFails.TryGetValue(d.Signature, out fails);
            var win = h.Where(p => p.T >= DateTime.Now.AddMinutes(-Settings.TempWindowMin)).ToList();
            if (win.Count == 0)
                liveStats.Text = fails >= 3 ? (Program.IsAdmin ? L.T("Диск не сообщает температуру.") : L.T("Диск не сообщает температуру — запустите программу от имени администратора.")) : L.T("Сбор данных…");
            else
                liveStats.Text = L.F("{0} °C  ·  мин {1}  ·  макс {2}  ·  ср. {3}", win[win.Count - 1].V.ToString("0"), win.Min(p => p.V).ToString("0"),
                                 win.Max(p => p.V).ToString("0"), win.Average(p => p.V).ToString("0.0"));
        }

        void AddLiveTempCard(DiskInfo d)
        {
            var card = FoldCard("health.temp", L.T("Температура в реальном времени"),
                L.T("Опрос выбранного диска, пока программа открыта. История хранится 30 минут для каждого диска."));
            liveStats = new TextBlock(L.T("Сбор данных…"), TStyle.Bold) { Single = true };
            int[] intervals = { 1, 2, 5, 10 };
            var cInt = new ThemedCombo(intervals.Select(v => L.F("каждые {0} с", v)).ToArray());
            cInt.SelectedIndex = Math.Max(0, Array.IndexOf(intervals, Settings.TempIntervalSec));
            cInt.SelectedIndexChanged += (s, e) =>
            {
                Settings.TempIntervalSec = intervals[cInt.SelectedIndex]; Settings.Save();
                tempTimer.Interval = Settings.TempIntervalSec * 1000;
            };
            int[] windows = { 2, 5, 10, 30 };
            var cWin = new ThemedCombo(windows.Select(v => L.F("за {0} мин", v)).ToArray());
            cWin.SelectedIndex = Math.Max(0, Array.IndexOf(windows, Settings.TempWindowMin));
            liveChart = new TempChart { Warn = d.TempWarn, Bad = d.TempBad, Window = TimeSpan.FromMinutes(Settings.TempWindowMin) };
            var chart = liveChart;
            cWin.SelectedIndexChanged += (s, e) =>
            {
                Settings.TempWindowMin = windows[cWin.SelectedIndex]; Settings.Save();
                chart.Window = TimeSpan.FromMinutes(Settings.TempWindowMin);
                UpdateLive(d);
            };
            card.Controls.Add(new Row { MinHeight = 36 }.Add(liveStats, -1).Add(cInt, 130).Add(cWin, 110));
            card.Controls.Add(liveChart);
            page.Controls.Add(card);
            liveSig = d.Signature;
            UpdateLive(d);
        }

        // ---------------------------------------------------------------- Stress test
        string cpuInfoName;
        CpuSensors cpuSensors;
        GpuSensors gpuSensors;
        bool sensorsLoading;
        const double CpuTempLimit = 95, GpuTempLimit = 90;

        static string Dur(TimeSpan t) { return (t.TotalHours >= 1 ? (int)t.TotalHours + ":" : "") + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00"); }

        const string GpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
        static bool PrefersDiscreteGpu()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(GpuPrefKey)) return k != null && Convert.ToString(k.GetValue(Application.ExecutablePath)).Contains("GpuPreference=2"); }
            catch { return false; }
        }
        static void SetDiscreteGpu(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(GpuPrefKey))
                    if (on) k.SetValue(Application.ExecutablePath, "GpuPreference=2;"); else k.DeleteValue(Application.ExecutablePath, false);
            }
            catch { }
        }

        string gpuInfoName;

        void BuildStress(string kind)
        {
            BeginPage();
            if (Demo.On && cpuSensors == null)
            {
                cpuSensors = Demo.Cpu();
                cpuInfoName = cpuSensors.Info.Name;
            }
            if (cpuSensors == null && !sensorsLoading)
            {
                sensorsLoading = true;
                Task.Run(() => new KeyValuePair<CpuSensors, GpuSensors>(new CpuSensors(), new GpuSensors())).ContinueWith(t => BeginInvoke(new Action(() =>
                {
                    sensorsLoading = false;
                    if (t.IsFaulted) return;
                    cpuSensors = t.Result.Key; gpuSensors = t.Result.Value;
                    cpuInfoName = cpuSensors.Info.Name + "  ·  " + L.F("ядер {0}, потоков {1}", cpuSensors.Info.Cores, cpuSensors.Info.Threads);
                    if ((section == "cpu" || section == "gpu") && !busy) SelectSection(section);
                })));
            }

            page.Controls.Add(new Banner(BannerKind.Warning, L.T("Максимальная нагрузка"), kind == "cpu"
                ? L.F("Стресс-тест сильно нагревает процессор и разряжает батарею ноутбука. Следите за температурой: тест остановится сам при {0} °C. Ошибки вычислений означают нестабильность: перегрев, слишком сильный разгон или нехватку питания.", CpuTempLimit)
                : L.F("Стресс-тест сильно нагревает видеокарту и разряжает батарею ноутбука. Следите за температурой: тест остановится сам при {0} °C. Артефакты и сбросы драйвера означают нестабильность видеокарты или блока питания.", GpuTempLimit)));

            if (cpuSensors == null)
            {
                page.Controls.Add(new TextBlock(L.T("Подготовка датчиков…"), TStyle.Muted));
                EndPage();
                return;
            }
            if (kind == "cpu") BuildCpuStress();
            else { BuildGpuInfo(); BuildGpuStress(); }
            EndPage();
        }

        static ThemedCombo DurationCombo(out TimeSpan[] values)
        {
            values = new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.Zero };
            var c = new ThemedCombo(L.T("1 минута"), L.T("5 минут"), L.T("15 минут"), L.T("30 минут"), L.T("1 час"), L.T("до остановки"));
            c.SelectedIndex = 1;
            return c;
        }

        int stressRunning;
        void StressBusy(bool on)
        {
            stressRunning += on ? 1 : -1;
            SetBusy(stressRunning > 0);
        }

        void BuildCpuStress()
        {
            var s = cpuSensors;
            var card = Card(L.T("Стресс-тест процессора"), s.Info.Name);
            TimeSpan[] durs;
            var cDur = DurationCombo(out durs);
            int all = Math.Max(1, s.Info.Threads);
            int[] thr = all > 2 ? new[] { all, Math.Max(1, all / 2), 1 } : new[] { all, 1 };
            var cThr = new ThemedCombo(thr.Select(v => v == all ? L.F("все потоки ({0})", v) : v == 1 ? L.T("1 поток") : L.F("{0} потоков", v)).ToArray());
            cThr.SelectedIndex = 0;
            var start = new FlatButton(L.T("Запустить"), BtnKind.Primary, "");
            var stop = new FlatButton(L.T("Остановить"), BtnKind.Secondary) { Enabled = false };
            card.Controls.Add(new Row { MinHeight = 36 }.Add(new TextBlock(L.T("Длительность"), TStyle.Body) { Single = true }, 110).Add(cDur, -1)
                .Add(new Panel(), 6).Add(new TextBlock(L.T("Потоки"), TStyle.Body) { Single = true }, 64).Add(cThr, -1));
            card.Controls.Add(new Row { MinHeight = 38 }.Add(start, 160).Add(stop, 130).Add(new Panel(), -1));
            var prog = new ProgressLine();
            var status = new TextBlock(L.T("Готов к тесту"), TStyle.Muted) { Single = true };
            card.Controls.Add(prog);
            card.Controls.Add(status);

            var grid = new TileGrid { MinTile = 150, TileH = 92 };
            var tLoad = new StatTile(L.T("Загрузка"), "—");
            var tClock = new StatTile(L.T("Частота"), "—", s.Info.MaxMHz > 0 ? L.F("базовая {0} ГГц", (s.Info.MaxMHz / 1000.0).ToString("0.00")) : null);
            var tTemp = new StatTile(L.T("Температура"), "—", L.T("датчик недоступен"));
            var tPerf = new StatTile(L.T("Быстродействие"), "—", "GFLOPS (double)");
            var tErr = new StatTile(L.T("Ошибки вычислений"), "—");
            var tTime = new StatTile(L.T("Время"), "—");
            grid.Controls.AddRange(new Control[] { tLoad, tClock, tTemp, tPerf, tErr, tTime });
            card.Controls.Add(grid);

            double? t0 = Demo.On ? 78 : s.Temperature();
            var chart = new TempChart { Window = TimeSpan.FromMinutes(5) };
            if (t0.HasValue) { chart.Warn = 85; chart.Bad = CpuTempLimit; }
            else { chart.Warn = double.NaN; chart.Bad = double.NaN; chart.Unit = " " + L.T("МГц"); chart.Axis = ""; }
            card.Controls.Add(new TextBlock(t0.HasValue ? L.T("Температура процессора") : L.T("Частота процессора (датчик температуры недоступен)"), TStyle.Bold) { Single = true });
            card.Controls.Add(chart);
            page.Controls.Add(card);

            CpuStress run = null;
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            var clock = new Stopwatch();
            TimeSpan limit = TimeSpan.Zero;
            long lastIter = 0; double lastT = 0;
            Action<string> finish = null;
            timer.Tick += (o, e) =>
            {
                if (run == null) return;
                double load = s.Load(), mhz = s.ClockMHz();
                double? temp = s.Temperature();
                double now = clock.Elapsed.TotalSeconds;
                long it = run.Iterations;
                double gflops = now > lastT ? (it - lastIter) * CpuStress.FlopsPerIteration / (now - lastT) / 1e9 : 0;
                lastIter = it; lastT = now;
                tLoad.Set(load.ToString("0") + " %", L.F("{0} потоков теста", run.ThreadCount));
                if (mhz > 0) tClock.Set(L.F("{0} ГГц", (mhz / 1000).ToString("0.00")), s.Info.MaxMHz > 0 ? L.F("базовая {0} ГГц", (s.Info.MaxMHz / 1000.0).ToString("0.00")) : null);
                if (temp.HasValue)
                {
                    tTemp.Set(temp.Value.ToString("0") + " °C", L.T("датчик ACPI"));
                    tTemp.ValueColor = () => temp >= 90 ? Theme.P.Danger : temp >= 80 ? Theme.P.Warning : Theme.P.Success;
                }
                tPerf.Set(gflops.ToString("0.0"), "GFLOPS (double)");
                long errs = run.Errors;
                tErr.Set(errs.ToString("N0"), errs == 0 ? L.T("стабильно") : L.T("НЕСТАБИЛЬНО"));
                tErr.ValueColor = () => errs == 0 ? Theme.P.Success : Theme.P.Danger;
                tTime.Set(Dur(clock.Elapsed), limit > TimeSpan.Zero ? L.F("из {0}", Dur(limit)) : L.T("до остановки"));
                double? point = t0.HasValue ? temp : (mhz > 0 ? mhz : (double?)null);
                if (point.HasValue) { chart.Points.Add(new TempPoint { T = DateTime.Now, V = point.Value }); chart.Invalidate(); }
                if (limit > TimeSpan.Zero)
                {
                    prog.Value = Math.Min(1, clock.Elapsed.TotalSeconds / limit.TotalSeconds);
                    if (clock.Elapsed >= limit) { finish(L.T("Тест завершён")); return; }
                }
                status.Text = L.F("Идёт тест…  итераций {0}", it.ToString("N0"));
                if (temp >= CpuTempLimit) finish(L.F("Остановлено: процессор нагрелся до {0} °C", temp.Value.ToString("0")));
            };
            finish = reason =>
            {
                if (run == null) return;
                var r = run; run = null;
                timer.Stop();
                r.Stop();
                clock.Stop();
                prog.Busy = false;
                start.Enabled = true; stop.Enabled = false; cDur.Enabled = cThr.Enabled = true;
                status.Text = reason + "  ·  " + Dur(clock.Elapsed) + "  ·  " + L.F("ошибок вычислений: {0}", r.Errors.ToString("N0"));
                StressBusy(false);
                if (reason.StartsWith(L.T("Остановлено:")))
                    FxMessage.Show(this, reason + L.T(". Проверьте охлаждение (пыль, термопаста, подставка для ноутбука)."), L.T("Стресс-тест"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (r.Errors > 0)
                    FxMessage.Show(this, L.F("Процессор выдал {0} неверных результатов — система нестабильна. Уберите разгон / undervolt, проверьте охлаждение и питание.", r.Errors.ToString("N0")),
                        L.T("Стресс-тест"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            start.Click += (o, e) =>
            {
                if (run != null) return;
                limit = durs[cDur.SelectedIndex];
                chart.Points.Clear();
                Telemetry.Count("action.cpu_stress");
                run = new CpuStress();
                run.Start(thr[cThr.SelectedIndex]);
                clock.Restart(); lastIter = 0; lastT = 0;
                s.Load();
                StressBusy(true);
                start.Enabled = false; stop.Enabled = true; cDur.Enabled = cThr.Enabled = false;
                prog.Value = 0; prog.Busy = limit == TimeSpan.Zero;
                status.Text = L.T("Запуск…");
                timer.Start();
            };
            stop.Click += (o, e) => finish(L.T("Остановлено вручную"));
            card.Disposed += (o, e) => { if (run != null) { run.Stop(); run = null; } timer.Dispose(); };
            if (Demo.On)
            {
                // screenshot: a test that has been running for 4 min 12 s
                tLoad.Set("100 %", L.F("{0} потоков теста", 16));
                tClock.Set(L.F("{0} ГГц", 4.85.ToString("0.00")), L.F("базовая {0} ГГц", 4.20.ToString("0.00")));
                tTemp.Set("78 °C", L.T("датчик ACPI")); tTemp.ValueColor = () => Theme.P.Success;
                tPerf.Set("214.6", "GFLOPS (double)");
                tErr.Set("0", L.T("стабильно")); tErr.ValueColor = () => Theme.P.Success;
                tTime.Set(Dur(TimeSpan.FromSeconds(252)), L.F("из {0}", Dur(TimeSpan.FromMinutes(5))));
                prog.Value = 252 / 300.0;
                status.Text = L.F("Идёт тест…  итераций {0}", 18406221L.ToString("N0"));
                start.Enabled = false; stop.Enabled = true; cDur.Enabled = cThr.Enabled = false;
                chart.Points.AddRange(Demo.Curve(252, 46, 78, 1.6, 1));
            }
        }

        void BuildGpuStress()
        {
            var gs = gpuSensors;
            var card = Card(L.T("Стресс-тест видеокарты"), Demo.On ? Demo.GpuName : gs != null && gs.HasNvml ? gs.NvName : L.T("Нагрузка через OpenGL — работает с видеокартами NVIDIA, AMD и Intel"));
            TimeSpan[] durs;
            var cDur = DurationCombo(out durs);
            var start = new FlatButton(L.T("Запустить"), BtnKind.Primary, "");
            var stop = new FlatButton(L.T("Остановить"), BtnKind.Secondary) { Enabled = false };
            card.Controls.Add(new Row { MinHeight = 36 }.Add(new TextBlock(L.T("Длительность"), TStyle.Body) { Single = true }, 110).Add(cDur, -1));
            card.Controls.Add(new Row { MinHeight = 38 }.Add(start, 160).Add(stop, 130).Add(new Panel(), -1));
            var prog = new ProgressLine();
            var status = new TextBlock(L.T("Готов к тесту"), TStyle.Muted) { Single = true };
            card.Controls.Add(prog);
            card.Controls.Add(status);
            var view = new GpuStressView();
            card.Controls.Add(view);

            var grid = new TileGrid { MinTile = 150, TileH = 92 };
            var tFps = new StatTile(L.T("Кадров в секунду"), "—");
            var tLoad = new StatTile(L.T("Загрузка GPU"), "—");
            var tTemp = new StatTile(L.T("Температура"), "—", gs != null && gs.HasNvml ? "NVIDIA" : L.T("только для NVIDIA"));
            var tClock = new StatTile(L.T("Частота ядра"), "—");
            var tPower = new StatTile(L.T("Потребление"), "—");
            var tWork = new StatTile(L.T("Быстродействие"), "—", L.T("Гпикс·итераций/с"));
            grid.Controls.AddRange(new Control[] { tFps, tLoad, tTemp, tClock, tPower, tWork });
            card.Controls.Add(grid);

            bool hasTemp = Demo.On || (gs != null && gs.HasNvml && gs.NvTemp().HasValue);
            var chart = new TempChart { Window = TimeSpan.FromMinutes(5) };
            if (hasTemp) { chart.Warn = 83; chart.Bad = GpuTempLimit; }
            else { chart.Warn = double.NaN; chart.Bad = double.NaN; chart.Unit = " %"; chart.Axis = "%"; }
            card.Controls.Add(new TextBlock(hasTemp ? L.T("Температура видеокарты") : L.T("Загрузка видеокарты"), TStyle.Bold) { Single = true });
            card.Controls.Add(chart);

            var pref = new Toggle(L.T("Использовать мощную (дискретную) видеокарту на ноутбуках — нужен перезапуск программы"), PrefersDiscreteGpu());
            pref.CheckedChanged += (o, e) => SetDiscreteGpu(pref.Checked);
            card.Controls.Add(pref);
            page.Controls.Add(card);

            bool running = false;
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            var clock = new Stopwatch();
            TimeSpan limit = TimeSpan.Zero;
            Action<string> finish = null;
            timer.Tick += (o, e) =>
            {
                if (!running) return;
                if (view.Error != null) { finish(L.T("Ошибка:") + " " + view.Error); return; }
                double? load = gs != null ? (gs.NvLoad() ?? gs.Load()) : null, temp = gs != null ? gs.NvTemp() : null,
                        mhz = gs != null ? gs.NvClock() : null, watt = gs != null ? gs.NvPower() : null;
                tFps.Set(view.Fps.ToString("0"), L.F("проходов за кадр: {0}", view.Passes));
                if (load.HasValue) tLoad.Set(load.Value.ToString("0") + " %", null);
                if (temp.HasValue)
                {
                    tTemp.Set(temp.Value.ToString("0") + " °C", "NVIDIA");
                    tTemp.ValueColor = () => temp >= 85 ? Theme.P.Danger : temp >= 78 ? Theme.P.Warning : Theme.P.Success;
                }
                if (mhz.HasValue) tClock.Set(L.F("{0} МГц", mhz.Value.ToString("0")), null);
                if (watt.HasValue) tPower.Set(L.F("{0} Вт", watt.Value.ToString("0")), null);
                tWork.Set(view.Work.ToString("0.0"), L.T("Гпикс·итераций/с"));
                double? point = hasTemp ? temp : load;
                if (point.HasValue) { chart.Points.Add(new TempPoint { T = DateTime.Now, V = point.Value }); chart.Invalidate(); }
                status.Text = L.F("Идёт тест на: {0}", view.Renderer ?? "…") + "  ·  " + Dur(clock.Elapsed);
                if (limit > TimeSpan.Zero)
                {
                    prog.Value = Math.Min(1, clock.Elapsed.TotalSeconds / limit.TotalSeconds);
                    if (clock.Elapsed >= limit) { finish(L.T("Тест завершён")); return; }
                }
                if (temp >= GpuTempLimit) finish(L.F("Остановлено: видеокарта нагрелась до {0} °C", temp.Value.ToString("0")));
            };
            finish = reason =>
            {
                if (!running) return;
                running = false;
                timer.Stop();
                view.StopRender();
                clock.Stop();
                prog.Busy = false;
                start.Enabled = true; stop.Enabled = false; cDur.Enabled = true;
                status.Text = reason + "  ·  " + Dur(clock.Elapsed) + (view.Renderer != null ? "  ·  " + view.Renderer : "");
                StressBusy(false);
                if (reason.StartsWith(L.T("Остановлено:")))
                    FxMessage.Show(this, reason + L.T(". Проверьте охлаждение видеокарты."), L.T("Стресс-тест"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (reason.StartsWith(L.T("Ошибка")))
                    FxMessage.Show(this, reason, L.T("Стресс-тест видеокарты"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            start.Click += (o, e) =>
            {
                if (running) return;
                limit = durs[cDur.SelectedIndex];
                chart.Points.Clear();
                running = true;
                Telemetry.Count("action.gpu_stress");
                view.StartRender();
                clock.Restart();
                StressBusy(true);
                start.Enabled = false; stop.Enabled = true; cDur.Enabled = false;
                prog.Value = 0; prog.Busy = limit == TimeSpan.Zero;
                status.Text = L.T("Запуск…");
                timer.Start();
            };
            stop.Click += (o, e) => finish(L.T("Остановлено вручную"));
            card.Disposed += (o, e) => { running = false; view.StopRender(); timer.Dispose(); };
            if (Demo.On)
            {
                tFps.Set("143", L.F("проходов за кадр: {0}", 24));
                tLoad.Set("99 %", null);
                tTemp.Set("71 °C", "NVIDIA"); tTemp.ValueColor = () => Theme.P.Success;
                tClock.Set(L.F("{0} МГц", 2745), null);
                tPower.Set(L.F("{0} Вт", 198), null);
                tWork.Set("412.8", L.T("Гпикс·итераций/с"));
                prog.Value = 188 / 300.0;
                status.Text = L.F("Идёт тест на: {0}", Demo.GpuName + "/PCIe/SSE2") + "  ·  " + Dur(TimeSpan.FromSeconds(188));
                start.Enabled = false; stop.Enabled = true; cDur.Enabled = false;
                chart.Points.AddRange(Demo.Curve(188, 38, 71, 1.2, 2));
            }
        }

        // ---------------------------------------------------------------- RAM
        RamInfo ramInfo;
        bool ramLoading;
        RamUsageView ramUsage;
        System.Windows.Forms.Timer ramTimer;
        CancellationTokenSource ramCts;
        RamTestResult ramLast;
        string ramLog;

        void BuildRam()
        {
            BeginPage();
            if (ramInfo == null && !ramLoading)
            {
                ramLoading = true;
                Task.Run(() => RamService.Load()).ContinueWith(t => BeginInvoke(new Action(() =>
                {
                    ramLoading = false;
                    ramInfo = t.IsFaulted ? new RamInfo() : t.Result;
                    if (section == "ram" && !busy) SelectSection("ram");
                })));
            }

            // usage
            var usage = Card(L.T("Использование"));
            ramUsage = new RamUsageView();
            ramUsage.Set(RamService.Status());
            usage.Controls.Add(ramUsage);
            page.Controls.Add(usage);
            if (ramTimer == null)
            {
                ramTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                ramTimer.Tick += (s, e) =>
                {
                    if (section != "ram" || !Visible || ramUsage == null || ramUsage.IsDisposed) { ramTimer.Stop(); return; }
                    ramUsage.Set(RamService.Status());
                };
            }
            ramTimer.Start();

            // modules
            var info = ramInfo;
            var mods = FoldCard("ram.modules", L.T("Модули памяти"), info == null ? L.T("Загрузка…") : info.Modules.Count == 0 ? L.T("Windows не сообщила сведения о модулях.") : info.Summary);
            if (info != null && info.Modules.Count > 0)
            {
                var table = new SimpleTable(new[] { L.T("Слот"), L.T("Объём"), L.T("Тип"), L.T("Частота"), L.T("Производитель"), L.T("Модель") }, new[] { -3, 70, 124, -2, -2, -4 });
                foreach (var m in info.Modules)
                    table.Rows.Add(new[]
                    {
                        m.Slot.Length > 0 && m.Bank.Length > 0 && m.Bank != m.Slot ? m.Bank + " · " + m.Slot : m.Slot.Length > 0 ? m.Slot : m.Bank,
                        DiskService.FormatSize(m.Capacity),
                        (m.Type + " " + m.FormFactor).Trim(),
                        m.ConfiguredSpeed > 0 ? L.F("{0} МТ/с", m.ConfiguredSpeed) + (m.Speed > m.ConfiguredSpeed ? L.F(" (до {0})", m.Speed) : "") : m.Speed > 0 ? L.F("{0} МТ/с", m.Speed) : "—",
                        m.Manufacturer.Length > 0 ? m.Manufacturer : "—",
                        m.PartNumber.Length > 0 ? m.PartNumber : "—",
                    });
                mods.Controls.Add(table);
                var st = RamService.Status();
                var notes = new List<string>();
                if (info.Slots > info.Modules.Count) notes.Add(L.F("Свободных слотов: {0}.", info.Slots - info.Modules.Count));
                if (info.MaxCapacity > 0) notes.Add(L.F("Максимум, который поддерживает плата: {0}.", DiskService.FormatSize(info.MaxCapacity)));
                if (info.Installed > st.Total) notes.Add(L.F("Зарезервировано оборудованием (встроенная графика, прошивка): {0}.", DiskService.FormatSize(info.Installed - st.Total)));
                if (info.Modules.Any(m => m.Speed > 0 && m.ConfiguredSpeed > 0 && m.ConfiguredSpeed < m.Speed))
                    notes.Add(L.T("Память работает ниже своей номинальной частоты — возможно, в BIOS не включён профиль XMP / EXPO (или так ограничивает процессор / ноутбук)."));
                if (notes.Count > 0) mods.Controls.Add(new TextBlock(string.Join("\n", notes), TStyle.Muted));
            }
            page.Controls.Add(mods);

            BuildRamTest();

            // Windows Memory Diagnostic
            var wd = Card(L.T("Полная проверка при перезагрузке"),
                L.T("Встроенное «Средство проверки памяти Windows» проверяет ВСЮ память до загрузки системы — так находятся ошибки и в той части, которую занимает Windows. Компьютер перезагрузится, проверка займёт 15–60 минут, результат появится здесь после входа в систему."));
            var last = new TextBlock(L.T("Ищу результат последней проверки…"), TStyle.Body);
            var run = new FlatButton(L.T("Запустить проверку Windows…"), BtnKind.Secondary, "");
            run.Click += (s, e) =>
            {
                if (!FxMessage.Confirm(this, L.T("Проверка памяти Windows"),
                    L.T("Откроется окно «Средство проверки памяти Windows». В нём выберите «Выполнить перезагрузку и проверку» или «Выполнить проверку при следующем включении». Перед перезагрузкой сохраните открытые документы."), L.T("Открыть средство"), false)) return;
                Telemetry.Count("action.mem_diag");
                try { RamService.StartWindowsDiagnostic(); }
                catch (Exception ex) { FxMessage.Show(this, ex.Message, L.T("Проверка памяти Windows"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            wd.Controls.Add(last);
            wd.Controls.Add(new Row { MinHeight = 38 }.Add(run, 280).Add(new Panel(), -1));
            page.Controls.Add(wd);
            Task.Run(() => RamService.LastDiagnostic()).ContinueWith(t => BeginInvoke(new Action(() =>
            {
                if (last.IsDisposed) return;
                var r = t.IsFaulted ? null : t.Result;
                if (r == null) last.Text = L.T("Проверка памяти Windows на этом компьютере ещё не запускалась (или журнал уже очищен).");
                else
                {
                    last.Text = L.T("Последняя проверка:") + " " + r.When.ToString("dd.MM.yyyy HH:mm") + " — " + (r.Errors ? L.T("ОБНАРУЖЕНЫ ОШИБКИ.") : L.T("ошибок нет.")) + " " + r.Message;
                    last.ColorFn = r.Errors ? (Func<Color>)(() => Theme.P.Danger) : () => Theme.P.Success;
                }
                page.PerformLayout();
            })));
            EndPage();
        }

        void BuildRamTest()
        {
            var st = RamService.Status();
            var card = Card(L.T("Быстрая проверка в Windows"),
                L.T("Записывает в память шаблоны (нули, единицы, шахматка, адреса, бегущая единица, случайные данные) во всех потоках процессора и сверяет прочитанное. Проверяется только свободная память, которую Windows выдаст программе. Во время теста компьютер может подтормаживать — закройте тяжёлые программы."));
            double[] fracs = { 0.5, 0.7, 0.85 };
            var cSize = new ThemedCombo(fracs.Select(f => L.F("{0} % свободной — {1}", (int)(f * 100), DiskService.FormatSize((ulong)(st.Available * f)))).ToArray());
            cSize.SelectedIndex = 1;
            int[] passesOpt = { 1, 2, 3, 5 };
            var cPass = new ThemedCombo(passesOpt.Select(v => v == 1 ? L.F("{0} проход", v) : v < 5 ? L.F("{0} прохода", v) : L.F("{0} проходов", v)).ToArray());
            cPass.SelectedIndex = 0;
            card.Controls.Add(new Row { MinHeight = 36 }
                .Add(new TextBlock(L.T("Объём"), TStyle.Body) { Single = true }, 70).Add(cSize, -1)
                .Add(new Panel(), 6).Add(new TextBlock(L.T("Проходы"), TStyle.Body) { Single = true }, 76).Add(cPass, 140));
            var start = new FlatButton(L.T("Начать проверку"), BtnKind.Primary, "");
            var stop = new FlatButton(L.T("Остановить"), BtnKind.Secondary) { Enabled = false };
            card.Controls.Add(new Row { MinHeight = 38 }.Add(start, 190).Add(stop, 130).Add(new Panel(), -1));
            var prog = new ProgressLine();
            var status = new TextBlock(ramLast == null ? L.T("Готов к проверке") : RamSummary(ramLast), TStyle.Muted) { Single = true };
            card.Controls.Add(prog);
            card.Controls.Add(status);

            var grid = new TileGrid { MinTile = 160, TileH = 92 };
            var tSize = new StatTile(L.T("Проверено"), "—");
            var tErr = new StatTile(L.T("Ошибок"), "—");
            var tSpeed = new StatTile(L.T("Скорость"), "—", L.T("запись + чтение"));
            var tTime = new StatTile(L.T("Время"), "—");
            grid.Controls.AddRange(new Control[] { tSize, tErr, tSpeed, tTime });
            card.Controls.Add(grid);
            var log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
                BorderStyle = BorderStyle.None, Height = Theme.Dp(140), WordWrap = true,
                Text = ramLog ?? L.T("Здесь появится журнал проверки."),
            };
            card.Controls.Add(log);
            page.Controls.Add(card);

            Action<RamTestResult> show = r =>
            {
                tSize.Set(DiskService.FormatSize((ulong)r.Bytes), L.F("{0} × {1} шаблонов", r.Passes, r.Patterns));
                tErr.Set(r.Cancelled && r.Errors == 0 ? "—" : r.Errors.ToString("N0"), r.Errors == 0 ? (r.Cancelled ? L.T("проверка прервана") : L.T("память в порядке")) : L.T("память неисправна"));
                tErr.ValueColor = () => r.Errors > 0 ? Theme.P.Danger : r.Cancelled ? Theme.P.Muted : Theme.P.Success;
                tSpeed.Set(r.GBps > 0 ? L.F("{0} ГБ/с", r.GBps.ToString("0.0")) : "—", L.T("запись + чтение"));
                tTime.Set(r.Time.TotalMinutes >= 1 ? L.F("{0} мин {1} с", (int)r.Time.TotalMinutes, r.Time.Seconds) : L.F("{0} с", r.Time.Seconds), null);
                prog.Value = r.Cancelled ? 0 : 1;
            };
            if (ramLast != null) show(ramLast);

            stop.Click += (s, e) => { if (ramCts != null) { ramCts.Cancel(); status.Text = L.T("Останавливаю…"); } };
            start.Click += async (s, e) =>
            {
                if (busy) return;
                long bytes = (long)(RamService.Status().Available * fracs[cSize.SelectedIndex]) / (1L << 20) * (1L << 20);
                int passes = passesOpt[cPass.SelectedIndex];
                SetBusy(true);
                start.Enabled = false; stop.Enabled = true; cSize.Enabled = cPass.Enabled = false;
                foreach (var t in new[] { tSize, tErr, tSpeed, tTime }) t.Set("—", null);
                tErr.ValueColor = null;
                log.Text = ""; ramLog = "";
                ramCts = new CancellationTokenSource();
                var token = ramCts.Token;
                var clock = Stopwatch.StartNew();
                long lastTick = -1000;
                Action<string> append = line => BeginInvoke(new Action(() =>
                {
                    string l = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine;
                    ramLog += l;
                    if (!log.IsDisposed) log.AppendText(l);
                }));
                Action<string, double> report = (phase, f) =>
                {
                    long now = clock.ElapsedMilliseconds;
                    if (now - lastTick < 100 && f < 1) return;
                    lastTick = now;
                    BeginInvoke(new Action(() =>
                    {
                        if (prog.IsDisposed) return;
                        prog.Value = f;
                        status.Text = phase + "…  " + (int)(f * 100) + " %";
                    }));
                };
                append(L.F("Начало проверки: {0}, проходов: {1}, потоков: {2}", DiskService.FormatSize((ulong)bytes), passes, Environment.ProcessorCount));
                RamTestResult res;
                Telemetry.Count("action.ram_test");
                try { res = await Task.Run(() => RamTest.Run(bytes, passes, report, append, token)); }
                catch (Exception ex) { res = null; append(L.T("ОШИБКА:") + " " + ex.Message); }
                await Task.Delay(150);
                ramCts = null;
                SetBusy(false);
                if (!start.IsDisposed) { start.Enabled = true; stop.Enabled = false; cSize.Enabled = cPass.Enabled = true; }
                if (res == null) { status.Text = L.T("Проверка не выполнена."); return; }
                ramLast = res;
                show(res);
                status.Text = RamSummary(res);
                append(RamSummary(res));
                if (!res.Cancelled)
                    FxMessage.Show(this, res.Errors == 0
                            ? L.F("Проверено {0} — ошибок не найдено.", DiskService.FormatSize((ulong)res.Bytes))
                            : L.F("Найдено ошибок: {0}. Память, вероятно, неисправна или нестабильна (разгон / XMP). Проверьте модули по одному и запустите полную проверку Windows.", res.Errors.ToString("N0")),
                        L.T("Проверка памяти"), MessageBoxButtons.OK, res.Errors == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            };
        }

        static string RamSummary(RamTestResult r)
        {
            return (r.Cancelled ? L.T("Прервано:") : L.T("Готово:")) + " " + DiskService.FormatSize((ulong)r.Bytes) + L.F(", ошибок {0}", r.Errors.ToString("N0")) +
                   (r.GBps > 0 ? ", " + L.F("{0} ГБ/с", r.GBps.ToString("0.0")) : "");
        }

        void OpenVolume(char letter)
        {
            Telemetry.Count("action.open_volume");
            try { VolumeOps.Open(letter); }
            catch (Exception ex)
            {
                FxMessage.Show(this, L.F("Не удалось открыть {0}:", letter) + "\n" + ex.Message, L.T("Проводник"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        async void PartitionAction(DiskInfo d, PartInfo p, string action)
        {
            if (busy || loading) return;
            if (action == "open") { OpenVolume(p.Letter); return; }

            string name = (p.Letter != '\0' ? p.Letter + ": " : "") +
                          (p.Label.Length > 0 ? "«" + p.Label + "» " : "") + "(" + (p.FileSystem.Length > 0 ? p.FileSystem + ", " : "") + p.Type + ", " +
                          DiskService.FormatSize(p.Size) + ")";
            Func<string> op;
            string title;
            if (action == "letter")
            {
                char newLetter;
                if (!FxMessage.AskLetter(this, p, out newLetter)) return;
                Telemetry.Count("action.change_letter");
                op = () => VolumeOps.ChangeLetter(d.Number, p, newLetter);
                title = L.T("Изменение буквы");
            }
            else if (action == "delete")
            {
                string text = L.F("Том {0} на диске {1} будет удалён вместе со всеми файлами. Освободившееся место станет нераспределённым.", name, d.Number);
                if (p.IsService)
                    text += "\n\n" + L.F("Это служебный раздел ({0}). Удаляйте его, только если диск больше не будет загрузочным или не нужен раздел восстановления.", p.Type);
                if (!FxMessage.Confirm(this, L.T("Удалить том?"), text, L.T("Удалить том"), true)) return;
                Telemetry.Count("action.delete_partition");
                op = () => VolumeOps.Delete(d, p);
                title = L.T("Удаление тома");
            }
            else return;

            SetBusy(true);
            string err;
            try { err = await Task.Run(op); }
            catch (Exception ex) { err = ex.Message; }
            SetBusy(false);
            if (err != null)
                FxMessage.Show(this, err, L.F("{0}: ошибка", title), MessageBoxButtons.OK, MessageBoxIcon.Error);
            RefreshDisks();
        }

        async void EjectDisk(DiskCard card)
        {
            if (busy || loading) return;
            var d = card.Disk;
            SetBusy(true);
            card.Ejecting = true;
            card.Invalidate();
            string err;
            Telemetry.Count("action.eject");
            try { err = await Task.Run(() => Ejector.Eject(d.Number)); }
            catch (Exception ex) { err = ex.Message; }
            card.Ejecting = false;
            SetBusy(false);
            if (err == null)
            {
                if (sel == d) sel = null;
                FxMessage.Show(this, L.F("«{0}» можно отключить от компьютера.", d.Model), L.T("Безопасное извлечение"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                if (!card.IsDisposed) card.Invalidate();
                FxMessage.Show(this, L.F("Не удалось извлечь «{0}».", d.Model) + "\n\n" + err, L.T("Безопасное извлечение"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshDisks();
        }

        void RestartApp()
        {
            if (busy)
            {
                FxMessage.Show(this, L.T("Дождитесь завершения текущей операции."), "Foxide Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Settings.Save();
            try
            {
                // the new instance inherits our elevation, so no second UAC prompt
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--wait-pid " + Process.GetCurrentProcess().Id) { UseShellExecute = false, WorkingDirectory = Application.StartupPath });
            }
            catch (Exception ex)
            {
                FxMessage.Show(this, L.T("Не удалось перезапустить:") + " " + ex.Message, "Foxide Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ExitApp();
        }

        void SetBusy(bool b)
        {
            busy = b;
            foreach (var n in nav.Values) n.Enabled = !b;
            foreach (var t in tabs) t.Enabled = !b;
            foreach (Control c in listStack.Controls) c.Enabled = !b;
            btnRefresh.Enabled = !b && !loading;
        }

        // ---------------------------------------------------------------- Settings
        void BuildSettings()
        {
            BeginPage();

            var look = Card(L.T("Оформление"));
            var cTheme = new ThemedCombo(L.T("Как в системе"), L.T("Светлая"), L.T("Тёмная"));
            cTheme.SelectedIndex = Settings.ThemeMode;
            cTheme.SelectedIndexChanged += (s, e) =>
            {
                Settings.ThemeMode = cTheme.SelectedIndex; Settings.Save();
                SetTheme(Settings.ThemeMode);
            };
            look.Controls.Add(FormRow(L.T("Тема"), cTheme));
            var cLang = new ThemedCombo("English", "Русский");
            cLang.SelectedIndex = Settings.Language == "ru" ? 1 : 0;
            cLang.SelectedIndexChanged += (s, e) =>
            {
                string want = cLang.SelectedIndex == 1 ? "ru" : "en";
                if (want == Settings.Language) return;
                Settings.Language = want; Settings.Save();
                if (FxMessage.Confirm(this, want == "ru" ? "Язык" : "Language",
                        want == "ru" ? "Язык интерфейса изменится после перезапуска программы. Перезапустить сейчас?"
                                     : "The interface language will change after the program restarts. Restart now?",
                        want == "ru" ? "Перезапустить" : "Restart", false))
                    RestartApp();
            };
            look.Controls.Add(FormRow(L.T("Язык / Language"), cLang));
            page.Controls.Add(look);

            var speed = Card(L.T("Тест скорости"));
            var cSize = SizeCombo();
            var cSecs = SecsCombo();
            speed.Controls.Add(FormRow(L.T("Размер тестового файла"), cSize, 230));
            speed.Controls.Add(FormRow(L.T("Длительность тестов 4K"), cSecs, 230));
            page.Controls.Add(speed);

            var fmt = Card(L.T("Форматирование по умолчанию"), L.T("Эти значения подставляются на вкладке «Очистка» для новых разделов."));
            string[] fsIds = { "NTFS", "exFAT", "FAT32" };
            var cFs = new ThemedCombo(L.T("NTFS — диски для Windows"), L.T("exFAT — флешки, Mac, PS4 / PS5, Xbox One"), L.T("FAT32 — старые ПК, PS3, Xbox 360, Wii, ТВ, магнитолы"));
            cFs.SelectedIndex = Math.Max(0, Array.IndexOf(fsIds, Settings.DefaultFs));
            cFs.SelectedIndexChanged += (s, e) => { Settings.DefaultFs = fsIds[cFs.SelectedIndex]; Settings.Save(); };
            var cSt = new ThemedCombo("GPT", "MBR");
            cSt.SelectedIndex = Settings.DefaultStyle == "MBR" ? 1 : 0;
            cSt.SelectedIndexChanged += (s, e) => { Settings.DefaultStyle = (string)cSt.SelectedItem; Settings.Save(); };
            var tq = new Toggle(L.T("Быстрое форматирование"), Settings.QuickFormat);
            tq.CheckedChanged += (s, e) => { Settings.QuickFormat = tq.Checked; Settings.Save(); };
            fmt.Controls.Add(FormRow(L.T("Файловая система"), cFs, 230));
            fmt.Controls.Add(FormRow(L.T("Стиль разделов"), cSt, 230));
            fmt.Controls.Add(tq);
            page.Controls.Add(fmt);

            var lst = Card(L.T("Список дисков"));
            var tv = new Toggle(L.T("Показывать виртуальные диски (VHD, Storage Spaces)"), Settings.ShowVirtual);
            tv.CheckedChanged += (s, e) => { Settings.ShowVirtual = tv.Checked; Settings.Save(); UpdateCounts(); };
            var ta = new Toggle(L.T("Обновлять список при подключении и отключении устройств"), Settings.AutoRefresh);
            ta.CheckedChanged += (s, e) => { Settings.AutoRefresh = ta.Checked; Settings.Save(); };
            lst.Controls.Add(tv);
            lst.Controls.Add(ta);
            page.Controls.Add(lst);

            // ---- live temperature
            var tcard = Card(L.T("Температура в реальном времени"));
            var tl = new Toggle(L.T("Показывать график и обновлять температуру выбранного диска"), Settings.LiveTemp);
            tl.CheckedChanged += (s, e) =>
            {
                Settings.LiveTemp = tl.Checked; Settings.Save();
                if (tl.Checked) tempTimer.Start(); else tempTimer.Stop();
            };
            tcard.Controls.Add(tl);
            tcard.Controls.Add(new TextBlock(L.T("Если выключить, график на вкладке «Здоровье» скрывается и диск не опрашивается, пока вы смотрите его карточку."), TStyle.Small));
            page.Controls.Add(tcard);

            // ---- background mode & alerts
            var bg = Card(L.T("Фоновый режим и уведомления о перегреве"));
            var tAuto = new Toggle(L.T("Запускать вместе с Windows (в фоне, значок в трее)"), false) { Enabled = false };
            var autoState = new TextBlock(L.T("Проверяю…"), TStyle.Small);
            var tTray = new Toggle(L.T("При закрытии окна сворачивать в трей, а не выходить"), Settings.MinimizeToTray);
            var tAlerts = new Toggle(L.T("Уведомлять о перегреве дисков"), Settings.TempAlerts);
            int[] ssdT = { 50, 55, 60, 65, 70, 75, 80, 85 }, hddT = { 40, 45, 50, 55, 60, 65 }, every = { 10, 30, 60, 300 };
            var cSsd = new ThemedCombo(ssdT.Select(v => L.F("от {0} °C", v)).ToArray());
            cSsd.SelectedIndex = Math.Max(0, Array.IndexOf(ssdT, Settings.AlertSsd));
            var cHdd = new ThemedCombo(hddT.Select(v => L.F("от {0} °C", v)).ToArray());
            cHdd.SelectedIndex = Math.Max(0, Array.IndexOf(hddT, Settings.AlertHdd));
            var cEvery = new ThemedCombo(L.T("каждые 10 с"), L.T("каждые 30 с"), L.T("каждую минуту"), L.T("каждые 5 минут"));
            cEvery.SelectedIndex = Math.Max(0, Array.IndexOf(every, Settings.AlertIntervalSec));
            var tHdd = new Toggle(L.T("Проверять и HDD (запросы могут не давать им засыпать)"), Settings.AlertPollHdd);
            var test = new FlatButton(L.T("Показать пример уведомления"), BtnKind.Secondary, "");

            Action syncAlerts = () => { cSsd.Enabled = cHdd.Enabled = cEvery.Enabled = tHdd.Enabled = test.Enabled = Settings.TempAlerts; };
            tTray.CheckedChanged += (s, e) => { Settings.MinimizeToTray = tTray.Checked; Settings.Save(); InitTray(); };
            var tSens = new Toggle(L.T("Показывать температуры процессора, видеокарты и памяти в меню значка"), Settings.TraySensors);
            tSens.CheckedChanged += (s, e) => { Settings.TraySensors = tSens.Checked; Settings.Save(); };
            var tBat = new Toggle(L.T("Показывать заряд и здоровье аккумулятора в меню значка"), Settings.TrayBattery);
            tBat.CheckedChanged += (s, e) => { Settings.TrayBattery = tBat.Checked; Settings.Save(); };
            tAlerts.CheckedChanged += (s, e) => { Settings.TempAlerts = tAlerts.Checked; Settings.Save(); InitTray(); syncAlerts(); if (tAlerts.Checked) PollAlerts(); };
            cSsd.SelectedIndexChanged += (s, e) => { Settings.AlertSsd = ssdT[cSsd.SelectedIndex]; Settings.Save(); alertSent.Clear(); };
            cHdd.SelectedIndexChanged += (s, e) => { Settings.AlertHdd = hddT[cHdd.SelectedIndex]; Settings.Save(); alertSent.Clear(); };
            cEvery.SelectedIndexChanged += (s, e) => { Settings.AlertIntervalSec = every[cEvery.SelectedIndex]; Settings.Save(); alertTimer.Interval = Settings.AlertIntervalSec * 1000; };
            tHdd.CheckedChanged += (s, e) => { Settings.AlertPollHdd = tHdd.Checked; Settings.Save(); };
            test.Click += (s, e) =>
            {
                var d = sel ?? disks.FirstOrDefault();
                if (d == null) return;
                Notify(d, d.TempC ?? (d.IsRotating ? Settings.AlertHdd : Settings.AlertSsd), d.IsRotating ? Settings.AlertHdd : Settings.AlertSsd);
            };
            bool applying = false;
            tAuto.CheckedChanged += async (s, e) =>
            {
                if (applying) return;
                bool want = tAuto.Checked;
                tAuto.Enabled = false;
                autoState.Text = want ? L.T("Создаю задачу в Планировщике…") : L.T("Удаляю задачу…");
                string err = await Task.Run(() => want ? Autostart.Enable() : Autostart.Disable());
                if (err != null)
                {
                    applying = true; tAuto.Checked = !want; applying = false;
                    FxMessage.Show(this, err, L.T("Автозапуск"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else if (want && !Settings.MinimizeToTray) tTray.Checked = true;   // background start needs the tray
                autoState.Text = tAuto.Checked
                    ? L.F("Включено: задача «{0}» в Планировщике заданий, запуск через 15 с после входа, без запроса UAC. Если переместите FoxideHelper.exe — включите заново.", Autostart.TaskName)
                    : L.T("Выключено.");
                tAuto.Enabled = Program.IsAdmin;
                page.PerformLayout();
            };
            bg.Controls.Add(tAuto);
            bg.Controls.Add(autoState);
            bg.Controls.Add(tTray);
            bg.Controls.Add(tSens);
            if (BatteryService.HasBattery) bg.Controls.Add(tBat);
            bg.Controls.Add(tAlerts);
            bg.Controls.Add(FormRow(L.T("Порог для SSD / NVMe"), cSsd, 230));
            bg.Controls.Add(FormRow(L.T("Порог для HDD"), cHdd, 230));
            bg.Controls.Add(FormRow(L.T("Проверять температуру"), cEvery, 230));
            bg.Controls.Add(tHdd);
            bg.Controls.Add(new Row { MinHeight = 38 }.Add(test, 280).Add(new Panel(), -1));
            bg.Controls.Add(new TextBlock(L.T("Уведомление приходит в центр уведомлений Windows не чаще раза в 15 минут на диск и снова срабатывает, когда диск остынет на 3 °C ниже порога. Нажмите на уведомление, чтобы открыть этот диск."), TStyle.Small));
            page.Controls.Add(bg);
            syncAlerts();
            Task.Run(() => Autostart.IsEnabled()).ContinueWith(t => BeginInvoke(new Action(() =>
            {
                if (tAuto.IsDisposed) return;
                applying = true; tAuto.Checked = !t.IsFaulted && t.Result; applying = false;
                tAuto.Enabled = Program.IsAdmin;
                autoState.Text = !Program.IsAdmin ? L.T("Нужны права администратора.") : tAuto.Checked
                    ? L.F("Включено: задача «{0}» в Планировщике заданий.", Autostart.TaskName)
                    : L.T("Выключено.");
                page.PerformLayout();
            })));

            var about = Card(L.T("О программе"));
            var ver = typeof(MainForm).Assembly.GetName().Version;
            about.Controls.Add(new TextBlock("Foxide Helper " + ver.Major + "." + ver.Minor + "." + ver.Build, TStyle.Bold));
            about.Controls.Add(new TextBlock(L.T("Портативная утилита для дисков, памяти, процессора, видеокарты, аккумулятора и охлаждения. Установка не нужна — достаточно одного файла FoxideHelper.exe."), TStyle.Muted));
            about.Controls.Add(new TextBlock(L.T("Папка данных (настройки, отчёты):") + " " + (Settings.DataDir ?? L.T("не удалось создать")), TStyle.Small));
            var restart = new FlatButton(L.T("Перезапустить программу"), BtnKind.Secondary, "");
            restart.Click += (s, e) => RestartApp();
            var folder = new FlatButton(L.T("Открыть папку программы"), BtnKind.Ghost, "");
            folder.Click += (s, e) =>
            {
                try { Process.Start("explorer.exe", "/select,\"" + Application.ExecutablePath + "\""); } catch { }
            };
            var data = new FlatButton(L.T("Открыть папку данных"), BtnKind.Ghost, "\uE8B7");
            data.Click += (s, e) => { try { if (Settings.DataDir != null) Process.Start("explorer.exe", "\"" + Settings.DataDir + "\""); } catch { } };
            about.Controls.Add(new Row { MinHeight = 38 }.Add(restart, 250).Add(folder, 250).Add(data, 250).Add(new Panel(), -1));
            page.Controls.Add(about);

            var upd = Card(L.T("Обновления"));
            var tUpd = new Toggle(L.T("Проверять обновления при запуске"), Settings.CheckUpdates);
            tUpd.CheckedChanged += (s, e) => { Settings.CheckUpdates = tUpd.Checked; Settings.Save(); };
            var checkNow = new FlatButton(L.T("Проверить обновления"), BtnKind.Secondary, "");
            checkNow.Click += (s, e) => CheckForUpdates(true);
            updStatus = new TextBlock(updLast ?? L.F("Текущая версия {0}.", Updater.CurrentText), TStyle.Muted);
            upd.Controls.Add(tUpd);
            upd.Controls.Add(new Row { MinHeight = 38 }.Add(checkNow, 250).Add(new Panel(), -1));
            upd.Controls.Add(updStatus);
            upd.Controls.Add(new TextBlock(L.T("Обновления скачиваются из релизов GitHub-репозитория программы. Перед установкой проверяются размер и контрольная сумма SHA-256 файла."), TStyle.Small));
            page.Controls.Add(upd);

            var stats = Card(L.T("Статистика и отчёты о сбоях"));
            var tStats = new Toggle(L.T("Отправлять анонимную статистику и отчёты о сбоях"), Settings.SendStats);
            tStats.CheckedChanged += (s, e) => { Settings.SendStats = tStats.Checked; Settings.Save(); if (!tStats.Checked) Telemetry.DropPending(); };
            stats.Controls.Add(tStats);
            stats.Controls.Add(new TextBlock(L.T("Раз в день программа сообщает Foxide Studio версию программы и Windows, язык, число запусков, какими разделами и функциями пользовались, какие настройки включены и общие сведения о железе (сколько дисков каждого типа, производитель видеокарты, объём памяти, есть ли батарея), а при сбое — текст ошибки. Имени ПК, имени пользователя, моделей, серийных номеров и содержимого дисков в отчётах нет: копия программы отличается только случайным номером, созданным при первом запуске."), TStyle.Small));
            page.Controls.Add(stats);

            var support = Card(L.T("Поддержать проект"));
            support.Controls.Add(new TextBlock(L.T("Foxide Helper бесплатный и с открытым исходным кодом. Если программа вам помогла, поддержите разработку на Patreon."), TStyle.Muted));
            var bPat = new FlatButton(L.T("Поддержать на Patreon"), BtnKind.Primary, "");
            bPat.Click += (s, e) => { Telemetry.Count("action.patreon_settings"); OpenUrl(PatreonUrl); };
            var bGit = new FlatButton(L.T("Исходный код на GitHub"), BtnKind.Ghost, "");
            bGit.Click += (s, e) => { Telemetry.Count("action.github"); OpenUrl(RepoUrl); };
            support.Controls.Add(new Row { MinHeight = 38 }.Add(bPat, 250).Add(bGit, 250).Add(new Panel(), -1));
            page.Controls.Add(support);

            EndPage();
        }

        // ---------------------------------------------------------------- self-test hooks
        public void ForTest(string sectionId, int tabIndex)
        {
            if (sectionId != null) SelectSection(sectionId);
            if (tabIndex >= 0 && Array.IndexOf(SystemSections, sectionId) < 0) SelectTab(tabIndex);
        }

        public void ScrollPage(int y) { page.AutoScrollPosition = new Point(0, y); }

        public bool SelectDiskNumber(int n)
        {
            var d = Filtered().FirstOrDefault(x => x.Number == n);
            if (d == null) return false;
            BuildList(); ShowDisk(d);
            return true;
        }
    }
}
