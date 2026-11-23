using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DiskDelta
{
    internal static class ExplorerLocation
    {
        public static void OpenFolder(string path)
        {
            if (!Directory.Exists(path)) throw new IOException("文件夹不可用。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        public static void Reveal(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("路径不可用。");
            if (string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase))
            {
                OpenFolder(path);
                return;
            }

            Application.OleRequired();
            IntPtr item;
            uint attributes;
            int result = SHParseDisplayName(path, IntPtr.Zero, out item, 0, out attributes);
            Marshal.ThrowExceptionForHR(result);
            try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0)); }
            finally { Marshal.FreeCoTaskMem(item); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHParseDisplayName(string displayName, IntPtr binding, out IntPtr item,
            uint requestedAttributes, out uint attributes);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
    }
}
