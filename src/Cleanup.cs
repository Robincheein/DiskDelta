using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DiskDelta
{
    internal sealed class CleanupFile
    {
        public string Root;
        public string Path;
        public PrivateFiles.Info Info;
    }

    internal sealed class CleanupGroup
    {
        public string Name;
        public string Description;
        public string App;
        public string BrowserData;
        public readonly List<CleanupFile> Files = new List<CleanupFile>();
        public long Bytes { get { return Files.Sum(item => item.Info.Size); } }
    }

    internal sealed class Cleanup
    {
        private readonly string local;

        public Cleanup(string localAppData) { local = PrivateFiles.FullPath(localAppData); }

        public List<CleanupGroup> Scan()
        {
            var groups = new List<CleanupGroup>
            {
                new CleanupGroup
                {
                    Name = "DirectX 着色器缓存",
                    Description = "DirectX 着色器缓存。应用下次使用时会重新生成。"
                },
                Browser("Chrome 网页缓存", "chrome", @"Google\Chrome\User Data"),
                Browser("Edge 网页缓存", "msedge", @"Microsoft\Edge\User Data")
            };
            long cutoff = DateTime.UtcNow.AddDays(-7).ToFileTimeUtc();
            Walk(Path.Combine(local, "D3DSCache"), cutoff, groups[0]);
            foreach (CleanupGroup group in groups.Skip(1))
            {
                string profileRoot = Path.Combine(local, group.BrowserData);
                foreach (string profile in Entries(profileRoot, true))
                {
                    string name = Path.GetFileName(profile);
                    int index;
                    if (name == "Default" || (name.StartsWith("Profile ", StringComparison.Ordinal) &&
                        int.TryParse(name.Substring(8), out index) && index >= 0))
                        Walk(Path.Combine(profile, @"Cache\Cache_Data"), cutoff, group);
                }
            }
            return groups;
        }

        public sealed class Result
        {
            public int Deleted;
            public int Skipped;
            public long Bytes;
        }

        public static Result Apply(IEnumerable<CleanupGroup> groups)
        {
            var result = new Result();
            foreach (CleanupGroup group in groups)
            {
                if (group.App != null && IsRunning(group.App))
                {
                    result.Skipped += group.Files.Count;
                    continue;
                }
                foreach (CleanupFile file in group.Files)
                {
                    if (!PrivateFiles.Below(file.Path, file.Root) || !PrivateFiles.TryDelete(file.Path, file.Info))
                    {
                        result.Skipped++;
                        continue;
                    }
                    result.Deleted++;
                    result.Bytes += file.Info.Size;
                }
            }
            return result;
        }

        private static bool IsRunning(string app)
        {
            Process[] processes = Process.GetProcessesByName(app);
            foreach (Process process in processes) process.Dispose();
            return processes.Length > 0;
        }

        private static CleanupGroup Browser(string name, string app, string path)
        {
            return new CleanupGroup
            {
                Name = name, App = app, BrowserData = path,
                Description = "网页 HTTP 缓存。退出浏览器后可清理。"
            };
        }

        private static void Walk(string root, long cutoff, CleanupGroup group)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                string folder = pending.Pop();
                foreach (string path in Entries(folder, false))
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else
                    {
                        PrivateFiles.Info info;
                        if (PrivateFiles.TryCandidate(path, cutoff, out info))
                            group.Files.Add(new CleanupFile { Root = root, Path = path, Info = info });
                    }
                }
            }
        }

        private static string[] Entries(string folder, bool directoriesOnly)
        {
            try
            {
                PrivateFiles.VerifyDirectory(folder);
                return directoriesOnly ? Directory.GetDirectories(folder) : Directory.GetFileSystemEntries(folder);
            }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }
    }
}
