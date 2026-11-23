using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace DiskDelta
{
    internal sealed class DailySchedule
    {
        public static readonly TimeSpan DefaultTime = TimeSpan.FromHours(20);
        private static readonly XNamespace Schema = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        private const int TaskCreateOrUpdate = 6;
        private const int TaskInteractiveToken = 3;
        private readonly string executable;
        private readonly string sid;
        private readonly string name;
        private readonly string root;
        private readonly string volumeId;

        public DailySchedule(string executable, string root, string volumeId)
        {
            this.executable = executable;
            this.root = root;
            this.volumeId = volumeId;
            using (var user = WindowsIdentity.GetCurrent())
                sid = user.User.Value;
            name = "DiskDelta-" + sid + (root == Program.DefaultRoot ? "" : "-" + root[0] + "-" + (volumeId ?? "unknown"));
        }

        public sealed class Status
        {
            public TimeSpan Time;
            public bool MatchesExecutable;
            public bool Enabled;
        }

        public Status Read()
        {
            dynamic folder = TaskFolder();
            dynamic entry = FindTask(folder);
            if (entry == null)
                return null;
            var task = XDocument.Parse((string)entry.Xml);
            var start = task.Descendants(Schema + "StartBoundary").FirstOrDefault();
            var command = task.Descendants(Schema + "Command").FirstOrDefault();
            DateTime startTime;
            if (start == null || !DateTime.TryParse(start.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out startTime))
                return null;
            return new Status
            {
                Time = startTime.TimeOfDay,
                Enabled = (bool)entry.Enabled,
                MatchesExecutable = command != null && string.Equals(command.Value.Trim('"'), executable,
                    StringComparison.OrdinalIgnoreCase)
            };
        }

        public void Enable(TimeSpan time)
        {
            string xml = CreateDefinition(time, DateTime.Today).ToString();
            dynamic folder = TaskFolder();
            folder.RegisterTask(name, xml, TaskCreateOrUpdate, sid, null, TaskInteractiveToken, null);
            var status = Read();
            if (status == null || !status.Enabled || !status.MatchesExecutable || status.Time != time)
                throw new InvalidOperationException("无法验证任务计划中的每日扫描。");
        }

        internal XDocument CreateDefinition(TimeSpan time, DateTime day)
        {
            string start = day.Add(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            string letter = root.Substring(0, 2);
            string arguments = "--daily " + time.ToString(@"hh\:mm", CultureInfo.InvariantCulture) +
                " " + letter + " " + (volumeId ?? "-");
            return new XDocument(
                new XElement(Schema + "Task", new XAttribute("version", "1.2"),
                    Node("RegistrationInfo", Node("Description", "磁盘日记每日快照 " + letter)),
                    Node("Triggers",
                        Node("CalendarTrigger", Node("StartBoundary", start), Node("Enabled", "true"),
                            Node("ScheduleByDay", Node("DaysInterval", "1"))),
                        Node("LogonTrigger", Node("Enabled", "true"), Node("UserId", sid))),
                    Node("Principals", Node("Principal", new XAttribute("id", "CurrentUser"),
                        Node("UserId", sid), Node("LogonType", "InteractiveToken"), Node("RunLevel", "LeastPrivilege"))),
                    Node("Settings",
                        Node("MultipleInstancesPolicy", "IgnoreNew"),
                        Node("DisallowStartIfOnBatteries", "false"), Node("StopIfGoingOnBatteries", "false"),
                        Node("StartWhenAvailable", "true"), Node("Enabled", "true"),
                        Node("ExecutionTimeLimit", "PT2H"),
                        Node("RestartOnFailure", Node("Interval", "PT10M"), Node("Count", "2"))),
                    Node("Actions", new XAttribute("Context", "CurrentUser"),
                        Node("Exec", Node("Command", executable),
                            Node("Arguments", arguments)))));
        }

        public void Disable()
        {
            dynamic folder = TaskFolder();
            if (FindTask(folder) != null)
                folder.DeleteTask(name, 0);
        }

        private static XElement Node(string name, params object[] children)
        {
            return new XElement(Schema + name, children);
        }

        private object FindTask(dynamic folder)
        {
            try { return folder.GetTask(name); }
            catch (FileNotFoundException) { return null; }
            catch (COMException ex)
            {
                const int NotFound = unchecked((int)0x80070002);
                if (ex.HResult == NotFound) return null;
                throw;
            }
        }

        private static object TaskFolder()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            service.Connect();
            return service.GetFolder(@"\");
        }
    }
}
