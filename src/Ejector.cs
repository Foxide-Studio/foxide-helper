using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace FoxideDisk
{
    /// <summary>"Safely remove hardware" for a disk: finds the removable device node above the disk and asks PnP to eject it.</summary>
    public static class Ejector
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType, StringBuilder vetoName, int nameLength, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, int property, out int regType, out uint buffer, ref int length, int flags);

        const int CM_DRP_CAPABILITIES = 0x10;
        const uint CM_DEVCAP_REMOVABLE = 0x4;

        static string PnpId(int diskNumber)
        {
            using (var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index = " + diskNumber))
                foreach (ManagementObject o in s.Get())
                {
                    var v = o["PNPDeviceID"];
                    if (v != null) return v.ToString();
                }
            return null;
        }

        static bool IsRemovable(uint inst)
        {
            int type, len = 4; uint caps;
            if (CM_Get_DevNode_Registry_PropertyW(inst, CM_DRP_CAPABILITIES, out type, out caps, ref len, 0) != 0) return false;
            return (caps & CM_DEVCAP_REMOVABLE) != 0;
        }

        /// <summary>Returns null on success, otherwise a user-facing reason.</summary>
        public static string Eject(int diskNumber)
        {
            string id = PnpId(diskNumber);
            if (id == null) return L.T("Диск не найден — возможно, его уже отключили.");
            uint inst;
            if (CM_Locate_DevNodeW(out inst, id, 0) != 0) return L.T("Windows не нашла устройство диска.");

            // walk up from the disk to the first node that Windows considers removable (USB storage, card reader slot, …)
            uint target = inst, cur = inst;
            bool found = false;
            for (int i = 0; i < 6; i++)
            {
                if (IsRemovable(cur)) { target = cur; found = true; break; }
                uint parent;
                if (CM_Get_Parent(out parent, cur, 0) != 0) break;
                cur = parent;
            }
            if (!found) return L.T("Это устройство нельзя извлечь программно (Windows не считает его съёмным).");

            string last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var name = new StringBuilder(512);
                int veto;
                int r = CM_Request_Device_EjectW(target, out veto, name, name.Capacity, 0);
                if (r == 0 && veto == 0) return null;
                last = VetoText(veto, name.ToString(), r);
                if (last == null) return null;   // already removed
                if (veto == 5 || veto == 3 || veto == 4) break;   // in use — retrying will not help
                System.Threading.Thread.Sleep(500);
            }
            return last;
        }

        static string VetoText(int veto, string name, int cr)
        {
            string who = string.IsNullOrEmpty(name) ? "" : L.F("\n\nБлокирует: {0}", name);
            switch (veto)
            {
                case 3: return L.T("Диск используется программой. Закройте её и повторите.") + who;
                case 4: return L.T("Диск используется службой Windows.") + who;
                case 5: case 6: case 2:
                    return L.T("Диск сейчас используется: открыты файлы, окно Проводника или программа работает с ним. Закройте их и повторите.") + who;
                case 7: case 11: return L.T("Драйвер устройства не разрешает извлечение.") + who;
                case 10: return L.T("Это устройство нельзя отключить.") + who;
                case 12: return L.T("Недостаточно прав для извлечения.");
                case 13: return null;
                default: return L.F("Windows не удалось извлечь устройство (код {0}).", cr + (veto != 0 ? ", veto " + veto : "")) + who;
            }
        }
    }
}
