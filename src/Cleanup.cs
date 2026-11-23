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
        public string[] Apps;
        public string Root;
        public Func<IEnumerable<string>> Roots;
        public string ProfileCache;
        public bool ChromiumProfiles;
        public bool Recursive = true;
        public int Days = 7;
        public Func<string, bool> Accepts;
        public readonly List<CleanupFile> Files = new List<CleanupFile>();
        public RecycleSnapshot Recycle;
        public bool IsRecycleBin;
        public long Count { get { return Recycle == null ? Files.Count : Recycle.Count; } }
        public long Bytes { get { return Recycle == null ? Files.Sum(item => item.Info.Size) : Recycle.Bytes; } }
    }

    internal sealed class Cleanup
    {
        private readonly string local;
        private readonly string roaming;
        private readonly string documents;
        private readonly string windows;

        public Cleanup(string localAppData, string roamingAppData, string userDocuments, string windowsDirectory)
        {
            local = PrivateFiles.FullPath(localAppData);
            roaming = roamingAppData;
            documents = userDocuments;
            windows = windowsDirectory;
        }

        public List<CleanupGroup> Options()
        {
            var groups = new List<CleanupGroup>
            {
                new CleanupGroup { Category = "临时文件与回收站", Name = "用户 Temp 临时文件", Root = "Temp", Days = 14,
                    Description = "当前用户的 AppData\\Local\\Temp 中的旧临时文件。请先退出正在安装或更新的软件。" },
                new CleanupGroup { Category = "临时文件与回收站", Name = "Windows Temp 临时文件", Days = 14,
                    Roots = () => In(windows, "Temp"), Accepts = IsTemporary,
                    Description = "Windows\\Temp 中可访问的旧 .tmp 和 .temp 文件。" },
                new CleanupGroup { Category = "临时文件与回收站", Name = "Windows 回收站", IsRecycleBin = true,
                    Description = "预览当前用户在各盘的回收站。清空由 Windows 确认，会永久删除执行时回收站里的项目。" },
                new CleanupGroup { Category = "Windows 缓存", Name = "DirectX 着色器缓存", Root = "D3DSCache",
                    Description = "DirectX 应用生成的着色器缓存。" },
                ExplorerCache("缩略图缓存", "thumbcache_", "文件资源管理器保存的缩略图。"),
                ExplorerCache("图标缓存", "iconcache_", "文件资源管理器保存的图标。"),
                new CleanupGroup { Category = "Windows 缓存", Name = "远程桌面位图缓存", Apps = new[] { "mstsc" },
                    Root = @"Microsoft\Terminal Server Client\Cache", Recursive = false,
                    Description = "远程桌面显示缓存。退出远程桌面客户端后可清理。",
                    Accepts = name => HasExtension(name, ".bmc") ||
                        (name.StartsWith("Cache", StringComparison.OrdinalIgnoreCase) && HasExtension(name, ".bin")) }
            };
            groups.Add(new CleanupGroup
            {
                Category = "聊天软件", Name = "微信临时文件", Days = 14,
                Apps = new[] { "Weixin", "WeChat", "WeixinAppEx", "WeChatAppEx" },
                Roots = WeChatTemp, Accepts = IsTemporary,
                Description = "微信默认存储目录的旧 .tmp 和 .temp 文件。请先退出微信。"
            });
            groups.Add(new CleanupGroup
            {
                Category = "聊天软件", Name = "QQ 临时文件", Days = 14,
                Apps = new[] { "QQ", "QQNT", "QQExternal" },
                Roots = QQTemp, Accepts = IsTemporary,
                Description = "QQ 默认存储目录及 WinTemp 的旧 .tmp 和 .temp 文件。请先退出 QQ。"
            });
            AddApplicationCache(groups, "VS Code", "Code", "Code");
            AddApplicationCache(groups, "Qoder", "Qoder", "Qoder");
            AddApplicationCache(groups, "Discord", "discord", "Discord");
            AddChromium(groups, "Chrome", "chrome", @"Google\Chrome\User Data");
            AddChromium(groups, "Edge", "msedge", @"Microsoft\Edge\User Data");
            AddChromium(groups, "Brave", "brave", @"BraveSoftware\Brave-Browser\User Data");
            groups.Add(new CleanupGroup { Category = "浏览器缓存", Name = "Firefox 网页缓存", Apps = new[] { "firefox" },
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
                if (group.IsRecycleBin) continue;
                var roots = group.Roots == null ? In(local, group.Root) : group.Roots();
                long cutoff = now.AddDays(-group.Days).ToFileTimeUtc();
                foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (group.ProfileCache == null)
                        Walk(root, cutoff, group);
                    else
                        foreach (string profile in Entries(root, true).Where(path => !group.ChromiumProfiles || IsChromiumProfile(path)))
                            Walk(Path.Combine(profile, group.ProfileCache), cutoff, group);
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
                if (group.Apps != null && group.Apps.Any(IsRunning))
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

        private static bool IsTemporary(string name) { return HasExtension(name, ".tmp", ".temp"); }

        private static bool IsChromiumProfile(string path)
        {
            string name = Path.GetFileName(path);
            int index;
            return name == "Default" || (name.StartsWith("Profile ", StringComparison.Ordinal) &&
                int.TryParse(name.Substring(8), out index) && index >= 0);
        }

        private static IEnumerable<string> In(string directory, string relative)
        {
            if (string.IsNullOrEmpty(directory)) yield break;
            string root;
            try { root = PrivateFiles.FullPath(Path.Combine(directory, relative)); }
            catch (IOException) { yield break; }
            catch (ArgumentException) { yield break; }
            yield return root;
        }

        private IEnumerable<string> WeChatTemp()
        {
            return In(documents, "xwechat_files").SelectMany(root => Entries(root, true))
                .Where(account => Path.GetFileName(account).StartsWith("wxid_", StringComparison.OrdinalIgnoreCase))
                .Select(account => Path.Combine(account, "temp"))
                .Concat(In(documents, "WeChat Files").SelectMany(root => Entries(root, true))
                    .Select(account => Path.Combine(account, @"FileStorage\Temp")))
                .Concat(In(roaming, @"Tencent\xwechat\Temp"))
                .Concat(In(local, @"Tencent\Weixin\Temp"))
                .Concat(In(local, @"Tencent\WeChat\Temp"));
        }

        private IEnumerable<string> QQTemp()
        {
            return In(roaming, @"Tencent\Users").SelectMany(root => Entries(root, true))
                .Where(account => Path.GetFileName(account).All(char.IsDigit))
                .Select(account => Path.Combine(account, "WinTemp"))
                .Concat(In(documents, "Tencent Files").SelectMany(root => Entries(root, true))
                    .Where(account => Path.GetFileName(account).All(char.IsDigit))
                    .Select(account => Path.Combine(account, @"nt_qq\nt_temp")))
                .Concat(In(roaming, @"Tencent\QQ\Temp"))
                .Concat(In(local, @"Tencent\QQ\Temp"));
        }

        private void AddApplicationCache(List<CleanupGroup> groups, string name, string app, string root)
        {
            groups.Add(new CleanupGroup
            {
                Category = "软件缓存", Name = name + " 缓存", Apps = new[] { app },
                Roots = () => In(roaming, root).SelectMany(folder => new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" }
                    .Select(cache => Path.Combine(folder, cache))),
                Description = name + " 的网页、代码和图形缓存。请先退出软件。"
            });
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
                Category = "浏览器缓存", Name = name, Apps = new[] { app }, Root = root, ProfileCache = cache, ChromiumProfiles = true,
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

        internal static string[] Entries(string folder, bool directoriesOnly = false)
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
