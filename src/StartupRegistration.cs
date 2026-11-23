using System;
using System.IO;
using Microsoft.Win32;

namespace DiskDelta
{
    internal sealed class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DiskDelta.Startup";
        private readonly string keyPath;
        private readonly string command;

        public StartupRegistration(string executable, string keyPath = RunKey)
        {
            this.keyPath = keyPath;
            command = "\"" + Path.GetFullPath(executable) + "\" --startup";
        }

        public sealed class Status
        {
            public bool Enabled;
            public bool MatchesExecutable;
        }

        public Status Read()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                string stored = key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                return new Status
                {
                    Enabled = !string.IsNullOrEmpty(stored),
                    MatchesExecutable = string.Equals(stored, command, StringComparison.OrdinalIgnoreCase)
                };
            }
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                if (command.Length > 260) throw new IOException("EXE 路径过长，无法注册开机自启动。");
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
                    key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                    if (key != null) key.DeleteValue(ValueName, false);
            }

            Status status = Read();
            if (enabled ? !status.MatchesExecutable : status.Enabled)
                throw new IOException("无法验证开机自启动设置。");
        }
    }
}
