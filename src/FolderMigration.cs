using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;

namespace DiskDelta
{
    public sealed class MigrationRecord
    {
        public string Id { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        [XmlIgnore] public string Backup { get { return Source + ".DiskDelta-" + Id + ".backup"; } }
        [XmlIgnore] public string PendingLink { get { return Source + ".DiskDelta-" + Id + ".link"; } }
        [XmlIgnore] public string Staging { get { return Target + ".DiskDelta-" + Id + ".staging"; } }
        public override string ToString() { return Path.GetFileName(Source) + "   →   " + Path.GetPathRoot(Target); }
    }

    internal sealed class FolderMigration
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(MigrationRecord));
        private static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Packages", "Programs", "Temp", "CrashDumps", "DiskDelta",
            "ConnectedDevicesPlatform", "Comms", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".git",
            ".codex", ".config", ".cache", ".local"
        };

        private readonly string profile;
        private readonly string journal;
        public FolderMigration(string userProfile, string dataDirectory)
        {
            profile = PrivateFiles.FullPath(userProfile);
            journal = Path.Combine(dataDirectory, "Migrations");
        }

        public MigrationRecord Preview(string source, string destinationFolder)
        {
            source = ValidateSource(source);
            string destination = PrivateFiles.FullPath(destinationFolder);
            RequireNtfs(destination);
            if (string.Equals(Path.GetPathRoot(destination), Program.DefaultRoot, StringComparison.OrdinalIgnoreCase) ||
                Volume.ReadId(Path.GetPathRoot(destination)) == Volume.ReadId(Program.DefaultRoot))
                throw new IOException("请选择 C 盘以外的固定 NTFS 磁盘。");
            PrivateFiles.VerifyDirectory(destination);
            PrivateFiles.VerifyDirectory(source);
            string target = Path.Combine(destination, Path.GetFileName(source));
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("目标同名路径已存在：" + target);
            return new MigrationRecord { Id = Guid.NewGuid().ToString("N"), Source = source, Target = target };
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

        public void Move(MigrationRecord item)
        {
            ValidateRecord(item);
            RequireNtfs(item.Target);
            using (Lock())
            {
                ReadTree(item.Source);
                PrivateFiles.VerifyDirectory(Path.GetDirectoryName(item.Target));
                if (Directory.Exists(item.Target) || File.Exists(item.Target))
                    throw new IOException("目标同名路径已存在：" + item.Target);
                Save(item);
                PrivateFiles.CreateDirectory(item.Staging);
                var acl = Directory.GetAccessControl(item.Source, AccessControlSections.Access);
                acl.SetAccessRuleProtection(true, true);
                Directory.SetAccessControl(item.Staging, acl);
                Copy(item.Source, item.Staging);

                PrivateFiles.VerifyDirectory(item.Source);
                PrivateFiles.VerifyDirectory(item.Staging);
                Directory.Move(item.Source, item.Backup);
                try
                {
                    VerifyCopy(item.Backup, item.Staging, false);
                    Directory.Move(item.Staging, item.Target);
                    PrivateFiles.CreateDirectory(item.PendingLink);
                    PrivateFiles.CreateJunction(item.PendingLink, item.Target);
                    Directory.Move(item.PendingLink, item.Source);
                    if (!PrivateFiles.PointsTo(item.Source, item.Target))
                        throw new IOException("无法确认目录联接。请检查迁移记录里的两份目录。");
                }
                catch
                {
                    if (!Directory.Exists(item.Source) && !File.Exists(item.Source))
                        Directory.Move(item.Backup, item.Source);
                    throw;
                }
            }
        }

        public void Restore(MigrationRecord item)
        {
            ValidateRecord(item);
            using (Lock())
            {
                PrivateFiles.VerifyDirectory(Path.GetDirectoryName(item.Source));
                PrivateFiles.VerifyDirectory(item.Backup);
                bool hadLink = PrivateFiles.PointsTo(item.Source, item.Target);
                if (hadLink)
                    Directory.Move(item.Source, item.PendingLink);
                else if (Directory.Exists(item.Source) || File.Exists(item.Source))
                    throw new IOException("原路径已被占用，或联接目标有变化。两份数据已保留。");

                try { Directory.Move(item.Backup, item.Source); }
                catch
                {
                    if (hadLink && !Directory.Exists(item.Source) && !File.Exists(item.Source))
                        Directory.Move(item.PendingLink, item.Source);
                    throw;
                }
                if (PrivateFiles.PointsTo(item.PendingLink, item.Target))
                    Directory.Delete(item.PendingLink, false);
            }
        }

        public Cleanup.Result ReleaseBackup(MigrationRecord item)
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
                VerifyCopy(item.Backup, item.Target, true);
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
            try { PrivateFiles.VerifyDirectory(path); Directory.Delete(path, false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string ValidateSource(string value)
        {
            string source = PrivateFiles.FullPath(value);
            if (!PrivateFiles.Below(source, profile) || Path.GetPathRoot(source) != Program.DefaultRoot)
                throw new IOException("请选择 C 盘当前用户目录里的单个软件配置或缓存文件夹。");
            string[] parts = source.Substring(profile.Length + 1).Split('\\');
            bool appData = parts.Length >= 3 && parts[0].Equals("AppData", StringComparison.OrdinalIgnoreCase) &&
                new[] { "Local", "Roaming", "LocalLow" }.Contains(parts[1], StringComparer.OrdinalIgnoreCase) &&
                !Protected.Contains(parts[2]);
            bool dotFolder = parts[0].StartsWith(".", StringComparison.Ordinal) && !Protected.Contains(parts[0]);
            bool nestedDotFolder = parts.Length >= 2 &&
                new[] { ".config", ".cache" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase) && !Protected.Contains(parts[1]);
            if (!appData && !dotFolder && !nestedDotFolder)
                throw new IOException("请选择单个应用的 AppData 子目录或用户目录下的软件点文件夹。");
            RequireNtfs(source);
            return source;
        }

        private void ValidateRecord(MigrationRecord item)
        {
            Guid id;
            if (item == null || !Guid.TryParseExact(item.Id, "N", out id) ||
                ValidateSource(item.Source) != item.Source || PrivateFiles.FullPath(item.Target) != item.Target ||
                Path.GetPathRoot(item.Target) == Program.DefaultRoot ||
                !string.Equals(Path.GetFileName(item.Source), Path.GetFileName(item.Target), StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法校验迁移记录路径。");
        }

        private static void RequireNtfs(string path)
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
                foreach (string path in Directory.EnumerateFileSystemEntries(parent))
                {
                    FileAttributes attrs = File.GetAttributes(path);
                    if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Encrypted)) != 0)
                        throw new IOException("发现链接、云占位或加密文件：" + path);
                    bool directory = (attrs & FileAttributes.Directory) != 0;
                    result.Add(path.Substring(root.Length + 1), directory);
                    if (directory) folders.Push(path);
                }
            }
            return result;
        }

        private static void Copy(string source, string target)
        {
            string robocopy = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
            string arguments = "\"" + source + "\" \"" + target + "\" /E /COPY:DATS /DCOPY:DAT /SL /XJ /R:0 /W:0 /NFL /NDL /NJH /NJS /NP";
            using (var process = Process.Start(new ProcessStartInfo(robocopy, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            }))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                if (process.ExitCode >= 8)
                {
                    string text = (output.Result + errors.Result).Trim();
                    throw new IOException("文件复制未完成（" + process.ExitCode + "）\n" + text.Substring(0, Math.Min(text.Length, 700)));
                }
            }
        }

        private static void VerifyCopy(string source, string target, bool permitExtraTargetEntries)
        {
            var original = ReadTree(source);
            var copy = ReadTree(target);
            if (!permitExtraTargetEntries && original.Count != copy.Count)
                throw new IOException("两份目录的项目数量不同，已保留原始数据。");
            using (SHA256 hash = SHA256.Create())
            {
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
                }
            }
        }
    }
}
