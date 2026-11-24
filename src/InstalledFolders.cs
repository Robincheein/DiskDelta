using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Win32;

namespace DiskDelta
{
    internal sealed class InstalledFolders
    {
        public static readonly string[] Roots = { @"C:\Program Files", @"C:\Program Files (x86)" };
        public const string JournalName = "DiskDelta-MigrationRecords";
        private static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Common Files", "WindowsApps", "ModifiableWindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection",
            "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer", "WindowsPowerShell", "Internet Explorer",
            "Reference Assemblies", "dotnet", "MSBuild", "Microsoft SDKs", "Windows Kits", "Microsoft Visual Studio",
            "Microsoft.NET", "Microsoft Office", "Microsoft Office 15", "Microsoft Update Health Tools", JournalName
        };
        private readonly Dictionary<string, List<string>> registered = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> services = new List<string>();
        private bool serviceInventoryComplete;

        public InstalledFolders()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (uninstall == null) continue;
                foreach (string key in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using (var app = uninstall.OpenSubKey(key))
                        {
                            if (app == null || object.Equals(app.GetValue("SystemComponent"), 1)) continue;
                            string name = app.GetValue("DisplayName") as string;
                            string path = AsPath(app.GetValue("InstallLocation") as string);
                            if (string.IsNullOrWhiteSpace(name) || !IsAppFolder(path)) continue;
                            List<string> names;
                            if (!registered.TryGetValue(path, out names)) registered.Add(path, names = new List<string>());
                            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name.Trim());
                        }
                    }
                    catch (SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            serviceInventoryComplete = ReadServices();
        }

        public IEnumerable<string> RegisteredPaths { get { return registered.Keys; } }
        public bool NeedsElevatedServiceCheck { get { return !serviceInventoryComplete; } }

        public string Name(string path)
        {
            List<string> names;
            return registered.TryGetValue(path, out names) ? string.Join(" / ", names) : Path.GetFileName(path);
        }

        public string BlockReason(string path, bool requireCompleteServiceInventory = true)
        {
            if (!IsAppFolder(path)) return "系统、共享或受保护的目录";
            if (string.Equals(Path.GetFileName(path), "Steam", StringComparison.OrdinalIgnoreCase))
                return "请用 Steam 存储管理移动游戏";
            List<string> names;
            if (!registered.TryGetValue(path, out names)) return "缺少明确的系统安装登记";
            if (names.Count != 1 || registered.Keys.Any(item => PrivateFiles.Below(item, path) || PrivateFiles.Below(path, item)))
                return "目录由多个安装项共用";
            if (services.Any(item => PrivateFiles.Below(item, path))) return "有服务或驱动登记在此目录";
            return requireCompleteServiceInventory && !serviceInventoryComplete ? "无法完整读取系统服务登记" : null;
        }

        public static string RootFor(string path)
        {
            return path == null ? null : Roots.FirstOrDefault(root => PrivateFiles.Below(path, root));
        }

        public static bool IsAppFolder(string path)
        {
            string root = RootFor(path);
            if (root == null) return false;
            var parts = path.Substring(root.Length + 1).Split('\\');
            return parts.Length <= 2 && parts.All(part => part.Length > 0 && !Protected.Contains(part) &&
                part.IndexOf(".DiskDelta-", StringComparison.OrdinalIgnoreCase) < 0);
        }

        private bool ReadServices()
        {
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (root == null) return false;
                    bool complete = true;
                    foreach (string key in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (var service = root.OpenSubKey(key))
                            {
                                if (service == null) continue;
                                AddServicePath(service.GetValue("ImagePath") as string);
                                using (var parameters = service.OpenSubKey("Parameters"))
                                    if (parameters != null) AddServicePath(parameters.GetValue("ServiceDll") as string);
                            }
                        }
                        catch (SecurityException) { complete = false; }
                        catch (UnauthorizedAccessException) { complete = false; }
                    }
                    return complete;
                }
            }
            catch (IOException) { return false; }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private void AddServicePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = Environment.ExpandEnvironmentVariables(value.Trim());
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int quote = value.IndexOf('"', 1);
                if (quote > 1) value = value.Substring(1, quote - 1);
            }
            else
            {
                int end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end < 0) end = value.IndexOf(".sys", StringComparison.OrdinalIgnoreCase);
                if (end < 0) end = value.IndexOf(".dll", StringComparison.OrdinalIgnoreCase);
                if (end >= 0) value = value.Substring(0, end + 4);
            }
            string path = AsPath(value.StartsWith(@"\??\", StringComparison.Ordinal) ? value.Substring(4) : value);
            if (path != null) services.Add(path);
        }

        private static string AsPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim().Trim('"');
            if (path.Length < 3 || path[1] != ':' || path[2] != '\\') return null;
            try { return PrivateFiles.FullPath(path); }
            catch (IOException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }
    }
}
