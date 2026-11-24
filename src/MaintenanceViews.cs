using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskDelta
{
    internal static class MaintenanceView
    {
        public static readonly Color Green = Color.FromArgb(0, 104, 92);
        public static readonly Color Muted = Color.FromArgb(75, 99, 110);
        public static readonly Color Soft = Color.FromArgb(242, 246, 248);

        public static Button Action(string text, bool primary = false)
        {
            var button = new Button
            {
                Text = text, AutoSize = true, Height = 29, MinimumSize = new Size(85, 29),
                FlatStyle = FlatStyle.Flat, ForeColor = primary ? Color.White : Green,
                BackColor = primary ? Green : Color.White, UseVisualStyleBackColor = false,
                Margin = new Padding(0, 1, 7, 1)
            };
            button.FlatAppearance.BorderColor = primary ? Green : Color.FromArgb(222, 232, 236);
            return button;
        }

        public static Label Note(string text)
        {
            return new Label { Text = text, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        }

        public static FlowLayoutPanel Actions(params Control[] controls)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            bar.Controls.AddRange(controls);
            return bar;
        }

        public static TableLayoutPanel Layout(params float[] rows)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = rows.Length,
                Padding = new Padding(5, 1, 5, 1), Margin = Padding.Empty
            };
            foreach (float row in rows)
                panel.RowStyles.Add(row == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, row));
            return panel;
        }

        public static TreeView Tree()
        {
            return new TreeView
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White,
                HideSelection = false, ShowNodeToolTips = true
            };
        }

        public static bool Confirm(Control owner, string prompt, string title)
        {
            return MessageBox.Show(owner, prompt, title, MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
    }

    internal sealed class CleanupView : UserControl
    {
        private const int CountColumnWidth = 76;
        private const int SizeColumnWidth = 112;
        private readonly Cleanup cleaner;
        private readonly ScanCache<CleanupSnapshot> cache;
        private readonly Button scan = MaintenanceView.Action("扫描项目", true);
        private readonly Button apply = MaintenanceView.Action("清理勾选项");
        private readonly Button system = MaintenanceView.Action("Windows 磁盘清理");
        private readonly Button duplicates = MaintenanceView.Action("查找重复文件");
        private readonly Button recycle = MaintenanceView.Action("打开回收站");
        private readonly Label status = MaintenanceView.Note("");
        private readonly Label details = MaintenanceView.Note("");
        private readonly TreeView files = MaintenanceView.Tree();
        private readonly ContextMenuStrip fileMenu = new ContextMenuStrip();
        private readonly SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Panel2Collapsed = true };
        private readonly DataGridView list = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.None, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            EditMode = DataGridViewEditMode.EditOnEnter
        };
        private bool busy;
        private bool started;
        private CleanupSnapshot snapshot;
        public bool Mutating { get; private set; }

        public CleanupView(string localAppData, string dataPath)
        {
            cache = new ScanCache<CleanupSnapshot>(dataPath, "cleanup-scan-v1.bin.gz");
            cleaner = new Cleanup(localAppData,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("TEMP"));
            var main = MaintenanceView.Layout(37, 0, 24);
            Controls.Add(main);
            main.Controls.Add(MaintenanceView.Actions(scan, apply, duplicates, system), 0, 0);
            main.Controls.Add(split, 0, 1);
            main.Controls.Add(status, 0, 2);
            split.Panel1.Controls.Add(list);
            var preview = MaintenanceView.Layout(125, 0);
            var previewHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            previewHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            previewHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewHeader.Controls.Add(details, 0, 0);
            recycle.Anchor = AnchorStyles.Right;
            recycle.Visible = false;
            previewHeader.Controls.Add(recycle, 1, 0);
            preview.Controls.Add(previewHeader, 0, 0);
            preview.Controls.Add(files, 0, 1);
            split.Panel2.Controls.Add(preview);

            list.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "选", Width = 42 });
            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "可清理项目", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true
            });
            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Count", HeaderText = "数量", MinimumWidth = CountColumnWidth, ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Size", HeaderText = "大小", MinimumWidth = SizeColumnWidth, ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            foreach (DataGridViewColumn column in list.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            list.EnableHeadersVisualStyles = false;
            list.ColumnHeadersDefaultCellStyle.BackColor = MaintenanceView.Soft;
            list.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            list.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            list.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            list.GridColor = Color.FromArgb(222, 232, 236);
            list.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 244, 238);
            list.DefaultCellStyle.SelectionForeColor = Color.FromArgb(24, 44, 53);
            list.RowTemplate.Height = 33;
            list.CellClick += delegate(object sender, DataGridViewCellEventArgs args)
            {
                if (args.RowIndex >= 0 && !busy) ShowGroup((CleanupGroup)list.Rows[args.RowIndex].Tag);
            };
            list.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode != Keys.Enter || list.CurrentRow == null || busy) return;
                ShowGroup((CleanupGroup)list.CurrentRow.Tag);
                args.Handled = args.SuppressKeyPress = true;
            };
            list.CurrentCellDirtyStateChanged += delegate
            {
                if (list.IsCurrentCellDirty) list.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            list.CellValueChanged += delegate { apply.Enabled = !busy && Selected().Any(); };
            scan.Click += async delegate { await Scan(); };
            apply.Click += async delegate { await Apply(); };
            system.Click += delegate { OpenDiskCleanup(); };
            duplicates.Click += delegate
            {
                using (var dialog = new DuplicatesWindow(Font)) dialog.ShowDialog(this);
            };
            recycle.Click += delegate
            {
                try { RecycleBin.Open(); }
                catch (Exception ex) { status.Text = ex.Message; }
            };
            files.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs args) { Populate(args.Node); };
            files.NodeMouseDoubleClick += delegate(object sender, TreeNodeMouseClickEventArgs args) { LoadMore(args.Node); };
            files.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter && LoadMore(files.SelectedNode)) args.Handled = args.SuppressKeyPress = true;
            };
            ConfigureFileMenu();
            ScaleColumns(96);
            ShowResults(cleaner.Options());
            apply.Enabled = false;
        }

        public async void ActivateView()
        {
            if (started) return;
            started = true;
            Busy(true);
            status.Text = "正在读取上次扫描…";
            try
            {
                var saved = await Task.Run(() => cleaner.Restore(cache.Read()));
                if (IsDisposed) return;
                if (saved == null) await Scan();
                else DisplaySnapshot(saved, true);
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = "读取上次扫描失败 · " + ex.Message; }
            finally { if (!IsDisposed) Busy(false); }
        }

        internal void ScaleColumns(int dpi)
        {
            double scale = dpi / 96.0;
            list.Columns["Count"].MinimumWidth = (int)Math.Round(CountColumnWidth * scale);
            list.Columns["Size"].MinimumWidth = (int)Math.Round(SizeColumnWidth * scale);
            var padding = new Padding(0, 0, (int)Math.Round(6 * scale), 0);
            list.Columns["Count"].DefaultCellStyle.Padding = padding;
            list.Columns["Size"].DefaultCellStyle.Padding = padding;
        }

        private void ConfigureFileMenu()
        {
            var open = fileMenu.Items.Add("打开文件夹", null, delegate { PathAction(ExplorerLocation.OpenFolder); });
            fileMenu.Items.Add("在资源管理器中定位", null, delegate { PathAction(ExplorerLocation.Reveal); });
            fileMenu.Items.Add("复制路径", null, delegate { PathAction(Clipboard.SetText); });
            fileMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                PathItem item = files.SelectedNode == null ? null : files.SelectedNode.Tag as PathItem;
                e.Cancel = item == null;
                open.Visible = item != null && item.IsDirectory;
            };
            files.ContextMenuStrip = fileMenu;
            files.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) files.SelectedNode = files.GetNodeAt(e.Location);
            };
            Disposed += delegate { fileMenu.Dispose(); };
        }

        private void PathAction(Action<string> action)
        {
            PathItem item = files.SelectedNode == null ? null : files.SelectedNode.Tag as PathItem;
            if (item == null) return;
            try { action(item.Path); }
            catch (Exception ex) { status.Text = ex.Message; }
        }

        private void OpenDiskCleanup()
        {
            try
            {
                string systemPath = Environment.SystemDirectory;
                var info = new ProcessStartInfo(Path.Combine(systemPath, "cleanmgr.exe"),
                    "/d " + Path.GetPathRoot(systemPath)[0]) { UseShellExecute = true };
                Process.Start(info);
            }
            catch (Exception ex) { status.Text = "无法打开 Windows 磁盘清理 · " + ex.Message; }
        }

        private IEnumerable<CleanupGroup> Selected()
        {
            return list.Rows.Cast<DataGridViewRow>().Where(row => object.Equals(row.Cells[0].Value, true))
                .Select(row => (CleanupGroup)row.Tag).Where(item => item != null && item.Count > 0);
        }

        private void Busy(bool value)
        {
            busy = value;
            scan.Enabled = list.Enabled = system.Enabled = duplicates.Enabled = recycle.Enabled = !busy;
            apply.Enabled = !busy && Selected().Any();
        }

        private async Task Scan()
        {
            Busy(true);
            status.Text = "正在扫描清理项目…";
            try
            {
                var result = new CleanupSnapshot { Groups = await ReadGroups(), ScannedUtc = DateTime.UtcNow };
                bool saved = await Task.Run(() => cache.Save(result));
                if (IsDisposed) return;
                DisplaySnapshot(result, saved);
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = "扫描失败 · " + ex.Message; }
            finally { if (!IsDisposed) Busy(false); }
        }

        private void DisplaySnapshot(CleanupSnapshot result, bool saved)
        {
            snapshot = result;
            ShowResults(result.Groups);
            status.Text = "上次扫描 " + Dashboard.Date(result.ScannedUtc.ToLocalTime()) + " · " +
                result.Groups.Sum(item => item.Count).ToString("N0") + " 项 · " +
                Dashboard.Bytes(result.Groups.Sum(item => item.Bytes)) + (saved ? "" : " · 未保存");
        }

        private async Task Apply()
        {
            var chosen = Selected().ToList();
            bool emptyBin = chosen.Any(group => group.IsRecycleBin);
            int fileCount = chosen.Sum(group => group.Files.Count);
            string prompt = fileCount > 0 ? "永久删除勾选类别中预览的 " + fileCount.ToString("N0") + " 个文件？" : "";
            if (emptyBin) prompt += (fileCount > 0 ? "\n\n" : "") + "清空整个 Windows 回收站将另外弹出系统确认。";
            if (chosen.Count == 0 || !MaintenanceView.Confirm(this,
                prompt, "确认清理")) return;
            Mutating = true;
            Busy(true);
            status.Text = "正在清理勾选项…";
            try
            {
                string binStatus = "";
                if (emptyBin)
                {
                    try
                    {
                        if (RecycleBin.Empty(Handle))
                        {
                            chosen.First(group => group.IsRecycleBin).Recycle = new RecycleSnapshot();
                            binStatus = " · 已清空回收站";
                        }
                        else binStatus = " · 已取消清空回收站";
                    }
                    catch (Exception ex) { binStatus = " · 回收站清理失败 " + ex.Message; }
                }
                var result = await Task.Run(() => cleaner.Apply(chosen));
                bool saved = await Task.Run(() => cache.Save(snapshot));
                if (IsDisposed) return;
                ShowResults(snapshot.Groups);
                status.Text = (fileCount > 0 ? "已删除 " + result.Deleted.ToString("N0") + " 个文件 · " + Dashboard.Bytes(result.Bytes) +
                    "（逻辑大小） · 跳过 " + result.Skipped.ToString("N0") + " 个" + binStatus :
                    binStatus.TrimStart(' ', '·')) + (saved ? "" : " · 结果未保存");
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = "清理中断 · " + ex.Message; }
            finally { Mutating = false; if (!IsDisposed) Busy(false); }
        }

        private async Task<List<CleanupGroup>> ReadGroups()
        {
            var binTask = RecycleBin.ScanAsync();
            var groups = await Task.Run(() => cleaner.Scan());
            var bin = groups.First(group => group.IsRecycleBin);
            try { bin.Recycle = await binTask; }
            catch (Exception ex) { bin.Description += "\n读取回收站失败 · " + ex.Message; }
            return groups;
        }

        private void ShowResults(IEnumerable<CleanupGroup> groups)
        {
            var active = list.CurrentRow == null ? null : list.CurrentRow.Tag as CleanupGroup;
            DataGridViewRow previewRow = null, firstResult = null;
            list.SuspendLayout();
            list.Rows.Clear();
            try
            {
                foreach (var category in groups.GroupBy(group => group.Category))
                {
                    int header = list.Rows.Add(null, category.Key, "", "");
                    DataGridViewRow heading = list.Rows[header];
                    heading.Cells[0] = new DataGridViewTextBoxCell { Value = "" };
                    heading.ReadOnly = true;
                    heading.DefaultCellStyle.BackColor = MaintenanceView.Soft;
                    heading.DefaultCellStyle.SelectionBackColor = MaintenanceView.Soft;
                    heading.DefaultCellStyle.ForeColor = MaintenanceView.Green;
                    heading.DefaultCellStyle.SelectionForeColor = MaintenanceView.Green;
                    foreach (CleanupGroup group in category)
                    {
                        bool scanned = snapshot != null && (!group.IsRecycleBin || group.Recycle != null);
                        int index = list.Rows.Add(false, group.Name, scanned ? group.Count.ToString("N0") : "—",
                            scanned ? Dashboard.Bytes(group.Bytes) : "—");
                        DataGridViewRow row = list.Rows[index];
                        row.Tag = group;
                        row.Cells[0].ReadOnly = !scanned || group.Count == 0;
                        row.Cells[1].ToolTipText = group.Description;
                        if (firstResult == null && group.Count > 0) firstResult = row;
                        if (active != null && active.Name == group.Name) previewRow = row;
                    }
                }
            }
            finally { list.ResumeLayout(); }
            list.ClearSelection();
            split.Panel2Collapsed = true;
            files.Nodes.Clear();
            var selected = previewRow ?? firstResult;
            if (selected != null)
            {
                list.CurrentCell = selected.Cells[1];
                ShowGroup((CleanupGroup)selected.Tag);
            }
        }

        private void ShowGroup(CleanupGroup group)
        {
            if (group == null) return;
            if (split.Panel2Collapsed)
            {
                split.Panel2Collapsed = false;
                split.SplitterDistance = split.Width * 48 / 100;
            }
            recycle.Visible = group.IsRecycleBin;
            string counts = group.Count.ToString("N0") + (group.IsRecycleBin ? " 项" : " 个文件") + " · " + Dashboard.Bytes(group.Bytes);
            details.Text = group.Description + "\n清理后：" + group.Effect;
            if (group.IsRecycleBin)
            {
                if (group.Recycle != null) details.Text += "\n" + counts;
            }
            else
            {
                if (group.Days > 0) details.Text += "\n按最后修改时间筛选 " + group.Days + " 天以前的文件。";
                if (group.Inspection != null)
                {
                    details.Text += "\n" + counts;
                    var skipped = new List<string>();
                    if (group.Inspection.Opened == 0) skipped.Add("未找到可读目录");
                    if (group.Inspection.Recent > 0) skipped.Add("过新 " + group.Inspection.Recent.ToString("N0"));
                    if (group.Inspection.OtherTypes > 0) skipped.Add("类型不匹配 " + group.Inspection.OtherTypes.ToString("N0"));
                    if (group.Inspection.Excluded > 0) skipped.Add("链接或受保护 " + group.Inspection.Excluded.ToString("N0"));
                    if (group.Inspection.Unreadable > 0) skipped.Add("无法读取 " + group.Inspection.Unreadable.ToString("N0"));
                    if (skipped.Count > 0) details.Text += " · " + string.Join(" · ", skipped);
                }
            }
            files.BeginUpdate();
            try
            {
                files.Nodes.Clear();
                if (group.IsRecycleBin)
                {
                    if (group.Recycle != null) ShowRecycle(group.Recycle);
                    return;
                }
                foreach (var root in group.Files.GroupBy(item => item.Root))
                {
                    var directory = new PreviewDirectory();
                    foreach (CleanupFile file in root)
                        directory.Add(file.Path.Substring(root.Key.Length + 1).Split('\\'), file);
                    string caption = Path.GetFileName(root.Key);
                    if (group.ProfileCache != null)
                    {
                        string profile = root.Key.Substring(0, root.Key.Length - group.ProfileCache.Length).TrimEnd('\\');
                        caption = Path.GetFileName(profile) + " / " + caption;
                    }
                    files.Nodes.Add(Node(caption, root.Key, directory));
                }
                ExpandPreview();
                if (group.Inspection != null && group.Inspection.Paths.Count != 0)
                {
                    var checkedPaths = new TreeNode("扫描位置");
                    foreach (string path in group.Inspection.Paths)
                        checkedPaths.Nodes.Add(new TreeNode(path)
                        {
                            ToolTipText = path, Tag = new PathItem { Path = path, IsDirectory = true }
                        });
                    files.Nodes.Add(checkedPaths);
                    if (group.Files.Count == 0) checkedPaths.Expand();
                }
            }
            finally { files.EndUpdate(); }
        }

        private void ShowRecycle(RecycleSnapshot snapshot)
        {
            foreach (var group in snapshot.Entries.GroupBy(item => string.IsNullOrEmpty(item.Parent) ? "原位置未知" : item.Parent)
                .OrderBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var node = new TreeNode(group.Key) { ToolTipText = group.Key };
                foreach (RecycleEntry entry in group.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                    node.Nodes.Add(new TreeNode(entry.Name + (entry.IsDirectory ? "  ·  文件夹" :
                        "  ·  " + Dashboard.Bytes(entry.Bytes))));
                files.Nodes.Add(node);
                node.Expand();
            }
            if (snapshot.Entries.Count < snapshot.Count)
                files.Nodes.Add("更多项目可在 Windows 回收站中查看");
        }

        private void ExpandPreview()
        {
            const int initialNodes = 700;
            var queue = new Queue<TreeNode>(files.Nodes.Cast<TreeNode>());
            int visible = queue.Count;
            while (queue.Count > 0 && visible < initialNodes)
            {
                var node = queue.Dequeue();
                Populate(node);
                node.Expand();
                visible += node.Nodes.Count;
                foreach (TreeNode child in node.Nodes)
                    if (child.Nodes.Count > 0) queue.Enqueue(child);
            }
            if (files.Nodes.Count > 0) files.TopNode = files.Nodes[0];
        }

        private sealed class PreviewDirectory
        {
            public readonly Dictionary<string, PreviewDirectory> Directories = new Dictionary<string, PreviewDirectory>(StringComparer.Ordinal);
            public readonly List<CleanupFile> Items = new List<CleanupFile>();

            public void Add(string[] parts, CleanupFile file)
            {
                PreviewDirectory directory = this;
                for (int index = 0; index < parts.Length - 1; index++)
                {
                    PreviewDirectory next;
                    if (!directory.Directories.TryGetValue(parts[index], out next))
                        directory.Directories.Add(parts[index], next = new PreviewDirectory());
                    directory = next;
                }
                directory.Items.Add(file);
            }
        }

        private sealed class PathItem
        {
            public string Path;
            public bool IsDirectory;
            public PreviewDirectory Children;
        }

        private sealed class PreviewPage
        {
            public CleanupFile[] Items;
            public int Offset;
        }

        private static TreeNode Node(string name, string path, PreviewDirectory directory)
        {
            var node = new TreeNode(name)
            {
                ToolTipText = path, Tag = new PathItem { Path = path, IsDirectory = true, Children = directory }
            };
            node.Nodes.Add("...");
            return node;
        }

        private static void Populate(TreeNode node)
        {
            var item = node.Tag as PathItem;
            var directory = item == null ? null : item.Children;
            if (directory == null) return;
            item.Children = null;
            node.Nodes.Clear();
            foreach (var branch in directory.Directories.OrderBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase))
                node.Nodes.Add(Node(branch.Key, Path.Combine(item.Path, branch.Key), branch.Value));
            AddPage(node, new PreviewPage { Items = directory.Items.OrderBy(entry => entry.Path, StringComparer.CurrentCultureIgnoreCase).ToArray() });
        }

        private static bool LoadMore(TreeNode node)
        {
            var page = node == null ? null : node.Tag as PreviewPage;
            if (page == null) return false;
            TreeNode parent = node.Parent;
            node.Remove();
            AddPage(parent, page);
            if (parent.Nodes.Count > 0) parent.TreeView.SelectedNode = parent.Nodes[parent.Nodes.Count - 1];
            return true;
        }

        private static void AddPage(TreeNode node, PreviewPage page)
        {
            int end = Math.Min(page.Items.Length, page.Offset + 200);
            for (; page.Offset < end; page.Offset++)
            {
                CleanupFile file = page.Items[page.Offset];
                node.Nodes.Add(new TreeNode(Path.GetFileName(file.Path) + "  ·  " + Dashboard.Bytes(file.Info.Size))
                {
                    ToolTipText = file.Path, Tag = new PathItem { Path = file.Path }
                });
            }
            if (page.Offset < page.Items.Length)
                node.Nodes.Add(new TreeNode("双击显示更多文件…") { Tag = page });
        }
    }

    internal sealed class MigrationView : UserControl
    {
        private readonly FolderMigration service;
        private readonly string profile;
        private readonly TextBox source = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox destination = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox records = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Button move = MaintenanceView.Action("迁移并建立联接", true);
        private readonly Button restore = MaintenanceView.Action("恢复原目录");
        private readonly Button release = MaintenanceView.Action("释放 C 盘备份");
        private readonly Label status = MaintenanceView.Note("");
        private readonly Label paths = MaintenanceView.Note("");
        private readonly ToolTip tips = new ToolTip();
        private bool busy;
        public bool Mutating { get { return busy; } }

        public MigrationView(string profileDirectory, string dataDirectory)
        {
            profile = profileDirectory;
            service = new FolderMigration(profileDirectory, dataDirectory);
            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            Controls.Add(body);
            var layout = MaintenanceView.Layout(32, 32, 32, 35, 33, 35, 58, 38);
            layout.Dock = DockStyle.Top;
            layout.Height = 300;
            body.Controls.Add(layout);
            var heading = MaintenanceView.Note("迁移应用目录到其他 NTFS 盘");
            heading.Dock = DockStyle.None;
            heading.AutoSize = true;
            heading.Margin = new Padding(5, 6, 5, 0);
            var help = MaintenanceView.Action("?");
            help.AutoSize = false;
            help.MinimumSize = Size.Empty;
            help.Size = new Size(23, 23);
            help.Margin = new Padding(0, 1, 0, 0);
            help.AccessibleName = "目录迁移说明";
            tips.SetToolTip(help, "目录迁移说明");
            help.Click += delegate { ShowHelp(); };
            layout.Controls.Add(MaintenanceView.Actions(heading, help), 0, 0);
            layout.Controls.Add(PathInput("C 盘源目录", source, profileDirectory), 0, 1);
            layout.Controls.Add(PathInput("目标父目录", destination, ""), 0, 2);
            layout.Controls.Add(MaintenanceView.Actions(move), 0, 3);
            layout.Controls.Add(WithLabel("迁移记录", records), 0, 4);
            layout.Controls.Add(MaintenanceView.Actions(restore, release), 0, 5);
            paths.AutoEllipsis = true;
            layout.Controls.Add(paths, 0, 6);
            status.AutoEllipsis = true;
            layout.Controls.Add(status, 0, 7);
            move.Click += async delegate { await Migrate(); };
            restore.Click += async delegate { await Restore(); };
            release.Click += async delegate { await Release(); };
            records.SelectedIndexChanged += delegate { ShowRecord(); };
            Reload(null);
            Disposed += delegate { tips.Dispose(); };
        }

        private void ShowHelp()
        {
            using (var dialog = new Form
            {
                Text = "目录迁移说明", Font = Font, BackColor = Color.White, ClientSize = new Size(608, 520),
                MinimumSize = new Size(430, 360), StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, ShowInTaskbar = false
            })
            using (var bold = new Font(Font, FontStyle.Bold))
            {
                var layout = MaintenanceView.Layout(0, 36);
                layout.Padding = new Padding(14, 10, 14, 8);
                var text = new RichTextBox
                {
                    Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
                    BackColor = Color.White, BorderStyle = BorderStyle.None, AccessibleName = "目录迁移操作、例子与风险"
                };
                AddHelp(text, bold, "目录迁移是什么",
                    "把 C 盘中某个软件的配置或缓存目录复制并校验到另一个盘，再在原路径建立 NTFS 目录联接。软件仍可以使用原来的 C 盘路径，实际读写会到目标盘。");
                AddHelp(text, bold, "举例：把 ExampleApp 的缓存迁到 D 盘",
                    "C 盘源目录填\nC:\\Users\\小林\\AppData\\Local\\ExampleApp\\Cache\n\n" +
                    "目标父目录选择已存在的 D:\\迁移目录\\ExampleApp\n" +
                    "实际目标会是 D:\\迁移目录\\ExampleApp\\Cache。原 C 盘路径会指向这个目标。");
                AddHelp(text, bold, "迁移前",
                    "完全退出相关软件，包括它的托盘和后台进程。先挑一个明确属于该软件的缓存或配置子目录，避免整搬用户目录、AppData 或安装目录。" +
                    "目标盘须为长期连接的固定 NTFS 盘，留足空间。目标父目录下须留出源目录的同名位置。");
                AddHelp(text, bold, "什么时候释放 C 盘空间",
                    "迁移完成后，原文件会作为备份留在 C 盘。先重新打开软件，确认功能、配置和读写正常。再次关掉软件后，可以在迁移记录里选择「释放 C 盘备份」。" +
                    "程序会核对备份文件及其数据流，目标文件缺失或被改写时会保留备份。释放会永久删除原盘备份。");
                AddHelp(text, bold, "如何恢复及日常注意事项",
                    "备份还在时，关闭相关软件并选择「恢复原目录」，即可把迁移当时的备份放回 C 盘。目标盘的文件仍会保留，迁移后在目标盘新增或修改的数据不会自动合并回 C 盘。" +
                    "释放备份后无法用该按钮恢复。联接使用固定的盘符和路径，目标盘断开、改盘符或目录改名时，软件可能无法读取配置或缓存。");
                text.Select(0, 0);
                var close = MaintenanceView.Action("关闭", true);
                close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                close.DialogResult = DialogResult.OK;
                dialog.CancelButton = close;
                layout.Controls.Add(text, 0, 0);
                layout.Controls.Add(close, 0, 1);
                dialog.Controls.Add(layout);
                dialog.AutoScaleDimensions = new SizeF(96, 96);
                dialog.AutoScaleMode = AutoScaleMode.Dpi;
                dialog.ShowDialog(this);
            }
        }

        private static void AddHelp(RichTextBox text, Font bold, string title, string body)
        {
            text.SelectionFont = bold;
            text.AppendText(title + "\n");
            text.SelectionFont = text.Font;
            text.AppendText(body + "\n\n");
        }

        private Control PathInput(string label, TextBox text, string initial)
        {
            var browse = MaintenanceView.Action("浏览…");
            browse.Dock = DockStyle.Fill;
            browse.Margin = Padding.Empty;
            text.AccessibleName = label;
            text.Dock = DockStyle.None;
            text.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            var row = (TableLayoutPanel)WithLabel(label, text);
            row.ColumnCount = 3;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            row.Controls.Add(browse, 2, 0);
            browse.Click += delegate
            {
                using (var picker = new FolderBrowserDialog
                {
                    Description = label, SelectedPath = Directory.Exists(text.Text) ? text.Text : initial,
                    ShowNewFolderButton = text == destination
                })
                    if (picker.ShowDialog(this) == DialogResult.OK) text.Text = picker.SelectedPath;
            };
            return row;
        }

        private static Control WithLabel(string label, Control field)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.Controls.Add(MaintenanceView.Note(label), 0, 0);
            row.Controls.Add(field, 1, 0);
            return row;
        }

        private void Reload(string selected)
        {
            try
            {
                var saved = service.Load();
                records.Items.Clear();
                records.Items.AddRange(saved.ToArray());
                if (saved.Count > 0) records.SelectedIndex = Math.Max(0, saved.FindIndex(item => item.Id == selected));
                else ShowRecord();
            }
            catch (Exception ex) { status.Text = "读取迁移记录失败 · " + ex.Message; }
        }

        private void ShowRecord()
        {
            var record = records.SelectedItem as MigrationRecord;
            bool backup = record != null && Directory.Exists(record.Backup);
            restore.Enabled = release.Enabled = !busy && backup;
            if (record == null)
                paths.Text = "暂无迁移记录";
            else
                paths.Text = "源：" + record.Source + "\n目标：" + record.Target + "\n原盘备份：" + (backup ? record.Backup : "无");
            tips.SetToolTip(paths, paths.Text + (record != null && Directory.Exists(record.Staging) ? "\n暂存：" + record.Staging : ""));
        }

        private async Task Migrate()
        {
            try
            {
                var record = service.Preview(source.Text.Trim(), destination.Text.Trim());
                if (!MaintenanceView.Confirm(this, "请确认相关软件已关闭。\n\n原路径：" + record.Source +
                    "\n目标路径：" + record.Target + "\n\n程序会复制并校验文件，保留原盘备份，然后在原路径建立目录联接。",
                    "确认迁移目录")) return;
                await Run(record, "正在复制、校验和建立联接…", () =>
                {
                    service.Move(record);
                    return "迁移完成。请打开原软件验证，再决定是否释放 C 盘备份。";
                });
            }
            catch (Exception ex) { status.Text = ex.Message; tips.SetToolTip(status, ex.Message); }
        }

        private async Task Restore()
        {
            var record = records.SelectedItem as MigrationRecord;
            if (record == null || !MaintenanceView.Confirm(this, "关闭相关软件后，把原始备份恢复到 C 盘？\n目标盘中的现有数据会保留。",
                "确认恢复原目录")) return;
            await Run(record, "正在恢复原目录…", () =>
            {
                service.Restore(record);
                return "原目录已恢复，目标盘副本已保留。";
            });
        }

        private async Task Release()
        {
            var record = records.SelectedItem as MigrationRecord;
            if (record == null || !MaintenanceView.Confirm(this,
                "请确认软件在新位置运行正常，且现在已关闭。\n\n将校验目标中的每个备份文件，校验通过后永久删除 C 盘备份。\n\n" +
                record.Backup, "确认释放原盘备份")) return;
            await Run(record, "正在核对目标文件，再释放已验证的备份…", () =>
            {
                var result = service.ReleaseBackup(record);
                return "已删除 " + result.Deleted.ToString("N0") + " 个备份文件 · " + Dashboard.Bytes(result.Bytes) +
                    "（逻辑大小） · 跳过 " + result.Skipped.ToString("N0") + " 个";
            });
        }

        private async Task Run(MigrationRecord record, string message, Func<string> action)
        {
            busy = true;
            move.Enabled = records.Enabled = source.Enabled = destination.Enabled = restore.Enabled = release.Enabled = false;
            status.Text = message;
            try
            {
                string result = await Task.Run(action);
                if (!IsDisposed) status.Text = result;
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = "操作中断 · " + ex.Message; }
            finally
            {
                if (!IsDisposed)
                {
                    busy = false;
                    move.Enabled = records.Enabled = source.Enabled = destination.Enabled = true;
                    tips.SetToolTip(status, status.Text);
                    Reload(record.Id);
                }
            }
        }
    }
}
