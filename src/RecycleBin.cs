using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DiskDelta
{
    internal sealed class RecycleSnapshot
    {
        public long Count;
        public long Bytes;
        public readonly List<RecycleEntry> Entries = new List<RecycleEntry>();
    }

    internal sealed class RecycleEntry
    {
        public string Name;
        public string Parent;
        public long Bytes;
        public bool IsDirectory;
    }

    internal static class RecycleBin
    {
        public static Task<RecycleSnapshot> ScanAsync()
        {
            var result = new TaskCompletionSource<RecycleSnapshot>();
            var worker = new Thread(() =>
            {
                try { result.SetResult(Scan()); }
                catch (Exception ex) { result.SetException(ex); }
            }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return result.Task;
        }

        private static RecycleSnapshot Scan()
        {
            var info = new BinInfo { Size = Marshal.SizeOf(typeof(BinInfo)) };
            Marshal.ThrowExceptionForHR(SHQueryRecycleBin(null, ref info));
            var snapshot = new RecycleSnapshot { Count = info.Count, Bytes = info.Bytes };
            if (info.Count == 0) return snapshot;

            object shell = null, folder = null, items = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                folder = ((dynamic)shell).NameSpace(10);
                if (folder == null) return snapshot;
                items = ((dynamic)folder).Items();
                int count = ((dynamic)items).Count;
                for (int index = 0; index < count; index++)
                {
                    object entry = null;
                    try
                    {
                        entry = ((dynamic)items).Item(index);
                        if (entry == null) continue;
                        dynamic item = entry;
                        snapshot.Entries.Add(new RecycleEntry
                        {
                            Name = item.Name,
                            Parent = Convert.ToString(item.ExtendedProperty("System.Recycle.DeletedFrom"), CultureInfo.CurrentCulture),
                            Bytes = Convert.ToInt64(item.ExtendedProperty("System.Size"), CultureInfo.InvariantCulture),
                            IsDirectory = item.IsFolder
                        });
                    }
                    catch (COMException) { }
                    finally { Release(entry); }
                }
            }
            finally { Release(items); Release(folder); Release(shell); }
            return snapshot;
        }

        private static void Release(object item)
        {
            if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }

        public static void Open()
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                "shell:RecycleBinFolder") { UseShellExecute = true });
        }

        public static bool Empty(IntPtr owner)
        {
            int result = SHEmptyRecycleBin(owner, null, 0);
            if (result == unchecked((int)0x800704c7)) return false;
            Marshal.ThrowExceptionForHR(result);
            return result == 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BinInfo
        {
            public int Size;
            public long Bytes;
            public long Count;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string volume, ref BinInfo info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr owner, string volume, uint flags);
    }
}
