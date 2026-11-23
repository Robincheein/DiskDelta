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
        public string Category;
        public string Name;
        public string Description;
        public string App;
        public string Root;
        public string ProfileCache;
        public bool ChromiumProfiles;
        public bool Recursive = true;
        public int Days = 7;
        public Func<string, bool> Accepts;
        public readonly List<CleanupFile> Files = new List<CleanupFile>();
        public long Bytes { get { return Files.Sum(item => item.Info.Size); } }
    }

    internal sealed class Cleanup
    {
        private readonly string local;

        public Cleanup(string localAppData) { local = PrivateFiles.FullPath(localAppData); }

        public List<CleanupGroup> Options()
        {
            var groups = new List<CleanupGroup>
            {
                new CleanupGroup { Category = "临时文件", Name = "用户临时文件", Root = "Temp", Days = 14,
                    Description = "当前用户临时目录中的 .tmp 和 .temp 文件。",
                    Accepts = name => HasExtension(name, ".tmp", ".temp") },
                new CleanupGroup { Category = "Windows 缓存", Name = "DirectX 着色器缓存", Root = "D3DSCache",
                    Description = "DirectX 应用生成的着色器缓存。" },
                ExplorerCache("缩略图缓存", "thumbcache_", "文件资源管理器保存的缩略图。"),
                ExplorerCache("图标缓存", "iconcache_", "文件资源管理器保存的图标。"),
                new CleanupGroup { Category = "Windows 缓存", Name = "远程桌面位图缓存", App = "mstsc",
                    Root = @"Microsoft\Terminal Server Client\Cache", Recursive = false,
                    Description = "远程桌面显示缓存。退出远程桌面客户端后可清理。",
                    Accepts = name => HasExtension(name, ".bmc") ||
                        (name.StartsWith("Cache", StringComparison.OrdinalIgnoreCase) && HasExtension(name, ".bin")) }
            };
            AddChromium(groups, "Chrome", "chrome", @"Google\Chrome\User Data");
            AddChromium(groups, "Edge", "msedge", @"Microsoft\Edge\User Data");
            AddChromium(groups, "Brave", "brave", @"BraveSoftware\Brave-Browser\User Data");
            groups.Add(new CleanupGroup { Category = "浏览器缓存", Name = "Firefox 网页缓存", App = "firefox",
                Root = @"Mozilla\Firefox\Profiles", ProfileCache = @"cache2\entries", Recursive = false,
                Description = "Firefox 下载的网页缓存。退出浏览器后可清理。" });
            groups.Add(new CleanupGroup { Category = "诊断记录", Name = "应用崩溃转储", Root = "CrashDumps", Days = 14,
                Description = "当前用户的应用崩溃转储，用于排查故障。", Recursive = false,
                Accepts = name => HasExtension(name, ".dmp", ".mdmp", ".hdmp") });
            groups.Add(new CleanupGroup { Category = "诊断记录", Name = "Windows 错误报告", Days = 14,
                Root = @"Microsoft\Windows\WER\ReportArchive", Description = "已归档的错误报告和诊断附件。",
                Accepts = name => HasExtension(name, ".wer", ".dmp", ".mdmp", ".hdmp", ".cab") });
            return groups;
        }

        public List<CleanupGroup> Scan()
        {
            var groups = Options();
            DateTime now = DateTime.UtcNow;
            foreach (CleanupGroup group in groups)
            {
                string root = Path.Combine(local, group.Root);
                long cutoff = now.AddDays(-group.Days).ToFileTimeUtc();
                if (group.ProfileCache == null)
                {
                    Walk(root, cutoff, group);
                    continue;
                }
                foreach (string profile in Entries(root, true))
                {
                    string name = Path.GetFileName(profile);
                    int index;
                    if (!group.ChromiumProfiles || name == "Default" || (name.StartsWith("Profile ", StringComparison.Ordinal) &&
                        int.TryParse(name.Substring(8), out index) && index >= 0))
                        Walk(Path.Combine(profile, group.ProfileCache), cutoff, group);
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

        private static bool HasExtension(string name, params string[] extensions)
        {
            return extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
        }

        private static CleanupGroup ExplorerCache(string name, string prefix, string description)
        {
            return new CleanupGroup
            {
                Category = "Windows 缓存", Name = name, Root = @"Microsoft\Windows\Explorer", Description = description,
                Recursive = false, Accepts = file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    HasExtension(file, ".db")
            };
        }

        private static void AddChromium(List<CleanupGroup> groups, string name, string app, string root)
        {
            groups.Add(Chromium(name + " 网页缓存", app, root, @"Cache\Cache_Data", "网页 HTTP 缓存。"));
            groups.Add(Chromium(name + " 代码缓存", app, root, "Code Cache", "网页的 JavaScript 和 WebAssembly 编译缓存。"));
            groups.Add(Chromium(name + " 图形缓存", app, root, "GPUCache", "网页的图形渲染缓存。"));
        }

        private static CleanupGroup Chromium(string name, string app, string root, string cache, string description)
        {
            return new CleanupGroup
            {
                Category = "浏览器缓存", Name = name, App = app, Root = root, ProfileCache = cache, ChromiumProfiles = true,
                Description = description + "退出浏览器后可清理。"
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
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (group.Recursive) pending.Push(path);
                        continue;
                    }
                    PrivateFiles.Info info;
                    if ((group.Accepts == null || group.Accepts(Path.GetFileName(path))) &&
                        PrivateFiles.TryCandidate(path, cutoff, out info))
                        group.Files.Add(new CleanupFile { Root = root, Path = path, Info = info });
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
