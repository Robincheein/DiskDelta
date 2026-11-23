using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskDelta
{
    internal sealed class Volume
    {
        public readonly string Root;
        public readonly string Id;
        private readonly string label;
        public string Letter { get { return Root.Substring(0, 2); } }

        private Volume(DriveInfo drive)
        {
            Root = drive.Name;
            Id = ReadId(Root);
            label = drive.VolumeLabel;
        }

        public static IEnumerable<Volume> Available()
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                Volume volume;
                try { volume = drive.IsReady ? new Volume(drive) : null; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (volume != null) yield return volume;
            }
        }

        public static string NormalizeRoot(string value)
        {
            if (value != null && (value.Length == 2 || (value.Length == 3 && value[2] == '\\')) &&
                value[1] == ':' && char.ToUpperInvariant(value[0]) >= 'A' && char.ToUpperInvariant(value[0]) <= 'Z')
                return char.ToUpperInvariant(value[0]) + @":\";
            throw new ArgumentException("请选择有效的本地盘符。", "value");
        }

        public static string ReadId(string root)
        {
            var name = new StringBuilder(64);
            if (GetVolumeNameForVolumeMountPoint(root, name, name.Capacity))
            {
                string path = name.ToString();
                int start = path.IndexOf('{');
                Guid id;
                if (start >= 0 && Guid.TryParse(path.Substring(start).TrimEnd('\\'), out id))
                    return id.ToString("N");
            }
            uint serial, maxName, flags;
            return GetVolumeInformation(root, null, 0, out serial, out maxName, out flags, null, 0)
                ? serial.ToString("x8", CultureInfo.InvariantCulture) : null;
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(label) ? Letter : Letter + "  " + label;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, int length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformation(string root, StringBuilder name, int nameLength,
            out uint serial, out uint maxName, out uint flags, StringBuilder filesystem, int filesystemLength);
    }
}
