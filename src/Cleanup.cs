using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace DiskDelta
{
    [DataContract]
    internal sealed class CleanupSnapshot
    {
        [DataMember(IsRequired = true)] public DateTime ScannedUtc;
        [DataMember(IsRequired = true)] public List<CleanupGroup> Groups;
    }

    [DataContract]
    internal sealed class CleanupFile
    {
        [DataMember(IsRequired = true)] public string Root;
        [DataMember(IsRequired = true)] public string Path;
        [DataMember(IsRequired = true)] public PrivateFiles.Info Info;
    }

    [DataContract]
    internal sealed class CleanupInspection
    {
        [DataMember] public readonly List<string> Paths = new List<string>();
        [DataMember] public int Opened;
        [DataMember] public int Recent;
        [DataMember] public int OtherTypes;
        [DataMember] public int Excluded;
        [DataMember] public int Unreadable;
    }

    [DataContract]
    internal sealed class CleanupGroup
    {
        public string Category;
        [DataMember(IsRequired = true)] public string Name;
        public string Description;
        public string Effect;
        public string[] Apps;
        public string Root;
        public Func<IEnumerable<string>> Roots;
        public string ProfileCache;
        public bool ChromiumProfiles;
        public bool Recursive = true;
        public int Days;
        public Func<string, bool> Accepts;
        [DataMember(IsRequired = true)] public readonly List<CleanupFile> Files = new List<CleanupFile>();
        [DataMember] public RecycleSnapshot Recycle;
        [DataMember] public CleanupInspection Inspection;
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
        private readonly string userProfile;
        private readonly string configuredTemp;

        public Cleanup(string localAppData, string roamingAppData, string userDocuments, string windowsDirectory,
            string userProfile = null, string configuredTemp = null)
        {
            local = PrivateFiles.FullPath(localAppData);
            roaming = roamingAppData;
            documents = userDocuments;
            windows = windowsDirectory;
            this.userProfile = userProfile;
            this.configuredTemp = configuredTemp;
        }

        public List<CleanupGroup> Options()
        {
            var groups = new List<CleanupGroup>
            {
                new CleanupGroup { Category = "临时文件与回收站", Name = "用户 Temp 临时文件", Roots = TempRoots, Days = 7,
                    Description = "当前用户的 Temp 目录及 Windows 为该用户指定的临时目录。",
                    Effect = "正在安装、更新或恢复文件的软件可能仍需要临时文件。请先退出这些程序。" },
                new CleanupGroup { Category = "临时文件与回收站", Name = "Windows Temp 临时文件", Days = 7,
                    Roots = () => In(windows, "Temp"), Accepts = IsTemporary,
                    Description = "Windows Temp 中可访问的旧 .tmp 和 .temp 文件。",
                    Effect = "安装或更新可能仍使用临时文件。请等它们完成后再清理。" },
                new CleanupGroup { Category = "临时文件与回收站", Name = "Windows 回收站", IsRecycleBin = true,
                    Description = "预览当前用户在各盘的回收站。",
                    Effect = "Windows 会另行确认清空整个回收站。清空后，项目无法从回收站还原。" },
                new CleanupGroup { Category = "Windows 缓存", Name = "DirectX 着色器缓存", Root = "D3DSCache",
                    Description = "DirectX 应用生成的着色器缓存。",
                    Effect = "游戏或应用会重新编译着色器，首次加载可能变慢或短暂卡顿。" },
                ExplorerCache("缩略图缓存", "thumbcache_", "文件资源管理器保存的缩略图。"),
                ExplorerCache("图标缓存", "iconcache_", "文件资源管理器保存的图标。"),
                new CleanupGroup { Category = "Windows 缓存", Name = "远程桌面位图缓存", Apps = new[] { "mstsc" },
                    Root = @"Microsoft\Terminal Server Client\Cache", Recursive = false,
                    Description = "远程桌面显示缓存。退出远程桌面客户端后可清理。",
                    Effect = "下次连接会重建画面缓存，初次显示可能稍慢。连接配置和凭据保留。",
                    Accepts = name => HasExtension(name, ".bmc") ||
                        (name.StartsWith("Cache", StringComparison.OrdinalIgnoreCase) && HasExtension(name, ".bin")) }
            };
            groups.Add(new CleanupGroup
            {
                Category = "聊天软件", Name = "微信临时文件", Days = 14,
                Apps = new[] { "Weixin", "WeChat", "WeixinAppEx", "WeChatAppEx" },
                Roots = WeChatTemp, Accepts = IsTemporary,
                Description = "微信临时目录中的旧 .tmp 和 .temp 文件。请先退出微信。",
                Effect = "未完成的临时传输可能需要重试。聊天数据库、附件和配置保留。"
            });
            groups.Add(new CleanupGroup
            {
                Category = "聊天软件", Name = "QQ 临时文件", Days = 14,
                Apps = new[] { "QQ", "QQNT", "QQExternal" },
                Roots = QQTemp, Accepts = IsTemporary,
                Description = "QQ 临时目录及 WinTemp 中的旧 .tmp 和 .temp 文件。请先退出 QQ。",
                Effect = "未完成的临时传输可能需要重试。聊天数据库、附件和配置保留。"
            });
            AddApplicationCache(groups, "VS Code", "Code", "Code");
            AddApplicationCache(groups, "VS Code Insiders", "Code - Insiders", "Code - Insiders");
            AddApplicationCache(groups, "Cursor", "Cursor", "Cursor");
            AddApplicationCache(groups, "Qoder", "Qoder", "Qoder");
            AddApplicationCache(groups, "Discord", "discord", "Discord");
            AddChromium(groups, "Chrome", "chrome", @"Google\Chrome\User Data");
            AddChromium(groups, "Edge", "msedge", @"Microsoft\Edge\User Data");
            AddChromium(groups, "Brave", "brave", @"BraveSoftware\Brave-Browser\User Data");
            AddChromium(groups, "Vivaldi", "vivaldi", @"Vivaldi\User Data");
            AddSharedGraphics(groups, "Chrome", "chrome", @"Google\Chrome\User Data");
            AddSharedGraphics(groups, "Edge", "msedge", @"Microsoft\Edge\User Data");
            AddOpera(groups, "Opera", "Opera Stable");
            AddOpera(groups, "Opera GX", "Opera GX Stable");
            groups.Add(new CleanupGroup { Category = "浏览器缓存", Name = "Firefox 网页缓存", Apps = new[] { "firefox" },
                Root = @"Mozilla\Firefox\Profiles", ProfileCache = "cache2",
                Description = "Firefox 下载的网页缓存。退出浏览器后可清理。",
                Effect = "网页资源会重新下载。登录状态、书签和历史记录保留。" });
            groups.Add(new CleanupGroup { Category = "诊断记录", Name = "应用崩溃转储", Root = "CrashDumps", Days = 14,
                Description = "当前用户的应用崩溃转储，用于排查故障。", Recursive = false,
                Effect = "清理后无法用这些转储排查此前的崩溃。",
                Accepts = name => HasExtension(name, ".dmp", ".mdmp", ".hdmp") });
            groups.Add(new CleanupGroup { Category = "诊断记录", Name = "Windows 错误报告", Days = 14,
                Root = @"Microsoft\Windows\WER\ReportArchive", Description = "已归档的错误报告和诊断附件。",
                Effect = "清理后将失去这些旧故障的本地诊断记录。",
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
                group.Inspection = new CleanupInspection();
                long cutoff = group.Days == 0 ? long.MaxValue : now.AddDays(-group.Days).ToFileTimeUtc();
                var roots = RootsFor(group).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                group.Inspection.Paths.AddRange(roots.Count == 0 ? BaseRootsFor(group) : roots);
                foreach (string root in roots)
                {
                    string[] initial = OpenForScan(root, group.Inspection);
                    if (initial == null) continue;
                    group.Inspection.Opened++;
                    Walk(root, initial, cutoff, group);
                }
            }
            return groups;
        }

        private IEnumerable<string> RootsFor(CleanupGroup group)
        {
            var roots = BaseRootsFor(group);
            if (group.ProfileCache == null) return roots;
            return roots.SelectMany(root => Entries(root, true))
                .Where(path => !group.ChromiumProfiles || IsChromiumProfile(path))
                .Select(profile => Path.Combine(profile, group.ProfileCache));
        }

        private IEnumerable<string> BaseRootsFor(CleanupGroup group)
        {
            return group.Roots == null ? In(local, group.Root) : group.Roots();
        }

        public CleanupSnapshot Restore(CleanupSnapshot snapshot)
        {
            if (snapshot == null || snapshot.ScannedUtc == default(DateTime) || snapshot.Groups == null ||
                snapshot.Groups.Any(group => group == null || group.Name == null || group.Files == null))
                return null;
            var groups = Options();
            var stored = snapshot.Groups.ToLookup(group => group.Name, StringComparer.Ordinal);
            if (snapshot.Groups.Count != groups.Count) return null;
            foreach (CleanupGroup group in groups)
            {
                var matches = stored[group.Name];
                if (matches.Count() != 1) return null;
                CleanupGroup cached = matches.First();
                if (group.IsRecycleBin)
                {
                    if (cached.Recycle != null && (cached.Recycle.Entries == null ||
                        cached.Recycle.Entries.Any(item => item == null) || cached.Recycle.Count < 0 || cached.Recycle.Bytes < 0))
                        return null;
                    group.Recycle = cached.Recycle;
                }
                else
                {
                    if (cached.Inspection == null || cached.Inspection.Paths == null ||
                        cached.Inspection.Paths.Any(string.IsNullOrEmpty)) return null;
                    if (cached.Files.Any(file => file == null || string.IsNullOrEmpty(file.Path) ||
                        string.IsNullOrEmpty(file.Root) || !PrivateFiles.Below(file.Path, file.Root) ||
                        file.Path.Length <= file.Root.Length + 1 || file.Info.Size < 0 ||
                        (group.ProfileCache != null && !file.Root.EndsWith("\\" + group.ProfileCache, StringComparison.OrdinalIgnoreCase))))
                        return null;
                    group.Files.AddRange(cached.Files);
                    group.Inspection = cached.Inspection;
                }
            }
            snapshot.Groups = groups;
            return snapshot;
        }

        public sealed class Result
        {
            public int Deleted;
            public int Skipped;
            public long Bytes;
        }

        public Result Apply(IEnumerable<CleanupGroup> groups)
        {
            var result = new Result();
            foreach (CleanupGroup group in groups)
            {
                if (group.IsRecycleBin) continue;
                if (group.Apps != null && group.Apps.Any(IsRunning))
                {
                    result.Skipped += group.Files.Count;
                    continue;
                }
                var roots = new HashSet<string>(RootsFor(group), StringComparer.OrdinalIgnoreCase);
                var removed = new HashSet<CleanupFile>();
                long cutoff = group.Days == 0 ? long.MaxValue : DateTime.UtcNow.AddDays(-group.Days).ToFileTimeUtc();
                foreach (CleanupFile file in group.Files)
                {
                    if (!roots.Contains(file.Root) || !Allowed(group, file, cutoff) ||
                        !PrivateFiles.TryDelete(file.Path, file.Info))
                    {
                        result.Skipped++;
                        continue;
                    }
                    result.Deleted++;
                    result.Bytes += file.Info.Size;
                    removed.Add(file);
                }
                group.Files.RemoveAll(removed.Contains);
            }
            return result;
        }

        private static bool Allowed(CleanupGroup group, CleanupFile file, long cutoff)
        {
            if (!PrivateFiles.Below(file.Path, file.Root) || file.Info.Written > cutoff)
                return false;
            try
            {
                return string.Equals(PrivateFiles.FullPath(file.Path), file.Path, StringComparison.OrdinalIgnoreCase) &&
                    (group.Recursive || string.Equals(Path.GetDirectoryName(file.Path), file.Root, StringComparison.OrdinalIgnoreCase)) &&
                    (group.Accepts == null || group.Accepts(Path.GetFileName(file.Path)));
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
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

        private IEnumerable<string> TempRoots()
        {
            return In(local, "Temp").Concat(In(configuredTemp, "").Where(path =>
                !string.Equals(path, Path.Combine(windows ?? "", "Temp"), StringComparison.OrdinalIgnoreCase) &&
                !PrivateFiles.Below(path, Path.Combine(local, "Temp")) &&
                (string.Equals(Path.GetFileName(path), "Temp", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFileName(path), "Tmp", StringComparison.OrdinalIgnoreCase))));
        }

        private IEnumerable<string> ChatRoots(string name)
        {
            return In(documents, name).Concat(In(userProfile, name))
                .Concat(DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed)
                    .SelectMany(drive => In(drive.RootDirectory.FullName, name)))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<string> WeChatTemp()
        {
            return ChatRoots("xwechat_files").SelectMany(root => Entries(root, true))
                .Where(account => Path.GetFileName(account).StartsWith("wxid_", StringComparison.OrdinalIgnoreCase))
                .Select(account => Path.Combine(account, "temp"))
                .Concat(ChatRoots("WeChat Files").SelectMany(root => Entries(root, true))
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
                .Concat(ChatRoots("Tencent Files").SelectMany(root => Entries(root, true))
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
                Roots = () => In(roaming, root).SelectMany(folder => Under(folder,
                    "Cache", "Code Cache", "GPUCache", "CachedData", "DawnGraphiteCache", "DawnWebGPUCache")),
                Description = name + " 的网页、代码和图形缓存。请先退出软件。",
                Effect = "缓存会自动重建，首次启动或加载内容可能较慢。应用设置和用户数据保留。"
            });
        }

        private static IEnumerable<string> Under(string folder, params string[] names)
        {
            return names.Select(name => Path.Combine(folder, name));
        }

        private static CleanupGroup ExplorerCache(string name, string prefix, string description)
        {
            return new CleanupGroup
            {
                Category = "Windows 缓存", Name = name, Root = @"Microsoft\Windows\Explorer", Description = description,
                Effect = "资源管理器会重建缓存。首次打开文件夹时，图标或缩略图可能稍后才出现。",
                Recursive = false, Accepts = file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    HasExtension(file, ".db")
            };
        }

        private static void AddChromium(List<CleanupGroup> groups, string name, string app, string root)
        {
            groups.Add(Chromium(name + " 网页缓存", app, root, "Cache", "网页 HTTP 缓存。",
                "常访问网页的资源会重新下载。登录状态、书签和历史记录保留。"));
            groups.Add(Chromium(name + " 代码缓存", app, root, "Code Cache", "网页的 JavaScript 和 WebAssembly 编译缓存。",
                "网页脚本会重新编译，首次加载可能较慢。"));
            groups.Add(Chromium(name + " 图形缓存", app, root, "GPUCache", "网页的图形渲染缓存。",
                "浏览器会重新生成图形缓存，首次渲染可能较慢。"));
        }

        private void AddSharedGraphics(List<CleanupGroup> groups, string name, string app, string root)
        {
            groups.Add(new CleanupGroup
            {
                Category = "浏览器缓存", Name = name + " 共享着色器缓存", Apps = new[] { app },
                Roots = () => In(local, root).SelectMany(folder => Under(folder,
                    "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "GPUPersistentCache")),
                Description = "浏览器各用户配置共用的着色器和 GPU 缓存。请先退出浏览器。",
                Effect = "浏览器会重新编译着色器，首次渲染可能较慢。"
            });
        }

        private void AddOpera(List<CleanupGroup> groups, string name, string relative)
        {
            string root = Path.Combine("Opera Software", relative);
            groups.Add(new CleanupGroup
            {
                Category = "浏览器缓存", Name = name + " 缓存", Apps = new[] { "opera" },
                Roots = () => In(local, root).SelectMany(folder => Under(folder, "Cache", "System Cache"))
                    .Concat(In(roaming, root).SelectMany(folder => Under(folder, "Code Cache", "GPUCache"))),
                Description = "网页、脚本和图形缓存。退出浏览器后可清理。",
                Effect = "网页资源会重新下载，代码和图形缓存会重新生成。登录状态、书签和历史记录保留。"
            });
        }

        private static CleanupGroup Chromium(string name, string app, string root, string cache, string description, string effect)
        {
            return new CleanupGroup
            {
                Category = "浏览器缓存", Name = name, Apps = new[] { app }, Root = root, ProfileCache = cache, ChromiumProfiles = true,
                Description = description + "退出浏览器后可清理。", Effect = effect
            };
        }

        private static void Walk(string root, string[] initial, long cutoff, CleanupGroup group)
        {
            var pending = new Stack<string[]>();
            pending.Push(initial);
            while (pending.Count != 0)
            {
                foreach (string path in pending.Pop())
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (IOException) { group.Inspection.Unreadable++; continue; }
                    catch (UnauthorizedAccessException) { group.Inspection.Unreadable++; continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { group.Inspection.Excluded++; continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (group.Recursive)
                        {
                            string[] children = OpenForScan(path, group.Inspection);
                            if (children != null) pending.Push(children);
                        }
                        continue;
                    }
                    if (group.Accepts != null && !group.Accepts(Path.GetFileName(path)))
                    {
                        group.Inspection.OtherTypes++;
                        continue;
                    }
                    PrivateFiles.Info info;
                    switch (PrivateFiles.InspectCleanup(path, cutoff, out info))
                    {
                        case PrivateFiles.CleanupCheck.Ready:
                            group.Files.Add(new CleanupFile { Root = root, Path = path, Info = info });
                            break;
                        case PrivateFiles.CleanupCheck.Recent: group.Inspection.Recent++; break;
                        case PrivateFiles.CleanupCheck.Protected: group.Inspection.Excluded++; break;
                        default: group.Inspection.Unreadable++; break;
                    }
                }
            }
        }

        private static string[] OpenForScan(string path, CleanupInspection inspection)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) return null;
                PrivateFiles.VerifyDirectory(path);
                return Directory.GetFileSystemEntries(path);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { inspection.Unreadable++; return null; }
            catch (UnauthorizedAccessException) { inspection.Unreadable++; return null; }
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
