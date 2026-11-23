using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace DiskDelta
{
    internal sealed class HistoryView : UserControl
    {
        public readonly DataGridView Records = Dashboard.Grid();
        public event Action<Report> OpenSaved;
        public event Action<Report, Report> OpenComparison;
        private readonly SpaceChart chart = new SpaceChart { Dock = DockStyle.Fill };
        private readonly Label summary = MaintenanceView.Note("");
        private readonly Label feedback = MaintenanceView.Note("");
        private readonly Button compare = MaintenanceView.Action("查看文件差异");
        private readonly List<string> checkedIds = new List<string>(2);
        private List<Report> reports = new List<Report>();
        private History history;
        private bool fillsRows;
        private bool followsLatest = true;
        private bool working;

        public HistoryView()
        {
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.White };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(split);

            Records.ReadOnly = false;
            Records.EditMode = DataGridViewEditMode.EditOnEnter;
            Records.Columns.Add(new DataGridViewCheckBoxColumn
            {
                HeaderText = "选", Width = 32, MinimumWidth = 30, Tag = 32, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            Dashboard.Column(Records, "扫描时间", 0);
            Dashboard.Column(Records, "可用", 72);
            Records.Columns[1].ReadOnly = Records.Columns[2].ReadOnly = true;
            Records.CurrentCellDirtyStateChanged += delegate
            {
                if (Records.IsCurrentCellDirty && Records.CurrentCell is DataGridViewCheckBoxCell)
                    Records.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            Records.CellValueChanged += ChangeSelection;
            Records.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.ColumnIndex > 0) RaiseSaved(e.RowIndex);
            };
            Records.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter || Records.CurrentCell == null) return;
                e.Handled = e.SuppressKeyPress = true;
                RaiseSaved(Records.CurrentCell.RowIndex);
            };
            Records.Margin = new Padding(0, 1, 12, 0);
            split.Controls.Add(Records, 0, 0);

            var board = MaintenanceView.Layout(28, 0, 47, 34, 22);
            var caption = MaintenanceView.Note("可用空间");
            caption.ForeColor = Color.FromArgb(24, 44, 53);
            board.Controls.Add(caption, 0, 0);
            board.Controls.Add(chart, 0, 1);
            board.Controls.Add(summary, 0, 2);
            board.Controls.Add(MaintenanceView.Actions(compare), 0, 3);
            board.Controls.Add(feedback, 0, 4);
            split.Controls.Add(board, 1, 0);
            compare.Click += delegate
            {
                Report[] pair = Pair();
                if (pair.Length == 2 && OpenComparison != null)
                    OpenComparison(pair[0], pair[1]);
            };
            UpdatePair();
        }

        public void ShowReports(History source, List<Report> items, bool reset)
        {
            history = source;
            reports = items;
            var ids = new HashSet<string>(reports.Select(item => item.Id), StringComparer.Ordinal);
            if (reset) followsLatest = true;
            if (followsLatest)
            {
                checkedIds.Clear();
                checkedIds.AddRange(reports.Take(2).Reverse().Select(item => item.Id));
            }
            else
                checkedIds.RemoveAll(id => !ids.Contains(id));
            feedback.Text = "";
            fillsRows = true;
            Records.SuspendLayout();
            try
            {
                Records.Rows.Clear();
                foreach (Report item in reports)
                {
                    int row = Records.Rows.Add(checkedIds.Contains(item.Id), Dashboard.Date(item.Started), Dashboard.Bytes(item.Free));
                    Records.Rows[row].Tag = item;
                    Records.Rows[row].Cells[1].ToolTipText = Dashboard.Date(item.Started);
                }
                Records.ClearSelection();
            }
            finally
            {
                Records.ResumeLayout();
                fillsRows = false;
            }
            UpdatePair();
        }

        private void ChangeSelection(object sender, DataGridViewCellEventArgs e)
        {
            if (fillsRows || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var report = Records.Rows[e.RowIndex].Tag as Report;
            if (report == null) return;
            followsLatest = false;
            checkedIds.Remove(report.Id);
            if (object.Equals(Records.Rows[e.RowIndex].Cells[0].Value, true))
            {
                checkedIds.Add(report.Id);
                if (checkedIds.Count > 2) checkedIds.RemoveAt(0);
            }
            fillsRows = true;
            try
            {
                foreach (DataGridViewRow row in Records.Rows)
                    row.Cells[0].Value = checkedIds.Contains(((Report)row.Tag).Id);
            }
            finally { fillsRows = false; }
            feedback.Text = "";
            UpdatePair();
        }

        private void RaiseSaved(int row)
        {
            if (!working && OpenSaved != null) OpenSaved((Report)Records.Rows[row].Tag);
        }

        private Report[] Pair()
        {
            return reports.Where(item => checkedIds.Contains(item.Id)).Reverse().ToArray();
        }

        private void UpdatePair()
        {
            Report[] pair = Pair();
            chart.ShowSeries(reports, checkedIds);
            bool hasPair = pair.Length == 2;
            bool hasFiles = hasPair && history != null && history.CanCompareFiles(pair[0], pair[1]);
            compare.Visible = hasPair;
            compare.Enabled = hasFiles && !working;
            if (pair.Length < 2)
            {
                summary.Text = reports.Count == 0 ? "" : "选择两次扫描进行对比";
                return;
            }
            long used = (pair[1].Capacity - pair[1].Free) - (pair[0].Capacity - pair[0].Free);
            summary.Text = "可用  " + Dashboard.Bytes(pair[0].Free) + "  →  " + Dashboard.Bytes(pair[1].Free) +
                "\n占用变化  " + Dashboard.Signed(used);
            if (!working) feedback.Text = hasFiles ? "" : "文件明细不可用";
        }

        public void SetWorking(bool active, string message)
        {
            working = active;
            Records.Enabled = !active;
            UpdatePair();
            if (!string.IsNullOrEmpty(message)) feedback.Text = message;
        }

        private sealed class SpaceChart : Control
        {
            private readonly Color teal = Color.FromArgb(0, 104, 92);
            private readonly Color muted = Color.FromArgb(75, 99, 110);
            private readonly Color grid = Color.FromArgb(222, 232, 236);
            private readonly ToolTip tooltip = new ToolTip();
            private Report[] items = new Report[0];
            private HashSet<string> selected = new HashSet<string>();
            private PointF[] points = new PointF[0];
            private int hover = -1;

            public SpaceChart()
            {
                BackColor = Color.White;
                DoubleBuffered = true;
                ResizeRedraw = true;
                AccessibleName = "可用空间折线图";
            }

            public void ShowSeries(List<Report> reports, List<string> checkedIds)
            {
                items = reports.AsEnumerable().Reverse().ToArray();
                selected = new HashSet<string>(checkedIds, StringComparer.Ordinal);
                points = new PointF[0];
                hover = -1;
                tooltip.SetToolTip(this, "");
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (items.Length == 0)
                {
                    TextRenderer.DrawText(e.Graphics, "暂无扫描记录", Font, ClientRectangle, muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }

                Graphics g = e.Graphics;
                float scale = g.DpiX / 96f;
                Rectangle area = Rectangle.FromLTRB((int)(68 * scale), (int)(12 * scale),
                    Width - (int)(12 * scale), Height - (int)(31 * scale));
                if (area.Width < 15 || area.Height < 20) return;
                long smallest = items.Min(item => item.Free);
                long largest = items.Max(item => item.Free);
                double padding = Math.Max(1, (largest - smallest) * .12);
                if (smallest == largest) padding = Math.Max(1, largest * .04);
                double low = Math.Max(0, smallest - padding);
                double high = largest + padding;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var line = new Pen(grid, scale))
                {
                    for (int index = 0; index <= 3; index++)
                    {
                        int y = area.Top + area.Height * index / 3;
                        g.DrawLine(line, area.Left, y, area.Right, y);
                        var label = new Rectangle(0, y - Font.Height / 2, area.Left - (int)(8 * scale), Font.Height + 2);
                        long bytes = (long)Math.Round(high - (high - low) * index / 3);
                        TextRenderer.DrawText(g, Dashboard.Bytes(bytes), Font, label, muted,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                }
                DateTime first = items[0].Started.ToUniversalTime();
                double duration = (items[items.Length - 1].Started.ToUniversalTime() - first).TotalSeconds;
                points = items.Select((item, index) => new PointF(
                    area.Left + area.Width * (float)(duration > 0 ?
                        (item.Started.ToUniversalTime() - first).TotalSeconds / duration :
                        items.Length == 1 ? .5 : index / (double)(items.Length - 1)),
                    area.Bottom - area.Height * (float)((item.Free - low) / (high - low)))).ToArray();
                using (var stroke = new Pen(teal, 2 * scale))
                using (var marker = new SolidBrush(teal))
                {
                    if (points.Length > 1) g.DrawLines(stroke, points);
                    for (int index = 0; index < points.Length; index++)
                    {
                        PointF p = points[index];
                        bool chosen = selected.Contains(items[index].Id);
                        float radius = (chosen ? 4.5f : 2.5f) * scale;
                        var circle = new RectangleF(p.X - radius, p.Y - radius, radius * 2, radius * 2);
                        if (chosen) { g.FillEllipse(Brushes.White, circle); g.DrawEllipse(stroke, circle); }
                        else g.FillEllipse(marker, circle);
                    }
                }
                bool sameDay = items[0].Started.Date == items[items.Length - 1].Started.Date;
                string format = sameDay && items.Length > 1 ? "HH:mm" : "MM-dd";
                var axis = Rectangle.FromLTRB(area.Left, area.Bottom + (int)(6 * scale), area.Right, Height);
                TextRenderer.DrawText(g, items[0].Started.ToString(format), Font, axis, muted,
                    (items.Length == 1 ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.NoPadding);
                if (items.Length > 1)
                    TextRenderer.DrawText(g, items[items.Length - 1].Started.ToString(format), Font, axis, muted,
                        TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int closest = -1;
                float distance = 225;
                for (int index = 0; index < points.Length; index++)
                {
                    float dx = points[index].X - e.X, dy = points[index].Y - e.Y;
                    float candidate = dx * dx + dy * dy;
                    if (candidate < distance) { closest = index; distance = candidate; }
                }
                if (closest == hover) return;
                hover = closest;
                tooltip.SetToolTip(this, hover >= 0 ?
                    Dashboard.Date(items[hover].Started) + " · " + Dashboard.Bytes(items[hover].Free) : "");
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) tooltip.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
