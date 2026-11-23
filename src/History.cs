using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace DiskDelta
{
    internal sealed class History
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Report));
        public readonly string DirectoryPath;
        private readonly string excluded;

        public History(string directory) : this(directory, directory) { }

        private History(string directory, string excluded)
        {
            DirectoryPath = Path.GetFullPath(directory);
            this.excluded = Path.GetFullPath(excluded).TrimEnd('\\') + "\\";
        }

        public static History ForDrive(string directory, string root, string volumeId)
        {
            if (root == Program.DefaultRoot) return new History(directory);
            return new History(Path.Combine(directory, "Volumes", root[0] + "-" + (volumeId ?? "unknown")), directory);
        }

        public List<Report> LoadFor(string root, string volumeId)
        {
            return Load().Where(item => item.Root == root &&
                (item.VolumeId == volumeId || (root == Program.DefaultRoot && item.VolumeId == null))).ToList();
        }

        public IEnumerable<DirectoryChange> ReadTree(Report report)
        {
            return Scanner.ReadDirectoryChanges(TreePath(report.Id));
        }

        public bool RebuildTree(Report report)
        {
            if (report.Baseline == default(DateTime) || !File.Exists(SnapshotPath(report.Id))) return false;
            using (new FileStream(Path.Combine(DirectoryPath, "scan.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                if (File.Exists(TreePath(report.Id))) return true;
                Report baseline = Load().FirstOrDefault(item => item.Root == report.Root && item.Started == report.Baseline &&
                    (item.VolumeId == report.VolumeId || item.VolumeId == null));
                if (baseline == null || !File.Exists(SnapshotPath(baseline.Id)) || !File.Exists(SnapshotPath(report.Id))) return false;
                string temporary = TreePath(report.Id) + ".tmp";
                try
                {
                    // Rebuild only the tree. Keep the saved comparison and the rankings.
                    Scanner.Compare(SnapshotPath(baseline.Id), SnapshotPath(report.Id), temporary,
                        new Report { Root = report.Root }, CancellationToken.None);
                    File.Move(temporary, TreePath(report.Id));
                    return true;
                }
                finally { TryDelete(temporary); }
            }
        }

        public List<Report> Load()
        {
            var reports = new List<Report>();
            if (!Directory.Exists(DirectoryPath))
                return reports;
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.report.xml"))
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    using (var reader = XmlReader.Create(stream))
                    {
                        var report = (Report)Serializer.Deserialize(reader);
                        if (report != null && IsId(report.Id) &&
                            string.Equals(path, ReportPath(report.Id), StringComparison.OrdinalIgnoreCase))
                            reports.Add(report);
                    }
                }
                catch (IOException) { }
                catch (InvalidOperationException) { }
            }
            return reports.OrderByDescending(report => report.Id, StringComparer.Ordinal).ToList();
        }

        public Report Scan(string root, TimeSpan? scheduledTime, Action<long, string> progress,
            CancellationToken cancellation, string expectedVolumeId = null)
        {
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("请选择本地磁盘上的目录。", "root");
            root = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string volumeRoot = Path.GetPathRoot(root);
            string volumeId = Volume.ReadId(volumeRoot);
            if (expectedVolumeId != null && volumeId != expectedVolumeId)
                throw new IOException("该盘符的磁盘已变更或不可用。");
            Directory.CreateDirectory(DirectoryPath);
            using (new FileStream(Path.Combine(DirectoryPath, "scan.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                var started = DateTime.Now;
                var reports = LoadFor(root, volumeId);
                if (scheduledTime.HasValue && (started.TimeOfDay < scheduledTime.Value ||
                    reports.Any(item => item.Started.Date == started.Date && item.Started.TimeOfDay >= scheduledTime.Value)))
                    return null;

                var available = reports.Where(item => File.Exists(SnapshotPath(item.Id))).ToList();
                var baseline = available.FirstOrDefault(item => item.Started.Date < started.Date)
                    ?? available.LastOrDefault(item => item.Started.Date == started.Date)
                    ?? available.FirstOrDefault();
                var report = new Report
                {
                    Root = root,
                    VolumeId = volumeId,
                    Id = started.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture),
                    Started = started,
                    FileChanges = new List<Change>(),
                    FolderChanges = new List<Change>()
                };
                string temporarySnapshot = SnapshotPath(report.Id) + ".tmp";
                string temporaryReport = ReportPath(report.Id) + ".tmp";
                string temporaryTree = TreePath(report.Id) + ".tmp";
                try
                {
                    Scanner.Capture(temporarySnapshot, excluded, report, progress, cancellation);
                    if (expectedVolumeId != null && Volume.ReadId(volumeRoot) != expectedVolumeId)
                        throw new IOException("扫描期间磁盘已变更或不可用。");
                    if (baseline != null)
                    {
                        if (progress != null) progress(report.Files, null);
                        Scanner.Compare(SnapshotPath(baseline.Id), temporarySnapshot, temporaryTree, report, cancellation);
                        report.Baseline = baseline.Started;
                        report.BaselineFree = baseline.Free;
                        report.BaselineUnreadable = baseline.Unreadable;
                    }

                    cancellation.ThrowIfCancellationRequested();
                    using (var stream = File.Create(temporaryReport))
                        Serializer.Serialize(stream, report);
                    File.Move(temporarySnapshot, SnapshotPath(report.Id));
                    if (baseline != null) File.Move(temporaryTree, TreePath(report.Id));
                    File.Move(temporaryReport, ReportPath(report.Id));
                    Prune(root, volumeId);
                    return report;
                }
                finally
                {
                    TryDelete(temporaryReport);
                    TryDelete(temporarySnapshot);
                    TryDelete(temporaryTree);
                }
            }
        }

        private void Prune(string root, string volumeId)
        {
            var reports = LoadFor(root, volumeId);
            var days = reports.GroupBy(item => item.Started.Date).ToList();
            if (days.Count == 0)
                return;
            var snapshots = new HashSet<string>(StringComparer.Ordinal) { SnapshotPath(days[0].First().Id) };
            if (days.Count > 1)
                snapshots.Add(SnapshotPath(days[1].First().Id));
            else
                snapshots.Add(SnapshotPath(days[0].Last().Id));

            foreach (Report report in reports)
            {
                string snapshot = SnapshotPath(report.Id);
                if (!snapshots.Contains(snapshot))
                    TryDelete(snapshot);
            }
            foreach (var day in days.Take(30))
            {
                // During the first day keep the original baseline as well as the latest scan.
                foreach (Report report in day.Skip(1))
                    if (days.Count > 1 || !snapshots.Contains(SnapshotPath(report.Id)))
                        TryDelete(ReportPath(report.Id));
            }
            foreach (var day in days.Skip(30))
                foreach (Report report in day)
                    TryDelete(ReportPath(report.Id));

            var remaining = Load();
            DeleteOrphans(".snapshot.gz", new HashSet<string>(remaining.Select(item => SnapshotPath(item.Id)), StringComparer.Ordinal));
            DeleteOrphans(".folders.gz", new HashSet<string>(remaining.Select(item => TreePath(item.Id)), StringComparer.Ordinal));
            DeleteOrphans(".snapshot.gz.tmp", null);
            DeleteOrphans(".report.xml.tmp", null);
            DeleteOrphans(".folders.gz.tmp", null);
        }

        private void DeleteOrphans(string suffix, HashSet<string> keep)
        {
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*" + suffix))
            {
                string filename = Path.GetFileName(path);
                if (filename.EndsWith(suffix, StringComparison.Ordinal) &&
                    IsId(filename.Substring(0, filename.Length - suffix.Length)) &&
                    (keep == null || !keep.Contains(path)))
                    TryDelete(path);
            }
        }

        private static bool IsId(string value)
        {
            DateTime timestamp;
            return DateTime.TryParseExact(value, "yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out timestamp);
        }

        public void WriteScheduledError(Exception error)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "daily-error.txt"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " · " + error.Message);
        }

        public string ReadScheduledError()
        {
            try { return File.ReadAllText(Path.Combine(DirectoryPath, "daily-error.txt")); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void ClearScheduledError() { TryDelete(Path.Combine(DirectoryPath, "daily-error.txt")); }
        private string SnapshotPath(string id) { return Path.Combine(DirectoryPath, id + ".snapshot.gz"); }
        private string ReportPath(string id) { return Path.Combine(DirectoryPath, id + ".report.xml"); }
        private string TreePath(string id) { return Path.Combine(DirectoryPath, id + ".folders.gz"); }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
