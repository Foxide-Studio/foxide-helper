using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FoxideDisk
{
    /// <summary>
    /// Anonymous statistics and crash reports for Foxide Studio (switch: Settings → "Statistics and crash reports").
    /// Once a day: random install id, program and Windows version, interface language, launches and counters of the
    /// sections used. On a crash: exception type, message and stack, with the user / PC names masked.
    /// No PC name, user name, serial numbers or disk data are ever sent. Reports that failed are retried on the next start.
    /// </summary>
    public static class Telemetry
    {
        const string Endpoint = "https://hub.foxidestudio.com/api/foxidehelper/";
        const int MaxPendingCrashes = 10;
        static readonly object sync = new object();
        static readonly Dictionary<string, int> counters = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Called once the main window is up.</summary>
        /// <summary>Diagnostic runs (--shot / --dump) send nothing.</summary>
        public static bool Off;

        public static void Start()
        {
            if (Off || !Settings.SendStats) return;
            if (string.IsNullOrEmpty(Settings.InstallId)) Settings.InstallId = Guid.NewGuid().ToString("N");
            Settings.Launches++;
            LoadCounters();
            Settings.Save();
            Task.Run(() => { SendCrashes(); SendPing(); });
        }

        /// <summary>Usage counter, e.g. "section.ram" or "action.speedtest". Sent with the next daily report.</summary>
        public static void Count(string name)
        {
            if (Off || !Settings.SendStats) return;
            lock (sync)
            {
                int n; counters.TryGetValue(name, out n);
                counters[name] = n + 1;
                SaveCounters();
            }
        }

        /// <summary>Stores a crash to send it on the next start (the process may be dying right now).</summary>
        public static void SaveCrash(Exception ex, string kind)
        {
            try
            {
                if (ex == null || !Settings.SendStats) return;
                string dir = Dir("crashes");
                if (Directory.GetFiles(dir, "*.json").Length >= MaxPendingCrashes) return;
                var r = new Dictionary<string, object>
                {
                    { "id", Guid.NewGuid().ToString("N") }, { "kind", kind },
                    { "type", ex.GetType().FullName }, { "message", Mask(ex.Message) },
                    { "stack", Mask(ex.ToString()) }, { "time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                    { "version", Updater.CurrentText },
                };
                File.WriteAllText(Path.Combine(dir, r["id"] + ".json"), new JavaScriptSerializer().Serialize(r), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Statistics switched off: forget what has not been sent.</summary>
        public static void DropPending()
        {
            lock (sync) { counters.Clear(); SaveCounters(); }
            try { foreach (var f in Directory.GetFiles(Dir("crashes"), "*.json")) File.Delete(f); } catch { }
            Settings.Launches = 0; Settings.Save();
        }

        // ---------------------------------------------------------------- sending
        static void SendPing()
        {
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (Settings.StatsDay == today) return;
            Dictionary<string, int> snap;
            lock (sync) snap = new Dictionary<string, int>(counters);
            var r = Common();
            r["launches"] = Settings.Launches;
            r["counters"] = snap;
            if (!Post("stats/ping", r)) return;
            lock (sync)
            {
                foreach (var kv in snap)
                {
                    int n; counters.TryGetValue(kv.Key, out n);
                    if (n - kv.Value > 0) counters[kv.Key] = n - kv.Value; else counters.Remove(kv.Key);
                }
                SaveCounters();
            }
            Settings.StatsDay = today; Settings.Launches = 0; Settings.Save();
        }

        static void SendCrashes()
        {
            string[] files;
            try { files = Directory.GetFiles(Dir("crashes"), "*.json"); } catch { return; }
            foreach (var f in files)
            {
                try
                {
                    var rep = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                    if (rep == null) { File.Delete(f); continue; }
                    var r = Common();
                    foreach (var kv in rep) r[kv.Key] = kv.Value;
                    if (Post("crash/report", r)) File.Delete(f);
                }
                catch { }
            }
        }

        static Dictionary<string, object> Common()
        {
            return new Dictionary<string, object>
            {
                { "install", Settings.InstallId }, { "version", Updater.CurrentText }, { "lang", L.Lang },
                { "os", OsVersion() }, { "x64", Environment.Is64BitOperatingSystem },
                { "culture", CultureInfo.CurrentUICulture.Name },
            };
        }

        /// <summary>10.0.build.UBR from the registry (Environment.OSVersion depends on the manifest).</summary>
        static string OsVersion()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    object maj = k.GetValue("CurrentMajorVersionNumber"), min = k.GetValue("CurrentMinorVersionNumber"),
                           build = k.GetValue("CurrentBuildNumber"), ubr = k.GetValue("UBR");
                    if (maj != null && build != null) return maj + "." + (min ?? 0) + "." + build + "." + (ubr ?? 0);
                }
            }
            catch { }
            return Environment.OSVersion.Version.ToString();
        }

        static bool Post(string ep,Dictionary<string, object> body)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Endpoint + ep);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.UserAgent = "FoxideHelper/" + Updater.CurrentText;
                req.Timeout = 15000;
                byte[] data = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) return (int)resp.StatusCode == 200;
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                return hr != null && (int)hr.StatusCode == 400;   // malformed report: drop it instead of retrying forever
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- storage
        static string Dir(string name)
        {
            string baseDir = Settings.FilePath != null ? Path.GetDirectoryName(Settings.FilePath)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FoxideDisk");
            string d = Path.Combine(baseDir, "FoxideHelper." + name);
            Directory.CreateDirectory(d);
            return d;
        }

        static string CountersFile { get { return Path.Combine(Dir("stats"), "counters.txt"); } }

        static void LoadCounters()
        {
            lock (sync)
                try
                {
                    if (!File.Exists(CountersFile)) return;
                    foreach (var line in File.ReadAllLines(CountersFile))
                    {
                        int i = line.IndexOf('='), n;
                        if (i > 0 && int.TryParse(line.Substring(i + 1), out n)) counters[line.Substring(0, i)] = n;
                    }
                }
                catch { }
        }

        static void SaveCounters()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in counters) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(CountersFile, sb.ToString());
            }
            catch { }
        }

        /// <summary>User and PC names out of messages and paths.</summary>
        static string Mask(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            try
            {
                string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (prof.Length > 3) s = s.Replace(prof, "%USERPROFILE%");
                if (Environment.UserName.Length > 1) s = s.Replace(Environment.UserName, "%USER%");
                if (Environment.MachineName.Length > 1) s = s.Replace(Environment.MachineName, "%PC%");
            }
            catch { }
            return s.Length > 32000 ? s.Substring(0, 32000) : s;
        }
    }
}
