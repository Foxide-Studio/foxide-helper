using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("Foxide Helper")]
[assembly: AssemblyProduct("Foxide Helper")]
[assembly: AssemblyDescription("Disk, memory and CPU/GPU toolkit for Windows")]
[assembly: AssemblyCompany("Foxide Studio")]
[assembly: AssemblyCopyright("© 2026 Foxide Studio")]
[assembly: AssemblyVersion("1.1.9.0")]
[assembly: AssemblyFileVersion("1.1.9.0")]

namespace FoxideDisk
{
    static class Program
    {
        public static bool IsAdmin;

        [STAThread]
        static int Main(string[] args)
        {
            try { IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Telemetry.SaveCrash(e.Exception, "ui");
                FxMessage.Show(L.T("Непредвиденная ошибка:") + "\n\n" + e.Exception.Message, "Foxide Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Telemetry.SaveCrash(e.ExceptionObject as Exception, "fatal");

            float scale = 1f;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            // screenshot demo (only together with --shot): invented hardware, English UI, 1.5x scale
            if (args.Length >= 2 && args[0] == "--shot" && Array.IndexOf(args, "--demo") >= 0)
            {
                Demo.On = true;
                Theme.FontScale = 1.5f / scale;
                scale = 1.5f;
                foreach (var a in args) if (a.StartsWith("--scroll=")) int.TryParse(a.Substring(9), out Demo.ScrollY);
                args = Array.FindAll(args, a => a != "--demo" && !a.StartsWith("--scroll="));
            }
            Theme.Init(scale);
            Settings.Load();
            L.Lang = Settings.Language;
            if (Demo.On) Demo.PrepareSettings();
            Theme.Set(Settings.ThemeMode);

            if (args.Length >= 2 && (args[0] == "--dump" || args[0] == "--shot")) Telemetry.Off = true;
            if (args.Length >= 2 && args[0] == "--dump") return Dump(args[1]);
            if (args.Length >= 2 && args[0] == "--shot") return Shot(args);

            // after an update or a restart: let the previous instance exit first, otherwise the single-instance check stops us
            int waitPid;
            if (args.Length >= 2 && args[0] == "--wait-pid" && int.TryParse(args[1], out waitPid))
            {
                try { using (var p = System.Diagnostics.Process.GetProcessById(waitPid)) p.WaitForExit(15000); } catch { }
            }
            Updater.Cleanup();

            // single instance: a second launch just brings the running one to the front
            bool created;
            using (var showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\FoxideHelper.Show", out created))
            {
                if (!created)
                {
                    if (Array.IndexOf(args, "--background") < 0) showEvent.Set();
                    return 0;
                }
                var form = new MainForm { StartHidden = Array.IndexOf(args, "--background") >= 0 };
                var waiter = new System.Threading.Thread(() =>
                {
                    while (true)
                    {
                        showEvent.WaitOne();
                        try { form.BeginInvoke(new Action(form.ShowFromTray)); } catch { return; }
                    }
                }) { IsBackground = true };
                waiter.Start();
                Application.Run(form);
            }
            return 0;
        }

        // Diagnostics: write detected disks to a text file.
        static int Dump(string path)
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var d in DiskService.Load())
                {
                    sb.AppendLine("Disk " + d.Number + ": " + d.Model + " | " + DiskService.FormatSize(d.Size) + " | " + d.KindName + " | " + d.BusName +
                        " | " + d.StyleName + " | rpm=" + d.Rpm + " (" + d.RpmSource + ") | health=" + d.Health + " (" + d.HealthStatus + ") predict=" + d.PredictFailure + " protected=" + d.IsProtected);
                    if (d.Rel != null)
                        sb.AppendLine("   rel: temp=" + d.Rel.Temperature + " wear=" + d.Rel.Wear + " poh=" + d.Rel.PowerOnHours + " rErr=" + d.Rel.ReadErrors);
                    else sb.AppendLine("   rel: n/a");
                    if (d.Smart != null)
                    {
                        sb.AppendLine("   smart: " + d.Smart.Source + " rows=" + d.Smart.Rows.Count);
                        foreach (var r in d.Smart.Rows)
                            sb.AppendLine("     " + r.Id + " " + r.Name + " = " + r.Value + " [" + r.Normalized + "] norm=" + r.Norm + " " + r.State + " " + r.Note);
                    }
                    else sb.AppendLine("   smart: n/a");
                    foreach (var p in d.Partitions)
                        sb.AppendLine("   #" + p.Number + " " + p.Type + " " + (p.Letter == '\0' ? "-" : p.Letter + ":") + " " + p.FileSystem + " '" + p.Label + "' " +
                            DiskService.FormatSize(p.Size) + (p.HasFree ? " free " + DiskService.FormatSize(p.Free) : "") + (p.IsService ? " [service]" : ""));
                }
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return 0;
        }

        // Diagnostics: render the window to PNG.  --shot out.png <section> <tab> <theme 1|2> [disk#]
        static int Shot(string[] args)
        {
            string section = args.Length > 2 ? args[2] : "all";
            int tab = args.Length > 3 ? int.Parse(args[3]) : 0;
            if (args.Length > 4) Theme.Set(int.Parse(args[4]));
            int disk = args.Length > 5 ? int.Parse(args[5]) : -1;
            IsAdmin = true; // render-only: show admin-only pages
            if (section == "confirm" || section == "msg")
            {
                var d = DiskService.Load().Find(x => x.Number == Math.Max(0, disk));
                using (var dlg = section == "msg" ? FxMessage.Sample() : new ConfirmDialog(d, new WipeOptions()))
                {
                    dlg.StartPosition = FormStartPosition.Manual; dlg.Location = new Point(-20000, -20000); dlg.ShowInTaskbar = false;
                    dlg.Show();
                    Application.DoEvents();
                    using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                    {
                        dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
                        bmp.Save(args[1], ImageFormat.Png);
                    }
                }
                return 0;
            }

            var f = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
            if (Demo.On) Demo.Attach(f);
            int h;
            if (args.Length > 6 && int.TryParse(args[6], out h)) f.Height = h;
            f.DisksLoaded += (s, e) =>
            {
                int wait;
                var t = new Timer { Interval = args.Length > 7 && int.TryParse(args[7], out wait) ? wait : 300 };
                f.ForTest(section, tab);
                if (disk >= 0) f.SelectDiskNumber(disk);
                t.Tick += (s2, e2) =>
                {
                    t.Stop();
                    Application.DoEvents();
                    if (Demo.On)
                    {
                        if (Demo.ScrollY > 0) { f.ScrollPage(Demo.ScrollY); Application.DoEvents(); }
                        Demo.Save(f, args[1]); f.Close(); return;
                    }
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(args[1], ImageFormat.Png);
                    }
                    f.Close();
                };
                t.Start();
            };
            Application.Run(f);
            return 0;
        }
    }
}
