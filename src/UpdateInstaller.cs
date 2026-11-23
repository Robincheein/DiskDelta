using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace DiskDelta
{
    internal static class UpdateInstaller
    {
        private const string Prefix = ".diskdelta-update-";
        private const string Payload = "download.exe";
        private const string Helper = "installer.exe";
        private const string Backup = "previous.exe";
        private const string Marker = "finished";

        public static string Prepare(string executable, GitHubUpdates source, UpdateRelease release)
        {
            string directory = Path.GetDirectoryName(executable);
            VerifyFile(executable);
            string stage = Path.Combine(directory, Prefix + Guid.NewGuid().ToString("N"));
            PrivateFiles.CreateDirectory(stage);
            try
            {
                File.SetAttributes(stage, File.GetAttributes(stage) | FileAttributes.Hidden);
                source.Download(release, Path.Combine(stage, Payload));
                File.Copy(executable, Path.Combine(stage, Helper));
                return stage;
            }
            catch
            {
                Discard(stage);
                throw;
            }
        }

        public static void Start(string stage, string executable, UpdateRelease release)
        {
            using (var current = Process.GetCurrentProcess())
            using (Process started = Process.Start(new ProcessStartInfo(Path.Combine(stage, Helper),
                "--apply-update " + current.Id.ToString(CultureInfo.InvariantCulture) + " \"" + Path.GetFileName(executable) + "\" " + release.Hash)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)
            }))
                if (started == null) throw new IOException("无法启动更新安装程序。");
        }

        public static int Apply(string helperPath, string[] args)
        {
            int pid;
            if (args.Length != 4 || !int.TryParse(args[1], out pid) || pid <= 0 ||
                args[2] != Path.GetFileName(args[2]) || !args[2].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !GitHubUpdates.IsHash(args[3])) return 2;
            string stage = Path.GetDirectoryName(helperPath);
            if (!IsStage(stage) || Path.GetFileName(helperPath) != Helper) return 2;
            string target = Path.Combine(Path.GetDirectoryName(stage), args[2]);
            string payload = Path.Combine(stage, Payload);
            string backup = Path.Combine(stage, Backup);
            bool exited = false;
            bool replaced = false;
            try
            {
                PrivateFiles.VerifyDirectory(stage);
                VerifyFile(target);
                VerifyFile(payload);
                GitHubUpdates.Verify(payload, args[3]);
                if (GitHubUpdates.VersionOf(payload) <= GitHubUpdates.VersionOf(target))
                    throw new IOException("下载的版本并未高于当前版本。");
                WaitForExit(pid, target);
                exited = true;
                File.Replace(payload, target, backup);
                replaced = true;
                Launch(target);
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                if (exited)
                {
                    try
                    {
                        if (replaced) File.Replace(backup, target, null);
                        Launch(target);
                    }
                    catch (Exception restore) { message += "\n" + restore.Message; }
                }
                MessageBox.Show(message, "磁盘日记更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            try { File.WriteAllText(Path.Combine(stage, Marker), args[2]); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return 0;
        }

        private static void WaitForExit(int pid, string target)
        {
            Process old;
            try { old = Process.GetProcessById(pid); }
            catch (ArgumentException) { return; }
            using (old)
            {
                if (old.HasExited) return;
                string path;
                try { path = old.MainModule.FileName; }
                catch (InvalidOperationException) { if (old.HasExited) return; throw; }
                if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase) ||
                    !old.WaitForExit(30000))
                    throw new IOException("无法等待旧版磁盘日记退出。");
            }
        }

        private static void Launch(string executable)
        {
            using (Process started = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)
            }))
                if (started == null) throw new IOException("无法重新打开磁盘日记。");
        }

        private static void VerifyFile(string file)
        {
            PrivateFiles.VerifyDirectory(Path.GetDirectoryName(file));
            if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("更新路径必须是普通文件。");
            GitHubUpdates.VersionOf(file);
        }

        private static bool IsStage(string path)
        {
            string name = Path.GetFileName(path);
            Guid id;
            return name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(Prefix.Length), "N", out id);
        }

        public static void CleanCompleted(string executable)
        {
            try
            {
                foreach (string stage in Directory.EnumerateDirectories(Path.GetDirectoryName(executable), Prefix + "*"))
                {
                    if (!IsStage(stage) || (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0) continue;
                    string marker = Path.Combine(stage, Marker);
                    if (File.Exists(marker) && File.ReadAllText(marker) == Path.GetFileName(executable))
                        Discard(stage);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static void Discard(string stage)
        {
            if (!IsStage(stage)) return;
            try
            {
                PrivateFiles.VerifyDirectory(stage);
                foreach (string name in new[] { Payload, Helper, Backup, Marker })
                    File.Delete(Path.Combine(stage, name));
                Directory.Delete(stage, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
