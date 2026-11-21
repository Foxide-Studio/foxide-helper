using System;
using System.Collections.Generic;

namespace FoxideDisk
{
    /// <summary>
    /// Interface language. The source text is Russian; English is looked up by the Russian string
    /// in the tables of lang\En.*.cs (pairs "русский", "english"). A missing pair falls back to Russian.
    /// Strings with values use L.F("Диск {0}", n) — the placeholders must be kept in both languages.
    /// The language is applied at start; changing it in Settings restarts the program.
    /// </summary>
    public static partial class L
    {
        public static string Lang = "en";              // "en" | "ru"
        public static bool Ru { get { return Lang == "ru"; } }
        static Dictionary<string, string> en;

        public static string T(string ru)
        {
            if (ru == null || Ru) return ru;
            if (en == null) Build();
            string v;
            return en.TryGetValue(ru, out v) ? v : ru;
        }

        public static string F(string ru, params object[] args)
        {
            try { return string.Format(T(ru), args); }
            catch (FormatException) { return string.Format(ru, args); }
        }

        static void Build()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in new[] { EnCore, EnMainForm, EnViews, EnServices, EnHardware, EnBattery, EnPawnCpu, EnPawnBoard, EnPawnMem })
                for (int i = 0; i + 1 < t.Length; i += 2) d[t[i]] = t[i + 1];
            en = d;
        }
    }
}
