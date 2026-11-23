using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskDelta
{
    internal sealed class UpdateView : UserControl
    {
        private readonly GitHubUpdates updates;
        private readonly string executable;
        private readonly Func<bool> canInstall;
        private readonly Label status = MaintenanceView.Note("");
        private readonly Button check = MaintenanceView.Action("检查更新");
        private readonly Button install = MaintenanceView.Action("下载安装", true);
        private readonly Button releasePage = MaintenanceView.Action("发布页");
        private readonly Button saveProxy = MaintenanceView.Action("保存代理");
        private readonly TextBox proxy = new TextBox { Dock = DockStyle.Fill, AccessibleName = "GitHub 更新代理" };
        private readonly ToolTip tips = new ToolTip();
        private UpdateRelease available;

        public bool Busy { get; private set; }
        public event Action InstallReady;

        public UpdateView(GitHubUpdates updates, string executable, Func<bool> canInstall)
        {
            this.updates = updates;
            this.executable = executable;
            this.canInstall = canInstall;
            Dock = DockStyle.Fill;
            var layout = MaintenanceView.Layout(36, 36, 32);
            Controls.Add(layout);
            status.AutoEllipsis = true;
            status.TextChanged += delegate { tips.SetToolTip(status, status.Text); };
            Disposed += delegate { tips.Dispose(); };
            layout.Controls.Add(WithAction(status, check), 0, 0);
            layout.Controls.Add(MaintenanceView.Actions(install, releasePage), 0, 1);
            try
            {
                status.Text = "当前 " + GitHubUpdates.DisplayVersion(updates.CurrentVersion);
                proxy.Text = updates.ReadProxy();
            }
            catch (Exception ex) { status.Text = ex.Message; }
            var proxyRow = new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty
            };
            proxyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 93));
            proxyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            proxyRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            proxyRow.Controls.Add(MaintenanceView.Note("代理（可选）"), 0, 0);
            proxyRow.Controls.Add(proxy, 1, 0);
            layout.Controls.Add(WithAction(proxyRow, saveProxy), 0, 2);

            install.Enabled = false;
            check.Click += async delegate { await Check(true); };
            install.Click += async delegate { await Install(); };
            releasePage.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(GitHubUpdates.ReleasePage) { UseShellExecute = true }); }
                catch (Exception ex) { status.Text = ex.Message; }
            };
            saveProxy.Click += async delegate
            {
                try
                {
                    updates.WriteProxy(proxy.Text);
                    await Check(true);
                }
                catch (Exception ex) { status.Text = ex.Message; }
            };
        }

        private static Control WithAction(Control control, Button action)
        {
            var layout = new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            action.Dock = DockStyle.Fill;
            action.Margin = new Padding(4, 0, 1, 2);
            layout.Controls.Add(control, 0, 0);
            layout.Controls.Add(action, 1, 0);
            return layout;
        }

        public async Task Check(bool refresh)
        {
            check.Enabled = saveProxy.Enabled = install.Enabled = false;
            status.Text = "正在检查 GitHub…";
            var result = await updates.CheckAsync(refresh);
            if (IsDisposed) return;
            available = result.Release;
            status.Text = "当前 " + GitHubUpdates.DisplayVersion(updates.CurrentVersion) + " · " + result.Message;
            check.Enabled = saveProxy.Enabled = true;
            install.Enabled = available != null;
        }

        private async Task Install()
        {
            var release = available;
            if (release == null || !canInstall())
            {
                status.Text = "请等当前操作完成后再安装更新。";
                return;
            }
            if (!MaintenanceView.Confirm(this,
                "安装 " + GitHubUpdates.DisplayVersion(release.Version) + " 后会重新打开磁盘日记。", "安装更新")) return;

            Busy = true;
            check.Enabled = saveProxy.Enabled = install.Enabled = false;
            status.Text = "正在下载并校验…";
            string stage = null;
            try
            {
                stage = await Task.Run(() => UpdateInstaller.Prepare(executable, updates, release));
                if (IsDisposed || !canInstall()) throw new InvalidOperationException("请等当前操作完成后再安装更新。");
                UpdateInstaller.Start(stage, executable, release);
                stage = null;
                Busy = false;
                if (InstallReady != null) InstallReady();
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = "更新失败 · " + ex.Message; }
            finally
            {
                if (stage != null) UpdateInstaller.Discard(stage);
                Busy = false;
                if (!IsDisposed) check.Enabled = saveProxy.Enabled = install.Enabled = true;
            }
        }
    }
}
