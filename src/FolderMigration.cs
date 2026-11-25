using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace DiskDelta
{
    public sealed class MigrationRecord
    {
        public string Id { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public bool Installed { get; set; }
        [XmlIgnore] public string Backup { get { return Source + ".DiskDelta-" + Id + ".backup"; } }
        [XmlIgnore] public string PendingLink { get { return Source + ".DiskDelta-" + Id + ".link"; } }
        [XmlIgnore] public string Staging { get { return Target + ".DiskDelta-" + Id + ".staging"; } }
        public override string ToString() { return Path.GetFileName(Source) + "   →   " + Path.GetPathRoot(Target); }
    }

    internal sealed class FolderMigration
    {
        // The same line protocol carries progress through the elevated migration pipe.
        internal const string VerificationProgressPrefix = "\u001eDiskDeltaVerify ";
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(MigrationRecord));
        private static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Packages", "Programs", "Temp", "CrashDumps", "DiskDelta",
            "ConnectedDevicesPlatform", "Comms", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".git",
            ".codex", ".config", ".cache", ".local"
        };

        private readonly string profile;
        private readonly string journal;
        private readonly bool installed;
        public FolderMigration(string userProfile, string dataDirectory, bool installed = false)
        {
            profile = PrivateFiles.FullPath(userProfile);
            this.installed = installed;
            journal = installed ? InstalledMigration.Journal : Path.Combine(dataDirectory, "Migrations");
        }

        public static string ResolveTarget(string source, string destination)
        {
            string name = Path.GetFileName(PrivateFiles.FullPath(source));
            destination = PrivateFiles.FullPath(destination);
            return string.Equals(Path.GetFileName(destination), name, StringComparison.OrdinalIgnoreCase)
                ? destination : Path.Combine(destination, name);
        }

        public MigrationRecord Preview(string source, string destination)
        {
            source = ValidateSource(source);
            PrivateFiles.VerifyDirectory(source);
            string target = PrivateFiles.FullPath(destination);
            if (target.Length == 3) throw new IOException("请填写目标盘内的文件夹路径。");
            var record = new MigrationRecord { Id = Guid.NewGuid().ToString("N"), Source = source, Target = target, Installed = installed };
            ValidateRecord(record);
            CheckDestination(record, false, null);
            return record;
        }

        private static void CheckDestination(MigrationRecord item, bool createParents, Action<string> log)
        {
            string root = Path.GetPathRoot(item.Target);
            RequireNtfs(root);
            if (root == Program.DefaultRoot || Volume.ReadId(root) == Volume.ReadId(Program.DefaultRoot))
                throw new IOException("请选择 C 盘以外的固定 NTFS 磁盘。");
            string parent = Path.GetDirectoryName(item.Target);
            string current = root;
            PrivateFiles.VerifyDirectory(current);
            foreach (string name in parent.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, name);
                if (!PrivateFiles.Exists(current))
                {
                    if (item.Installed) throw new IOException("受保护的目标目录不可用：" + current);
                    if (!createParents) return;
                    WriteLog(log, "Windows API 创建目录 " + current);
                    PrivateFiles.CreateDirectory(current);
                }
                PrivateFiles.VerifyDirectory(current);
            }
            if (PrivateFiles.Exists(item.Target))
            {
                if (item.Installed || (File.GetAttributes(PrivateFiles.Extended(item.Target)) & FileAttributes.Directory) == 0)
                    throw new IOException("目标位置已被占用，请选择其他位置。" + Environment.NewLine + item.Target);
                PrivateFiles.VerifyDirectory(item.Target);
                if (Entries(item.Target).Any())
                    throw new IOException("目标目录已有内容，请选择其他位置。" + Environment.NewLine + item.Target);
            }
        }

        public List<MigrationRecord> Load()
        {
            var result = new List<MigrationRecord>();
            if (!Directory.Exists(journal)) return result;
            foreach (string path in Directory.EnumerateFiles(journal, "*.xml").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    using (var input = XmlReader.Create(path))
                    {
                        var item = (MigrationRecord)Serializer.Deserialize(input);
                        ValidateRecord(item);
                        if (Path.GetFileNameWithoutExtension(path) == item.Id) result.Add(item);
                    }
                }
                catch (InvalidOperationException) { }
                catch (IOException) { }
                catch (ArgumentException) { }
            }
            return result;
        }

        public void Move(MigrationRecord item, Action<string> log = null)
        {
            ValidateRecord(item);
            RequireNtfs(item.Target);
            using (Lock())
            {
                WriteLog(log, "检查源目录 " + item.Source);
                var tree = ReadTree(item.Source);
                if (installed && tree.Any(entry => !entry.Value && entry.Key.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("安装目录包含驱动文件，已停止迁移。");
                if (installed && tree.Where(entry => !entry.Value).Sum(entry => new FileInfo(PrivateFiles.Extended(Path.Combine(item.Source, entry.Key))).Length) >
                    new DriveInfo(Path.GetPathRoot(item.Target)).AvailableFreeSpace)
                    throw new IOException("目标盘空间不足。");
                CheckDestination(item, true, log);
                WriteLog(log, "写入迁移记录，创建临时目录 " + item.Staging);
                Save(item);
                PrivateFiles.CreateDirectory(item.Staging);
                var acl = Directory.GetAccessControl(PrivateFiles.Extended(item.Source), AccessControlSections.Access);
                acl.SetAccessRuleProtection(true, true);
                Directory.SetAccessControl(PrivateFiles.Extended(item.Staging), acl);
                Copy(item.Source, item.Staging, log);

                PrivateFiles.VerifyDirectory(item.Source);
                PrivateFiles.VerifyDirectory(item.Staging);
                if (installed) InstalledMigration.RequireStopped(item.Source, item.Target);
                WriteLog(log, "备份源目录 " + item.Backup);
                MoveSource(item.Source, item.Backup, tree);
                string emptyTarget = item.Staging + ".empty";
                bool reservedEmpty = false;
                try
                {
                    VerifyCopy(item.Backup, item.Staging, false, log);
                    CheckDestination(item, false, log);
                    if (PrivateFiles.Exists(item.Target))
                    {
                        WriteLog(log, "复用空目标目录 " + item.Target);
                        PrivateFiles.MoveDirectory(item.Target, emptyTarget);
                        reservedEmpty = true;
                        PrivateFiles.VerifyDirectory(emptyTarget);
                        if (Entries(emptyTarget).Any())
                            throw new IOException("目标目录已有新文件，请选择其他位置。");
                    }
                    WriteLog(log, "写入最终目标 " + item.Target);
                    PrivateFiles.MoveDirectory(item.Staging, item.Target);
                    WriteLog(log, "Windows API 建立目录联接 " + item.Source + " → " + item.Target);
                    PrivateFiles.CreateDirectory(item.PendingLink);
                    PrivateFiles.CreateJunction(item.PendingLink, item.Target);
                    PrivateFiles.MoveDirectory(item.PendingLink, item.Source);
                    if (!PrivateFiles.PointsTo(item.Source, item.Target))
                        throw new IOException("无法确认目录联接。请检查迁移记录里的两份目录。");
                }
                catch
                {
                    WriteLog(log, "迁移未完成，正在尝试恢复原目录");
                    try
                    {
                        if (PrivateFiles.PointsTo(item.Source, item.Target))
                            Directory.Delete(PrivateFiles.Extended(item.Source), false);
                        if (!PrivateFiles.Exists(item.Source))
                            PrivateFiles.MoveDirectory(item.Backup, item.Source);
                    }
                    finally
                    {
                        if (reservedEmpty)
                        {
                            if (!PrivateFiles.Exists(item.Target)) PrivateFiles.MoveDirectory(emptyTarget, item.Target);
                            else WriteLog(log, "原空目录暂存在 " + emptyTarget);
                        }
                    }
                    throw;
                }
                if (reservedEmpty)
                {
                    RemoveEmpty(emptyTarget);
                    if (PrivateFiles.Exists(emptyTarget)) WriteLog(log, "原目标目录保留在 " + emptyTarget);
                }
            }
        }

        private static void MoveSource(string source, string backup, Dictionary<string, bool> tree)
        {
            try { PrivateFiles.MoveDirectory(source, backup); }
            catch (IOException ex)
            {
                if ((ex.HResult & 0xffff) != 5) throw;
                foreach (string path in new[] { source }.Concat(tree.Keys.Select(entry => Path.Combine(source, entry))))
                {
                    int code = PrivateFiles.DeleteSharingError(path);
                    if (code == 32 || code == 33) throw PrivateFiles.OperationError("无法备份源目录", path, code);
                }
                throw;
            }
        }

        public void Restore(MigrationRecord item, Action<string> log = null)
        {
            ValidateRecord(item);
            using (Lock())
            {
                PrivateFiles.VerifyDirectory(Path.GetDirectoryName(item.Source));
                PrivateFiles.VerifyDirectory(item.Backup);
                bool hadLink = PrivateFiles.PointsTo(item.Source, item.Target);
                if (hadLink)
                {
                    WriteLog(log, "暂时移开目录联接 " + item.Source);
                    PrivateFiles.MoveDirectory(item.Source, item.PendingLink);
                }
                else if (PrivateFiles.Exists(item.Source))
                    throw new IOException("原路径已被占用，或联接目标有变化。两份数据已保留。");

                WriteLog(log, "恢复 C 盘备份 " + item.Backup + " → " + item.Source);
                try { PrivateFiles.MoveDirectory(item.Backup, item.Source); }
                catch
                {
                    if (hadLink && !PrivateFiles.Exists(item.Source))
                        PrivateFiles.MoveDirectory(item.PendingLink, item.Source);
                    throw;
                }
                if (PrivateFiles.PointsTo(item.PendingLink, item.Target))
                    Directory.Delete(PrivateFiles.Extended(item.PendingLink), false);
            }
        }

        public Cleanup.Result ReleaseBackup(MigrationRecord item, Action<string> log = null)
        {
            ValidateRecord(item);
            RequireNtfs(item.Target);
            using (Lock())
            {
                if (!PrivateFiles.PointsTo(item.Source, item.Target))
                    throw new IOException("原路径的联接已改动，已保留备份。");
                var tree = ReadTree(item.Backup);
                var files = new List<BackupFile>();
                foreach (var entry in tree.Where(entry => !entry.Value))
                {
                    string path = Path.Combine(item.Backup, entry.Key);
                    string target = Path.Combine(item.Target, entry.Key);
                    PrivateFiles.Info original, destination;
                    if (!PrivateFiles.TryCandidate(path, long.MaxValue, out original) ||
                        !PrivateFiles.TryCandidate(target, long.MaxValue, out destination))
                        throw new IOException("备份或目标存在无法校验的文件，已保留备份：" + entry.Key);
                    files.Add(new BackupFile { Original = path, Target = target, Before = original, Copy = destination });
                }
                VerifyCopy(item.Backup, item.Target, true, log);
                WriteLog(log, "释放已验证的备份文件 " + item.Backup);
                var result = new Cleanup.Result();
                foreach (BackupFile file in files)
                {
                    if (!TryRelease(file)) { result.Skipped++; continue; }
                    result.Deleted++;
                    result.Bytes += file.Before.Size;
                }
                foreach (var entry in tree.Where(entry => entry.Value).OrderByDescending(entry => entry.Key.Length))
                    RemoveEmpty(Path.Combine(item.Backup, entry.Key));
                RemoveEmpty(item.Backup);
                return result;
            }
        }

        private sealed class BackupFile
        {
            public string Original, Target;
            public PrivateFiles.Info Before, Copy;
        }

        private static bool TryRelease(BackupFile file)
        {
            try
            {
                using (PrivateFiles.OpenForVerification(file.Target, "::$DATA"))
                {
                    PrivateFiles.Info current;
                    return PrivateFiles.TryCandidate(file.Target, long.MaxValue, out current) && file.Copy.Matches(current) &&
                        PrivateFiles.TryDelete(file.Original, file.Before);
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void RemoveEmpty(string path)
        {
            try { PrivateFiles.VerifyDirectory(path); Directory.Delete(PrivateFiles.Extended(path), false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string ValidateSource(string value)
        {
            string source = PrivateFiles.FullPath(value);
            if (installed ? !InstalledFolders.IsAppFolder(source) : !IsUserCandidate(source))
                throw new IOException("请选择 C 盘当前用户目录里的单个软件配置或缓存文件夹。");
            RequireNtfs(source);
            return source;
        }

        public bool IsUserCandidate(string source)
        {
            if (!PrivateFiles.Below(source, profile) || Path.GetPathRoot(source) != Program.DefaultRoot ||
                source.IndexOf(".DiskDelta-", profile.Length, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            string[] parts = source.Substring(profile.Length + 1).Split('\\');
            bool appData = parts.Length >= 3 && parts[0].Equals("AppData", StringComparison.OrdinalIgnoreCase) &&
                new[] { "Local", "Roaming", "LocalLow" }.Contains(parts[1], StringComparer.OrdinalIgnoreCase) &&
                !Protected.Contains(parts[2]);
            bool dotFolder = parts[0].StartsWith(".", StringComparison.Ordinal) && !Protected.Contains(parts[0]);
            bool nestedDotFolder = parts.Length >= 2 &&
                new[] { ".config", ".cache" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase) && !Protected.Contains(parts[1]);
            return appData || dotFolder || nestedDotFolder;
        }

        private void ValidateRecord(MigrationRecord item)
        {
            Guid id;
            if (item == null || item.Installed != installed || !Guid.TryParseExact(item.Id, "N", out id) ||
                ValidateSource(item.Source) != item.Source || PrivateFiles.FullPath(item.Target) != item.Target ||
                item.Target.Length == 3 ||
                Path.GetPathRoot(item.Target) == Program.DefaultRoot ||
                (installed && !string.Equals(InstalledMigration.TargetPath(item.Source, Path.GetPathRoot(item.Target)),
                    item.Target, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("无法校验迁移记录路径。");
        }

        internal static void RequireNtfs(string path)
        {
            var drive = new DriveInfo(Path.GetPathRoot(path));
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady ||
                !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException("迁移需要已连接的固定 NTFS 磁盘：" + drive.Name);
        }

        private FileStream Lock()
        {
            Directory.CreateDirectory(journal);
            return new FileStream(Path.Combine(journal, "move.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        }

        private void Save(MigrationRecord item)
        {
            string path = Path.Combine(journal, item.Id + ".xml");
            string temp = path + ".tmp";
            using (var output = File.Create(temp)) Serializer.Serialize(output, item);
            File.Move(temp, path);
        }

        private static Dictionary<string, bool> ReadTree(string root)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            var folders = new Stack<string>();
            folders.Push(root);
            while (folders.Count > 0)
            {
                string parent = folders.Pop();
                PrivateFiles.VerifyDirectory(parent);
                if (PrivateFiles.Streams(parent).Any(name => name != "::$DATA"))
                    throw new IOException("目录包含额外数据流，已保留原始数据：" + parent);
                foreach (string path in Entries(parent))
                {
                    FileAttributes attrs = File.GetAttributes(PrivateFiles.Extended(path));
                    if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Encrypted)) != 0)
                        throw new IOException("发现链接、云占位或加密文件：" + path);
                    bool directory = (attrs & FileAttributes.Directory) != 0;
                    result.Add(path.Substring(root.Length + 1), directory);
                    if (directory) folders.Push(path);
                }
            }
            return result;
        }

        private static IEnumerable<string> Entries(string directory)
        {
            return Directory.EnumerateFileSystemEntries(PrivateFiles.Extended(directory))
                .Select(path => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path);
        }

        internal static void WriteLog(Action<string> log, string message)
        {
            if (log != null) log(message);
        }

        private static void Copy(string source, string target, Action<string> log)
        {
            string robocopy = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
            string arguments = "\"" + source + "\" \"" + target + "\" /E /COPY:DATS /DCOPY:DAT /SL /XJ /R:0 /W:0 /NFL /NDL /NJH /NP";
            var outputEncoding = Encoding.GetEncoding((int)GetOEMCP());
            WriteLog(log, "命令 \"" + robocopy + "\" " + arguments);
            using (var process = Process.Start(new ProcessStartInfo(robocopy, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = outputEncoding, StandardErrorEncoding = outputEncoding
            }))
            {
                var tail = new Queue<string>();
                int windowsError = 0;
                string errorLine = null;
                var errorCodePattern = new Regex(@"\((?:0x)([0-9a-fA-F]{8})\)");
                DataReceivedEventHandler read = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (tail)
                    {
                        tail.Enqueue(e.Data.Trim());
                        if (tail.Count > 12) tail.Dequeue();
                        if (windowsError == 0)
                        {
                            Match error = errorCodePattern.Match(e.Data);
                            if (error.Success)
                            {
                                windowsError = Convert.ToInt32(error.Groups[1].Value, 16);
                                errorLine = e.Data.Trim();
                            }
                        }
                    }
                    WriteLog(log, e.Data.Trim());
                };
                process.OutputDataReceived += read;
                process.ErrorDataReceived += read;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var elapsed = Stopwatch.StartNew();
                while (!process.WaitForExit(3000))
                    WriteLog(log, "Robocopy 正在复制 · " + (int)elapsed.Elapsed.TotalSeconds + " 秒");
                process.WaitForExit();
                WriteLog(log, "Robocopy 退出代码 " + process.ExitCode);
                if (process.ExitCode >= 8)
                {
                    string step = "Robocopy 复制失败（退出代码 " + process.ExitCode + "）";
                    if (windowsError != 0) throw PrivateFiles.OperationError(step, errorLine, windowsError);
                    string text = string.Join(Environment.NewLine, tail);
                    throw new IOException(step + "\n" + source + " → " + target + "\n" + text);
                }
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        private static void VerifyCopy(string source, string target, bool permitExtraTargetEntries, Action<string> log)
        {
            WriteLog(log, "使用 SHA-256 校验两份目录的文件内容及数据流");
            ReportVerification(log, 0, -1);
            int count = 0;
            try
            {
                var original = ReadTree(source);
                var copy = ReadTree(target);
                if (!permitExtraTargetEntries && original.Count != copy.Count)
                    throw new IOException("两份目录的项目数量不同，已保留原始数据。");
                int total = original.Count(entry => !entry.Value);
                ReportVerification(log, 0, total);
                using (SHA256 hash = SHA256.Create())
                {
                    var elapsed = Stopwatch.StartNew();
                    foreach (var entry in original)
                    {
                        bool isDirectory;
                        if (!copy.TryGetValue(entry.Key, out isDirectory) || isDirectory != entry.Value)
                            throw new IOException("缺少对应项目：" + entry.Key);
                        if (isDirectory) continue;
                        string left = Path.Combine(source, entry.Key);
                        string right = Path.Combine(target, entry.Key);
                        var streams = PrivateFiles.Streams(left).ToArray();
                        if (!new HashSet<string>(streams, StringComparer.Ordinal).SetEquals(PrivateFiles.Streams(right)))
                            throw new IOException("文件数据流不同：" + entry.Key);
                        foreach (string stream in streams)
                        {
                            using (Stream input = PrivateFiles.OpenForVerification(left, stream))
                            using (Stream duplicate = PrivateFiles.OpenForVerification(right, stream))
                                if (input.Length != duplicate.Length || !hash.ComputeHash(input).SequenceEqual(hash.ComputeHash(duplicate)))
                                    throw new IOException("目标文件内容不同，已保留原始数据：" + entry.Key);
                        }
                        count++;
                        if (count == total || elapsed.ElapsedMilliseconds >= 150)
                        {
                            ReportVerification(log, count, total);
                            elapsed.Restart();
                        }
                    }
                }
            }
            finally { ReportVerification(log, -1, 0); }
            WriteLog(log, "校验完成 · " + count.ToString("N0") + " 个文件");
        }

        private static void ReportVerification(Action<string> log, int count, int total)
        {
            WriteLog(log, VerificationProgressPrefix + count.ToString(CultureInfo.InvariantCulture) + " " +
                total.ToString(CultureInfo.InvariantCulture));
        }
    }
}
