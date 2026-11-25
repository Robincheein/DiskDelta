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
                Info info = Inspect(handle, path);
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
                Info current = Inspect(handle, path);
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

        public static void MoveDirectory(string source, string destination)
        {
            if (!MoveFileEx(Extended(source), Extended(destination), 0))
                throw Error("无法重命名目录", source + " → " + destination);
        }

        public static int DeleteSharingError(string path)
        {
            using (SafeFileHandle handle = Open(path, DeleteAccess, 7, false))
                return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
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
                if (handle.IsInvalid) throw Error("无法打开链接目录", emptyFolder);
                if (!MatchesPath(handle, emptyFolder)) throw new IOException("链接目录路径已改变：" + emptyFolder);
                uint returned;
                if (!DeviceIoControl(handle, 0x000900a4, payload, payload.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw Error("无法建立目录联接", emptyFolder);
            }
        }

        public static bool PointsTo(string junction, string target)
        {
            bool symbolic;
            string stored = LinkPath(junction, out symbolic);
            return !symbolic && string.Equals(stored, @"\??\" + target, StringComparison.OrdinalIgnoreCase);
        }

        public static string DirectoryLinkTarget(string path, out string kind)
        {
            bool symbolic;
            string target = LinkPath(path, out symbolic);
            kind = target == null ? "重解析目录" : symbolic ? "符号链接" : "目录联接";
            if (target == null) return null;
            const string ntPath = @"\??\";
            if (target.StartsWith(ntPath + @"UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + target.Substring(ntPath.Length + 4);
            if (target.StartsWith(ntPath, StringComparison.Ordinal))
            {
                target = target.Substring(ntPath.Length);
                return target.Length > 1 && target[1] == ':' ? target : @"\\?\" + target;
            }
            if (!symbolic) return target;
            try { return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), target)); }
            catch (ArgumentException) { return target; }
            catch (IOException) { return target; }
            catch (NotSupportedException) { return target; }
        }

        private static string LinkPath(string junction, out bool symbolic)
        {
            symbolic = false;
            using (SafeFileHandle entry = Open(junction, ReadAttributes, 7, false))
            {
                if (entry.IsInvalid || !MatchesPath(entry, junction)) return null;
                var output = new byte[16384];
                uint count;
                if (!GetReparseData(entry, 0x000900a8, IntPtr.Zero, 0, output, output.Length, out count, IntPtr.Zero) ||
                    count < 16) return null;
                uint tag = BitConverter.ToUInt32(output, 0);
                symbolic = tag == 0xa000000cu;
                if (!symbolic && tag != 0xa0000003u || symbolic && count < 20) return null;
                int offset = (symbolic ? 20 : 16) + BitConverter.ToUInt16(output, 8);
                int length = BitConverter.ToUInt16(output, 10);
                return length > 0 && (length & 1) == 0 && offset + length <= count
                    ? Encoding.Unicode.GetString(output, offset, length) : null;
            }
        }

        public static bool Exists(string path)
        {
            try { File.GetAttributes(Extended(path)); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        public static FileStream OpenForVerification(string path, string streamName)
        {
            SafeFileHandle handle = Open(path + (streamName == "::$DATA" ? "" : streamName), GenericRead, 1, false);
            try
            {
                Info info = Inspect(handle, path + (streamName == "::$DATA" ? "" : streamName));
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

        private static Info Inspect(SafeFileHandle handle, string path)
        {
            Info info;
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) throw Error("无法读取文件或目录", path);
            return info;
        }

        private static IOException Error(string operation, string path)
        {
            return OperationError(operation, path, Marshal.GetLastWin32Error());
        }

        public static IOException OperationError(string operation, string path, int code)
        {
            string reason;
            switch (code)
            {
                case 32: case 33: reason = "文件或目录正被占用，请关闭相关程序和后台进程后重试"; break;
                case 5: reason = "Windows 拒绝访问，请检查此路径的权限"; break;
                case 2: case 3: reason = "文件或目录已不存在"; break;
                case 112: reason = "磁盘空间不足"; break;
                case 206: reason = "路径或单级文件名超出 Windows 支持的长度"; break;
                default: reason = new Win32Exception(code).Message; break;
            }
            return new IOException(operation + " · " + reason + "（Windows " + code + "）\n" + path,
                unchecked((int)0x80070000) | code);
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

        internal static string Extended(string path) { return @"\\?\" + path; }

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
