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
        private readonly Cleanup cleaner;
        private readonly Button scan = MaintenanceView.Action("扫描项目", true);
        private readonly Button apply = MaintenanceView.Action("清理勾选项");
        private readonly Button system = MaintenanceView.Action("Windows 磁盘清理");
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
        private bool hasScan;
        public bool Mutating { get; private set; }

        public CleanupView(string localAppData)
        {
            cleaner = new Cleanup(localAppData);
            var main = MaintenanceView.Layout(37, 0, 24);
            Controls.Add(main);
            main.Controls.Add(MaintenanceView.Actions(scan, apply, system), 0, 0);
            main.Controls.Add(split, 0, 1);
            main.Controls.Add(status, 0, 2);
            split.Panel1.Controls.Add(list);
            var preview = MaintenanceView.Layout(72, 0);
            preview.Controls.Add(details, 0, 0);
            preview.Controls.Add(files, 0, 1);
            split.Panel2.Controls.Add(preview);

            list.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "选", Width = 42 });
            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "可清理项目", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true
            });
            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "文件", Width = 62, ReadOnly = true,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "大小", Width = 90, ReadOnly = true,
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
            files.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs args) { Populate(args.Node); };
            ConfigureFileMenu();
            ShowResults(cleaner.Options());
            apply.Enabled = false;
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
                .Select(row => (CleanupGroup)row.Tag).Where(item => item != null && item.Files.Count > 0);
        }

        private void Busy(bool value)
        {
            busy = value;
            scan.Enabled = list.Enabled = system.Enabled = !busy;
            apply.Enabled = !busy && Selected().Any();
        }

        private async Task Scan()
        {
            Busy(true);
            status.Text = "正在扫描清理项目…";
            try
            {
                var groups = await Task.Run(() => cleaner.Scan());
                if (IsDisposed) return;
                hasScan = true;
                ShowResults(groups);
                status.Text = "共找到 " + groups.Sum(item => item.Files.Count).ToString("N0") + " 个文件 · " +
                    Dashboard.Bytes(groups.Sum(item => item.Bytes));
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = "扫描失败 · " + ex.Message; }
            finally { if (!IsDisposed) Busy(false); }
        }

        private async Task Apply()
        {
            var chosen = Selected().ToList();
            if (chosen.Count == 0 || !MaintenanceView.Confirm(this,
                "删除勾选类别中预览的 " + chosen.Sum(item => item.Files.Count).ToString("N0") +
                " 个文件？文件将直接删除以释放空间。", "确认清理")) return;
            Mutating = true;
            Busy(true);
            status.Text = "正在清理勾选项…";
            try
            {
                var result = await Task.Run(() => Cleanup.Apply(chosen));
                var groups = await Task.Run(() => cleaner.Scan());
                if (IsDisposed) return;
                ShowResults(groups);
                status.Text = "已删除 " + result.Deleted.ToString("N0") + " 个文件 · " + Dashboard.Bytes(result.Bytes) +
                    "（逻辑大小） · 跳过 " + result.Skipped.ToString("N0") + " 个";
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = "清理中断 · " + ex.Message; }
            finally { Mutating = false; if (!IsDisposed) Busy(false); }
        }

        private void ShowResults(IEnumerable<CleanupGroup> groups)
        {
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
                        int index = list.Rows.Add(false, group.Name, hasScan ? group.Files.Count.ToString("N0") : "—",
                            hasScan ? Dashboard.Bytes(group.Bytes) : "—");
                        DataGridViewRow row = list.Rows[index];
                        row.Tag = group;
                        row.Cells[0].ReadOnly = !hasScan || group.Files.Count == 0;
                        row.Cells[1].ToolTipText = group.Description;
                    }
                }
            }
            finally { list.ResumeLayout(); }
            list.ClearSelection();
            split.Panel2Collapsed = true;
            files.Nodes.Clear();
        }

        private void ShowGroup(CleanupGroup group)
        {
            if (group == null) return;
            if (split.Panel2Collapsed)
            {
                split.Panel2Collapsed = false;
                split.SplitterDistance = split.Width * 48 / 100;
            }
            details.Text = group.Description + "\n" + group.Days + " 天以上" + (hasScan ?
                " · " + group.Files.Count.ToString("N0") + " 个文件 · " + Dashboard.Bytes(group.Bytes) : "");
            files.BeginUpdate();
            try
            {
                files.Nodes.Clear();
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
            }
            finally { files.EndUpdate(); }
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
            foreach (CleanupFile file in directory.Items.OrderBy(entry => entry.Path, StringComparer.CurrentCultureIgnoreCase))
                node.Nodes.Add(new TreeNode(Path.GetFileName(file.Path) + "  ·  " + Dashboard.Bytes(file.Info.Size))
                {
                    ToolTipText = file.Path, Tag = new PathItem { Path = file.Path }
                });
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
            layout.Controls.Add(MaintenanceView.Note("迁移应用目录到其他 NTFS 盘"), 0, 0);
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
