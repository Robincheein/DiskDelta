using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DiskDelta
{
    internal sealed class ChangePalette
    {
        private const string FileName = "change-colors.txt";
        public static readonly ChangePalette Default = new ChangePalette(Color.FromArgb(184, 42, 52), Color.FromArgb(31, 89, 175));

        public readonly Color Increase;
        public readonly Color Decrease;
        private readonly Color increaseText, decreaseText, increaseFill, decreaseFill, increaseSelected, decreaseSelected;

        public ChangePalette(Color increase, Color decrease)
        {
            Increase = increase;
            Decrease = decrease;
            increaseText = TextColor(increase);
            decreaseText = TextColor(decrease);
            increaseFill = Tint(increase, 0.18);
            decreaseFill = Tint(decrease, 0.18);
            increaseSelected = Tint(increase, 0.29);
            decreaseSelected = Tint(decrease, 0.29);
        }

        public Color Foreground(long change) { return change > 0 ? increaseText : decreaseText; }

        public void Apply(DataGridViewCellStyle style, long change)
        {
            if (change == 0) return;
            style.ForeColor = style.SelectionForeColor = Foreground(change);
            style.BackColor = change > 0 ? increaseFill : decreaseFill;
            style.SelectionBackColor = change > 0 ? increaseSelected : decreaseSelected;
        }

        public void Preview(Button button, bool isIncrease)
        {
            button.BackColor = isIncrease ? increaseFill : decreaseFill;
            button.ForeColor = isIncrease ? increaseText : decreaseText;
            button.FlatAppearance.BorderColor = isIncrease ? increaseSelected : decreaseSelected;
        }

        public static ChangePalette Read(string directory)
        {
            try
            {
                string[] lines = File.ReadAllLines(Path.Combine(directory, FileName));
                int increase, decrease;
                if (lines.Length == 2 && TryColor(lines[0], out increase) && TryColor(lines[1], out decrease))
                    return new ChangePalette(Color.FromArgb(increase | unchecked((int)0xff000000)),
                        Color.FromArgb(decrease | unchecked((int)0xff000000)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Default;
        }

        public void Save(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, new[] { Hex(Increase), Hex(Decrease) });
                if (!MoveFileEx(temp, path, 1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static bool TryColor(string value, out int rgb)
        {
            return int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb) &&
                rgb >= 0 && rgb <= 0xffffff;
        }

        private static string Hex(Color color) { return (color.ToArgb() & 0xffffff).ToString("X6", CultureInfo.InvariantCulture); }

        private static Color TextColor(Color color)
        {
            double brightness = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
            double factor = brightness > 104 ? 104 / brightness : 1;
            return Color.FromArgb((int)(color.R * factor), (int)(color.G * factor), (int)(color.B * factor));
        }

        private static Color Tint(Color color, double amount)
        {
            return Color.FromArgb(255 - (int)((255 - color.R) * amount),
                255 - (int)((255 - color.G) * amount), 255 - (int)((255 - color.B) * amount));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string source, string destination, uint flags);
    }
}
