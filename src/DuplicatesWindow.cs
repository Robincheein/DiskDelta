using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskDelta
{
    internal sealed class DuplicatesWindow : Form
    {
        private const int PageSize = 40;
        private readonly Button pick = MaintenanceView.Action("选择文件夹");
        private readonly Button scan = MaintenanceView.Action("开始扫描", true);
        private readonly Button cancel = MaintenanceView.Action("停止扫描");
        private readonly Button delete = MaintenanceView.Action("删除勾选文件");
        private readonly Button previous = MaintenanceView.Action("上一页");
        private readonly Button next = MaintenanceView.Action("下一页");
        private readonly CheckBox recursive = new CheckBox { Text = "包含子文件夹", AutoSize = true, Checked = true, Margin = new Padding(4, 6, 0, 0) };
        private readonly TreeView tree = MaintenanceView.Tree();
        private readonly Label folder = MaintenanceView.Note("");
        private readonly Label status = MaintenanceView.Note("选择文件夹后开始扫描");
        private readonly Label pages = MaintenanceView.Note("");
        private readonly Dictionary<DuplicateSet, HashSet<CleanupFile>> selections = new Dictionary<DuplicateSet, HashSet<CleanupFile>>();
        private List<DuplicateSet> results = new List<DuplicateSet>();
        private CancellationTokenSource stop;
        private bool rendering;
        private bool deleting;
        private int page;

        public DuplicatesWindow(Font font)
        {
            Text = "同文件夹重复文件";
            Font = font;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(790, 560);
            MinimumSize = new Size(580, 370);
            ShowInTaskbar = false;
            MinimizeBox = false;
            tree.CheckBoxes = true;
            folder.AutoEllipsis = true;
            folder.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            pages.Dock = DockStyle.None;
            pages.AutoSize = true;
            pages.Margin = new Padding(5, 6, 5, 0);
            var layout = MaintenanceView.Layout(37, 23, 0, 36, 27);
            layout.Padding = new Padding(10, 8, 10, 6);
            layout.Controls.Add(MaintenanceView.Actions(pick, scan, cancel, recursive), 0, 0);
            layout.Controls.Add(folder, 0, 1);
            layout.Controls.Add(tree, 0, 2);
            layout.Controls.Add(MaintenanceView.Actions(delete, previous, next, pages), 0, 3);
            layout.Controls.Add(status, 0, 4);
            Controls.Add(layout);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;

            pick.Click += delegate { ChooseFolder(); };
            scan.Click += async delegate { await Scan(); };
            recursive.CheckedChanged += delegate
            {
                Reset();
                status.Text = "点击开始扫描";
            };
            cancel.Click += delegate { if (stop != null) stop.Cancel(); };
            delete.Click += async delegate { await Apply(); };
            previous.Click += delegate { page--; Render(); };
            next.Click += delegate { page++; Render(); };
            tree.BeforeCheck += BeforeCheck;
            tree.AfterCheck += AfterCheck;
            tree.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs e) { Fill(e.Node); };
            tree.NodeMouseDoubleClick += delegate(object sender, TreeNodeMouseClickEventArgs e)
            {
                if (e.Node.Tag is CleanupFile) Reveal(e.Node);
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("在资源管理器中定位", null, delegate { Reveal(tree.SelectedNode); });
            tree.ContextMenuStrip = menu;
            tree.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) tree.SelectedNode = tree.GetNodeAt(e.Location);
            };
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = tree.SelectedNode == null; };
            Disposed += delegate { menu.Dispose(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (deleting) e.Cancel = true;
                else if (stop != null) stop.Cancel();
            };
            RefreshButtons();
        }

        private void ChooseFolder()
        {
            using (var picker = new FolderBrowserDialog { SelectedPath = folder.Text, ShowNewFolderButton = false })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                if (string.Equals(folder.Text, picker.SelectedPath, StringComparison.OrdinalIgnoreCase)) return;
                folder.Text = picker.SelectedPath;
                Reset();
                status.Text = "点击开始扫描";
            }
        }

        private void Reset()
        {
            selections.Clear();
            results.Clear();
            page = 0;
            Render();
        }

        private async Task Scan(string completed = "")
        {
            Reset();
            if (string.IsNullOrEmpty(folder.Text))
            {
                status.Text = "请选择要扫描的文件夹";
                return;
            }
            var token = new CancellationTokenSource();
            stop = token;
            RefreshButtons();
            status.Text = completed + "正在比对同文件夹中的文件…";
            try
            {
                string path = PrivateFiles.FullPath(folder.Text);
                bool includeSubfolders = recursive.Checked;
                var found = await Task.Run(() => Duplicates.Scan(path, includeSubfolders, token.Token));
                if (IsDisposed) return;
                results = found;
                status.Text = completed + (results.Count == 0 ? "未找到重复文件" :
                    results.Count.ToString("N0") + " 组重复文件 · 可清理 " + Dashboard.Bytes(results.Sum(set => set.ExtraBytes)));
                Render();
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = completed + "扫描已停止"; }
            catch (Exception ex) { if (!IsDisposed) status.Text = completed + "扫描失败 · " + ex.Message; }
            finally
            {
                stop = null;
                token.Dispose();
                if (!IsDisposed) RefreshButtons();
            }
        }

        private async Task Apply()
        {
            var chosen = selections.Where(item => item.Value.Count > 0).ToDictionary(item => item.Key, item => item.Value);
            int count = chosen.Sum(item => item.Value.Count);
            if (count == 0 || !MaintenanceView.Confirm(this,
                "永久删除已勾选的 " + count.ToString("N0") + " 个文件？每组会保留至少一份核对过的文件。",
                "确认删除重复文件")) return;
            deleting = true;
            RefreshButtons();
            status.Text = "正在重新核对并删除…";
            try
            {
                var result = await Task.Run(() => Duplicates.Apply(chosen));
                await Scan("已删除 " + result.Deleted.ToString("N0") + " 个 · 跳过 " + result.Skipped.ToString("N0") + " 个。");
            }
            catch (Exception ex) { status.Text = "清理中断 · " + ex.Message; }
            finally { deleting = false; RefreshButtons(); }
        }

        private void Render()
        {
            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear();
                foreach (DuplicateSet set in results.Skip(page * PageSize).Take(PageSize))
                {
                    var group = new TreeNode(set.Folder + "  ·  " + set.Files.Length.ToString("N0") + " 份 × " +
                        Dashboard.Bytes(set.Files[0].Info.Size)) { Tag = set, ToolTipText = set.Folder };
                    group.Nodes.Add("…");
                    tree.Nodes.Add(group);
                    if (set.Files.Length <= 20) group.Expand();
                }
                if (tree.Nodes.Count > 0) tree.TopNode = tree.Nodes[0];
            }
            finally { tree.EndUpdate(); }
            RefreshButtons();
        }

        private void Fill(TreeNode node)
        {
            var set = node.Tag as DuplicateSet;
            if (set == null || node.Nodes.Count != 1 || node.Nodes[0].Tag != null) return;
            rendering = true;
            try
            {
                node.Nodes.Clear();
                HashSet<CleanupFile> selected;
                selections.TryGetValue(set, out selected);
                foreach (CleanupFile file in set.Files.OrderBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase))
                    node.Nodes.Add(new TreeNode(Path.GetFileName(file.Path))
                        { Tag = file, ToolTipText = file.Path, Checked = selected != null && selected.Contains(file) });
            }
            finally { rendering = false; }
        }

        private void BeforeCheck(object sender, TreeViewCancelEventArgs e)
        {
            if (rendering) return;
            var file = e.Node.Tag as CleanupFile;
            if (file == null) { e.Cancel = true; return; }
            var set = (DuplicateSet)e.Node.Parent.Tag;
            HashSet<CleanupFile> selected;
            if (!e.Node.Checked && selections.TryGetValue(set, out selected) && selected.Count == set.Files.Length - 1)
            {
                e.Cancel = true;
                status.Text = "每组须保留一份文件";
            }
        }

        private void AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (rendering) return;
            var set = (DuplicateSet)e.Node.Parent.Tag;
            var file = (CleanupFile)e.Node.Tag;
            HashSet<CleanupFile> selected;
            if (!selections.TryGetValue(set, out selected))
                selections.Add(set, selected = new HashSet<CleanupFile>());
            if (e.Node.Checked) selected.Add(file);
            else selected.Remove(file);
            status.Text = "已勾选 " + selections.Sum(item => item.Value.Count).ToString("N0") + " 个文件";
            RefreshButtons();
        }

        private void RefreshButtons()
        {
            bool idle = stop == null && !deleting;
            pick.Enabled = scan.Enabled = recursive.Enabled = tree.Enabled = idle;
            cancel.Visible = stop != null;
            delete.Enabled = idle && selections.Any(item => item.Value.Count > 0);
            previous.Enabled = idle && page > 0;
            next.Enabled = idle && (page + 1) * PageSize < results.Count;
            pages.Text = results.Count == 0 ? "" : (page + 1) + " / " + ((results.Count + PageSize - 1) / PageSize);
        }

        private void Reveal(TreeNode node)
        {
            if (node == null) return;
            try
            {
                var file = node.Tag as CleanupFile;
                if (file != null) ExplorerLocation.Reveal(file.Path);
                else ExplorerLocation.OpenFolder(((DuplicateSet)node.Tag).Folder);
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }
    }
}
