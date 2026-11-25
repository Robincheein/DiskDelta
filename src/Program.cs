using System;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace DiskDelta
{
    internal static class Program
    {
        internal const string DefaultRoot = @"C:\";
        internal static string DataPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskDelta"); }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            EnableLongPaths();
            if (args.Length > 0 && args[0] == "--apply-update")
                return UpdateInstaller.Apply(typeof(Program).Assembly.Location, args);
            if (args.Length > 0 && args[0] == "--migration-admin")
                return InstalledMigration.Run(args);
            bool startup = args.Length == 1 && args[0] == "--startup";
            if (args.Length != 0 && !startup)
                return Headless(args);
            return RunDesktop(DataPath, startup);
        }

        internal static void EnableLongPaths()
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        }

        internal static int RunDesktop(string dataPath, bool startup)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string name;
            using (var identity = WindowsIdentity.GetCurrent())
                name = @"Local\DiskDelta-" + identity.User.Value;
            bool first;
            using (var activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show"))
            using (var instance = new Mutex(true, name, out first))
            {
                if (!first)
                {
                    if (!startup) activate.Set();
                    return 0;
                }
                using (var window = new Dashboard(dataPath))
                using (var context = new ApplicationContext(startup && window.TrayEnabled ? null : window))
                {
                    if (context.MainForm == null)
                        window.FormClosed += delegate { context.ExitThread(); };
                    // The activation event also needs a UI handle when Windows starts us in the tray.
                    if (window.Handle == IntPtr.Zero) return 1;
                    window.BeginInvoke(new Action(window.CheckForUpdates));
                    RegisteredWaitHandle listener = ThreadPool.RegisterWaitForSingleObject(activate, delegate
                    {
                        try { window.BeginInvoke(new Action(window.ShowWindow)); }
                        catch (InvalidOperationException) { }
                    }, null, Timeout.Infinite, false);
                    try { Application.Run(context); }
                    finally { listener.Unregister(null); }
                }
            }
            return 0;
        }

        private static int Headless(string[] args)
        {
            TimeSpan time = TimeSpan.Zero;
            bool scheduled = (args.Length == 2 || args.Length == 4) && args[0] == "--daily" &&
                TimeSpan.TryParseExact(args[1], @"hh\:mm", CultureInfo.InvariantCulture, out time);
            bool manual = args[0] == "--scan" && args.Length <= 3;
            if (!scheduled && !manual)
                return 2;

            History history = null;
            try
            {
                if (scheduled && DateTime.Now.TimeOfDay < time) return 0;
                bool custom = manual && args.Length == 3;
                string root = DefaultRoot;
                string volumeId = null;
                if (custom) root = args[1];
                else if (manual && args.Length == 2) root = Volume.NormalizeRoot(args[1]);
                else if (scheduled && args.Length == 4)
                {
                    root = Volume.NormalizeRoot(args[2]);
                    if (args[3] != "-")
                    {
                        volumeId = args[3];
                        if (!IsVolumeId(volumeId)) return 2;
                    }
                }
                if (!custom && volumeId == null) volumeId = Volume.ReadId(root);
                history = custom ? new History(args[2]) : History.ForDrive(DataPath, root, volumeId);
                history.Scan(root, scheduled ? (TimeSpan?)time : null, null, CancellationToken.None, volumeId);
                if (scheduled) history.ClearScheduledError();
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    if (scheduled && history != null) history.WriteScheduledError(ex);
                    else Console.Error.WriteLine(ex.Message);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return 1;
            }
        }

        private static bool IsVolumeId(string value)
        {
            Guid guid;
            uint serial;
            return (value.Length == 32 && Guid.TryParseExact(value, "N", out guid)) ||
                (value.Length == 8 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out serial));
        }
    }
}
