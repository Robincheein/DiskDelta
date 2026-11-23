using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskDelta
{
    internal sealed class SettingsWindow : Form
    {
        private readonly string dataPath;
        private readonly string executable;
        private readonly StartupRegistration startup;
        private readonly CheckBox tray = new CheckBox { Text = "关闭或最小化时留在系统托盘", AutoSize = true };
        private readonly CheckBox autoStart = new CheckBox { Text = "开机自启动", AutoSize = true };
        private readonly Label generalStatus = MaintenanceView.Note("");
        private readonly Button rebind = MaintenanceView.Action("重新绑定");
        private readonly ComboBox disk = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 163, AccessibleName = "每日扫描磁盘" };
        private readonly CheckBox daily = new CheckBox { Text = "每日扫描", AutoSize = true };
        private readonly DateTimePicker time = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 79,
            AccessibleName = "每日扫描时间"
        };
        private readonly Button save = MaintenanceView.Action("保存计划", true);
        private readonly Label planStatus = MaintenanceView.Note("");
        private readonly Button increaseColor = MaintenanceView.Action("+ 增长颜色");
        private readonly Button decreaseColor = MaintenanceView.Action("− 减少颜色");
        private readonly Label colorStatus = MaintenanceView.Note("");
        private ChangePalette palette;
        private readonly UpdateView update;
        private bool updatingGeneral;
        private bool planReady;
        private bool saving;
        private int planRequest;

        public SettingsWindow(Font font, string dataPath, string executable, IEnumerable<Volume> volumes,
            Volume currentDisk, bool trayEnabled, Action<bool> setTray, GitHubUpdates updates, Func<bool> canInstall,
            ChangePalette palette, Action<ChangePalette> setPalette)
        {
            this.dataPath = dataPath;
            this.executable = executable;
            startup = new StartupRegistration(executable);
            Text = "设置";
            Font = font;
            BackColor = Color.White;
            ClientSize = new Size(465, 524);
            AutoScroll = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;

            var layout = MaintenanceView.Layout(110, 146, 79, 143, 46);
            layout.Dock = DockStyle.Top;
            layout.Height = 524;
            layout.Padding = new Padding(13, 7, 13, 8);
            Controls.Add(layout);
            var generalLayout = MaintenanceView.Layout(25, 25, 29);
            autoStart.Margin = tray.Margin = new Padding(7, 1, 0, 0);
            generalLayout.Controls.Add(tray, 0, 0);
            generalLayout.Controls.Add(autoStart, 0, 1);
            generalLayout.Controls.Add(StatusRow(generalStatus, rebind), 0, 2);
            layout.Controls.Add(Section("常规", generalLayout), 0, 0);

            var scheduleLayout = MaintenanceView.Layout(33, 36, 39);
            scheduleLayout.Controls.Add(MaintenanceView.Actions(new Label
            {
                Text = "磁盘", AutoSize = true, Margin = new Padding(2, 5, 8, 0)
            }, disk), 0, 0);
            daily.Margin = new Padding(2, 5, 12, 0);
            time.Margin = new Padding(0, 3, 4, 0);
            scheduleLayout.Controls.Add(MaintenanceView.Actions(daily, time), 0, 1);
            scheduleLayout.Controls.Add(StatusRow(planStatus, save), 0, 2);
            layout.Controls.Add(Section("扫描计划", scheduleLayout), 0, 1);

            this.palette = palette;
            var appearance = MaintenanceView.Layout(34, 20);
            var resetColors = MaintenanceView.Action("恢复默认");
            appearance.Controls.Add(MaintenanceView.Actions(increaseColor, decreaseColor, resetColors), 0, 0);
            appearance.Controls.Add(colorStatus, 0, 1);
            layout.Controls.Add(Section("变化颜色", appearance), 0, 2);
            PreviewPalette();
            increaseColor.Click += delegate { PickColor(true, setPalette); };
            decreaseColor.Click += delegate { PickColor(false, setPalette); };
            resetColors.Click += delegate { ChangeColors(ChangePalette.Default, setPalette); };

            update = new UpdateView(updates, executable, () => !saving && canInstall());
            layout.Controls.Add(Section("版本更新", update), 0, 3);
            update.InstallReady += delegate { DialogResult = DialogResult.Retry; Close(); };

            var close = MaintenanceView.Action("关闭");
            close.DialogResult = DialogResult.Cancel;
            close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            CancelButton = close;
            layout.Controls.Add(close, 0, 4);

            tray.Checked = trayEnabled;
            tray.CheckedChanged += delegate { ChangeGeneral(tray, setTray); };
            autoStart.CheckedChanged += delegate { ChangeGeneral(autoStart, startup.SetEnabled); };
            rebind.Click += delegate { ChangeGeneral(autoStart, startup.SetEnabled); };
            ReadStartup();

            var available = volumes.ToArray();
            disk.Items.AddRange(available);
            if (available.Length > 0)
                disk.SelectedIndex = Math.Max(0, Array.IndexOf(available, currentDisk));
            SetPlanControls(false);
            daily.CheckedChanged += delegate { time.Enabled = planReady && !saving && daily.Checked; };
            disk.SelectedIndexChanged += async delegate { await ReadPlan(); };
            Shown += async delegate { await ReadPlan(); };
            Shown += async delegate { await update.Check(false); };
            save.Click += async delegate { await SavePlan(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if ((saving || update.Busy) && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            };
            ResumeLayout(true);
        }

        private void PreviewPalette()
        {
            palette.Preview(increaseColor, true);
            palette.Preview(decreaseColor, false);
        }

        private void PickColor(bool increase, Action<ChangePalette> setPalette)
        {
            using (var picker = new ColorDialog { FullOpen = true, Color = increase ? palette.Increase : palette.Decrease })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                ChangeColors(increase ? new ChangePalette(picker.Color, palette.Decrease) :
                    new ChangePalette(palette.Increase, picker.Color), setPalette);
            }
        }

        private void ChangeColors(ChangePalette colors, Action<ChangePalette> setPalette)
        {
            try
            {
                colors.Save(dataPath);
                palette = colors;
                PreviewPalette();
                setPalette(colors);
                colorStatus.Text = "已保存";
            }
            catch (Exception ex) { colorStatus.Text = ex.Message; }
        }

        private static GroupBox Section(string title, Control content)
        {
            var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Margin = new Padding(1, 0, 1, 6) };
            box.Controls.Add(content);
            return box;
        }

        private static Control StatusRow(Label text, Button action)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, RowCount = 1, ColumnCount = 2 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            text.AutoEllipsis = true;
            action.Dock = DockStyle.Fill;
            action.Margin = new Padding(6, 0, 0, 3);
            row.Controls.Add(text, 0, 0);
            row.Controls.Add(action, 1, 0);
            return row;
        }

        private void ReadStartup()
        {
            updatingGeneral = true;
            try
            {
                var status = startup.Read();
                autoStart.Checked = status.Enabled;
                rebind.Visible = status.Enabled && !status.MatchesExecutable;
                if (rebind.Visible) generalStatus.Text = "自启动路径已变更";
            }
            catch (Exception ex)
            {
                autoStart.Enabled = rebind.Enabled = false;
                generalStatus.Text = ex.Message;
            }
            finally { updatingGeneral = false; }
        }

        private void ChangeGeneral(CheckBox option, Action<bool> apply)
        {
            if (updatingGeneral) return;
            try
            {
                apply(option.Checked);
                generalStatus.Text = "已保存";
                if (option == autoStart) ReadStartup();
            }
            catch (Exception ex)
            {
                if (option == autoStart) ReadStartup();
                else
                {
                    updatingGeneral = true;
                    option.Checked = !option.Checked;
                    updatingGeneral = false;
                }
                generalStatus.Text = ex.Message;
            }
        }

        private DailySchedule Schedule(Volume volume)
        {
            return new DailySchedule(executable, volume.Root, volume.Id);
        }

        private async Task ReadPlan()
        {
            int request = ++planRequest;
            var volume = disk.SelectedItem as Volume;
            SetPlanControls(false);
            daily.Checked = false;
            time.Value = DateTime.Today.Add(DailySchedule.DefaultTime);
            planStatus.Text = volume == null ? "没有可用的磁盘" : "正在读取计划…";
            if (volume == null) return;
            try
            {
                var status = await Task.Run(() => Schedule(volume).Read());
                if (IsDisposed || request != planRequest) return;
                daily.Checked = status != null && status.Enabled;
                if (status != null) time.Value = DateTime.Today.Add(status.Time);
                planStatus.Text = status != null && status.Enabled && !status.MatchesExecutable ? "计划路径已变更" : "";
                SetPlanControls(true);
            }
            catch (Exception ex)
            {
                if (!IsDisposed && request == planRequest) planStatus.Text = ex.Message;
            }
        }

        private async Task SavePlan()
        {
            var volume = disk.SelectedItem as Volume;
            if (volume == null || !planReady || saving || update.Busy) return;
            bool enabled = daily.Checked;
            var selectedTime = new TimeSpan(time.Value.Hour, time.Value.Minute, 0);
            saving = true;
            disk.Enabled = false;
            SetPlanControls(true);
            planStatus.Text = "正在保存计划…";
            try
            {
                await Task.Run(() =>
                {
                    var schedule = Schedule(volume);
                    if (enabled) schedule.Enable(selectedTime);
                    else schedule.Disable();
                    History.ForDrive(dataPath, volume.Root, volume.Id).ClearScheduledError();
                });
                if (!IsDisposed)
                    planStatus.Text = enabled ? "已保存 · 每日 " + selectedTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture) : "每日扫描已关闭";
            }
            catch (Exception ex) { if (!IsDisposed) planStatus.Text = ex.Message; }
            finally
            {
                saving = false;
                if (!IsDisposed)
                {
                    disk.Enabled = true;
                    SetPlanControls(true);
                }
            }
        }

        private void SetPlanControls(bool ready)
        {
            planReady = ready;
            daily.Enabled = save.Enabled = ready && !saving;
            time.Enabled = daily.Enabled && daily.Checked;
        }
    }
}
