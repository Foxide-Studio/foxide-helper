using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>One release from GitHub.</summary>
    public class UpdateInfo
    {
        public string Version, Url, Sha256, SumUrl, Notes, Released, Page;
        public long Size;
        public Version Ver { get { return new Version(Version); } }
    }

    /// <summary>
    /// Updates from GitHub Releases of the public repository. Nothing is sent but a standard API request.
    /// A release is used when its tag is vX.Y.Z (not a draft or pre-release) and it has the asset FoxideHelper.exe.
    /// The download is checked against the asset size and the SHA-256 digest GitHub reports for it
    /// (or a FoxideHelper.exe.sha256 asset, for releases uploaded before GitHub started computing digests).
    /// </summary>
    public static class Updater
    {
        public const string Repo = "Foxide-Studio/foxide-helper";
        const string Api = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";
        const string AssetName = "FoxideHelper.exe";

        public static Version Current { get { return typeof(Updater).Assembly.GetName().Version; } }
        public static string CurrentText { get { var v = Current; return v.Major + "." + v.Minor + "." + v.Build; } }

        static Updater()
        {
            // .NET Framework 4.x may still default to TLS 1.0; GitHub needs 1.2+
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /// <summary>The newest release if it is newer than this program, otherwise null.</summary>
        public static UpdateInfo Check()
        {
            UpdateInfo best = null;
            foreach (var u in List())
                if (best == null || u.Ver > best.Ver) best = u;
            return best != null && best.Ver > Current ? best : null;
        }

        /// <summary>All usable releases.</summary>
        public static List<UpdateInfo> List()
        {
            var res = new List<UpdateInfo>();
            var arr = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(Get(Api)) as object[];
            if (arr == null) throw new InvalidDataException(L.T("Неверный ответ GitHub."));
            foreach (var o in arr)
            {
                var r = o as Dictionary<string, object>;
                if (r == null || Bool(r, "draft") || Bool(r, "prerelease")) continue;
                string tag = Str(r, "tag_name").TrimStart('v', 'V');
                Version v;
                if (!System.Version.TryParse(tag, out v) || tag.Split('.').Length != 3) continue;
                Dictionary<string, object> exe = null, sum = null;
                var assets = r.ContainsKey("assets") ? r["assets"] as object[] : null;
                foreach (var ao in assets ?? new object[0])
                {
                    var a = ao as Dictionary<string, object>;
                    if (a == null) continue;
                    if (string.Equals(Str(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) exe = a;
                    else if (string.Equals(Str(a, "name"), AssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) sum = a;
                }
                if (exe == null) continue;
                var u = new UpdateInfo
                {
                    Version = tag, Url = Str(exe, "browser_download_url"), Size = Convert.ToInt64(exe.ContainsKey("size") ? exe["size"] : 0L),
                    Notes = Str(r, "body").Trim(), Released = Str(r, "published_at"), Page = Str(r, "html_url"),
                };
                string dg = Str(exe, "digest");
                if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) u.Sha256 = dg.Substring(7);
                else if (sum != null) u.SumUrl = Str(sum, "browser_download_url");
                if (u.Size <= 0 || !u.Url.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
                res.Add(u);
            }
            return res;
        }

        /// <summary>Downloads the release next to the exe as FoxideHelper.exe.new and checks size and SHA-256.</summary>
        public static string Download(UpdateInfo u, Action<int> progress, CancellationToken ct)
        {
            string sha = u.Sha256;
            if (string.IsNullOrEmpty(sha) && u.SumUrl != null)
            {
                var t = Get(u.SumUrl).Trim();
                sha = t.Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries)[0];
            }
            if (sha == null || sha.Length != 64) throw new InvalidDataException(L.T("У релиза нет контрольной суммы — обновление отклонено."));
            string target = Application.ExecutablePath + ".new";
            var req = Request(u.Url);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write))
            using (var hash = SHA256.Create())
            {
                var buf = new byte[81920];
                long done = 0; int n, last = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n);
                    hash.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (done > u.Size) throw new InvalidDataException(L.T("Файл обновления больше, чем указано в релизе."));
                    int p = (int)(done * 100 / u.Size);
                    if (p != last && progress != null) { last = p; progress(p); }
                }
                hash.TransformFinalBlock(buf, 0, 0);
                if (done != u.Size) throw new InvalidDataException(L.T("Файл обновления скачан не полностью."));
                if (!string.Equals(Hex(hash.Hash), sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.T("Контрольная сумма файла не совпала — обновление отклонено."));
            }
            return target;
        }

        /// <summary>
        /// Swaps the exe (a running exe can be renamed on the same volume) and starts the new one.
        /// The caller must close the program right after. The old file is removed on the next start.
        /// </summary>
        public static void Install(string newFile)
        {
            string exe = Application.ExecutablePath, old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);
            try { File.Move(newFile, exe); }
            catch { File.Move(old, exe); throw; }
            Process.Start(new ProcessStartInfo(exe, "--wait-pid " + Process.GetCurrentProcess().Id)
                { UseShellExecute = false, WorkingDirectory = Application.StartupPath });
        }

        /// <summary>Leftovers of a previous update.</summary>
        public static void Cleanup()
        {
            foreach (var f in new[] { Application.ExecutablePath + ".old", Application.ExecutablePath + ".new" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        // ---------------------------------------------------------------- http
        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "FoxideHelper/" + CurrentText;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 60000;
            return req;
        }

        static string Get(string url)
        {
            try
            {
                using (var resp = (HttpWebResponse)Request(url).GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return rd.ReadToEnd();
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null && ((int)hr.StatusCode == 403 || (int)hr.StatusCode == 429))
                    throw new Exception(L.T("GitHub временно ограничил число запросов. Попробуйте через час."));
                if (hr != null && (int)hr.StatusCode == 404) throw new Exception(L.T("Репозиторий с обновлениями не найден."));
                throw new Exception(L.T("GitHub недоступен:") + " " + ex.Message);
            }
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }

        static bool Bool(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
