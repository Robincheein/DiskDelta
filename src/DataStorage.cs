using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace DiskDelta
{
    internal sealed class DataStorage
    {
        private static readonly Regex HistoryFile = new Regex(@"^\d{8}-\d{6}-\d{7}\.(report\.xml|snapshot\.gz|folders\.gz)$", RegexOptions.IgnoreCase);
        private static readonly Regex VolumeFolder = new Regex(@"^[A-Z]-(?:unknown|[0-9a-f]{8}|[0-9a-f]{32})$", RegexOptions.IgnoreCase);
        private static readonly Regex CacheFile = new Regex(@"^(?:cleanup|software)-scan-v\d+\.bin\.gz$", RegexOptions.IgnoreCase);
        private readonly string control, legacy, defaultPath, config;
        public string Notice { get; private set; }

        public DataStorage(string control, string legacy, string defaultPath)
        {
            this.control = control;
            this.legacy = legacy;
            this.defaultPath = defaultPath;
            config = Path.Combine(control, "data-directory.txt");
        }

        public static DataStorage ForCurrentUser()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string legacy = Path.Combine(local, "DiskDelta");
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return new DataStorage(Path.Combine(local, "DiskDelta.Settings"), legacy,
                string.IsNullOrEmpty(documents) ? legacy : Path.Combine(documents, "DiskDelta"));
        }

        public string Resolve()
        {
            if (File.Exists(config)) return ReadPath();
            using (Acquire(true))
            {
                if (File.Exists(config)) return ReadPath();
                try
                {
                    RelocateCore(legacy, defaultPath);
                    return ReadPath();
                }
                catch (IOException ex) { return Fallback(ex); }
                catch (UnauthorizedAccessException ex) { return Fallback(ex); }
            }
        }

        private string Fallback(Exception error)
        {
            Notice = "无法切换到默认数据位置，暂用原目录。" + Environment.NewLine + error.Message;
            SavePath(legacy);
            return legacy;
        }

        public string Move(string current, string destination)
        {
            using (Acquire(true))
            {
                Notice = null;
                if (!Same(File.Exists(config) ? ReadPath() : legacy, current))
                    throw new IOException("数据位置已改变，请重新打开磁盘日记后再试。");
                return RelocateCore(current, destination);
            }
        }

        public FileStream OpenScan(out string path)
        {
            string resolved = Resolve();
            var handle = Acquire(false);
            try { path = File.Exists(config) ? ReadPath() : resolved; return handle; }
            catch { handle.Dispose(); throw; }
        }

        private string ReadPath() { return PrivateFiles.FullPath(File.ReadAllText(config).Trim()); }

        private FileStream Acquire(bool exclusive)
        {
            Directory.CreateDirectory(control);
            string path = Path.Combine(control, "data.lock");
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                    return new FileStream(path, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                        exclusive ? FileShare.None : FileShare.Read);
                }
                catch (IOException ex)
                {
                    if ((ex.HResult & 0xffff) != 32 && (ex.HResult & 0xffff) != 33) throw;
                    if (attempt >= 600) throw new IOException("磁盘扫描或数据迁移仍在运行，请稍后重试。", ex);
                    Thread.Sleep(200);
                }
            }
        }

        private static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        private string RelocateCore(string current, string destination)
        {
            destination = PrivateFiles.FullPath(destination.Trim());
            if (Same(current, destination)) { SavePath(destination); return destination; }
            if (destination.Length == 3 || PrivateFiles.Below(destination, current) ||
                PrivateFiles.Below(current, destination) || Same(control, destination) ||
                PrivateFiles.Below(destination, control) || PrivateFiles.Below(control, destination))
                throw new IOException("请选择独立的数据目录。");
            string parent = Path.GetDirectoryName(destination);
            EnsureFolder(parent);
            bool present = PrivateFiles.Exists(destination);
            if (present)
            {
                PrivateFiles.VerifyDirectory(destination);
                if (Directory.EnumerateFileSystemEntries(destination).Any())
                    throw new IOException("目标目录已有内容，请选择空目录或新建目录。");
            }

            var files = ReadFiles(current).ToArray();
            string staging = Path.Combine(parent, ".DiskDelta-data-" + Guid.NewGuid().ToString("N"));
            PrivateFiles.CreateDirectory(staging);
            var copied = new Dictionary<string, PrivateFiles.Info>();
            string empty = staging + ".empty";
            bool reserved = false, published = false, committed = false;
            try
            {
                using (var hash = SHA256.Create())
                    foreach (string relative in files)
                    {
                        string source = Path.Combine(current, relative);
                        string target = Path.Combine(staging, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var original = PrivateFiles.OpenForVerification(source, "::$DATA"))
                        {
                            File.Copy(source, target);
                            using (var copy = File.OpenRead(target))
                                if (original.Length != copy.Length || !hash.ComputeHash(original).SequenceEqual(hash.ComputeHash(copy)))
                                    throw new IOException("数据校验失败：" + source);
                            PrivateFiles.Info info;
                            if (PrivateFiles.TryCandidate(source, long.MaxValue, out info)) copied.Add(source, info);
                        }
                    }
                if (present)
                {
                    PrivateFiles.VerifyDirectory(destination);
                    if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("目标目录新增了文件。");
                    PrivateFiles.MoveDirectory(destination, empty);
                    reserved = true;
                    PrivateFiles.VerifyDirectory(empty);
                    if (Directory.EnumerateFileSystemEntries(empty).Any()) throw new IOException("目标目录新增了文件。");
                }
                PrivateFiles.MoveDirectory(staging, destination);
                published = true;
                SavePath(destination);
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    if (published) PrivateFiles.MoveDirectory(destination, staging);
                    if (reserved && !PrivateFiles.Exists(destination)) PrivateFiles.MoveDirectory(empty, destination);
                    CleanStage(staging, files);
                }
                else if (reserved) RemoveEmpty(empty);
            }
            int retained = files.Length - copied.Count;
            foreach (var file in copied)
                if (!PrivateFiles.TryDelete(file.Key, file.Value)) retained++;
            if (retained > 0) Notice = "数据目录已切换，原目录仍保留 " + retained + " 个已复制的文件。";
            return destination;
        }

        private static void EnsureFolder(string path)
        {
            if (!PrivateFiles.Exists(path))
            {
                string parent = Path.GetDirectoryName(path);
                if (parent == null) throw new IOException("数据所在的磁盘不可用：" + path);
                EnsureFolder(parent);
                PrivateFiles.CreateDirectory(path);
            }
            PrivateFiles.VerifyDirectory(path);
        }

        private static IEnumerable<string> ReadFiles(string root)
        {
            if (!PrivateFiles.Exists(root)) yield break;
            PrivateFiles.VerifyDirectory(root);
            foreach (string name in Files(root))
                if (HistoryFile.IsMatch(name) || CacheFile.IsMatch(name) || name == "daily-error.txt" ||
                    name == "tray.enabled" || name == "update-proxy.txt" || name == "change-colors.txt") yield return name;
            string volumes = Path.Combine(root, "Volumes");
            if (PrivateFiles.Exists(volumes))
            {
                PrivateFiles.VerifyDirectory(volumes);
                foreach (string dir in Directory.EnumerateDirectories(volumes))
                    if (VolumeFolder.IsMatch(Path.GetFileName(dir)))
                        foreach (string name in Files(dir))
                            if (HistoryFile.IsMatch(name) || name == "daily-error.txt")
                                yield return Path.Combine("Volumes", Path.GetFileName(dir), name);
            }
            string migrations = Path.Combine(root, "Migrations");
            if (!PrivateFiles.Exists(migrations)) yield break;
            foreach (string name in Files(migrations))
            {
                Guid id;
                if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out id)) yield return Path.Combine("Migrations", name);
            }
        }

        private static IEnumerable<string> Files(string path)
        {
            PrivateFiles.VerifyDirectory(path);
            return Directory.EnumerateFiles(path).Select(Path.GetFileName);
        }

        private void SavePath(string path)
        {
            string temp = config + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, path); PrivateFiles.ReplaceFile(temp, config); }
            finally { TryDiscard(temp); }
        }

        private static void CleanStage(string staging, IEnumerable<string> files)
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in files)
            {
                string path = Path.Combine(staging, name);
                TryDiscard(path);
                for (string dir = Path.GetDirectoryName(path); !Same(dir, staging); dir = Path.GetDirectoryName(dir)) dirs.Add(dir);
            }
            foreach (string dir in dirs.OrderByDescending(value => value.Length)) RemoveEmpty(dir);
            RemoveEmpty(staging);
        }

        private static void TryDiscard(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void RemoveEmpty(string path)
        {
            try { PrivateFiles.VerifyDirectory(path); Directory.Delete(path, false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
