using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FoxideDisk
{
    public class PartPlan
    {
        public long SizeMB;              // 0 = take all remaining space (only the last partition)
        public string FileSystem = "NTFS";
        public string Label = "";
        public char Letter;              // '\0' = assign automatically
    }

    public class WipeOptions
    {
        public string Style = "GPT";     // GPT | MBR
        public bool Quick = true;
        public bool ZeroFill;            // diskpart "clean all"
        public bool NoPartition;         // leave disk empty (initialized only)
        public List<PartPlan> Parts = new List<PartPlan> { new PartPlan() };

        public string Describe()
        {
            if (NoPartition) return L.T("Диск останется пустым (без разделов).");
            var parts = Parts.Select((p, i) =>
                (p.Letter != '\0' ? p.Letter + ": " : "") + p.FileSystem + " " +
                (i < Parts.Count - 1 ? DiskService.FormatSize((ulong)p.SizeMB << 20) : L.T("остаток")));
            return (Parts.Count == 1 ? L.T("Будет создан раздел: ") : L.F("Будет создано разделов: {0} — ", Parts.Count)) +
                   string.Join(", ", parts) + " (" + Style + ").";
        }
    }

    /// <summary>Removes every partition (including EFI / MSR / recovery / OEM) via diskpart and creates the planned volumes.</summary>
    public static class Wiper
    {
        public const long MinPartMB = 64;

        /// <summary>Space usable for partitions after GPT structures / MSR and alignment.</summary>
        public static long UsableMB(DiskInfo d, string style)
        {
            long mb = (long)(d.Size >> 20);
            return mb - (style == "MBR" ? 2 : 20);
        }

        public static long RestMB(DiskInfo d, WipeOptions o)
        {
            // the last partition always takes what is left, whatever size it had before
            return UsableMB(d, o.Style) - o.Parts.Take(Math.Max(0, o.Parts.Count - 1)).Sum(p => p.SizeMB);
        }

        /// <summary>Drive letters that are taken by volumes on other disks (this disk's letters are freed by the wipe).</summary>
        public static HashSet<char> BusyLetters(DiskInfo d)
        {
            var own = new HashSet<char>(d.Partitions.Where(p => p.Letter != '\0').Select(p => char.ToUpperInvariant(p.Letter)));
            var busy = new HashSet<char> { 'A', 'B' };
            foreach (var di in DriveInfo.GetDrives())
            {
                char c = char.ToUpperInvariant(di.Name[0]);
                if (!own.Contains(c)) busy.Add(c);
            }
            return busy;
        }

        public static string Validate(DiskInfo d, WipeOptions o, HashSet<char> busy = null)
        {
            if (d == null) return L.T("Диск не выбран.");
            if (d.IsProtected) return L.T("Это системный диск (на нём запущена Windows). Его нельзя очистить из работающей системы.");
            const ulong GB = 1024UL * 1024 * 1024;
            if (o.Style == "MBR" && d.Size > 2048 * GB) return L.T("MBR не поддерживает диски больше 2 ТБ. Выберите GPT.");
            if (o.NoPartition) return null;
            if (o.Parts.Count == 0) return L.T("Добавьте хотя бы один раздел.");
            if (o.Style == "MBR" && o.Parts.Count > 4) return L.T("На MBR можно создать не больше 4 разделов. Выберите GPT или уменьшите число разделов.");
            if (o.Parts.Count > 26) return L.T("Слишком много разделов.");

            for (int i = 0; i < o.Parts.Count - 1; i++)
                if (o.Parts[i].SizeMB < MinPartMB) return L.F("Раздел {0}: укажите размер не меньше {1} МБ.", i + 1, MinPartMB);
            long rest = RestMB(d, o);
            if (rest < MinPartMB)
                return L.F("Разделы не помещаются на диск: не хватает {0}. Уменьшите размеры (последний раздел получает оставшееся место).",
                           DiskService.FormatSize((ulong)Math.Max(0, MinPartMB - rest) << 20));

            for (int i = 0; i < o.Parts.Count; i++)
            {
                var p = o.Parts[i];
                long size = i == o.Parts.Count - 1 ? rest : p.SizeMB;
                int bps = d.LogicalSector >= 512 ? (int)d.LogicalSector : 512;
                if (p.FileSystem == "FAT32" && ((long)size << 20) > Fat32Format.MaxBytes(bps))
                    return L.F("Раздел {0}: FAT32 поддерживает тома до {1}. Уменьшите размер или выберите exFAT.", i + 1, DiskService.FormatSize((ulong)Fat32Format.MaxBytes(bps)));
                if (p.Letter != '\0')
                {
                    if (busy != null && busy.Contains(p.Letter)) return L.F("Буква {0}: уже занята другим диском.", p.Letter);
                    for (int j = 0; j < i; j++)
                        if (o.Parts[j].Letter == p.Letter) return L.F("Буква {0}: выбрана для двух разделов.", p.Letter);
                }
            }
            return null;
        }

        public static string SanitizeLabel(string label, string fs)
        {
            var sb = new StringBuilder();
            foreach (char c in label ?? "")
                if (c >= 32 && "\"*/:<>?\\|+,.;=[]".IndexOf(c) < 0) sb.Append(c);
            int max = fs == "NTFS" ? 32 : 11;
            var s = sb.ToString().Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        /// <summary>FAT32 partitions above Windows' 32 GB limit are formatted by Foxide Helper itself.</summary>
        public static bool IsBigFat(DiskInfo d, WipeOptions o, int i)
        {
            var p = o.Parts[i];
            if (p.FileSystem != "FAT32") return false;
            long mb = i == o.Parts.Count - 1 ? RestMB(d, o) : p.SizeMB;
            return (mb << 20) > Fat32Format.WindowsLimit;
        }

        public static string BuildScript(DiskInfo d, WipeOptions o)
        {
            var sb = new StringBuilder();
            sb.AppendLine("select disk " + d.Number.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("attributes disk clear readonly noerr");
            sb.AppendLine("online disk noerr");
            sb.AppendLine(o.ZeroFill ? "clean all" : "clean");
            sb.AppendLine("convert " + (o.Style == "MBR" ? "mbr" : "gpt") + " noerr");
            if (!o.NoPartition)
            {
                for (int i = 0; i < o.Parts.Count; i++)
                {
                    var p = o.Parts[i];
                    bool last = i == o.Parts.Count - 1;
                    bool big = IsBigFat(d, o, i);
                    sb.AppendLine("create partition primary" + (!last && p.SizeMB > 0 ? " size=" + p.SizeMB.ToString(CultureInfo.InvariantCulture) : "") +
                                  (big && o.Style == "MBR" ? " id=0c" : ""));
                    if (big) continue;   // formatted and lettered later by Fat32Format
                    string label = SanitizeLabel(p.Label, p.FileSystem);
                    sb.Append("format fs=" + p.FileSystem.ToLowerInvariant());
                    if (label.Length > 0) sb.Append(" label=\"" + label + "\"");
                    if (o.Quick) sb.Append(" quick");
                    sb.AppendLine();
                    sb.AppendLine(p.Letter != '\0' ? "assign letter=" + p.Letter : "assign");
                }
            }
            sb.AppendLine("exit");
            return sb.ToString();
        }

        /// <summary>Formats one large FAT32 partition created (unformatted) by diskpart, then gives it a letter.</summary>
        static int FormatBigFatPart(DiskInfo d, WipeOptions o, int i, Action<string> log)
        {
            var plan = o.Parts[i];
            PartInfo part = null;
            // the new volume shows up a moment after diskpart finishes
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var fresh = DiskService.Load().FirstOrDefault(x => x.Number == d.Number);
                if (fresh != null)
                {
                    var data = fresh.Partitions.Where(x => !x.IsService).OrderBy(x => x.Offset).ToList();
                    if (i < data.Count && data[i].VolumePath != null) { part = data[i]; break; }
                }
                System.Threading.Thread.Sleep(500);
            }
            if (part == null) { log(L.F("ОШИБКА: раздел {0} не найден после создания.", i + 1)); return 2; }
            log(L.F("Раздел {0}: FAT32 больше 32 ГБ — форматирует Foxide Helper ({1})", i + 1, DiskService.FormatSize(part.Size)));
            try { Fat32Format.FormatVolume(part.VolumePath, plan.Label, log); }
            catch (Exception ex) { log(L.F("ОШИБКА форматирования: {0}", ex.Message)); return 3; }
            System.Threading.Thread.Sleep(800);
            string err = VolumeOps.AssignLetter(d.Number, part.Number, plan.Letter);
            log(err == null ? (plan.Letter != '\0' ? L.F("Буква назначена: {0}:", plan.Letter) : L.T("Буква назначена автоматически")) : L.F("Не удалось назначить букву: {0}", err));
            return 0;
        }

        public class Step
        {
            public string Title, Script;
            public double Weight;
            public int BigFat = -1;
        }

        /// <summary>The wipe split into separate diskpart runs so progress can be shown per step.</summary>
        public static List<Step> BuildSteps(DiskInfo d, WipeOptions o)
        {
            string sel = "select disk " + d.Number.ToString(CultureInfo.InvariantCulture) + "\r\n";
            var steps = new List<Step>
            {
                new Step
                {
                    Title = o.ZeroFill ? L.T("Перезапись всего диска нулями") : L.T("Удаление всех разделов"),
                    Script = sel + "attributes disk clear readonly noerr\r\nonline disk noerr\r\n" + (o.ZeroFill ? "clean all" : "clean") + "\r\n",
                    Weight = o.ZeroFill ? 70 : 3,
                },
                new Step { Title = L.F("Создание разметки {0}", o.Style), Script = sel + "convert " + (o.Style == "MBR" ? "mbr" : "gpt") + " noerr\r\n", Weight = 1 },
            };
            if (o.NoPartition) return steps;
            for (int i = 0; i < o.Parts.Count; i++)
            {
                var p = o.Parts[i];
                bool last = i == o.Parts.Count - 1, big = IsBigFat(d, o, i);
                var sb = new StringBuilder(sel);
                sb.AppendLine("create partition primary" + (!last && p.SizeMB > 0 ? " size=" + p.SizeMB.ToString(CultureInfo.InvariantCulture) : "") +
                              (big && o.Style == "MBR" ? " id=0c" : ""));
                if (!big)
                {
                    string label = SanitizeLabel(p.Label, p.FileSystem);
                    sb.Append("format fs=" + p.FileSystem.ToLowerInvariant());
                    if (label.Length > 0) sb.Append(" label=\"" + label + "\"");
                    if (o.Quick) sb.Append(" quick");
                    sb.AppendLine();
                    sb.AppendLine(p.Letter != '\0' ? "assign letter=" + p.Letter : "assign");
                }
                string n = o.Parts.Count > 1 ? L.F("Раздел {0}: ", i + 1) : "";
                steps.Add(new Step
                {
                    Title = n + (big ? L.T("создание раздела") : (o.Quick ? L.F("создание и быстрое форматирование {0}", p.FileSystem) : L.F("создание и полное форматирование {0}", p.FileSystem))),
                    Script = sb.ToString(),
                    Weight = big ? 1 : o.Quick ? 3 : 40,
                });
                if (big) steps.Add(new Step { Title = n + L.T("форматирование FAT32"), BigFat = i, Weight = 4 });
            }
            return steps;
        }

        static readonly System.Text.RegularExpressions.Regex Percent =
            new System.Text.RegularExpressions.Regex(@"(\d{1,3})\s*%?\s*$");

        static bool IsNoise(string line)
        {
            string l = line.Trim();
            return l.Length == 0 || l.StartsWith("Microsoft DiskPart", StringComparison.OrdinalIgnoreCase) ||
                   l.StartsWith("(C)", StringComparison.Ordinal) || l.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase) ||
                   l.StartsWith("На компьютере", StringComparison.OrdinalIgnoreCase) || l.StartsWith("On computer", StringComparison.OrdinalIgnoreCase) ||
                   l.StartsWith("Завершение работы DiskPart", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Leaving DiskPart", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsProgressLine(string line, out int pct)
        {
            pct = -1;
            string l = line.ToLowerInvariant();
            if (l.IndexOf("процент", StringComparison.Ordinal) < 0 && l.IndexOf("percent", StringComparison.Ordinal) < 0) return false;
            var m = Percent.Match(line);
            if (m.Success) pct = Math.Min(100, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>progress(stepTitle, stepIndex, stepCount, overall 0..1, indeterminate)</summary>
        public static int Run(DiskInfo d, WipeOptions o, Action<string> log, Action<string, int, int, double, bool> progress)
        {
            Encoding oem;
            try { oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { oem = Encoding.Default; }

            var steps = BuildSteps(d, o);
            double total = steps.Sum(s => s.Weight), done = 0;
            for (int k = 0; k < steps.Count; k++)
            {
                var st = steps[k];
                double baseFrac = done / total, w = st.Weight / total;
                int lastPct = -1;
                progress(st.Title, k, steps.Count, baseFrac, true);
                log("— " + st.Title);
                int code;
                if (st.BigFat >= 0) code = FormatBigFatPart(d, o, st.BigFat, log);
                else
                {
                    string path = Path.Combine(Path.GetTempPath(), "foxidehelper_" + Guid.NewGuid().ToString("N") + ".txt");
                    try
                    {
                        File.WriteAllText(path, st.Script + "exit\r\n", oem);   // OEM code page: labels may contain Cyrillic
                        foreach (var line in st.Script.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                            if (!line.StartsWith("select disk", StringComparison.OrdinalIgnoreCase)) log("  > " + line);
                        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), "/s \"" + path + "\"")
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true,
                            StandardOutputEncoding = oem, StandardErrorEncoding = oem,
                        };
                        using (var p = new Process { StartInfo = psi })
                        {
                            int kk = k;
                            p.OutputDataReceived += (s, e) =>
                            {
                                if (e.Data == null) return;
                                int pct;
                                if (IsProgressLine(e.Data, out pct))
                                {
                                    if (pct >= 0 && pct != lastPct)
                                    {
                                        lastPct = pct;
                                        // 0 % followed straight by 100 % (quick format) keeps the "working" animation instead of a frozen bar
                                        progress(st.Title + "  ·  " + pct + " %", kk, steps.Count, baseFrac + w * pct / 100.0, pct == 0);
                                    }
                                    return;
                                }
                                if (!IsNoise(e.Data)) log(e.Data.Trim());
                            };
                            p.ErrorDataReceived += (s, e) => { if (e.Data != null && e.Data.Trim().Length > 0) log("! " + e.Data.TrimEnd()); };
                            p.Start();
                            p.BeginOutputReadLine();
                            p.BeginErrorReadLine();
                            p.WaitForExit();
                            code = p.ExitCode;
                        }
                    }
                    finally { try { File.Delete(path); } catch { } }
                }
                if (code != 0)
                {
                    log(L.F("Шаг «{0}» завершился с ошибкой (код {1}).", st.Title, code));
                    return code;
                }
                done += st.Weight;
                progress(st.Title, k, steps.Count, done / total, false);
            }
            return 0;
        }
        /// <summary>Re-reads disks and checks the target still is the same physical disk.</summary>
        public static DiskInfo Reverify(DiskInfo d)
        {
            var fresh = DiskService.Load().FirstOrDefault(x => x.Number == d.Number);
            if (fresh == null || fresh.Signature != d.Signature)
                throw new InvalidOperationException(L.T("Список дисков изменился (диск отключили или подключили другой). Обновите список и повторите."));
            if (fresh.IsProtected)
                throw new InvalidOperationException(L.T("Это системный диск — операция запрещена."));
            return fresh;
        }

    }
}
