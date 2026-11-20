using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>
    /// Start with Windows through a Task Scheduler logon task with highest privileges.
    /// (A plain HKCU\...\Run entry is silently skipped for apps that require elevation.)
    /// </summary>
    public static class Autostart
    {
        public const string TaskName = "Foxide Helper";

        static int Schtasks(string args, out string output)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            try { psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { }
            using (var p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        const string OldTaskName = "Foxide Disk";   // name before the rename

        /// <summary>Replaces the autostart task created by the old "Foxide Disk" build with a new one for this exe.</summary>
        public static void MigrateOldTask()
        {
            try
            {
                string o;
                if (Schtasks("/Query /TN \"" + OldTaskName + "\"", out o) != 0) return;
                if (Enable() == null) Schtasks("/Delete /TN \"" + OldTaskName + "\" /F", out o);
            }
            catch { }
        }

        public static bool IsEnabled()
        {
            try { string o; return Schtasks("/Query /TN \"" + TaskName + "\"", out o) == 0; }
            catch { return false; }
        }

        /// <summary>Returns null on success, otherwise an error text.</summary>
        public static string Enable()
        {
            string user = WindowsIdentity.GetCurrent().Name;
            string exe = Application.ExecutablePath;
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Foxide Helper: фоновый мониторинг температуры дисков</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(exe) + "\"</Command><Arguments>--background</Arguments>" +
"<WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n" +
"</Task>\r\n";
            string file = Path.Combine(Path.GetTempPath(), "foxidehelper_task_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode);
                string o;
                int rc = Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", out o);
                return rc == 0 ? null : (o.Trim().Length > 0 ? o.Trim() : L.F("schtasks вернул код {0}", rc));
            }
            catch (Exception ex) { return ex.Message; }
            finally { try { File.Delete(file); } catch { } }
        }

        public static string Disable()
        {
            try
            {
                if (!IsEnabled()) return null;
                string o;
                int rc = Schtasks("/Delete /TN \"" + TaskName + "\" /F", out o);
                return rc == 0 ? null : (o.Trim().Length > 0 ? o.Trim() : L.F("schtasks вернул код {0}", rc));
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
