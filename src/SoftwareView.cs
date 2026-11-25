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
    internal sealed class SoftwareView : UserControl
    {
        private readonly ListBox list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox filter = new TextBox { Dock = DockStyle.Fill, AccessibleName = "筛选软件" };
        private readonly TreeView paths = MaintenanceView.Tree();
        private readonly Button registry = MaintenanceView.Action("读取注册表");
        private readonly Button scan = MaintenanceView.Action("扫描所有盘符", true);
        private readonly Label status = MaintenanceView.Note("");
        private readonly ScanCache<SoftwareCatalog.Result> cache;
        private readonly Label details = MaintenanceView.Note("从左侧选择一个软件。");
        private List<SoftwareEntry> entries = new List<SoftwareEntry>();
        private CancellationTokenSource cancellation;
        private bool started;
        private bool reading;
        public bool Busy { get { return reading || cancellation != null; } }
        private SoftwareCatalog.Result snapshot;

        public SoftwareView(string dataPath)
        {
            cache = new ScanCache<SoftwareCatalog.Result>(dataPath, "software-scan-v1.bin.gz");
            var main = MaintenanceView.Layout(34, 0, 24);
            main.Controls.Add(MaintenanceView.Actions(scan, registry), 0, 0);
            var split = new SplitContainer { Dock = DockStyle.Fill };
            main.Controls.Add(split, 0, 1);
            main.Controls.Add(status, 0, 2);
            Controls.Add(main);
            var left = MaintenanceView.Layout(25, 26, 0);
            left.Controls.Add(MaintenanceView.Note("按软件名称或发布者筛选"), 0, 0);
            left.Controls.Add(filter, 0, 1);
            left.Controls.Add(list, 0, 2);
            split.Panel1.Controls.Add(left);
            var right = MaintenanceView.Layout(36, 0);
            right.Controls.Add(details, 0, 0);
            right.Controls.Add(paths, 0, 1);
            split.Panel2.Controls.Add(right);
            split.SizeChanged += delegate
            {
                if (split.Width > 175) split.SplitterDistance = split.Width * 35 / 100;
            };
            filter.TextChanged += delegate { ApplyFilter(); };
            list.SelectedIndexChanged += delegate { Display(list.SelectedItem as SoftwareEntry); };
            registry.Click += async delegate { await RefreshRegistry(); };
            scan.Click += async delegate { await Scan(); };
            paths.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs args) { FillFolder(args.Node); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("在资源管理器中定位", null, delegate { OpenLocation(paths.SelectedNode); });
            menu.Items.Add("复制完整路径", null, delegate
            {
                if (paths.SelectedNode != null && paths.SelectedNode.Tag is string)
                    Clipboard.SetText((string)paths.SelectedNode.Tag);
            });
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                args.Cancel = paths.SelectedNode == null || !(paths.SelectedNode.Tag is string);
            };
            paths.NodeMouseClick += delegate(object sender, TreeNodeMouseClickEventArgs args)
            {
                if (args.Button == MouseButtons.Right) paths.SelectedNode = args.Node;
            };
            paths.ContextMenuStrip = menu;
            Disposed += delegate
            {
                if (cancellation != null) cancellation.Cancel();
                menu.Dispose();
            };
        }

        public async void ActivateView()
        {
            if (started) return;
            started = true;
            reading = true;
            registry.Enabled = scan.Enabled = false;
            status.Text = "正在读取上次扫描…";
            SoftwareCatalog.Result saved = null;
            try { saved = await Task.Run(() => cache.Read()); }
            catch (Exception ex)
            {
                if (!IsDisposed) status.Text = "读取上次扫描失败 · " + ex.Message;
                return;
            }
            finally
            {
                reading = false;
                if (!IsDisposed) registry.Enabled = scan.Enabled = true;
            }
            if (IsDisposed) return;
            if (saved != null && saved.Valid) DisplaySnapshot(saved, true);
            else await Scan();
        }

        private async Task RefreshRegistry()
        {
            reading = true;
            registry.Enabled = scan.Enabled = false;
            status.Text = "正在读取应用登记信息…";
            try
            {
                var result = await Task.Run(() => SoftwareCatalog.UpdateRegistry(SoftwareCatalog.RegistryEntries(), snapshot));
                bool saved = await Task.Run(() => cache.Save(result));
                if (IsDisposed) return;
                DisplaySnapshot(result, saved);
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = "注册表扫描失败 · " + ex.Message; }
            finally
            {
                reading = false;
                if (!IsDisposed) registry.Enabled = scan.Enabled = true;
            }
        }

        private async Task Scan()
        {
            if (reading) return;
            if (cancellation != null)
            {
                cancellation.Cancel();
                scan.Enabled = false;
                return;
            }
            cancellation = new CancellationTokenSource();
            var run = cancellation;
            CancellationToken token = run.Token;
            scan.Text = "取消扫描";
            registry.Enabled = false;
            status.Text = "正在读取注册表并枚举盘符…";
            var progress = new Progress<string>(drive =>
            {
                if (!IsDisposed && cancellation == run && scan.Enabled) status.Text = "正在扫描 " + drive + " …";
            });
            try
            {
                var registered = await Task.Run(() => SoftwareCatalog.RegistryEntries());
                if (IsDisposed) return;
                token.ThrowIfCancellationRequested();
                DateTime registryUtc = DateTime.UtcNow;
                var found = await Task.Run(() => SoftwareCatalog.ScanAll(registered,
                    drive => ((IProgress<string>)progress).Report(drive), token));
                if (IsDisposed) return;
                token.ThrowIfCancellationRequested();
                scan.Enabled = false;
                found.RegistryUtc = registryUtc;
                found.FullScanUtc = DateTime.UtcNow;
                bool saved = await Task.Run(() => cache.Save(found));
                if (!IsDisposed) DisplaySnapshot(found, saved);
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = "全盘扫描已取消。"; }
            catch (Exception ex) { if (!IsDisposed) status.Text = "全盘扫描中断 · " + ex.Message; }
            finally
            {
                run.Dispose();
                cancellation = null;
                if (!IsDisposed)
                {
                    scan.Text = "扫描所有盘符";
                    scan.Enabled = registry.Enabled = true;
                }
            }
        }

        private void DisplaySnapshot(SoftwareCatalog.Result result, bool saved)
        {
            snapshot = result;
            entries = result.Entries;
            ApplyFilter();
            var timestamp = result.FullScanUtc == default(DateTime) ? "上次读取登记 " + Dashboard.Date(result.RegistryUtc.ToLocalTime()) :
                "上次全盘扫描 " + Dashboard.Date(result.FullScanUtc.ToLocalTime()) + " · " + string.Join("、", result.Drives);
            status.Text = timestamp + " · " + entries.Count.ToString("N0") + " 项软件" +
                (result.Unreadable > 0 ? " · " + result.Unreadable.ToString("N0") + " 个目录无法读取" : "") +
                (saved ? "" : " · 未保存");
        }

        private void ApplyFilter()
        {
            var previous = list.SelectedItem as SoftwareEntry;
            string query = filter.Text.Trim();
            var matches = entries.Where(entry => query.Length == 0 || Contains(entry.Name, query) || Contains(entry.Publisher, query)).ToList();
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                list.Items.AddRange(matches.ToArray());
                int selected = previous == null ? -1 : matches.FindIndex(item => item.Name == previous.Name && item.Version == previous.Version);
                if (matches.Count > 0) list.SelectedIndex = Math.Max(0, selected);
            }
            finally { list.EndUpdate(); }
        }

        private static bool Contains(string text, string query)
        {
            return text != null && text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void Display(SoftwareEntry entry)
        {
            paths.BeginUpdate();
            try
            {
                paths.Nodes.Clear();
                if (entry == null) { details.Text = "从左侧选择一个软件。"; return; }
                details.Text = entry.ToString() + (string.IsNullOrWhiteSpace(entry.Publisher) ? "" : " · " + entry.Publisher);
                if (entry.Locations.Count == 0)
                {
                    paths.Nodes.Add("目前没有可定位的路径。");
                    return;
                }
                foreach (var group in entry.Locations.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .GroupBy(item => !SoftwareCatalog.IsClue(item.Value)).OrderByDescending(item => item.Key))
                {
                    TreeNode category = paths.Nodes.Add(group.Key ? "登记位置" : "同名匹配");
                    foreach (var location in group)
                        category.Nodes.Add(CreateLocation(location.Key, location.Key, location.Value));
                    category.Expand();
                }
            }
            finally { paths.EndUpdate(); }
        }

        private static TreeNode CreateLocation(string path, string caption, string reason = null)
        {
            // Defer filesystem access until expansion. Entries on offline drives can still be copied.
            var node = new TreeNode(caption) { Tag = path, ToolTipText = path + (reason == null ? "" : "\n" + reason) };
            if (reason != null || Directory.Exists(path)) node.Nodes.Add("...");
            return node;
        }

        private static void FillFolder(TreeNode node)
        {
            string directory = node.Tag as string;
            if (directory == null || node.Nodes.Count != 1 || node.Nodes[0].Text != "...") return;
            node.Nodes.Clear();
            try
            {
                if (File.Exists(directory))
                {
                    node.Nodes.Add("文件 · 右键可在资源管理器中定位");
                    return;
                }
                PrivateFiles.VerifyDirectory(directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                    node.Nodes.Add(CreateLocation(path, Path.GetFileName(path)));
            }
            catch (IOException) { node.Nodes.Add("目录不可用"); }
            catch (UnauthorizedAccessException) { node.Nodes.Add("目录访问受限。"); }
        }

        private void OpenLocation(TreeNode node)
        {
            string path = node == null ? null : node.Tag as string;
            if (path == null) return;
            try
            {
                if (Directory.Exists(path)) ExplorerLocation.OpenFolder(path);
                else ExplorerLocation.Reveal(path);
            }
            catch (Exception ex) { status.Text = "无法打开路径 · " + ex.Message; }
        }
    }
}
