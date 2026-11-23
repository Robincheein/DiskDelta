using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskDelta
{
    internal sealed class Dashboard : Form
    {
        private static readonly Color Canvas = Color.FromArgb(242, 246, 248);
        private static readonly Color Ink = Color.FromArgb(24, 44, 53);
        private static readonly Color Muted = Color.FromArgb(75, 99, 110);
        private static readonly Color Line = Color.FromArgb(222, 232, 236);
        private static readonly Color Accent = Color.FromArgb(0, 104, 92);
        private static readonly Color Pale = Color.FromArgb(225, 244, 238);
        private static readonly Color Rise = Color.FromArgb(162, 73, 34);
        private const int BaseDpi = 96;
        private const int GridRowHeight = 25;
        private const int GridHeaderHeight = 29;
        private const int DpiChangedMessage = 0x02e0;

        private readonly string dataPath;
        private History history;
        private Volume volume;
        private DailySchedule schedule;
        private readonly ComboBox drives = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = true };
        private readonly ToolTip tips = new ToolTip();
        private readonly Button scanButton = Button("立即扫描", true);
        private readonly Button scheduleButton = Button("每日扫描", false);
        private readonly Label period = Label("", 9, Muted);
        private readonly Label state = Label("", 9, Muted);
        private readonly ProgressBar progressBar = new ProgressBar();
        private TableLayoutPanel page;
        private TableLayoutPanel header;
        private TableLayoutPanel cards;
        private bool maintenanceActive;
        private readonly Metric disk = new Metric("磁盘可用", true);
        private readonly Metric change = new Metric("磁盘占用净变化", false);
        private readonly Metric visible = new Metric("文件增长合计", false);
        private readonly Panel results = new Panel { BackColor = Color.White, Dock = DockStyle.Fill };
        private readonly DataGridView files = Grid();
        private readonly DataGridView folders = Grid();
        private readonly DataGridView dates = Grid();
        private readonly Label empty = Label("", 11, Muted);
        private readonly Label viewHint = Label("", 9, Muted);
        private readonly Button[] viewButtons =
        {
            Button("目录树", false), Button("增长文件", false), Button("历史记录", false),
            Button("安全清理", false), Button("目录迁移", false), Button("软件位置", false)
        };
        private readonly CleanupView cleanup;
        private readonly MigrationView migration;
        private readonly SoftwareView software = new SoftwareView { Dock = DockStyle.Fill };
        private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 20000 };
        private Report selected;
        private DailySchedule.Status scheduleStatus;
        private CancellationTokenSource scanning;
        private DateTime lastWrite;
        private int activeView;
        private bool closing;
        private int tableDpi = BaseDpi;
        private bool layoutReady;
        private string treeReportId;
        private DirectoryTree tree;
        private bool legacyTree;
        private readonly HashSet<string> rebuildAttempted = new HashSet<string>(StringComparer.Ordinal);
        private string rebuildPending;
        private string rebuildHint;
        private readonly List<DirectoryTree.Node> folderRows = new List<DirectoryTree.Node>();

        public Dashboard(string dataPath)
        {
            SuspendLayout();
            this.dataPath = dataPath;
            cleanup = new CleanupView(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { Dock = DockStyle.Fill };
            migration = new MigrationView(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dataPath) { Dock = DockStyle.Fill };
            Text = "磁盘日记";
            using (Icon icon = Icon.ExtractAssociatedIcon(typeof(Program).Assembly.Location))
                if (icon != null) Icon = (Icon)icon.Clone();
            BackColor = Canvas;
            ForeColor = Ink;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleDimensions = new SizeF(BaseDpi, BaseDpi);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1050, 672);
            MinimumSize = new Size(720, 460);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            Build();
            ConfigureTables();
            ResumeLayout(true);
            var choices = Volume.Available().ToList();
            drives.Items.AddRange(choices.ToArray());
            drives.SelectedIndexChanged += async delegate { await SelectDrive(); };
            if (choices.Count > 0)
            {
                int preferred = choices.FindIndex(item => item.Root == Program.DefaultRoot);
                drives.SelectedIndex = Math.Max(0, preferred);
            }
            else
            {
                period.Text = "没有可用的本地磁盘";
                scanButton.Enabled = scheduleButton.Enabled = false;
            }
            SetView(0);
            Shown += async delegate
            {
                if (activeView == 0) CheckLegacyTree();
                if (schedule != null) await ReadSchedule();
                if (!IsDisposed) refresh.Start();
            };
            refresh.Tick += delegate
            {
                if (scanning == null && history != null && Directory.GetLastWriteTimeUtc(history.DirectoryPath) != lastWrite)
                    LoadHistory(false);
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (cleanup.Mutating || migration.Mutating)
                {
                    e.Cancel = true;
                    state.Text = "清理或迁移操作正在完成，可以先最小化。";
                    return;
                }
                if (scanning == null) return;
                e.Cancel = true;
                closing = true;
                scanning.Cancel();
            };
            FormClosed += delegate { refresh.Dispose(); tips.Dispose(); };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            using (Graphics graphics = CreateGraphics())
                ScaleTables((int)Math.Round(graphics.DpiX));
            // Startup uses WinForms autoscaling. Subsequent monitor changes use the window DPI.
            AutoScaleMode = AutoScaleMode.None;
            layoutReady = true;

            Rectangle workArea = Screen.FromControl(this).WorkingArea;
            if (Width <= workArea.Width && Height <= workArea.Height) return;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, workArea.Width),
                Math.Min(MinimumSize.Height, workArea.Height));
            Size = new Size(Math.Min(Width, workArea.Width), Math.Min(Height, workArea.Height));
            CenterToScreen();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg != DpiChangedMessage || !layoutReady)
            {
                base.WndProc(ref message);
                return;
            }

            int dpi = (int)(message.WParam.ToInt64() & 0xffff);
            IntPtr rectangle = message.LParam;
            if (dpi <= 0 || rectangle == IntPtr.Zero)
            {
                base.WndProc(ref message);
                return;
            }
            var bounds = Rectangle.FromLTRB(Marshal.ReadInt32(rectangle), Marshal.ReadInt32(rectangle, 4),
                Marshal.ReadInt32(rectangle, 8), Marshal.ReadInt32(rectangle, 12));
            base.WndProc(ref message);
            if (dpi == tableDpi) return;

            SuspendLayout();
            try
            {
                float scale = dpi / (float)tableDpi;
                Scale(new SizeF(scale, scale));
                ScaleTables(dpi);
                Bounds = bounds;
            }
            finally { ResumeLayout(true); }
        }

        private int TablePixels(int units)
        {
            return (int)Math.Round(units * tableDpi / (double)BaseDpi);
        }

        private void ScaleTables(int dpi)
        {
            if (tableDpi == dpi) return;
            tableDpi = dpi;
            int height = TablePixels(GridRowHeight);
            var padding = new Padding(TablePixels(7), 0, TablePixels(7), 0);
            foreach (DataGridView grid in new[] { folders, files, dates })
            {
                grid.ColumnHeadersHeight = TablePixels(GridHeaderHeight);
                grid.ColumnHeadersDefaultCellStyle.Padding = padding;
                grid.DefaultCellStyle.Padding = padding;
                grid.RowTemplate.Height = height;
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    column.MinimumWidth = TablePixels(65);
                    int width = (int)column.Tag;
                    if (width > 0) column.Width = TablePixels(width);
                }
                if (grid.VirtualMode)
                {
                    // Recreate virtual rows from the template without unsharing each row.
                    int selectedRow = grid.CurrentCell == null ? -1 : grid.CurrentCell.RowIndex;
                    int firstRow = grid.FirstDisplayedScrollingRowIndex;
                    grid.RowCount = 0;
                    grid.RowCount = folderRows.Count;
                    if (selectedRow >= 0) grid.CurrentCell = grid.Rows[selectedRow].Cells[0];
                    if (firstRow >= 0) grid.FirstDisplayedScrollingRowIndex = firstRow;
                }
                else
                    foreach (DataGridViewRow row in grid.Rows) row.Height = height;
            }
        }

        private void Build()
        {
            page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 6), ColumnCount = 1, RowCount = 4
            };
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(page);

            header = new TableLayoutPanel
            {
                ColumnCount = 4, RowCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(header, 0, 0);
            drives.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            drives.Margin = Padding.Empty;
            drives.AccessibleName = "扫描磁盘";
            tips.SetToolTip(drives, "选择扫描磁盘");
            period.Dock = DockStyle.Fill;
            period.Margin = new Padding(12, 0, 6, 0);
            period.TextAlign = ContentAlignment.MiddleLeft;
            period.AutoEllipsis = true;
            period.TextChanged += delegate { tips.SetToolTip(period, period.Text); };
            scanButton.Dock = scheduleButton.Dock = DockStyle.Fill;
            scanButton.Margin = scheduleButton.Margin = new Padding(6, 1, 0, 1);
            scanButton.Click += async delegate { await Scan(); };
            scheduleButton.Click += async delegate { await EditSchedule(); };
            header.Controls.Add(drives, 0, 0);
            header.Controls.Add(period, 1, 0);
            header.Controls.Add(scheduleButton, 2, 0);
            header.Controls.Add(scanButton, 3, 0);

            cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cards.Controls.Add(disk, 0, 0);
            cards.Controls.Add(change, 1, 0);
            cards.Controls.Add(visible, 2, 0);
            visible.Margin = Padding.Empty;
            page.Controls.Add(cards, 0, 1);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(8, 3, 8, 6),
                RowCount = 2, ColumnCount = 1, Margin = Padding.Empty
            };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var toolbar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var segmented = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 456, Padding = new Padding(0, 2, 0, 0), WrapContents = false };
            for (int index = 0; index < viewButtons.Length; index++)
            {
                int view = index;
                viewButtons[index].Width = 70;
                viewButtons[index].Height = 27;
                viewButtons[index].Click += delegate { SetView(view); };
                segmented.Controls.Add(viewButtons[index]);
            }
            viewHint.Dock = DockStyle.Fill;
            viewHint.TextAlign = ContentAlignment.MiddleRight;
            viewHint.AutoEllipsis = true;
            toolbar.Controls.Add(viewHint);
            toolbar.Controls.Add(segmented);
            content.Controls.Add(toolbar, 0, 0);
            content.Controls.Add(results, 0, 1);
            page.Controls.Add(content, 0, 2);
            empty.Dock = DockStyle.Fill;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            results.Controls.AddRange(new Control[] { files, folders, dates, empty, cleanup, migration, software });

            var footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            state.Dock = DockStyle.Fill;
            state.TextAlign = ContentAlignment.MiddleLeft;
            state.AutoEllipsis = true;
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.MarqueeAnimationSpeed = 32;
            progressBar.Size = new Size(104, 7);
            progressBar.Location = new Point(footer.Width - 105, 10);
            progressBar.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            progressBar.Visible = false;
            footer.Controls.Add(state);
            footer.Controls.Add(progressBar);
            progressBar.BringToFront();
            page.Controls.Add(footer, 0, 3);
        }

        private void ConfigureTables()
        {
            Column(files, "变化", 73);
            Column(files, "增加", 114, true);
            Column(files, "当前大小", 114);
            Column(files, "文件路径", 0);
            ConfigureFolderTree();
            Column(dates, "扫描时间", 171);
            Column(dates, "磁盘可用", 119);
            Column(dates, "占用净变化", 126);
            Column(dates, "文件增长", 119);
            Column(dates, "对照快照", 0);
            dates.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                selected = (Report)dates.Rows[e.RowIndex].Tag;
                Display();
                SetView(0);
            };
            dates.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter || dates.CurrentRow == null) return;
                e.Handled = e.SuppressKeyPress = true;
                selected = (Report)dates.CurrentRow.Tag;
                Display();
                SetView(0);
            };
            AttachPathActions(files, false, index => (string)files.Rows[index].Tag);
        }

        private void ConfigureFolderTree()
        {
            folders.VirtualMode = true;
            Column(folders, "文件夹", 0);
            Column(folders, "对照大小", 110);
            Column(folders, "当前大小", 110);
            Column(folders, "净变化", 113);
            for (int index = 1; index < folders.Columns.Count; index++)
            {
                folders.Columns[index].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                folders.Columns[index].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }

            folders.CellValueNeeded += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                if (e.RowIndex >= folderRows.Count) return;
                DirectoryTree.Node node = folderRows[e.RowIndex];
                switch (e.ColumnIndex)
                {
                    case 0:
                        string arrow = node.Children.Count == 0 ? "　" : node.Expanded ? "▾ " : "▸ ";
                        e.Value = arrow + node.Caption;
                        break;
                    case 1: e.Value = node.Change.Before.HasValue ? Bytes(node.Change.Before.Value) : "—"; break;
                    case 2: e.Value = node.Change.After.HasValue ? Bytes(node.Change.After.Value) : "—"; break;
                    case 3: e.Value = node.Change.Delta.HasValue ? Signed(node.Change.Delta.Value) : "—"; break;
                }
            };
            folders.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (e.RowIndex < 0 || e.RowIndex >= folderRows.Count) return;
                DirectoryTree.Node node = folderRows[e.RowIndex];
                if (e.ColumnIndex == 0)
                    e.CellStyle.Padding = new Padding(TablePixels(7) + Indent(node), 0, TablePixels(7), 0);
                if (e.ColumnIndex == 3 && node.Change.Delta.HasValue)
                    e.CellStyle.ForeColor = node.Change.Delta.Value > 0 ? Rise :
                        node.Change.Delta.Value < 0 ? Accent : Muted;
            };
            folders.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                if (e.RowIndex < 0 || e.RowIndex >= folderRows.Count) return;
                DirectoryChange item = folderRows[e.RowIndex].Change;
                e.ToolTipText = item.Path + (item.Before.HasValue ?
                    "\n直属文件净变化 " + Signed(item.Direct) : "");
            };
            folders.CellMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex == 0 &&
                    e.X <= Indent(folderRows[e.RowIndex]) + TablePixels(24))
                    ToggleFolder(e.RowIndex);
            };
            folders.CellMouseDoubleClick += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && (e.ColumnIndex != 0 ||
                    e.X > Indent(folderRows[e.RowIndex]) + TablePixels(24)))
                    ToggleFolder(e.RowIndex);
            };
            folders.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (folders.CurrentCell == null) return;
                int row = folders.CurrentCell.RowIndex;
                DirectoryTree.Node node = folderRows[row];
                if (e.KeyCode == Keys.Right && node.Children.Count > 0)
                {
                    if (node.Expanded) folders.CurrentCell = folders.Rows[row + 1].Cells[0];
                    else ToggleFolder(row);
                }
                else if (e.KeyCode == Keys.Left)
                {
                    if (node.Expanded && node.Children.Count > 0) ToggleFolder(row);
                    else if (node.Parent != null) folders.CurrentCell = folders.Rows[folderRows.IndexOf(node.Parent)].Cells[0];
                }
                else if (e.KeyCode == Keys.Enter) ToggleFolder(row);
                else return;
                e.Handled = e.SuppressKeyPress = true;
            };
            AttachPathActions(folders, true, index => folderRows[index].Change.Path);
        }

        private int Indent(DirectoryTree.Node node)
        {
            return Math.Min(node.Depth, 18) * TablePixels(14);
        }

        private void ToggleFolder(int row)
        {
            DirectoryTree.Node node = folderRows[row];
            if (node.Children.Count == 0) return;
            node.Expanded = !node.Expanded;
            tree.Visible(folderRows);
            folders.RowCount = folderRows.Count;
            folders.CurrentCell = folders.Rows[row].Cells[0];
            folders.Invalidate();
        }

        private void LoadFolders()
        {
            if (selected == null || selected.Baseline == default(DateTime))
            {
                treeReportId = null;
                tree = null;
                legacyTree = false;
                folderRows.Clear();
                folders.RowCount = 0;
                return;
            }
            if (treeReportId == selected.Id) return;
            DirectoryTree saved = null;
            try { saved = new DirectoryTree(selected.Root, history.ReadTree(selected)); }
            catch (IOException) { }
            catch (InvalidDataException) { }
            legacyTree = saved == null;
            rebuildHint = null;
            tree = saved ?? DirectoryTree.FromLegacy(selected);
            treeReportId = selected.Id;
            tree.Visible(folderRows);
            folders.RowCount = folderRows.Count;
            folders.Invalidate();
        }

        private async void CheckLegacyTree()
        {
            if (!legacyTree || !Visible || selected == null) return;
            var requested = selected;
            var store = history;
            if (rebuildPending != null || !rebuildAttempted.Add(requested.Id)) return;
            rebuildPending = requested.Id;
            viewHint.Text = "正在还原目录大小…";
            bool restored = false;
            string message = "旧记录缺少完整快照";
            try { restored = await Task.Run(() => store.RebuildTree(requested)); }
            catch (IOException ex)
            {
                if ((ex.HResult & 0xffff) == 32)
                {
                    rebuildAttempted.Remove(requested.Id);
                    message = "扫描中，稍后可重试";
                }
                else message = "旧快照无法读取";
            }
            catch (Exception) { message = "旧快照无法读取"; }
            rebuildPending = null;
            if (IsDisposed || history != store || selected == null || selected.Id != requested.Id) return;
            if (restored)
            {
                treeReportId = null;
                if (activeView == 0) SetView(0);
            }
            else
            {
                rebuildHint = message;
                if (activeView == 0) viewHint.Text = message;
            }
        }

        private async Task SelectDrive()
        {
            volume = drives.SelectedItem as Volume;
            if (volume == null) return;
            history = History.ForDrive(dataPath, volume.Root, volume.Id);
            schedule = new DailySchedule(typeof(Program).Assembly.Location, volume.Root, volume.Id);
            scheduleStatus = null;
            scheduleButton.Text = "每日扫描";
            selected = null;
            treeReportId = null;
            LoadHistory(true);
            if (Visible) await ReadSchedule();
        }

        private void LoadHistory(bool newest)
        {
            string id = selected == null ? null : selected.Id;
            bool wasNewest = dates.Rows.Count > 0 && dates.Rows[0].Tag == selected;
            try
            {
                List<Report> reports = history.LoadFor(volume.Root, volume.Id);
                lastWrite = Directory.GetLastWriteTimeUtc(history.DirectoryPath);
                selected = newest || wasNewest ? reports.FirstOrDefault() :
                    reports.FirstOrDefault(report => report.Id == id) ?? reports.FirstOrDefault();
                dates.Rows.Clear();
                foreach (Report report in reports)
                {
                    bool compared = report.Baseline != default(DateTime);
                    int index = dates.Rows.Add(Date(report.Started), Bytes(report.Free),
                        compared ? Signed(report.BaselineFree - report.Free) : "—",
                        compared ? Bytes(report.Growth) : "—", compared ? Date(report.Baseline) : "初始快照");
                    dates.Rows[index].Tag = report;
                }
                dates.ClearSelection();
                Display();
            }
            catch (Exception ex)
            {
                state.Text = "读取历史失败 · " + ex.Message;
            }
        }

        private void Display()
        {
            files.Rows.Clear();
            bool hasBaseline = selected != null && selected.Baseline != default(DateTime);
            if (selected == null)
            {
                period.Text = volume.Letter + "  ·  等待首次扫描";
                try
                {
                    var drive = new DriveInfo(volume.Root);
                    SetDisk(drive.TotalFreeSpace, drive.TotalSize, "");
                }
                catch (IOException) { disk.Set("—", "无法读取 " + volume.Letter); }
            }
            else
            {
                period.Text = hasBaseline ? Date(selected.Baseline) + "  →  " + Date(selected.Started) :
                    "初始快照 · " + Date(selected.Started);
                SetDisk(selected.Free, selected.Capacity, "快照 · ");
            }

            if (hasBaseline)
            {
                long physicalChange = selected.BaselineFree - selected.Free;
                change.Set(Signed(physicalChange), "按 " + volume.Letter + " 可用空间计算", physicalChange > 0 ? Rise : Accent);
                visible.Set(Bytes(selected.Growth), string.Format("新增 {0:N0} · 增大 {1:N0}", selected.Added, selected.Expanded));
                tips.SetToolTip(visible, "只合计新增和增大的文件。可比较文件的逻辑大小净变化为 " + Signed(selected.Net) + "。");
                foreach (Change item in selected.FileChanges)
                    AddPathRow(files, item, item.Kind, Signed(item.Delta), Bytes(item.Size), item.Path);
            }
            else
            {
                change.Set("—", "等待第二次快照");
                visible.Set("—", "等待第二次快照");
                tips.SetToolTip(visible, null);
            }
            UpdateStatus();
            SetView(activeView);
        }

        private void SetDisk(long free, long total, string prefix)
        {
            double fraction = total > 0 ? (double)(total - free) / total : 0;
            disk.Set(Bytes(free), prefix + "总容量 " + Bytes(total) + "   ·   已用 " + fraction.ToString("P0"), Ink, fraction);
        }

        private void UpdateStatus()
        {
            if (scanning != null) return;
            string scheduledError = history.ReadScheduledError();
            if (scheduledError != null)
                state.Text = "每日扫描失败 · " + scheduledError;
            else if (selected == null)
                state.Text = "首次扫描只建立基准，第二次扫描开始显示变化。";
            else
            {
                state.Text = string.Format("已记录 {0:N0} 个文件   ·   无法读取 {1:N0} 个目录   ·   跳过 {2:N0} 个重解析点",
                    selected.Files, selected.Unreadable, selected.Links);
                if (selected.BaselineUnreadable > 0)
                    state.Text += string.Format("   ·   基准有 {0:N0} 个不可读目录", selected.BaselineUnreadable);
            }
            tips.SetToolTip(state, state.Text + "\n文件按逻辑大小统计。权限受限、硬链接、NTFS 元数据等会使文件合计与磁盘占用变化不同。");
        }

        private void SetView(int view)
        {
            activeView = view;
            bool maintenance = view >= 3;
            if (maintenance != maintenanceActive)
            {
                maintenanceActive = maintenance;
                page.SuspendLayout();
                header.Visible = cards.Visible = !maintenance;
                page.RowStyles[0].Height = maintenance ? 0 : TablePixels(42);
                page.RowStyles[1].Height = maintenance ? 0 : TablePixels(96);
                page.ResumeLayout(true);
            }
            for (int index = 0; index < viewButtons.Length; index++)
            {
                viewButtons[index].BackColor = index == view ? Pale : Color.White;
                viewButtons[index].ForeColor = index == view ? Accent : Muted;
                viewButtons[index].FlatAppearance.BorderColor = index == view ? Pale : Color.White;
            }
            cleanup.Visible = view == 3;
            migration.Visible = view == 4;
            software.Visible = view == 5;
            if (view >= 3)
            {
                files.Visible = folders.Visible = dates.Visible = empty.Visible = false;
                viewHint.Text = view == 3 ? "当前用户缓存" : view == 4 ? "配置与缓存目录" : "跨盘查找";
                if (view == 5) software.ActivateView();
                return;
            }
            if (view == 0) LoadFolders();
            var grid = view == 0 ? folders : view == 1 ? files : dates;
            bool hasRows = grid.Rows.Count != 0;
            folders.Visible = hasRows && view == 0;
            files.Visible = hasRows && view == 1;
            dates.Visible = hasRows && view == 2;
            empty.Visible = !hasRows;
            viewHint.Text = view == 0 ? (legacyTree ? (rebuildHint ?? "旧快照 · 大小待恢复") : "点击 ▸ 展开") :
                view == 1 ? "按增加量排序 · 最多 150 个" : "双击查看对应快照";
            if (view == 0 && legacyTree) CheckLegacyTree();
            if (hasRows) return;
            empty.Text = view == 2 ? "还没有扫描记录" :
                selected == null ? "点击「立即扫描」，建立第一份快照" :
                selected.Baseline == default(DateTime) ? "基准已保存。再次扫描即可查看变化。" :
                view == 1 ? "这次对比没有发现增长的文件" :
                legacyTree ? "该记录缺少目录树，再扫描一次即可生成。" : "这次对比没有发现目录变化";
        }

        private async Task Scan()
        {
            if (scanning != null)
            {
                scanning.Cancel();
                scanButton.Enabled = false;
                return;
            }
            scanning = new CancellationTokenSource();
            drives.Enabled = false;
            scanButton.Text = "取消扫描";
            progressBar.Visible = true;
            state.Text = "正在读取 " + volume.Letter + " 文件列表…";
            var progress = new Progress<Tuple<long, string>>(message =>
            {
                if (scanning == null) return;
                state.Text = message.Item2 == null ? "正在对比两次快照…" :
                    string.Format("已读取 {0:N0} 个文件   ·   {1}", message.Item1, message.Item2);
            });
            string error = null;
            try
            {
                await Task.Run(() => history.Scan(volume.Root, null,
                    (count, path) => ((IProgress<Tuple<long, string>>)progress).Report(Tuple.Create(count, path)),
                    scanning.Token, volume.Id));
            }
            catch (OperationCanceledException) { error = "扫描已取消。"; }
            catch (IOException ex)
            {
                error = (ex.HResult & 0xffff) == 32 ? "已有扫描在后台运行，请稍后查看。" : "扫描失败 · " + ex.Message;
            }
            catch (Exception ex) { error = "扫描失败 · " + ex.Message; }
            finally
            {
                scanning.Dispose();
                scanning = null;
                progressBar.Visible = false;
                scanButton.Text = "立即扫描";
                scanButton.Enabled = true;
                drives.Enabled = true;
            }
            if (closing) { Close(); return; }
            LoadHistory(error == null);
            if (error != null) state.Text = error;
        }

        private async Task ReadSchedule()
        {
            DailySchedule requested = schedule;
            try
            {
                var status = await Task.Run(() => requested.Read());
                if (IsDisposed || schedule != requested) return;
                scheduleStatus = status;
                scheduleButton.Text = scheduleStatus == null || !scheduleStatus.Enabled ? "每日扫描" :
                    scheduleStatus.MatchesExecutable ? "每日 " + FormatTime(scheduleStatus.Time) : "重新绑定计划";
                tips.SetToolTip(scheduleButton, scheduleStatus == null ? "设置 Windows 每日扫描任务" :
                    scheduleStatus.MatchesExecutable ? "管理 Windows 每日扫描任务" : "EXE 路径已变化。保存时间以更新任务。");
            }
            catch (Exception ex) { if (!IsDisposed && schedule == requested) tips.SetToolTip(scheduleButton, ex.Message); }
        }

        private async Task EditSchedule()
        {
            using (var dialog = new Form
            {
                Text = "每日扫描", Font = Font, BackColor = Color.White, ClientSize = new Size(372, 163),
                FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
            })
            {
                var title = Label("每天扫描 " + volume.Letter + " 的时间", 12, Ink, true);
                title.SetBounds(17, 13, 320, 26);
                var time = new DateTimePicker
                {
                    Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true,
                    Value = DateTime.Today.Add(scheduleStatus == null ? DailySchedule.DefaultTime : scheduleStatus.Time)
                };
                time.SetBounds(19, 48, 95, 27);
                var hint = Label("Windows 任务计划在你登录时运行。", 9, Muted);
                hint.SetBounds(18, 79, 340, 24);
                var save = Button("启用每日扫描", true);
                save.SetBounds(220, 110, 133, 35);
                save.DialogResult = DialogResult.OK;
                var disable = Button("关闭每日扫描", false);
                disable.SetBounds(81, 110, 132, 35);
                disable.DialogResult = DialogResult.No;
                disable.Visible = scheduleStatus != null;
                dialog.AcceptButton = save;
                dialog.Controls.AddRange(new Control[] { title, time, hint, save, disable });
                dialog.AutoScaleDimensions = new SizeF(96, 96);
                dialog.AutoScaleMode = AutoScaleMode.Dpi;
                DialogResult answer = dialog.ShowDialog(this);
                if (answer != DialogResult.OK && answer != DialogResult.No) return;
                scheduleButton.Enabled = false;
                DailySchedule requested = schedule;
                History requestedHistory = history;
                try
                {
                    var at = new TimeSpan(time.Value.Hour, time.Value.Minute, 0);
                    await Task.Run(() =>
                    {
                        if (answer == DialogResult.OK) requested.Enable(at);
                        else requested.Disable();
                        requestedHistory.ClearScheduledError();
                    });
                    if (IsDisposed || schedule != requested) return;
                    await ReadSchedule();
                    if (IsDisposed || schedule != requested) return;
                    UpdateStatus();
                    if (scanning == null)
                        state.Text = answer == DialogResult.OK ? "每日扫描已启用 · " + FormatTime(at) : "每日扫描已关闭。";
                }
                catch (Exception ex)
                {
                    if (!IsDisposed) MessageBox.Show(this, ex.Message, "任务计划未能保存", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { if (!IsDisposed) scheduleButton.Enabled = true; }
            }
        }

        private void AttachPathActions(DataGridView grid, bool isFolder, Func<int, string> rowPath)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(isFolder ? "打开目录" : "在资源管理器中定位", null,
                delegate { if (grid.CurrentCell != null) OpenPath(rowPath(grid.CurrentCell.RowIndex), isFolder); });
            menu.Items.Add("复制完整路径", null,
                delegate { if (grid.CurrentCell != null) Clipboard.SetText(rowPath(grid.CurrentCell.RowIndex)); });
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = grid.CurrentRow == null; };
            grid.ContextMenuStrip = menu;
            grid.CellMouseDown += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
                    grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            };
            if (!isFolder)
                grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
                {
                    if (e.RowIndex >= 0) OpenPath(rowPath(e.RowIndex), false);
                };
        }

        private void OpenPath(string path, bool isFolder)
        {
            try
            {
                string target = isFolder ? path : Path.GetDirectoryName(path);
                if (!Directory.Exists(target))
                    throw new DirectoryNotFoundException("该路径已移动、已删除或无法访问。");
                if (!isFolder && File.Exists(path))
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法打开路径", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static void AddPathRow(DataGridView grid, Change item, params object[] cells)
        {
            int index = grid.Rows.Add(cells);
            grid.Rows[index].Tag = item.Path;
            grid.Rows[index].Cells[cells.Length - 1].ToolTipText = item.Path;
        }

        private static DataGridView Grid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                ReadOnly = true, RowHeadersVisible = false, MultiSelect = false, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, AllowUserToResizeRows = false, EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = GridHeaderHeight, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Line,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            grid.RowTemplate.Height = GridRowHeight;
            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.White, ForeColor = Ink, SelectionBackColor = Pale, SelectionForeColor = Ink,
                Padding = new Padding(7, 0, 7, 0), WrapMode = DataGridViewTriState.False
            };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 252);
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Canvas, ForeColor = Muted, SelectionBackColor = Canvas, SelectionForeColor = Muted,
                Padding = new Padding(7, 0, 7, 0)
            };
            return grid;
        }

        private static void Column(DataGridView grid, string caption, int width, bool accent = false)
        {
            var column = new DataGridViewTextBoxColumn
            {
                HeaderText = caption, SortMode = DataGridViewColumnSortMode.NotSortable, MinimumWidth = 65, Tag = width
            };
            if (width == 0) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            else column.Width = width;
            if (accent) column.DefaultCellStyle.ForeColor = Accent;
            grid.Columns.Add(column);
        }

        private static Button Button(string caption, bool primary)
        {
            var button = new Button
            {
                Text = caption, Height = 37, FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 0, 3, 0),
                BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Accent,
                Cursor = Cursors.Hand, UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = primary ? Accent : Line;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0, 88, 77) : Pale;
            return button;
        }

        private static Label Label(string caption, float size, Color color, bool bold = false)
        {
            return new Label
            {
                Text = caption, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = color, BackColor = Color.Transparent
            };
        }

        private static string Date(DateTime value) { return value.ToString("yyyy-MM-dd HH:mm"); }
        private static string FormatTime(TimeSpan value) { return value.ToString(@"hh\:mm", CultureInfo.InvariantCulture); }
        private static string Signed(long value) { return (value > 0 ? "+" : value < 0 ? "−" : "") + Bytes(Math.Abs(value)); }

        internal static string Bytes(long value)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double amount = value;
            int unit = 0;
            while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
            return amount.ToString(unit == 0 ? "0" : "0.##") + " " + units[unit];
        }

        private sealed class Metric : Panel
        {
            private readonly Label value = Label("—", 19, Ink, true);
            private readonly Label detail = Label("", 9, Muted);
            private readonly bool hasBar;
            private double used;

            public Metric(string caption, bool hasBar)
            {
                this.hasBar = hasBar;
                Dock = DockStyle.Fill;
                Margin = new Padding(0, 0, 8, 0);
                BackColor = hasBar ? Color.FromArgb(234, 248, 243) : Color.White;
                DoubleBuffered = true;
                ResizeRedraw = true;
                Label title = Label(caption, 9, Muted, true);
                title.AutoSize = true;
                value.AutoSize = true;
                detail.AutoSize = true;
                title.Location = new Point(11, 6);
                value.Location = new Point(9, 23);
                detail.Location = new Point(11, 57);
                Controls.AddRange(new Control[] { title, value, detail });
            }

            public void Set(string number, string info, Color? color = null, double fraction = 0)
            {
                value.Text = number;
                value.ForeColor = color ?? Ink;
                detail.Text = info;
                used = Math.Max(0, Math.Min(1, fraction));
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var border = new Pen(Line))
                    e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                float scale = e.Graphics.DpiX / 96f;
                int inset = (int)Math.Round(12 * scale);
                int width = Width - 2 * inset;
                if (!hasBar || width <= 0) return;
                int y = Height - (int)Math.Round(6 * scale);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var track = new Pen(Color.FromArgb(190, 224, 214), 3 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var fill = new Pen(Accent, 3 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    e.Graphics.DrawLine(track, inset, y, inset + width, y);
                    if (used > 0) e.Graphics.DrawLine(fill, inset, y, inset + (int)(width * used), y);
                }
            }
        }
    }
}
