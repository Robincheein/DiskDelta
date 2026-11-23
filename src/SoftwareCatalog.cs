using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace DiskDelta
{
    internal sealed class SoftwareEntry
    {
        public string Name;
        public string Publisher;
        public string Version;
        public readonly Dictionary<string, string> Locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public override string ToString() { return string.IsNullOrEmpty(Version) ? Name : Name + "   " + Version; }
    }

    internal sealed class SoftwareCatalog
    {
        private const string Current = @"Software\Microsoft\Windows\CurrentVersion\";
        private static readonly Regex CommandPath = new Regex(@"^[A-Za-z]:\\[^""]+?\.(?:exe|ico|dll|msi|cmd|bat)(?=$|[\s,])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex VersionSuffix = new Regex(@"\s+v?\d+(?:\.\d+)+.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "setup", "install", "installer", "uninstall", "unins000", "unins001", "update", "app", "program", "windows",
            "launcher", "main", "client", "server", "data", "cache", "logs", "bin", "packages", "commonfiles", "programfiles",
            "programfilesx86", "programdata", "users", "system", "system32", "syswow64", "msiexec", "rundll32", "cmd",
            "powershell", "pwsh", "wscript", "explorer", "control"
        };

        public sealed class Result
        {
            public readonly List<string> Drives = new List<string>();
            public int Unreadable;
            public List<SoftwareEntry> Entries;
        }

        public static List<SoftwareEntry> RegistryEntries()
        {
            var entries = new Dictionary<string, SoftwareEntry>(StringComparer.OrdinalIgnoreCase);
            var appPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            using (var store = RegistryKey.OpenBaseKey(hive, view))
            {
                VisitKeys(store, Current + "Uninstall", (keyName, key) =>
                {
                    string name = Value(key, "DisplayName");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    if (object.Equals(key.GetValue("SystemComponent"), 1)) return;
                    string version = Value(key, "DisplayVersion");
                    string publisher = Value(key, "Publisher");
                    string identity = name + "\n" + publisher + "\n" + version;
                    SoftwareEntry entry;
                    if (!entries.TryGetValue(identity, out entry))
                        entries.Add(identity, entry = new SoftwareEntry { Name = name, Publisher = publisher, Version = version });
                    Add(entry, LocalPath(Value(key, "InstallLocation"), false), "注册表 · 安装目录");
                    Add(entry, LocalPath(Value(key, "DisplayIcon"), true), "注册表 · 程序或图标");
                    Add(entry, LocalPath(Value(key, "UninstallString"), true), "注册表 · 卸载程序");
                    Add(entry, LocalPath(Value(key, "QuietUninstallString"), true), "注册表 · 卸载程序");
                    Add(entry, LocalPath(Value(key, "ModifyPath"), true), "注册表 · 维护程序");
                });
                VisitKeys(store, Current + "App Paths", (name, key) =>
                {
                    string path = LocalPath(Value(key, ""), true);
                    if (path != null) appPaths[path] = Path.GetFileNameWithoutExtension(name);
                });
            }

            var locations = new Dictionary<string, List<SoftwareEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (SoftwareEntry entry in entries.Values)
            foreach (string path in entry.Locations.Keys)
            {
                Index(locations, path, entry);
                if (Path.HasExtension(path)) Index(locations, Path.GetDirectoryName(path), entry);
            }
            foreach (var application in appPaths)
            {
                var matched = FindOwner(locations, application.Key);
                if (matched == null)
                {
                    string key = application.Value + "\n" + application.Key;
                    matched = new SoftwareEntry { Name = application.Value, Publisher = "", Version = "" };
                    entries.Add(key, matched);
                }
                Add(matched, application.Key, "注册表 · App Paths");
            }
            return entries.Values.Concat(AppPackages()).OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.Version, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<SoftwareEntry> AppPackages()
        {
            var packages = new List<SoftwareEntry>();
            string shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            const string command = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-AppxPackage -PackageTypeFilter Main " +
                "| Select-Object Name,Version,InstallLocation,PublisherId | ConvertTo-Xml -NoTypeInformation -As String";
            try
            {
                using (var process = Process.Start(new ProcessStartInfo(shell, "-NoProfile -NonInteractive -Command \"" + command + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
                }))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        return packages;
                    }
                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output.Result)) return packages;
                    errors.Wait();
                    foreach (XElement item in XDocument.Parse(output.Result).Root.Elements("Object"))
                    {
                        var entry = new SoftwareEntry
                        {
                            Name = PackageValue(item, "Name"), Version = PackageValue(item, "Version"),
                            Publisher = PackageValue(item, "PublisherId")
                        };
                        if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                        Add(entry, LocalPath(PackageValue(item, "InstallLocation"), false), "应用包 · 安装目录");
                        packages.Add(entry);
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (IOException) { }
            catch (XmlException) { }
            return packages;
        }

        private static string PackageValue(XElement package, string name)
        {
            return package.Elements("Property").Where(item => (string)item.Attribute("Name") == name)
                .Select(item => item.Value).FirstOrDefault();
        }

        private static SoftwareEntry FindOwner(Dictionary<string, List<SoftwareEntry>> index, string path)
        {
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
            {
                List<SoftwareEntry> owners;
                if (index.TryGetValue(current, out owners))
                    return owners.Count == 1 ? owners[0] : null;
            }
            return null;
        }

        public static Result ScanAll(List<SoftwareEntry> entries, Action<string> progress, CancellationToken cancellation)
        {
            var result = new Result { Entries = entries };
            var aliases = new Dictionary<string, List<SoftwareEntry>>(StringComparer.Ordinal);
            foreach (SoftwareEntry entry in entries)
            {
                AddAlias(aliases, entry.Name, entry);
                foreach (string path in entry.Locations.Keys)
                {
                    var name = Path.GetFileName(path);
                    if (!string.IsNullOrEmpty(name))
                        AddAlias(aliases, name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(name) : name, entry);
                }
            }

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                cancellation.ThrowIfCancellationRequested();
                if (!drive.IsReady) continue;
                progress(drive.Name.Substring(0, 2));
                result.Drives.Add(drive.Name.Substring(0, 2));
                result.Unreadable += Scanner.Search(drive.Name, (folder, name, isDirectory) =>
                {
                    string key = Alias(isDirectory ? name : name.Substring(0, name.Length - 4));
                    List<SoftwareEntry> matched;
                    if (key.Length == 0 || !aliases.TryGetValue(key, out matched)) return;
                    foreach (SoftwareEntry item in matched)
                        Add(item, folder + name, isDirectory ? "匹配线索 · 同名目录" : "匹配线索 · 同名程序");
                }, cancellation);
            }
            return result;
        }

        private static void AddAlias(Dictionary<string, List<SoftwareEntry>> index, string name, SoftwareEntry entry)
        {
            string alias = Alias(VersionSuffix.Replace(name.Split('(')[0].Trim(), ""));
            if (alias.Length >= 3 || (alias.Length == 2 && alias == "qq")) Index(index, alias, entry);
        }

        private static void Index(Dictionary<string, List<SoftwareEntry>> index, string key, SoftwareEntry entry)
        {
            List<SoftwareEntry> list;
            if (!index.TryGetValue(key, out list)) index.Add(key, list = new List<SoftwareEntry>());
            if (!list.Contains(entry)) list.Add(entry);
        }

        private static string Alias(string value)
        {
            var text = new StringBuilder(value.Length);
            bool letters = false;
            foreach (char character in value)
            {
                if (!char.IsLetterOrDigit(character)) continue;
                letters |= char.IsLetter(character);
                text.Append(char.ToLowerInvariant(character));
            }
            string result = text.ToString();
            return !letters || Common.Contains(result) ||
                (result.StartsWith("unins", StringComparison.Ordinal) && result.Skip(5).All(char.IsDigit)) ? "" : result;
        }

        private static void Add(SoftwareEntry entry, string path, string source)
        {
            if (path != null && !entry.Locations.ContainsKey(path)) entry.Locations.Add(path, source);
        }

        private static string LocalPath(string raw, bool command)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string value = Environment.ExpandEnvironmentVariables(raw).Trim();
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int closing = value.IndexOf('"', 1);
                if (closing < 0) return null;
                value = value.Substring(1, closing - 1);
            }
            else if (command)
            {
                Match match = CommandPath.Match(value);
                if (!match.Success) return null;
                value = match.Value;
            }
            if (value.Length < 3 || value[1] != ':' || value[2] != '\\') return null;
            try
            {
                string path = PrivateFiles.FullPath(value);
                if (Path.GetFileName(path).Length == 0 || (command &&
                    Common.Contains((Path.GetFileNameWithoutExtension(path) ?? "").ToLowerInvariant()))) return null;
                return path;
            }
            catch (Exception ex)
            {
                if (ex is ArgumentException || ex is IOException || ex is NotSupportedException || ex is SecurityException) return null;
                throw;
            }
        }

        private static string Value(RegistryKey key, string name) { return (key.GetValue(name) as string ?? "").Trim(); }

        private static void VisitKeys(RegistryKey store, string path, Action<string, RegistryKey> visit)
        {
            try
            {
                using (RegistryKey parent = store.OpenSubKey(path))
                {
                    if (parent == null) return;
                    foreach (string name in parent.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey child = parent.OpenSubKey(name))
                                if (child != null) visit(name, child);
                        }
                        catch (UnauthorizedAccessException) { }
                        catch (SecurityException) { }
                        catch (IOException) { }
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            catch (IOException) { }
        }
    }
}
