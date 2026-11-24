using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        private const int BaseDpi = 96;
        private const int GridRowHeight = 25;
        private const int GridHeaderHeight = 29;
        private const int DpiChangedMessage = 0x02e0;

        private readonly string dataPath;
        private readonly GitHubUpdates updates;
        private History history;
        private Volume volume;
        private readonly ComboBox drives = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = true };
        private readonly ToolTip tips = new ToolTip();
        private readonly Button scanButton = Button("立即扫描", true);
        private readonly Button settings = Button("设置", false);
        private readonly Button toolbarSettings = Button("设置", false);
        private readonly Label period = Label("", 9, Muted);
        private readonly Label state = Label("", 9, Muted);
        private readonly ProgressBar progressBar = new ProgressBar();
        private TableLayoutPanel page;
        private TableLayoutPanel header;
        private TableLayoutPanel cards;
        private readonly Metric disk = new Metric("磁盘可用", true);
        private readonly Metric change = new Metric("磁盘占用净变化", false);
        private readonly Metric visible = new Metric("文件增长合计", false);
        private readonly Panel results = new Panel { BackColor = Color.White, Dock = DockStyle.Fill };
        private readonly DataGridView files = Grid();
        private readonly DataGridView folders = Grid();
        private readonly HistoryView timeline = new HistoryView { Dock = DockStyle.Fill };
        private readonly Label empty = Label("", 11, Muted);
        private readonly Label viewHint = Label("", 9, Muted);
        private readonly Button[] viewButtons =
        {
            Button("目录树", false), Button("增长文件", false), Button("历史记录", false),
            Button("安全清理", false), Button("目录迁移", false), Button("软件位置", false)
        };
        private readonly CleanupView cleanup;
        private readonly MigrationView migration;
        private readonly SoftwareView software;
        private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 20000 };
        private Report selected;
        private string latestReportId;
        private SnapshotComparison comparison;
        private CancellationTokenSource scanning;
        private DateTime lastWrite;
        private int activeView;
        private bool closing;
        private readonly NotifyIcon tray = new NotifyIcon { Text = "磁盘日记" };
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private bool leaving;
        private FormWindowState lastWindowState = FormWindowState.Normal;
        private int tableDpi = BaseDpi;
        private bool layoutReady;
        private string treeReportId;
        private DirectoryTree tree;
        private bool legacyTree;
        private readonly HashSet<string> rebuildAttempted = new HashSet<string>(StringComparer.Ordinal);
        private string rebuildPending;
        private string rebuildHint;
        private readonly List<DirectoryTree.Node> folderRows = new List<DirectoryTree.Node>();
        private ChangePalette palette;
        private int folderSortColumn = 3;
        private SortOrder folderSortOrder = SortOrder.Descending;
        private int fileSortColumn = 1;
        private SortOrder fileSortOrder = SortOrder.Descending;

        public Dashboard(string dataPath)
        {
            SuspendLayout();
            this.dataPath = dataPath;
            palette = ChangePalette.Read(dataPath);
            updates = new GitHubUpdates(dataPath, typeof(Program).Assembly.Location);
            cleanup = new CleanupView(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dataPath)
                { Dock = DockStyle.Fill };
            software = new SoftwareView(dataPath) { Dock = DockStyle.Fill };
            migration = new MigrationView(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dataPath) { Dock = DockStyle.Fill };
            Text = "磁盘日记";
            using (Icon icon = Icon.ExtractAssociatedIcon(typeof(Program).Assembly.Location))
                if (icon != null) Icon = (Icon)icon.Clone();
            ConfigureTray();
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
            drives.SelectedIndexChanged += delegate { SelectDrive(); };
            if (choices.Count > 0)
            {
                int preferred = choices.FindIndex(item => item.Root == Program.DefaultRoot);
                drives.SelectedIndex = Math.Max(0, preferred);
            }
            else
            {
                period.Text = "没有可用的本地磁盘";
                scanButton.Enabled = false;
            }
            SetView(0);
            Shown += delegate
            {
                if (history != null && Directory.GetLastWriteTimeUtc(history.DirectoryPath) != lastWrite)
                    LoadHistory(false);
                if (activeView == 0) CheckLegacyTree();
                refresh.Start();
            };
            refresh.Tick += delegate
            {
                if (scanning == null && history != null && Directory.GetLastWriteTimeUtc(history.DirectoryPath) != lastWrite)
                    LoadHistory(false);
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing) return;
                if (e.CloseReason == CloseReason.UserClosing && tray.Visible && !leaving)
                {
                    e.Cancel = true;
                    Hide();
                    return;
                }
                if (cleanup.Mutating || migration.Mutating)
                {
                    e.Cancel = true;
                    leaving = false;
                    ShowWindow();
                    state.Text = "操作进行中";
                    return;
                }
                if (scanning == null) return;
                e.Cancel = true;
                closing = true;
                scanning.Cancel();
            };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    if (tray.Visible && !leaving) Hide();
                }
                else lastWindowState = WindowState;
            };
            FormClosed += delegate
            {
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
                refresh.Dispose();
                tips.Dispose();
            };
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

        private string TrayPreference { get { return Path.Combine(dataPath, "tray.enabled"); } }
        public bool TrayEnabled { get { return tray.Visible; } }

        private void ConfigureTray()
        {
            tray.Icon = Icon ?? SystemIcons.Application;
            trayMenu.Items.Add("打开磁盘日记", null, delegate { ShowWindow(); });
            trayMenu.Items.Add("设置", null, delegate { ShowWindow(); EditSettings(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { leaving = true; Close(); });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowWindow(); };
            tray.Visible = File.Exists(TrayPreference);
        }

        public void ShowWindow()
        {
            if (IsDisposed) return;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = lastWindowState;
            Activate();
        }

        private void EditSettings()
        {
            using (var dialog = new SettingsWindow(Font, dataPath, typeof(Program).Assembly.Location,
                drives.Items.Cast<Volume>(), volume, tray.Visible, SetTray, updates, CanInstallUpdate,
                palette, ApplyPalette))
            {
                if (dialog.ShowDialog(this) == DialogResult.Retry)
                {
                    leaving = true;
                    Close();
                    return;
                }
            }
            if (history != null) UpdateStatus();
        }

        private void ApplyPalette(ChangePalette colors)
        {
            palette = colors;
            Report report = comparison == null ? selected : comparison.Report;
            if (report != null && report.Baseline != default(DateTime))
            {
                change.ValueColor = ChangeColor(OccupancyChange(report));
                visible.ValueColor = ChangeColor(report.Growth);
            }
            folders.Invalidate();
            files.Invalidate();
        }

        private Color ChangeColor(long delta) { return delta == 0 ? Ink : palette.Foreground(delta); }

        private bool CanInstallUpdate()
        {
            return scanning == null && !cleanup.Mutating && !migration.Mutating;
        }

        public async void CheckForUpdates()
        {
            var result = await updates.CheckAsync(false);
            if (!IsDisposed && result.Release != null)
            {
                settings.Text = toolbarSettings.Text = "设置 · 更新";
                tips.SetToolTip(settings, result.Message);
                tips.SetToolTip(toolbarSettings, result.Message);
            }
        }

        private void SetTray(bool enabled)
        {
            if (enabled)
            {
                Directory.CreateDirectory(dataPath);
                File.WriteAllText(TrayPreference, "");
            }
            else if (File.Exists(TrayPreference)) File.Delete(TrayPreference);
            tray.Visible = enabled;
        }

        private void ScaleTables(int dpi)
        {
            if (tableDpi == dpi) return;
            tableDpi = dpi;
            cleanup.ScaleColumns(dpi);
            int height = TablePixels(GridRowHeight);
            var padding = new Padding(TablePixels(7), 0, TablePixels(7), 0);
            foreach (DataGridView grid in new[] { folders, files, timeline.Records })
            {
                grid.ColumnHeadersHeight = TablePixels(GridHeaderHeight);
                grid.ColumnHeadersDefaultCellStyle.Padding = padding;
                grid.DefaultCellStyle.Padding = padding;
                grid.RowTemplate.Height = height;
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    int width = (int)column.Tag;
                    column.MinimumWidth = TablePixels(width > 0 ? Math.Min(width, 65) : 65);
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
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
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
            scanButton.Dock = DockStyle.Fill;
            scanButton.Margin = new Padding(6, 1, 0, 1);
            scanButton.Click += async delegate { await Scan(); };
            settings.Dock = DockStyle.Fill;
            settings.Margin = new Padding(0, 1, 3, 1);
            settings.Click += delegate { EditSettings(); };
            header.Controls.Add(drives, 0, 0);
            header.Controls.Add(period, 1, 0);
            header.Controls.Add(settings, 2, 0);
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
            toolbarSettings.Dock = DockStyle.Right;
            toolbarSettings.Width = 82;
            toolbarSettings.Click += delegate { EditSettings(); };
            toolbar.Controls.Add(viewHint);
            toolbar.Controls.Add(segmented);
            toolbar.Controls.Add(toolbarSettings);
            content.Controls.Add(toolbar, 0, 0);
            content.Controls.Add(results, 0, 1);
            page.Controls.Add(content, 0, 2);
            empty.Dock = DockStyle.Fill;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            results.Controls.AddRange(new Control[] { files, folders, timeline, empty, cleanup, migration, software });

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
            Column(files, "增加", 114);
            Column(files, "当前大小", 114);
            Column(files, "文件路径", 0);
            ConfigureSorting(files, SortFiles);
            ShowSort(files, fileSortColumn, fileSortOrder);
            files.SortCompare += CompareFileRows;
            files.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (e.ColumnIndex == 1 && e.RowIndex >= 0)
                {
                    var item = files.Rows[e.RowIndex].Tag as Change;
                    if (item != null) palette.Apply(e.CellStyle, item.Delta);
                }
            };
            ConfigureFolderTree();
            timeline.OpenSaved += delegate(Report report)
            {
                comparison = null;
                selected = report;
                treeReportId = null;
                Display();
                SetView(0);
            };
            timeline.OpenComparison += async delegate(Report earlier, Report later) { await OpenComparison(earlier, later); };
            AttachPathActions(files, false, index => ((Change)files.Rows[index].Tag).Path);
        }

        private void ConfigureFolderTree()
        {
            folders.VirtualMode = true;
            Column(folders, "文件夹", 0);
            Column(folders, "对照大小", 110);
            Column(folders, "当前大小", 110);
            Column(folders, "净变化", 113);
            ConfigureSorting(folders, SortFolders);
            ShowSort(folders, folderSortColumn, folderSortOrder);
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
                    palette.Apply(e.CellStyle, node.Change.Delta.Value);
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

        private void ConfigureSorting(DataGridView grid, Action<int> sort)
        {
            foreach (DataGridViewColumn column in grid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.Programmatic;
                column.HeaderCell.ToolTipText = "点击切换升序和降序";
            }
            grid.ColumnHeaderMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && e.ColumnIndex >= 0) sort(e.ColumnIndex);
            };
            grid.CellPainting += delegate(object sender, DataGridViewCellPaintingEventArgs e)
            {
                if (e.RowIndex != -1 || e.ColumnIndex < 0 ||
                    grid.Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection != SortOrder.None) return;
                e.Paint(e.ClipBounds, e.PaintParts);
                int x = e.CellBounds.Right - TablePixels(15), y = e.CellBounds.Top + e.CellBounds.Height / 2;
                int half = TablePixels(3);
                using (var ink = new SolidBrush(Color.FromArgb(130, 148, 157)))
                {
                    e.Graphics.FillPolygon(ink, new[] { new Point(x, y - half - 1), new Point(x - half, y - 1), new Point(x + half, y - 1) });
                    e.Graphics.FillPolygon(ink, new[] { new Point(x, y + half + 1), new Point(x - half, y + 1), new Point(x + half, y + 1) });
                }
                e.Handled = true;
            };
        }

        private static SortOrder NextSort(int column, int previous, SortOrder order, bool alphabetic)
        {
            if (column == previous) return order == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
            return alphabetic ? SortOrder.Ascending : SortOrder.Descending;
        }

        private static void ShowSort(DataGridView grid, int column, SortOrder order)
        {
            foreach (DataGridViewColumn item in grid.Columns)
                item.HeaderCell.SortGlyphDirection = item.Index == column ? order : SortOrder.None;
            grid.Invalidate();
        }

        private void SortFolders(int column)
        {
            folderSortOrder = NextSort(column, folderSortColumn, folderSortOrder, column == 0);
            folderSortColumn = column;
            if (tree != null)
            {
                var cell = folders.CurrentCell;
                var selectedNode = cell == null ? null : folderRows[cell.RowIndex];
                ShowFolderRows();
                int row = folderRows.IndexOf(selectedNode);
                if (row >= 0) folders.CurrentCell = folders.Rows[row].Cells[cell.ColumnIndex];
            }
            ShowSort(folders, folderSortColumn, folderSortOrder);
        }

        private void SortFiles(int column)
        {
            fileSortOrder = NextSort(column, fileSortColumn, fileSortOrder, column == 0 || column == 3);
            fileSortColumn = column;
            ApplyFileSort();
        }

        private void ApplyFileSort()
        {
            if (files.Rows.Count > 1)
                files.Sort(files.Columns[fileSortColumn], fileSortOrder == SortOrder.Ascending ?
                    ListSortDirection.Ascending : ListSortDirection.Descending);
            ShowSort(files, fileSortColumn, fileSortOrder);
        }

        private void CompareFileRows(object sender, DataGridViewSortCompareEventArgs e)
        {
            var left = (Change)files.Rows[e.RowIndex1].Tag;
            var right = (Change)files.Rows[e.RowIndex2].Tag;
            int result;
            switch (e.Column.Index)
            {
                case 0: result = StringComparer.CurrentCultureIgnoreCase.Compare(left.Kind, right.Kind); break;
                case 1: result = left.Delta.CompareTo(right.Delta); break;
                case 2: result = left.Size.CompareTo(right.Size); break;
                default: result = StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path); break;
            }
            e.SortResult = result != 0 ? result : StringComparer.Ordinal.Compare(left.Path, right.Path);
            e.Handled = true;
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
            if (comparison != null)
            {
                if (tree == comparison.Tree) return;
                tree = comparison.Tree;
                treeReportId = null;
                legacyTree = comparison.LegacyTree;
                rebuildHint = legacyTree ? "旧版记录" : null;
                ShowFolderRows();
                return;
            }
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
            ShowFolderRows();
        }

        private void ShowFolderRows()
        {
            tree.Sort(folderSortColumn, folderSortOrder == SortOrder.Descending);
            tree.Visible(folderRows);
            folders.RowCount = folderRows.Count;
            folders.Invalidate();
        }

        private async void CheckLegacyTree()
        {
            if (comparison != null || !legacyTree || !Visible || selected == null) return;
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
            if (IsDisposed || comparison != null || history != store || selected == null || selected.Id != requested.Id) return;
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

        private void SelectDrive()
        {
            volume = drives.SelectedItem as Volume;
            if (volume == null) return;
            history = History.ForDrive(dataPath, volume.Root, volume.Id);
            selected = null;
            comparison = null;
            treeReportId = null;
            LoadHistory(true, true);
        }

        private void LoadHistory(bool newest, bool resetTimeline = false)
        {
            string id = selected == null ? null : selected.Id;
            bool wasNewest = id != null && id == latestReportId;
            try
            {
                List<Report> reports = history.LoadFor(volume.Root, volume.Id);
                latestReportId = reports.Count == 0 ? null : reports[0].Id;
                lastWrite = Directory.GetLastWriteTimeUtc(history.DirectoryPath);
                selected = newest || wasNewest ? reports.FirstOrDefault() :
                    reports.FirstOrDefault(report => report.Id == id) ?? reports.FirstOrDefault();
                if (newest) comparison = null;
                timeline.ShowReports(history, reports, resetTimeline);
                Display();
            }
            catch (Exception ex)
            {
                state.Text = "读取历史失败 · " + ex.Message;
            }
        }

        private async Task OpenComparison(Report earlier, Report later)
        {
            History source = history;
            timeline.SetWorking(true, "正在对比快照…");
            string feedback = "";
            try
            {
                var result = await Task.Run(() => source.CompareFiles(earlier, later));
                if (IsDisposed || source != history) return;
                comparison = result;
                selected = later;
                Display();
                if (activeView == 2) SetView(0);
            }
            catch (Exception ex) { feedback = ex.Message; }
            finally { if (!IsDisposed) timeline.SetWorking(false, source == history ? feedback : ""); }
        }

        private void Display()
        {
            files.Rows.Clear();
            Report report = comparison == null ? selected : comparison.Report;
            bool hasBaseline = report != null && report.Baseline != default(DateTime);
            if (report == null)
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
                period.Text = hasBaseline ? Date(report.Baseline) + "  →  " + Date(report.Started) :
                    "初始快照 · " + Date(report.Started);
                SetDisk(report.Free, report.Capacity, "");
            }

            if (hasBaseline)
            {
                long physicalChange = OccupancyChange(report);
                change.Set(Signed(physicalChange), volume.Letter + " 占用", ChangeColor(physicalChange));
                visible.Set(Bytes(report.Growth), string.Format("新增 {0:N0} · 增大 {1:N0}", report.Added, report.Expanded),
                    ChangeColor(report.Growth));
                tips.SetToolTip(visible, "文件逻辑大小净变化 " + Signed(report.Net));
                foreach (Change item in report.FileChanges)
                    AddPathRow(files, item, item.Kind, Signed(item.Delta), Bytes(item.Size), item.Path);
            }
            else
            {
                change.Set("—", "等待第二次快照");
                visible.Set("—", "等待第二次快照");
                tips.SetToolTip(visible, null);
            }
            ApplyFileSort();
            UpdateStatus();
            SetView(activeView);
        }

        private static long OccupancyChange(Report report)
        {
            long baselineCapacity = report.BaselineCapacity > 0 ? report.BaselineCapacity : report.Capacity;
            return (report.Capacity - report.Free) - (baselineCapacity - report.BaselineFree);
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
                state.Text = "";
            else
                state.Text = string.Format("{0:N0} 个文件", selected.Files);
            tips.SetToolTip(state, selected == null ? "" : string.Format("无法读取目录 {0:N0} · 联接 {1:N0}",
                selected.Unreadable, selected.Links));
        }

        private void SetView(int view)
        {
            bool showHeader = view < 3;
            bool showCards = view < 2;
            if (showHeader != (activeView < 3) || showCards != (activeView < 2))
            {
                page.SuspendLayout();
                header.Visible = showHeader;
                cards.Visible = showCards;
                page.RowStyles[0].Height = showHeader ? TablePixels(42) : 0;
                page.RowStyles[1].Height = showCards ? TablePixels(96) : 0;
                page.ResumeLayout(true);
            }
            activeView = view;
            toolbarSettings.Visible = !showHeader;
            period.Visible = view < 2;
            state.Visible = view < 2 || scanning != null;
            for (int index = 0; index < viewButtons.Length; index++)
            {
                viewButtons[index].BackColor = index == view ? Pale : Color.White;
                viewButtons[index].ForeColor = index == view ? Accent : Muted;
                viewButtons[index].FlatAppearance.BorderColor = index == view ? Pale : Color.White;
            }
            cleanup.Visible = view == 3;
            migration.Visible = view == 4;
            software.Visible = view == 5;
            timeline.Visible = view == 2;
            if (view >= 2)
            {
                files.Visible = folders.Visible = empty.Visible = false;
                viewHint.Text = "";
                if (view == 3) cleanup.ActivateView();
                if (view == 5) software.ActivateView();
                return;
            }
            if (view == 0) LoadFolders();
            var grid = view == 0 ? folders : files;
            bool hasRows = grid.Rows.Count != 0;
            folders.Visible = hasRows && view == 0;
            files.Visible = hasRows && view == 1;
            empty.Visible = !hasRows;
            viewHint.Text = view == 0 && legacyTree ? (rebuildHint ?? "旧版记录") : "";
            if (view == 0 && legacyTree) CheckLegacyTree();
            if (hasRows) return;
            empty.Text = selected == null ? "点击「立即扫描」建立快照" :
                comparison == null && selected.Baseline == default(DateTime) ? "等待第二次扫描" :
                view == 1 ? "这次对比没有发现增长的文件" :
                legacyTree ? "目录明细不可用" : "没有目录变化";
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
            state.Visible = true;
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
                if (!isFolder && File.Exists(path)) ExplorerLocation.Reveal(path);
                else ExplorerLocation.OpenFolder(isFolder ? path : Path.GetDirectoryName(path));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法打开路径", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static void AddPathRow(DataGridView grid, Change item, params object[] cells)
        {
            int index = grid.Rows.Add(cells);
            grid.Rows[index].Tag = item;
            grid.Rows[index].Cells[cells.Length - 1].ToolTipText = item.Path;
        }

        internal static DataGridView Grid()
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

        internal static void Column(DataGridView grid, string caption, int width)
        {
            var column = new DataGridViewTextBoxColumn
            {
                HeaderText = caption, SortMode = DataGridViewColumnSortMode.NotSortable, MinimumWidth = 65, Tag = width
            };
            if (width == 0) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            else column.Width = width;
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

        internal static string Date(DateTime value) { return value.ToString("yyyy-MM-dd HH:mm"); }
        internal static string Signed(long value) { return (value > 0 ? "+" : value < 0 ? "−" : "") + Bytes(Math.Abs(value)); }

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
            public Color ValueColor { set { this.value.ForeColor = value; } }

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
