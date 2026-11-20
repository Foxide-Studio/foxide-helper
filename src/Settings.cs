using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FoxideDisk
{
    public static class Settings
    {
        public static int ThemeMode = 0;          // 0 system, 1 light, 2 dark
        public static int TestSizeMB = 1024;
        public static int RandomSeconds = 5;
        public static string DefaultFs = "NTFS";
        public static string DefaultStyle = "GPT";
        public static bool QuickFormat = true;
        public static bool ShowVirtual = false;
        public static bool AutoRefresh = true;
        public static bool SidebarCollapsed = false;
        public static bool LiveTemp = true;          // real-time chart / polling of the selected disk
        public static bool MinimizeToTray = false;   // closing the window keeps the app in the tray
        public static bool TempAlerts = true;        // background overheating notifications
        public static int AlertSsd = 70, AlertHdd = 55;
        public static int AlertIntervalSec = 30;
        public static bool AlertPollHdd = false;     // SMART polling can keep HDDs from spinning down
        public static int TempIntervalSec = 2;
        public static int TempWindowMin = 10;
        public static bool CheckUpdates = true;      // ask GitHub Releases for a newer version at start
        public static string Language = "en";        // "en" | "ru"; English by default
        public static bool SendStats = true;         // anonymous usage statistics and crash reports to Foxide Studio
        public static string InstallId = "";         // random id of this copy (not tied to the PC) — counts unique installs
        public static string StatsDay = "";          // last day (yyyy-MM-dd UTC) the statistics were sent
        public static int Launches = 0;              // launches since the last report
        public static string SkipVersion = "";       // "skip this version" in the update dialog
        public static readonly HashSet<string> CollapsedCards = new HashSet<string>();

        public static string FilePath;

        static string ResolvePath()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            string local = Path.Combine(exeDir, "FoxideHelper.ini");
            string old = Path.Combine(exeDir, "FoxideDisk.ini");   // settings of the app before the rename
            try { if (!File.Exists(local) && File.Exists(old)) File.Move(old, local); } catch { }
            try
            {
                using (new FileStream(local, FileMode.OpenOrCreate, FileAccess.ReadWrite)) { }
                return local;
            }
            catch
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FoxideDisk");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "FoxideHelper.ini");
            }
        }

        public static void Load()
        {
            try { FilePath = ResolvePath(); } catch { FilePath = null; return; }
            try
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                ThemeMode = Int(d, "Theme", ThemeMode, 0, 2);
                TestSizeMB = Int(d, "TestSizeMB", TestSizeMB, 64, 8192);
                RandomSeconds = Int(d, "RandomSeconds", RandomSeconds, 1, 60);
                DefaultFs = Str(d, "DefaultFs", DefaultFs);
                DefaultStyle = Str(d, "DefaultStyle", DefaultStyle);
                QuickFormat = Bool(d, "QuickFormat", QuickFormat);
                ShowVirtual = Bool(d, "ShowVirtual", ShowVirtual);
                AutoRefresh = Bool(d, "AutoRefresh", AutoRefresh);
                SidebarCollapsed = Bool(d, "SidebarCollapsed", SidebarCollapsed);
                TempIntervalSec = Int(d, "TempIntervalSec", TempIntervalSec, 1, 60);
                LiveTemp = Bool(d, "LiveTemp", LiveTemp);
                MinimizeToTray = Bool(d, "MinimizeToTray", MinimizeToTray);
                TempAlerts = Bool(d, "TempAlerts", TempAlerts);
                AlertSsd = Int(d, "AlertSsd", AlertSsd, 30, 100);
                AlertHdd = Int(d, "AlertHdd", AlertHdd, 30, 100);
                AlertIntervalSec = Int(d, "AlertIntervalSec", AlertIntervalSec, 5, 3600);
                AlertPollHdd = Bool(d, "AlertPollHdd", AlertPollHdd);
                TempWindowMin = Int(d, "TempWindowMin", TempWindowMin, 1, 30);
                CheckUpdates = Bool(d, "CheckUpdates", CheckUpdates);
                Language = Str(d, "Language", Language) == "ru" ? "ru" : "en";
                SendStats = Bool(d, "SendStats", SendStats);
                InstallId = Str(d, "InstallId", "");
                StatsDay = Str(d, "StatsDay", "");
                Launches = Int(d, "Launches", 0, 0, 100000);
                SkipVersion = Str(d, "SkipVersion", "");
                CollapsedCards.Clear();
                foreach (var k in Str(d, "CollapsedCards", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    CollapsedCards.Add(k.Trim());
            }
            catch { }
        }

        public static void Save()
        {
            if (FilePath == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("; Foxide Helper settings");
                sb.AppendLine("Theme=" + ThemeMode);
                sb.AppendLine("TestSizeMB=" + TestSizeMB);
                sb.AppendLine("RandomSeconds=" + RandomSeconds);
                sb.AppendLine("DefaultFs=" + DefaultFs);
                sb.AppendLine("DefaultStyle=" + DefaultStyle);
                sb.AppendLine("QuickFormat=" + QuickFormat);
                sb.AppendLine("ShowVirtual=" + ShowVirtual);
                sb.AppendLine("AutoRefresh=" + AutoRefresh);
                sb.AppendLine("SidebarCollapsed=" + SidebarCollapsed);
                sb.AppendLine("TempIntervalSec=" + TempIntervalSec);
                sb.AppendLine("LiveTemp=" + LiveTemp);
                sb.AppendLine("MinimizeToTray=" + MinimizeToTray);
                sb.AppendLine("TempAlerts=" + TempAlerts);
                sb.AppendLine("AlertSsd=" + AlertSsd);
                sb.AppendLine("AlertHdd=" + AlertHdd);
                sb.AppendLine("AlertIntervalSec=" + AlertIntervalSec);
                sb.AppendLine("AlertPollHdd=" + AlertPollHdd);
                sb.AppendLine("TempWindowMin=" + TempWindowMin);
                sb.AppendLine("CheckUpdates=" + CheckUpdates);
                sb.AppendLine("Language=" + Language);
                sb.AppendLine("SendStats=" + SendStats);
                sb.AppendLine("InstallId=" + InstallId);
                sb.AppendLine("StatsDay=" + StatsDay);
                sb.AppendLine("Launches=" + Launches);
                sb.AppendLine("SkipVersion=" + SkipVersion);
                sb.AppendLine("CollapsedCards=" + string.Join(",", CollapsedCards));
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static int Int(Dictionary<string, string> d, string k, int def, int min, int max)
        {
            string s; int v;
            if (d.TryGetValue(k, out s) && int.TryParse(s, out v)) return Math.Max(min, Math.Min(max, v));
            return def;
        }
        static string Str(Dictionary<string, string> d, string k, string def)
        {
            string s;
            return d.TryGetValue(k, out s) && s.Length > 0 ? s : def;
        }
        static bool Bool(Dictionary<string, string> d, string k, bool def)
        {
            string s; bool v;
            if (d.TryGetValue(k, out s) && bool.TryParse(s, out v)) return v;
            return def;
        }
    }
}
