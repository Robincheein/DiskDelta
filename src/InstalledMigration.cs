using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskDelta
{
    internal static class InstalledMigration
    {
        private const string DestinationName = "DiskDelta-ProgramFiles";
        private const string LogPipePrefix = "DiskDelta-Migration-";
        public static readonly string Journal = Path.Combine(InstalledFolders.Roots[0], InstalledFolders.JournalName);
        private static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier SystemAccount = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier Installer = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
        private static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        public static string Target(string source, string volume)
        {
            if (!InstalledFolders.IsAppFolder(source)) throw new IOException("请选择独立的安装目录。");
            volume = Volume.NormalizeRoot(volume);
            FolderMigration.RequireNtfs(volume);
            string id = Volume.ReadId(volume);
            if (id == null || volume == Program.DefaultRoot || id == Volume.ReadId(Program.DefaultRoot))
                throw new IOException("请选择 C 盘以外的固定 NTFS 磁盘。");
            return TargetPath(source, volume);
        }

        public static string TargetPath(string source, string volume)
        {
            string root = InstalledFolders.RootFor(source);
            return Path.Combine(volume, DestinationName, Path.GetFileName(root), source.Substring(root.Length + 1));
        }

        public static string Start(string action, string value, Action<string> log, string volume = null)
        {
            string id = Guid.NewGuid().ToString("N");
            string arguments = "--migration-admin " + action + " \"" + value + "\"" + (volume == null ? "" : " " + volume) + " --log " + id;
            var security = new PipeSecurity();
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(Administrators, PipeAccessRights.Write | PipeAccessRights.Synchronize,
                AccessControlType.Allow));
            try
            {
                using (var stop = new CancellationTokenSource())
                using (var pipe = new NamedPipeServerStream(LogPipePrefix + id, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 0, security))
                {
                    var receive = ReceiveLogs(pipe, stop.Token, log);
                    try
                    {
                        string exe = typeof(Program).Assembly.Location;
                        log("管理员命令 \"" + exe + "\" " + arguments);
                        using (var process = Process.Start(new ProcessStartInfo(exe, arguments)
                            { UseShellExecute = true, Verb = "runas" }))
                        {
                            process.WaitForExit();
                            receive.Wait(2000);
                            log("管理员子进程退出代码 " + process.ExitCode);
                            if (process.ExitCode != 0) return "安装目录操作未完成，请查看日志或迁移记录。";
                            return action == "move" ? "已建立联接，请验证软件后再决定是否释放备份。" :
                                action == "restore" ? "已恢复原安装目录，目标盘副本已保留。" :
                                action == "archive" ? "备份已转存，C 盘备份已释放。" : "备份释放操作已完成。";
                        }
                    }
                    finally { stop.Cancel(); }
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return "已取消管理员授权。";
                throw;
            }
        }

        private static async Task ReceiveLogs(NamedPipeServerStream pipe, CancellationToken stop, Action<string> log)
        {
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using (var input = new StreamReader(pipe, Encoding.UTF8))
                {
                    string line;
                    while ((line = await input.ReadLineAsync().ConfigureAwait(false)) != null)
                        log(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException ex) { if (!stop.IsCancellationRequested) log("迁移日志连接中断 · " + ex.Message); }
        }

        public static int Run(string[] args)
        {
            StreamWriter output = null;
            object outputLock = new object();
            Action<string> log = delegate(string text)
            {
                if (output == null) return;
                lock (outputLock)
                {
                    try { output.WriteLine(text); }
                    catch (IOException) { }
                }
            };
            try
            {
                int count = args.Length;
                if (count >= 4 && args[count - 2] == "--log")
                {
                    Guid logId;
                    if (!Guid.TryParseExact(args[count - 1], "N", out logId)) return 2;
                    using (var pipe = new NamedPipeClientStream(".", LogPipePrefix + logId.ToString("N"),
                        PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Identification))
                    {
                        pipe.Connect(4000);
                        using (output = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true })
                        {
                            try { return RunAction(args, count - 2, log); }
                            catch (Exception ex) { log("操作中断 · " + ex.Message); throw; }
                        }
                    }
                }
                return RunAction(args, count, log);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "安装目录迁移", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        private static int RunAction(string[] args, int count, Action<string> log)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("安装目录操作需要管理员权限。");
            if (count < 3) return 2;
            log("管理员子进程已启动");
            string action = args[1];
            RequireProtected(InstalledFolders.Roots[0]);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var service = new FolderMigration(profile, profile, true);
            if (action == "move" && count == 4)
            {
                string source = PrivateFiles.FullPath(args[2]);
                string target = Target(source, args[3]);
                log("管理员预检 " + source + " → " + target);
                string reason = new InstalledFolders().BlockReason(source);
                if (reason != null) throw new IOException(reason);
                RequireSourcePath(source);
                RequireStopped(source, target);
                PrepareTarget(target, log);
                PrepareProtectedDirectory(Journal, log);
                service.Move(service.Preview(source, target), log);
                return 0;
            }

            Guid id;
            if (count != 3 || !Guid.TryParseExact(args[2], "N", out id) ||
                (action != "restore" && action != "release" && action != "archive")) return 2;
            RequireProtected(Journal);
            var record = service.Load().Find(item => item.Id == args[2]);
            if (record == null) throw new IOException("找不到受保护的迁移记录。");
            RequireSourcePath(Path.GetDirectoryName(record.Source));
            RequireProtected(record.Backup);
            RequireStopped(record.Source, record.Target);
            if (action == "restore") service.Restore(record, log);
            else
            {
                RequireTarget(record.Target);
                var released = action == "archive" ? service.ArchiveBackup(record, log) : service.ReleaseBackup(record, log);
                if (released.Skipped > 0 || PrivateFiles.Exists(record.Backup))
                    throw new IOException("仍有 " + released.Skipped + " 个备份文件未释放。");
            }
            return 0;
        }

        public static void RequireSourcePath(string path)
        {
            string root = InstalledFolders.RootFor(path) ?? path;
            RequireProtected(root);
            if (path == root) return;
            string current = root;
            foreach (string part in path.Substring(root.Length + 1).Split('\\'))
            {
                current = Path.Combine(current, part);
                RequireProtected(current);
            }
        }

        private static void PrepareTarget(string path, Action<string> log)
        {
            string volume = Path.GetPathRoot(path);
            RequireProtected(volume, true);
            string current = volume;
            foreach (string part in Path.GetDirectoryName(path).Substring(volume.Length).Split('\\'))
            {
                current = Path.Combine(current, part);
                PrepareProtectedDirectory(current, log);
            }
        }

        public static void RequireTarget(string path)
        {
            string volume = Path.GetPathRoot(path);
            RequireProtected(volume, true);
            string current = volume;
            foreach (string part in path.Substring(volume.Length).Split('\\'))
            {
                current = Path.Combine(current, part);
                RequireProtected(current);
            }
        }

        private static void PrepareProtectedDirectory(string path, Action<string> log)
        {
            if (!PrivateFiles.Exists(path))
            {
                var acl = new DirectorySecurity();
                acl.SetOwner(Administrators);
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(Rule(Administrators, FileSystemRights.FullControl));
                acl.AddAccessRule(Rule(SystemAccount, FileSystemRights.FullControl));
                acl.AddAccessRule(Rule(Installer, FileSystemRights.FullControl));
                acl.AddAccessRule(Rule(Users, FileSystemRights.ReadAndExecute));
                log("Windows API 创建受保护目录 " + path);
                Directory.CreateDirectory(path, acl);
            }
            RequireProtected(path);
        }

        private static FileSystemAccessRule Rule(SecurityIdentifier sid, FileSystemRights rights)
        {
            return new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow);
        }

        private static bool Trusted(SecurityIdentifier sid) { return sid == Administrators || sid == SystemAccount || sid == Installer; }

        private static void RequireProtected(string path, bool volumeRoot = false)
        {
            PrivateFiles.VerifyDirectory(path);
            var acl = Directory.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner);
            var raw = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
            var rights = FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if (!volumeRoot) rights |= FileSystemRights.Delete | FileSystemRights.WriteData | FileSystemRights.AppendData |
                FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes;
            int mask = (int)rights | 0x10000000 | (volumeRoot ? 0 : 0x40000000);
            if (raw.DiscretionaryAcl == null || !Trusted(raw.Owner))
                throw new IOException("目录所有者或权限未通过校验：" + path);
            foreach (GenericAce entry in raw.DiscretionaryAcl)
            {
                var rule = entry as QualifiedAce;
                if (rule != null && rule.AceQualifier == AceQualifier.AccessAllowed &&
                    (rule.AceFlags & AceFlags.InheritOnly) == 0 && (rule.AccessMask & mask) != 0 && !Trusted(rule.SecurityIdentifier))
                    throw new IOException("普通账户可以修改安装路径，无法安全迁移：" + path);
            }
        }

        public static void RequireStopped(string source, string target)
        {
            foreach (var process in Process.GetProcesses())
            using (process)
            {
                string path;
                try { path = process.MainModule == null ? null : process.MainModule.FileName; }
                catch (Win32Exception) { continue; }
                catch (InvalidOperationException) { continue; }
                catch (NotSupportedException) { continue; }
                if (path != null && (PrivateFiles.Below(path, source) || PrivateFiles.Below(path, target)))
                    throw new IOException("请先退出正在运行的程序：" + Path.GetFileName(path));
            }
        }
    }
}
