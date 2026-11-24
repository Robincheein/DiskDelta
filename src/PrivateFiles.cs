using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskDelta
{
    internal static class PrivateFiles
    {
        private const uint ReadAttributes = 0x80, DeleteAccess = 0x10000, GenericRead = 0x80000000, GenericWrite = 0x40000000;
        private const uint OpenExisting = 3, OpenReparse = 0x00200000, BackupSemantics = 0x02000000;
        private const FileAttributes Skip = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly |
            FileAttributes.Offline | FileAttributes.System;

        [StructLayout(LayoutKind.Sequential)]
        [DataContract]
        internal struct Info
        {
            public FileAttributes Attributes;
            public long Created { get { return Combine(CreationHigh, CreationLow); } }
            public long Written { get { return Combine(WriteHigh, WriteLow); } }
            public long Size { get { return Combine(SizeHigh, SizeLow); } }
            [DataMember] public uint CreationLow, CreationHigh;
            public uint AccessLow, AccessHigh;
            [DataMember] public uint WriteLow, WriteHigh;
            [DataMember] public uint Volume, SizeHigh, SizeLow;
            public uint Links;
            [DataMember] public uint IndexHigh, IndexLow;

            public bool Matches(Info current)
            {
                return Volume == current.Volume && IndexHigh == current.IndexHigh && IndexLow == current.IndexLow &&
                    Created == current.Created && Written == current.Written && Size == current.Size;
            }
        }

        private static long Combine(uint high, uint low) { return ((long)high << 32) | low; }

        public static string FullPath(string value)
        {
            string path = Path.GetFullPath(value);
            if (path.Length < 3 || path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0)
                throw new IOException("请选择本地磁盘上的普通目录。");
            path = char.ToUpperInvariant(path[0]) + path.Substring(1);
            return path.Length == 3 ? path : path.TrimEnd('\\');
        }

        public static bool Below(string path, string directory)
        {
            return path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static void VerifyDirectory(string path)
        {
            using (SafeFileHandle handle = Open(path, ReadAttributes, 7, false))
            {
                Info info = Inspect(handle);
                if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
                    !MatchesPath(handle, path))
                    throw new IOException("目录包含链接、挂载点或路径已改变：" + path);
            }
        }

        public static bool TryCandidate(string path, long cutoff, out Info info)
        {
            info = default(Info);
            using (SafeFileHandle handle = Open(path, ReadAttributes, 7, false))
                return !handle.IsInvalid && GetFileInformationByHandle(handle, out info) &&
                    (info.Attributes & Skip) == 0 && info.Links == 1 && info.Written <= cutoff &&
                    info.Created <= cutoff && MatchesPath(handle, path);
        }

        internal enum CleanupCheck { Ready, Protected, Unavailable }

        public static CleanupCheck InspectCleanup(string path, out Info info)
        {
            info = default(Info);
            using (SafeFileHandle handle = Open(path, ReadAttributes, 7, false))
            {
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) return CleanupCheck.Unavailable;
                if ((info.Attributes & Skip) != 0 || info.Links != 1 || !MatchesPath(handle, path))
                    return CleanupCheck.Protected;
                return CleanupCheck.Ready;
            }
        }

        public static bool TryDelete(string path, Info expected)
        {
            byte delete = 1;
            using (SafeFileHandle handle = Open(path, ReadAttributes | DeleteAccess, 0, false))
            {
                Info current;
                return !handle.IsInvalid && GetFileInformationByHandle(handle, out current) &&
                    (current.Attributes & Skip) == 0 && current.Links == 1 && expected.Matches(current) &&
                    MatchesPath(handle, path) && SetFileInformationByHandle(handle, 4, ref delete, 1);
            }
        }

        public static FileStream OpenDuplicate(string path, Info expected, bool forDeletion)
        {
            SafeFileHandle handle = Open(path, GenericRead | (forDeletion ? DeleteAccess : 0), forDeletion ? 0u : 1u, false);
            try
            {
                Info current = Inspect(handle);
                if ((current.Attributes & Skip) != 0 || current.Links != 1 ||
                    !expected.Matches(current) || !MatchesPath(handle, path) || !SingleStream(handle))
                    throw new IOException("文件已改变，或包含额外的数据流。");
                return new FileStream(handle, FileAccess.Read, 128 * 1024);
            }
            catch { handle.Dispose(); throw; }
        }

        public static bool DeleteDuplicate(FileStream stream, string path)
        {
            byte delete = 1;
            return MatchesPath(stream.SafeFileHandle, path) && SingleStream(stream.SafeFileHandle) &&
                SetFileInformationByHandle(stream.SafeFileHandle, 4, ref delete, 1);
        }

        private static bool SingleStream(SafeFileHandle handle)
        {
            var data = new byte[64];
            if (!GetFileInformationByHandleEx(handle, 7, data, (uint)data.Length)) return false;
            const string name = "::$DATA";
            return BitConverter.ToUInt32(data, 0) == 0 && BitConverter.ToInt32(data, 4) == name.Length * 2 &&
                Encoding.Unicode.GetString(data, 24, name.Length * 2) == name;
        }

        public static void CreateDirectory(string path)
        {
            if (!CreateDirectoryNative(Extended(path), IntPtr.Zero)) throw Error("无法新建目录", path);
        }

        public static void ReplaceFile(string source, string destination)
        {
            const uint replaceExisting = 1;
            if (!MoveFileEx(source, destination, replaceExisting)) throw Error("无法保存文件", destination);
        }

        public static void CreateJunction(string emptyFolder, string target)
        {
            VerifyDirectory(emptyFolder);
            VerifyDirectory(target);
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            byte[] display = Encoding.Unicode.GetBytes(target);
            byte[] payload;
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes))
            {
                writer.Write(0xa0000003u);
                writer.Write(checked((ushort)(substitute.Length + display.Length + 12)));
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write(checked((ushort)substitute.Length));
                writer.Write(checked((ushort)(substitute.Length + 2)));
                writer.Write(checked((ushort)display.Length));
                writer.Write(substitute);
                writer.Write((ushort)0);
                writer.Write(display);
                writer.Write((ushort)0);
                payload = bytes.ToArray();
            }
            using (SafeFileHandle handle = Open(emptyFolder, GenericWrite, 0, false))
            {
                if (handle.IsInvalid || !MatchesPath(handle, emptyFolder)) throw Error("无法打开链接目录", emptyFolder);
                uint returned;
                if (!DeviceIoControl(handle, 0x000900a4, payload, payload.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw Error("无法建立目录联接", emptyFolder);
            }
        }

        public static bool PointsTo(string junction, string target)
        {
            using (SafeFileHandle entry = Open(junction, ReadAttributes, 7, false))
            {
                if (entry.IsInvalid || !MatchesPath(entry, junction)) return false;
                var output = new byte[16384];
                uint count;
                if (!GetReparseData(entry, 0x000900a8, IntPtr.Zero, 0, output, output.Length, out count, IntPtr.Zero) ||
                    count < 16 || BitConverter.ToUInt32(output, 0) != 0xa0000003u) return false;
                int offset = 16 + BitConverter.ToUInt16(output, 8);
                int length = BitConverter.ToUInt16(output, 10);
                return offset + length <= count &&
                    string.Equals(Encoding.Unicode.GetString(output, offset, length), @"\??\" + target, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static FileStream OpenForVerification(string path, string streamName)
        {
            SafeFileHandle handle = Open(path + (streamName == "::$DATA" ? "" : streamName), GenericRead, 1, false);
            try
            {
                Info info = Inspect(handle);
                if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                    !MatchesPath(handle, path, streamName))
                    throw new IOException("文件路径已改变：" + path);
                return new FileStream(handle, FileAccess.Read);
            }
            catch { handle.Dispose(); throw; }
        }

        public static IEnumerable<string> Streams(string path)
        {
            StreamInfo stream;
            using (SearchHandle handle = FindFirstStreamW(Extended(path), 0, out stream, 0))
            {
                if (handle.IsInvalid)
                {
                    if (Marshal.GetLastWin32Error() == 38) yield break;
                    throw Error("无法读取文件数据流", path);
                }
                do { yield return stream.Name; } while (FindNextStreamW(handle, out stream));
                if (Marshal.GetLastWin32Error() != 38) throw Error("无法读完文件数据流", path);
            }
        }

        private static Info Inspect(SafeFileHandle handle)
        {
            Info info;
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) throw Error("无法读取文件或目录", "");
            return info;
        }

        private static IOException Error(string operation, string path)
        {
            return new IOException(operation + " " + path + " · " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        private static bool MatchesPath(SafeFileHandle handle, string path, string stream = null)
        {
            var output = new StringBuilder(512);
            uint length = GetFinalPathNameByHandleW(handle, output, (uint)output.Capacity, 0);
            if (length >= output.Capacity)
            {
                output = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandleW(handle, output, (uint)output.Capacity, 0);
            }
            if (length == 0 || length >= output.Capacity) return false;
            string finalPath = output.ToString().TrimEnd('\\');
            string expected = Extended(path).TrimEnd('\\');
            if (string.Equals(finalPath, expected, StringComparison.OrdinalIgnoreCase)) return true;
            return stream != null && (string.Equals(finalPath, expected + stream, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(finalPath, expected + stream.Substring(0, stream.LastIndexOf(':')), StringComparison.OrdinalIgnoreCase));
        }

        private static string Extended(string path) { return @"\\?\" + path; }

        private static SafeFileHandle Open(string path, uint access, uint share, bool follow)
        {
            return CreateFileW(Extended(path), access, share, IntPtr.Zero, OpenExisting,
                BackupSemantics | (follow ? 0 : OpenReparse), IntPtr.Zero);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StreamInfo
        {
            public long Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
        }
        private sealed class SearchHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SearchHandle() : base(true) { }
            protected override bool ReleaseHandle() { return FindClose(handle); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string source, string destination, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Info info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, byte[] info, uint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref byte payload, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDirectoryW", SetLastError = true)]
        private static extern bool CreateDirectoryNative(string path, IntPtr security);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int size,
            IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool GetReparseData(SafeFileHandle file, uint code, IntPtr input, int size,
            byte[] output, int outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SearchHandle FindFirstStreamW(string path, int level, out StreamInfo stream, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextStreamW(SearchHandle file, out StreamInfo stream);
        [DllImport("kernel32.dll")]
        private static extern bool FindClose(IntPtr file);
    }
}
