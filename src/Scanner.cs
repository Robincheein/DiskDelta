using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiskDelta
{
    public sealed class Change
    {
        public string Path { get; set; }
        public long Delta { get; set; }
        public long Size { get; set; }
        public string Kind { get; set; }
    }

    public sealed class Report
    {
        public string Id { get; set; }
        public DateTime Started { get; set; }
        public DateTime Baseline { get; set; }
        public string Root { get; set; }
        public string VolumeId { get; set; }
        public long Capacity { get; set; }
        public long Free { get; set; }
        public long BaselineFree { get; set; }
        public long Files { get; set; }
        public int Unreadable { get; set; }
        public int BaselineUnreadable { get; set; }
        public int Links { get; set; }
        public long Added { get; set; }
        public long Expanded { get; set; }
        public long Net { get; set; }
        public long Growth { get; set; }
        public List<Change> FileChanges { get; set; }
        public List<Change> FolderChanges { get; set; }
    }

    internal sealed class DirectoryChange
    {
        public string Path;
        public long? Before;
        public long? After;
        public long? Delta;
        public long Direct;
    }

    internal static class Scanner
    {
        private const int SnapshotVersion = 1;
        private const int EndOfDirectory = 18;
        private const uint DirectoryFlag = 0x10;
        private const uint ReparseFlag = 0x400;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        private enum Record : byte { End, File, Inaccessible }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Entry
        {
            public uint Attributes;
            private System.Runtime.InteropServices.ComTypes.FILETIME created, accessed, written;
            public uint SizeHigh, SizeLow;
            private uint reserved0, reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            private string shortName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileEx(string path, int infoLevel, out Entry entry,
            int searchOperation, IntPtr filter, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFile(IntPtr handle, out Entry entry);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr handle);

        private struct Child
        {
            public string Key;
            public long Size;
            public bool IsDirectory { get { return Key.EndsWith("\\", StringComparison.Ordinal); } }
        }

        public static int Search(string root, Action<string, string, bool> visit, CancellationToken cancellation)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            int unreadable = 0;
            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                string folder = pending.Pop();
                List<Child> children;
                int skipped;
                if (ReadDirectory(folder, out children, out skipped, cancellation) != 0)
                {
                    unreadable++;
                    continue;
                }
                foreach (Child child in children)
                {
                    if (child.IsDirectory)
                    {
                        pending.Push(folder + child.Key);
                        visit(folder, child.Key.TrimEnd('\\'), true);
                    }
                    else if (child.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        visit(folder, child.Key, false);
                }
            }
            return unreadable;
        }

        public static void Capture(string output, string excluded, Report report,
            Action<long, string> progress, CancellationToken cancellation)
        {
            var drive = new DriveInfo(Path.GetPathRoot(report.Root));
            report.Capacity = drive.TotalSize;
            report.Free = drive.TotalFreeSpace;
            using (var stream = File.Create(output))
            using (var compressed = new GZipStream(stream, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(new BufferedStream(compressed, 64 * 1024)))
            {
                writer.Write(SnapshotVersion);
                Visit(report.Root, excluded, report, writer, progress, Stopwatch.StartNew(), cancellation);
                writer.Write((byte)Record.End);
            }
        }

        private static void Visit(string directory, string excluded, Report report, BinaryWriter writer,
            Action<long, string> progress, Stopwatch timer, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.Equals(directory, excluded, StringComparison.OrdinalIgnoreCase))
                return;

            int skipped;
            List<Child> children;
            int error = ReadDirectory(directory, out children, out skipped, cancellation);
            if (error != 0)
            {
                if (directory == report.Root)
                    throw new Win32Exception(error, "无法读取扫描根目录 " + directory);
                writer.Write((byte)Record.Inaccessible);
                writer.Write(directory);
                report.Unreadable++;
                return;
            }

            report.Links += skipped;
            children.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
            foreach (Child child in children)
            {
                cancellation.ThrowIfCancellationRequested();
                string path = directory + child.Key;
                if (child.IsDirectory)
                {
                    Visit(path, excluded, report, writer, progress, timer, cancellation);
                    continue;
                }
                writer.Write((byte)Record.File);
                writer.Write(path);
                writer.Write(child.Size);
                report.Files++;
                if (progress != null && timer.ElapsedMilliseconds >= 250)
                {
                    progress(report.Files, directory);
                    timer.Restart();
                }
            }
        }

        private static int ReadDirectory(string directory, out List<Child> children, out int skipped,
            CancellationToken cancellation)
        {
            children = new List<Child>();
            skipped = 0;
            Entry entry;
            string pattern = @"\\?\" + directory + "*";
            IntPtr handle = FindFirstFileEx(pattern, 1, out entry, 0, IntPtr.Zero, 2);
            if (handle == InvalidHandle)
            {
                int error = Marshal.GetLastWin32Error();
                return error == 2 || error == EndOfDirectory ? 0 : error;
            }
            try
            {
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (entry.Name == "." || entry.Name == "..")
                        continue;
                    if ((entry.Attributes & ReparseFlag) != 0)
                    {
                        skipped++;
                        continue;
                    }
                    bool isDirectory = (entry.Attributes & DirectoryFlag) != 0;
                    children.Add(new Child
                    {
                        Key = isDirectory ? entry.Name + "\\" : entry.Name,
                        Size = ((long)entry.SizeHigh << 32) | entry.SizeLow
                    });
                } while (FindNextFile(handle, out entry));
                int error = Marshal.GetLastWin32Error();
                return error == EndOfDirectory ? 0 : error;
            }
            finally { FindClose(handle); }
        }

        private sealed class Snapshot : IDisposable
        {
            private readonly BinaryReader reader;
            public Record Type;
            public string Path;
            public long Size;
            public string Inaccessible;

            public Snapshot(string filename)
            {
                var decompressed = new GZipStream(File.OpenRead(filename), CompressionMode.Decompress);
                reader = new BinaryReader(new BufferedStream(decompressed, 64 * 1024));
                try
                {
                    if (reader.ReadInt32() != SnapshotVersion)
                        throw new InvalidDataException("快照版本不受支持。");
                    Next();
                }
                catch
                {
                    reader.Dispose();
                    throw;
                }
            }

            // Error records remain active until the merge moves past their path.
            public void MarkInaccessible()
            {
                Inaccessible = Path;
                Next();
            }

            public bool Covers(string path)
            {
                if (Inaccessible == null)
                    return true;
                if (path.StartsWith(Inaccessible, StringComparison.Ordinal))
                    return false;
                Inaccessible = null;
                return true;
            }

            public void Next()
            {
                Type = (Record)reader.ReadByte();
                if (Type == Record.End)
                {
                    Path = null;
                    return;
                }
                if (Type != Record.File && Type != Record.Inaccessible)
                    throw new InvalidDataException("快照记录损坏。");
                Path = reader.ReadString();
                Size = Type == Record.File ? reader.ReadInt64() : 0;
            }

            public void Dispose() { reader.Dispose(); }
        }

        public static void Compare(string previous, string current, string treeFile, Report report, CancellationToken cancellation)
        {
            var files = new Ranking(150);
            using (var before = new Snapshot(previous))
            using (var after = new Snapshot(current))
            using (var output = File.Create(treeFile))
            using (var compressed = new GZipStream(output, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(new BufferedStream(compressed, 64 * 1024)))
            {
                writer.Write(1);
                var folders = new FolderTotals(report.Root, writer);
                while (before.Type != Record.End || after.Type != Record.End)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int order = before.Type == Record.End ? 1 :
                        after.Type == Record.End ? -1 : string.CompareOrdinal(before.Path, after.Path);
                    if (order <= 0 && before.Type == Record.Inaccessible)
                    {
                        before.MarkInaccessible();
                        continue;
                    }
                    if (order >= 0 && after.Type == Record.Inaccessible)
                    {
                        after.MarkInaccessible();
                        continue;
                    }

                    string path = order <= 0 ? before.Path : after.Path;
                    bool previousCovered = before.Covers(path);
                    bool currentCovered = after.Covers(path);
                    long oldSize = order <= 0 ? before.Size : 0;
                    long newSize = order >= 0 ? after.Size : 0;
                    long delta = newSize - oldSize;
                    if (previousCovered && currentCovered)
                    {
                        folders.Add(path, oldSize, newSize, order != 0 || delta != 0);
                        report.Net += delta;
                        if (delta > 0)
                        {
                            report.Growth += delta;
                            if (order > 0) report.Added++;
                            else report.Expanded++;
                            files.Add(path, delta, newSize, order > 0 ? "新增" : "增大");
                        }
                    }
                    if (order <= 0) before.Next();
                    if (order >= 0) after.Next();
                }
                folders.Finish();
                writer.Write("");
            }
            report.FileChanges = files.Results();
        }

        public static IEnumerable<DirectoryChange> ReadDirectoryChanges(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var compressed = new GZipStream(stream, CompressionMode.Decompress))
            using (var reader = new BinaryReader(new BufferedStream(compressed, 64 * 1024)))
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException("目录树版本不受支持。");
                for (string folder = reader.ReadString(); folder.Length != 0; folder = reader.ReadString())
                {
                    long before = reader.ReadInt64();
                    long after = reader.ReadInt64();
                    yield return new DirectoryChange
                    {
                        Path = folder, Before = before, After = after, Delta = after - before, Direct = reader.ReadInt64()
                    };
                }
            }
        }

        private sealed class Ranking
        {
            private readonly int limit;
            private readonly SortedSet<Change> largest = new SortedSet<Change>(Comparer<Change>.Create((left, right) =>
            {
                int order = left.Delta.CompareTo(right.Delta);
                return order != 0 ? order : string.CompareOrdinal(right.Path, left.Path);
            }));

            public Ranking(int limit) { this.limit = limit; }

            public void Add(string path, long delta, long size, string kind)
            {
                if (largest.Count == limit && delta < largest.Min.Delta)
                    return;
                largest.Add(new Change { Path = path, Delta = delta, Size = size, Kind = kind });
                if (largest.Count > limit) largest.Remove(largest.Min);
            }

            public List<Change> Results() { return largest.Reverse().ToList(); }
        }

        private sealed class FolderTotals
        {
            private sealed class Folder
            {
                public string Prefix;
                public long Before;
                public long After;
                public long Direct;
                public bool Changed;
            }

            private readonly Stack<Folder> open = new Stack<Folder>();
            private readonly BinaryWriter writer;

            public FolderTotals(string root, BinaryWriter writer)
            {
                this.writer = writer;
                open.Push(new Folder { Prefix = root });
            }

            public void Add(string file, long before, long after, bool changed)
            {
                while (!file.StartsWith(open.Peek().Prefix, StringComparison.Ordinal))
                    FinishFolder();
                int start = open.Peek().Prefix.Length;
                for (int slash = file.IndexOf('\\', start); slash >= 0; slash = file.IndexOf('\\', slash + 1))
                    open.Push(new Folder { Prefix = file.Substring(0, slash + 1) });
                Folder folder = open.Peek();
                folder.Before += before;
                folder.After += after;
                folder.Direct += after - before;
                folder.Changed |= changed;
            }

            private void FinishFolder()
            {
                Folder folder = open.Pop();
                if (folder.Changed)
                {
                    writer.Write(folder.Prefix);
                    writer.Write(folder.Before);
                    writer.Write(folder.After);
                    writer.Write(folder.Direct);
                }
                if (open.Count == 0) return;
                Folder parent = open.Peek();
                parent.Before += folder.Before;
                parent.After += folder.After;
                parent.Changed |= folder.Changed;
            }

            public void Finish()
            {
                while (open.Count > 0) FinishFolder();
            }
        }
    }
}
