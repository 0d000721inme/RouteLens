using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class HistoryDialog : Form
    {
        private readonly HistoryStore _store;
        private readonly Timer _timer = new Timer();
        private readonly ToolTip _tips = new ToolTip();
        private readonly Label _download, _upload, _groups, _status, _empty;
        private readonly ComboBox _period, _dimension, _retention;
        private readonly CheckBox _enabled;
        private readonly TextBox _search;
        private readonly DataGridView _grid;
        private HistoryReport _lastReport;
        private bool _busy, _closing;
        private int _requestVersion;

        public HistoryDialog(HistoryStore store)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            Text = "历史流量 · RouteLens"; StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1060, 720); MinimumSize = new Size(860, 600); ShowInTaskbar = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f); Padding = new Padding(22, 16, 22, 14);
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 7 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int h in new[] { 64, 104, 39, 44, 44 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 63)); Controls.Add(root);

            TableLayoutPanel heading = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 2 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heading.Controls.Add(Label("历史流量", 20, UiPalette.Ink), 0, 0); heading.Controls.Add(Label("回看程序、网站和代理链的连接流量", 9, UiPalette.Muted), 0, 1); root.Controls.Add(heading, 0, 0);

            RoundedPanel summary = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(18, 9, 18, 9), Radius = 16 };
            TableLayoutPanel metrics = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 3, RowCount = 1, BackColor = UiPalette.Surface };
            metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            _download = Label("—", 22, UiPalette.Accent); _upload = Label("—", 22, UiPalette.Ink); _groups = Label("—", 22, UiPalette.Ink);
            metrics.Controls.Add(Metric("下载流量", _download, "所选时段"), 0, 0); metrics.Controls.Add(Metric("上传流量", _upload, "所选时段"), 1, 0); metrics.Controls.Add(Metric("分组数量", _groups, "按当前维度汇总"), 2, 0);
            summary.Controls.Add(metrics); root.Controls.Add(summary, 0, 1);

            _status = Label("正在读取历史…", 9, UiPalette.Muted); _status.Margin = new Padding(3, 4, 0, 0); _status.AutoEllipsis = true; root.Controls.Add(_status, 0, 2);
            FlowLayoutPanel filters = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            filters.Controls.Add(InlineLabel("时间", 45)); _period = Choice(118, "今日", "近 7 天", "近 30 天", "近 90 天"); filters.Controls.Add(_period);
            filters.Controls.Add(InlineLabel("查看", 45)); _dimension = Choice(118, "程序", "域名", "代理链", "最终节点"); filters.Controls.Add(_dimension);
            filters.Controls.Add(InlineLabel("筛选", 45));
            _search = new TextBox { Font = UiPalette.Body(9.5f), Width = 202, Margin = new Padding(0, 7, 12, 0), AccessibleName = "筛选历史排行" }; filters.Controls.Add(_search);
            root.Controls.Add(filters, 0, 3);

            FlowLayoutPanel settings = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            _enabled = new ModernCheck { Text = "记录历史", Checked = store.Enabled, Width = 118, Height = 34, Margin = new Padding(0, 3, 8, 0), AccessibleName = "启用本地历史流量记录" };
            settings.Controls.Add(_enabled); settings.Controls.Add(InlineLabel("保留", 48)); _retention = Choice(100, "7 天", "30 天", "90 天");
            _retention.SelectedIndex = store.RetentionDays == 7 ? 0 : store.RetentionDays == 90 ? 2 : 1; settings.Controls.Add(_retention);
            Button clear = new ModernButton { Text = "清除历史", IconName = "close", Width = 116, Height = 34, Margin = new Padding(6, 3, 0, 0), Font = UiPalette.Body(9) };
            settings.Controls.Add(clear); root.Controls.Add(settings, 0, 4);
            _tips.SetToolTip(_enabled, "关闭时不再记录；已有历史仍保存在本机。首次开启前没有历史。\n仅记录经过 Clash 且连续采样可比较的连接。");
            _tips.SetToolTip(_retention, "历史会在本机按选择的天数清理，今天包含在内。");
            _tips.SetToolTip(clear, "清除本机流量历史；不会更改 Clash 配置或系统网络设置。");

            RoundedPanel table = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(1), Radius = 12 };
            _grid = new HistoryGrid { Dock = DockStyle.Fill, BackgroundColor = UiPalette.Surface, BorderStyle = BorderStyle.None, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 38,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, GridColor = Color.FromArgb(238, 241, 243), AccessibleName = "历史流量排行" };
            _grid.RowTemplate.Height = 40;
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Raised, ForeColor = UiPalette.Muted, Font = UiPalette.Body(8.8f), SelectionBackColor = UiPalette.Raised, SelectionForeColor = UiPalette.Ink, Padding = new Padding(8, 0, 0, 0) };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink, Font = UiPalette.Body(9.5f), Padding = new Padding(8, 0, 0, 0), NullValue = "—" };
            AddColumn("rank", "#", 6, 44); AddColumn("name", "程序", 43, 170); AddColumn("download", "下载", 17, 110); AddColumn("upload", "上传", 17, 110); AddColumn("total", "总流量", 17, 110);
            foreach (string column in new[] { "download", "upload", "total" }) _grid.Columns[column].DefaultCellStyle.Font = UiPalette.Mono(9.5f);
            _empty = Label("开启记录后，新的连接流量会显示在这里。", 10.5f, UiPalette.Muted); _empty.BackColor = UiPalette.Surface; _empty.TextAlign = ContentAlignment.MiddleCenter;
            _empty.Dock = DockStyle.None;
            _grid.Controls.Add(_empty);
            _grid.Resize += delegate { _empty.Bounds = new Rectangle(0, _grid.ColumnHeadersHeight, _grid.ClientSize.Width, Math.Max(0, _grid.ClientSize.Height - _grid.ColumnHeadersHeight)); };
            table.Controls.Add(_grid); root.Controls.Add(table, 0, 5);

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0, 7, 0, 0), ColumnCount = 2, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98)); footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label note = Label("只包含开启记录后、经过 Clash 且可计算增量的连接。短连接可能遗漏；这不是整机或计费流量。\r\n历史含程序和域名，仅保存在本机；清除时将删除本地历史。", 8.4f, UiPalette.Muted); note.AutoEllipsis = true; footer.Controls.Add(note, 0, 0);
            Button close = new ModernButton { Text = "关闭", Width = 90, Height = 36, Dock = DockStyle.Top, Margin = new Padding(8, 2, 0, 0), DialogResult = DialogResult.OK };
            footer.Controls.Add(close, 1, 0); root.Controls.Add(footer, 0, 6); CancelButton = close;

            _period.SelectedIndexChanged += delegate { Changed(); }; _dimension.SelectedIndexChanged += delegate { Changed(); };
            _search.TextChanged += delegate { ApplyReport(); };
            _enabled.CheckedChanged += delegate { _store.Enabled = _enabled.Checked; Changed(); };
            _retention.SelectedIndexChanged += delegate { _store.RetentionDays = _retention.SelectedIndex == 0 ? 7 : _retention.SelectedIndex == 2 ? 90 : 30; Changed(); };
            clear.Click += delegate
            {
                if (MessageBox.Show(this, "清除本机保存的全部流量历史？此操作无法撤销。", "清除历史", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _store.Clear(); Changed();
            };
            _timer.Interval = 5000; _timer.Tick += delegate { if (_enabled.Checked) RefreshData(); };
            Shown += delegate { RefreshData(); _timer.Start(); };
            FormClosed += delegate { _closing = true; _timer.Stop(); };
        }
        private static Label Label(string text, float size, Color color)
        { return new Label { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0), Font = UiPalette.Body(size), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }; }
        private static Label InlineLabel(string text, int width)
        { return new Label { Text = text, Width = width, Height = 34, Margin = new Padding(0, 3, 0, 0), Font = UiPalette.Body(9), ForeColor = UiPalette.Muted, TextAlign = ContentAlignment.MiddleLeft }; }
        private static ComboBox Choice(int width, params string[] options)
        {
            ComboBox value = new ComboBox { Width = width, Height = 32, Margin = new Padding(0, 5, 13, 0), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = UiPalette.Body(9.5f) };
            value.Items.AddRange(options); value.SelectedIndex = 0; return value;
        }
        private static Control Metric(string name, Label value, string caption)
        {
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), BackColor = UiPalette.Surface, ColumnCount = 1, RowCount = 3 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            panel.Controls.Add(Label(name, 9, UiPalette.Muted), 0, 0); panel.Controls.Add(value, 0, 1); panel.Controls.Add(Label(caption, 8.3f, UiPalette.Muted), 0, 2); return panel;
        }
        private void AddColumn(string name, string title, float weight, int minimum)
        { _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable }); }
        private void Changed() { _requestVersion++; RefreshData(); }
        private void RefreshData()
        {
            if (_busy || _closing || IsDisposed || !IsHandleCreated) return;
            int version = _requestVersion, days = _period.SelectedIndex == 0 ? 1 : _period.SelectedIndex == 1 ? 7 : _period.SelectedIndex == 2 ? 30 : 90;
            string dimension = _dimension.SelectedIndex == 1 ? "domain" : _dimension.SelectedIndex == 2 ? "chain" : _dimension.SelectedIndex == 3 ? "node" : "program";
            _busy = true;
            Task.Run(delegate { return _store.GetReport(days, dimension); }).ContinueWith(delegate(Task<HistoryReport> task)
            {
                if (_closing || IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((Action)delegate
                {
                    if (_closing || IsDisposed) return;
                    _busy = false;
                    if (version != _requestVersion) { RefreshData(); return; }
                    _lastReport = task.IsFaulted ? new HistoryReport { Message = "历史读取失败" } : task.Result;
                    ApplyReport();
                }); } catch (InvalidOperationException) { }
            });
        }
        private void ApplyReport()
        {
            HistoryReport report = _lastReport;
            if (report == null) return;
            _download.Text = report.Available ? Bytes(report.DownloadBytes) : "—"; _upload.Text = report.Available ? Bytes(report.UploadBytes) : "—";
            _groups.Text = report.Available ? report.Entries.Count.ToString("N0") : "—";
            _status.Text = (report.Message ?? "等待历史数据") + " · 保留 " + report.RetentionDays + " 天"; _tips.SetToolTip(_status, _status.Text);
            _status.ForeColor = report.Available ? UiPalette.Muted : UiPalette.Warn;
            _grid.Columns["name"].HeaderText = _dimension.SelectedIndex == 1 ? "域名" : _dimension.SelectedIndex == 2 ? "代理链" : _dimension.SelectedIndex == 3 ? "最终节点" : "程序";
            string term = _search.Text.Trim();
            List<HistoryEntry> entries = report.Entries.Where(x => term.Length == 0 || (x.Name ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 || (x.Detail ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0).Take(500).ToList();
            string selected = _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as string;
            int top = _grid.FirstDisplayedScrollingRowIndex;
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                foreach (HistoryEntry entry in entries)
                {
                    int index = _grid.Rows.Add((indexText(_grid.Rows.Count + 1)), entry.Name, Bytes(entry.DownloadBytes), Bytes(entry.UploadBytes), Bytes(Add(entry.DownloadBytes, entry.UploadBytes)));
                    _grid.Rows[index].Tag = entry.Key;
                    _grid.Rows[index].Cells["name"].ToolTipText = String.IsNullOrWhiteSpace(entry.Detail) ? entry.Name : entry.Detail;
                    if (selected != null && String.Equals(selected, entry.Key, StringComparison.OrdinalIgnoreCase)) { _grid.CurrentCell = _grid.Rows[index].Cells["name"]; _grid.Rows[index].Selected = true; }
                }
                if (top >= 0 && _grid.Rows.Count > 0) _grid.FirstDisplayedScrollingRowIndex = Math.Min(top, _grid.Rows.Count - 1);
            }
            finally { _grid.ResumeLayout(); }
            _empty.Text = report.Available ? (report.Entries.Count == 0 ? (report.Enabled ? "等待新的可计量连接。" : "当前时段没有历史记录。") : "没有匹配的历史记录。") : (report.Message ?? "暂时无法读取历史");
            _empty.Visible = entries.Count == 0; if (_empty.Visible) _empty.BringToFront();
            if (report.Entries.Count > 500 && term.Length == 0) { _status.Text += " · 显示前 500 项，可用筛选查找"; _tips.SetToolTip(_status, _status.Text); }
        }
        private static string indexText(int value) { return value.ToString("N0"); }
        private static long Add(long a, long b) { return b > Int64.MaxValue - a ? Int64.MaxValue : a + b; }
        private static string Bytes(long value)
        {
            double amount = Math.Max(0, value); string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; int unit = 0;
            while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
            return amount.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }
        protected override void Dispose(bool disposing) { if (disposing) { _closing = true; _timer.Dispose(); _tips.Dispose(); } base.Dispose(disposing); }
        private sealed class HistoryGrid : DataGridView { public HistoryGrid() { DoubleBuffered = true; } }
    }
}
