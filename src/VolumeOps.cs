using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;

namespace FoxideDisk
{
    /// <summary>Per-volume operations through the Windows Storage Management API (same engine as Disk Management).</summary>
    public static class VolumeOps
    {
        const string NS = @"\\.\root\Microsoft\Windows\Storage";

        public static bool CanModify(DiskInfo d, PartInfo p, out string why)
        {
            why = null;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            char w = string.IsNullOrEmpty(win) ? 'C' : char.ToUpperInvariant(win[0]);
            if (p.IsSystem || p.IsBoot || char.ToUpperInvariant(p.Letter) == w)
            { why = L.T("Это раздел, с которого работает Windows."); return false; }
            if (d.IsProtected && p.IsService)
            { why = L.T("Служебный раздел системного диска нужен Windows для загрузки и восстановления."); return false; }
            return true;
        }

        public static void Open(char letter)
        {
            Process.Start("explorer.exe", letter + ":\\");
        }

        /// <summary>Letters not used by any volume (plus the volume's own current letter).</summary>
        public static List<char> FreeLetters(char current)
        {
            var busy = new HashSet<char>(DriveInfo.GetDrives().Select(x => char.ToUpperInvariant(x.Name[0])));
            var res = new List<char>();
            for (char c = 'D'; c <= 'Z'; c++)
                if (!busy.Contains(c) || c == char.ToUpperInvariant(current)) res.Add(c);
            return res;
        }

        static ManagementObject Find(int disk, int part)
        {
            var scope = new ManagementScope(NS);
            scope.Connect();
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT * FROM MSFT_Partition WHERE DiskNumber = " + disk + " AND PartitionNumber = " + part)))
                foreach (ManagementObject o in s.Get()) return o;
            return null;
        }

        static string Invoke(ManagementObject o, string method, Dictionary<string, object> args)
        {
            ManagementBaseObject inP = null;
            if (args != null)
            {
                inP = o.GetMethodParameters(method);
                foreach (var kv in args) inP[kv.Key] = kv.Value;
            }
            ManagementBaseObject outP = o.InvokeMethod(method, inP, null);
            uint rv = outP == null ? 0 : Convert.ToUInt32(outP["ReturnValue"], CultureInfo.InvariantCulture);
            if (rv == 0) return null;
            string msg = null;
            try
            {
                var ext = outP["ExtendedStatus"];
                var mo = ext as ManagementBaseObject;
                if (mo != null) msg = Convert.ToString(mo["Message"]);
                else if (ext != null)
                {
                    var t = ext.ToString();
                    int i = t.IndexOf("Message = \"", StringComparison.Ordinal);
                    if (i >= 0) { i += 11; int j = t.IndexOf('"', i); if (j > i) msg = t.Substring(i, j - i); }
                }
            }
            catch { }
            return L.F("{0} (код {1})", string.IsNullOrEmpty(msg) ? Describe(rv) : msg.Trim(), rv);
        }

        static string Describe(uint rv)
        {
            switch (rv)
            {
                case 1: return L.T("Операция не поддерживается для этого раздела");
                case 2: return L.T("Неизвестная ошибка");
                case 4: return L.T("Ошибка выполнения");
                case 5: return L.T("Неверный параметр");
                case 40001: return L.T("Доступ запрещён");
                case 40002: return L.T("Недостаточно ресурсов");
                case 42002: return L.T("Буква или путь доступа уже используется");
                case 42012: return L.T("Это системный, загрузочный раздел или раздел подкачки");
                default: return L.T("Windows не выполнила операцию");
            }
        }

        /// <summary>Changes / removes the drive letter. newLetter '\0' = remove the letter.</summary>
        public static string ChangeLetter(int disk, PartInfo p, char newLetter)
        {
            var o = Find(disk, p.Number);
            if (o == null) return L.T("Раздел не найден — обновите список дисков.");
            char old = p.Letter;
            if (old != '\0')
            {
                var err = Invoke(o, "RemoveAccessPath", new Dictionary<string, object> { { "AccessPath", old + ":\\" } });
                if (err != null) return L.F("Не удалось убрать букву {0}: — {1}", old, err);
            }
            if (newLetter != '\0')
            {
                var err = Invoke(o, "AddAccessPath", new Dictionary<string, object> { { "AccessPath", newLetter + ":\\" } });
                if (err != null)
                {
                    if (old != '\0')   // put the old letter back so the volume does not "disappear"
                        try { Invoke(o, "AddAccessPath", new Dictionary<string, object> { { "AccessPath", old + ":\\" } }); } catch { }
                    return L.F("Не удалось назначить букву {0}: — {1}", newLetter, err);
                }
            }
            return null;
        }

        /// <summary>Gives a volume a drive letter; '\0' = let Windows pick a free one.</summary>
        public static string AssignLetter(int disk, int partNumber, char letter)
        {
            var o = Find(disk, partNumber);
            if (o == null) return L.T("Раздел не найден.");
            return letter != '\0'
                ? Invoke(o, "AddAccessPath", new Dictionary<string, object> { { "AccessPath", letter + ":\\" } })
                : Invoke(o, "AddAccessPath", new Dictionary<string, object> { { "AssignDriveLetter", true } });
        }

        /// <summary>Deletes the partition (and its volume). Service partitions that Windows refuses are removed via diskpart "override".</summary>
        public static string Delete(DiskInfo d, PartInfo p)
        {
            var o = Find(d.Number, p.Number);
            if (o == null) return L.T("Раздел не найден — обновите список дисков.");
            string err = Invoke(o, "DeleteObject", null);
            if (err == null) return null;
            if (!p.IsService) return err;

            // EFI / MSR / recovery / OEM: Windows protects them, diskpart can remove them with "override"
            string script = "select disk " + d.Number + "\r\nselect partition " + p.Number + "\r\ndelete partition override\r\nexit\r\n";
            string path = Path.Combine(Path.GetTempPath(), "foxidehelper_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(path, script, Encoding.ASCII);
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), "/s \"" + path + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                try { psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { }
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode == 0) return null;
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    return err + "\n\ndiskpart: " + (lines.Length > 0 ? lines[lines.Length - 1].Trim() : L.F("код {0}", proc.ExitCode));
                }
            }
            finally { try { File.Delete(path); } catch { } }
        }
    }
}
