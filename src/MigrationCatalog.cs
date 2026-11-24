using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiskDelta
{
    internal sealed class MigrationOption
    {
        public string Source;
        public string Name;
        public string Target;
        public string Status;
        public bool Installed;
        public bool Linked;
        public bool CanUse;
    }

    internal static class MigrationCatalog
    {
        private static readonly HashSet<string> CacheNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cache", "Code Cache", "GPUCache", "CachedData"
        };

        public static List<MigrationOption> Scan(string profile, FolderMigration user, IEnumerable<MigrationRecord> records = null)
        {
            var installed = new InstalledFolders();
            var options = new List<MigrationOption>();
            var userSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in new[] { "", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow", ".cache", ".config" })
            foreach (string entry in Cleanup.Entries(Path.Combine(profile, root), true))
            {
                if (!userSeen.Add(entry)) continue;
                Add(options, entry, user, null, false);
                if (!user.IsUserCandidate(entry) || IsLink(entry)) continue;
                foreach (string child in Cleanup.Entries(entry, true))
                {
                    string name = Path.GetFileName(child);
                    if ((CacheNames.Contains(name) || IsLink(child)) && userSeen.Add(child))
                        Add(options, child, user, null, false, Path.GetFileName(entry) + " / " + name);
                }
            }

            var candidates = new HashSet<string>(installed.RegisteredPaths, StringComparer.OrdinalIgnoreCase);
            if (records != null)
                foreach (MigrationRecord record in records)
                    if (record.Installed) candidates.Add(record.Source);
                    else if (userSeen.Add(record.Source)) Add(options, record.Source, user, null, false);
            foreach (string root in InstalledFolders.Roots)
            foreach (string entry in Cleanup.Entries(root, true))
            {
                candidates.Add(entry);
                if (!InstalledFolders.IsAppFolder(entry)) continue;
                foreach (string child in Cleanup.Entries(entry, true))
                    if (IsLink(child)) candidates.Add(child);
            }
            foreach (string entry in candidates)
                Add(options, entry, user, installed, true);
            return options.OrderBy(item => item.Linked ? 0 : item.CanUse ? 1 : 2)
                .ThenBy(item => item.Installed).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void Add(List<MigrationOption> rows, string path, FolderMigration user, InstalledFolders installed,
            bool isInstall, string name = null)
        {
            try
            {
                if (path.IndexOf(".DiskDelta-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(path, InstalledMigration.Journal, StringComparison.OrdinalIgnoreCase)) return;
                FileAttributes attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Directory) == 0) return;
                var row = new MigrationOption
                {
                    Source = path, Installed = isInstall, Name = name ?? (isInstall ? installed.Name(path) : Path.GetFileName(path))
                };
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    row.Linked = true;
                    row.Target = PrivateFiles.DirectoryLinkTarget(path, out row.Status);
                    if (row.Target == null) row.Status += " · 目标不可读取";
                }
                else if (isInstall)
                {
                    row.Status = installed.BlockReason(path, false);
                    if (row.Status == null)
                    {
                        try { InstalledMigration.RequireSourcePath(path); }
                        catch (IOException) { row.Status = "目录权限或路径未通过校验"; }
                        catch (UnauthorizedAccessException) { row.Status = "目录权限无法读取"; }
                    }
                    row.CanUse = row.Status == null;
                    if (row.CanUse) row.Status = installed.NeedsElevatedServiceCheck ? "高风险 · 需管理员预检" : "高风险 · 可预检";
                }
                else
                {
                    if (!user.IsUserCandidate(path)) return;
                    row.CanUse = (attrs & (FileAttributes.Offline | FileAttributes.Encrypted)) == 0;
                    row.Status = row.CanUse ? "可预检" : "云占位或加密目录";
                }
                rows.Add(row);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { rows.Add(Unavailable(path, isInstall)); }
            catch (UnauthorizedAccessException) { rows.Add(Unavailable(path, isInstall)); }
        }

        private static MigrationOption Unavailable(string path, bool installed)
        {
            return new MigrationOption { Source = path, Name = Path.GetFileName(path), Installed = installed, Status = "目录无法读取" };
        }
    }
}
