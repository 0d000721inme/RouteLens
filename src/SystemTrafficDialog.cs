using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class SystemTrafficDialog : Form
    {
        private readonly Func<bool> _mainPaused;
        private readonly SystemTrafficMonitor _monitor = new SystemTrafficMonitor();
        private readonly Timer _timer = new Timer();
        private readonly ToolTip _tips = new ToolTip();
        private readonly Label _state, _active, _measured, _capture, _empty;
        private readonly Button _toggle;
        private readonly DataGridView _grid;
        private bool _localPaused, _wasPaused, _busy, _closing;
        private int _generation;

        public SystemTrafficDialog(Func<bool> isMainPaused)
        {
            _mainPaused = isMainPaused;
            Text = "系统网卡流量 · RouteLens"; StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1050, 700); MinimumSize = new Size(860, 590); ShowInTaskbar = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f); Padding = new Padding(22, 16, 22, 14);
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 6 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int h in new[] { 64, 104, 44, 45 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); Controls.Add(root);
            TableLayoutPanel heading = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 2 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heading.Controls.Add(Label("系统网卡流量", 20, UiPalette.Ink), 0, 0); heading.Controls.Add(Label("逐张网卡查看 Windows 提供的实时上下行", 9, UiPalette.Muted), 0, 1); root.Controls.Add(heading, 0, 0);

            RoundedPanel summary = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(18, 9, 18, 9), Radius = 16 };
            TableLayoutPanel metrics = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 3, RowCount = 1, BackColor = UiPalette.Surface };
            metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            _active = Label("—", 22, UiPalette.Ink); _measured = Label("—", 22, UiPalette.Accent); _capture = Label("等待采样", 14, UiPalette.Ink);
            metrics.Controls.Add(Metric("活动网卡", _active, "系统报告已连接"), 0, 0); metrics.Controls.Add(Metric("已计速", _measured, "有连续采样的网卡"), 1, 0); metrics.Controls.Add(Metric("最近采样", _capture, "约每秒更新"), 2, 0);
            summary.Controls.Add(metrics); root.Controls.Add(summary, 0, 1);

            _state = Label("正在读取网卡…", 9, UiPalette.Muted); _state.Margin = new Padding(3, 4, 0, 0); root.Controls.Add(_state, 0, 2);
            TableLayoutPanel toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 2, RowCount = 1 };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label title = Label("网卡列表", 11, UiPalette.Ink); title.Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold); toolbar.Controls.Add(title, 0, 0);
            _toggle = new ModernButton { Text = "暂停网卡", IconName = "pause", Width = 116, Height = 34, Dock = DockStyle.Top, Margin = new Padding(0, 3, 0, 0), Font = UiPalette.Body(9) };
            _toggle.Click += delegate { _localPaused = !_localPaused; _generation++; _monitor.Reset(); RefreshData(); }; toolbar.Controls.Add(_toggle, 1, 0); root.Controls.Add(toolbar, 0, 3);
            _tips.SetToolTip(_toggle, "只暂停此窗口的网卡采样；恢复后重新建立基线。");

            RoundedPanel table = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(1), Radius = 12 };
            _grid = new AdapterGrid { Dock = DockStyle.Fill, BackgroundColor = UiPalette.Surface, BorderStyle = BorderStyle.None, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 38,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, GridColor = Color.FromArgb(238, 241, 243), AccessibleName = "Windows 系统网卡实时流量" };
            _grid.RowTemplate.Height = 40;
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Raised, ForeColor = UiPalette.Muted, Font = UiPalette.Body(8.8f), SelectionBackColor = UiPalette.Raised, SelectionForeColor = UiPalette.Ink, Padding = new Padding(8, 0, 0, 0) };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink, Font = UiPalette.Body(9.5f), Padding = new Padding(8, 0, 0, 0), NullValue = "—" };
            AddColumn("name", "网卡名称", 34, 170); AddColumn("kind", "类型", 15, 90); AddColumn("download", "下载速度", 19, 110); AddColumn("upload", "上传速度", 19, 110); AddColumn("status", "状态", 13, 90);
            _grid.Columns["download"].DefaultCellStyle.Font = UiPalette.Mono(9.5f); _grid.Columns["upload"].DefaultCellStyle.Font = UiPalette.Mono(9.5f);
            _grid.Columns["kind"].ToolTipText = "物理或虚拟类型来自 Windows；回环和隧道根据系统网卡类型标注，无法判断时显示未知。";
            _empty = Label("等待网卡数据", 10.5f, UiPalette.Muted); _empty.BackColor = UiPalette.Surface; _empty.TextAlign = ContentAlignment.MiddleCenter;
            table.Controls.Add(_grid); table.Controls.Add(_empty); root.Controls.Add(table, 0, 4);

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0, 7, 0, 0), ColumnCount = 2, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98)); footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label note = Label("速度来自 Windows 网卡计数差值；同一流量可能同时经过物理网卡和隧道，不能把各行相加。\r\n这里不提供程序级流量归属；程序和网站请查看 Clash 原始连接。", 8.4f, UiPalette.Muted); note.AutoEllipsis = true; footer.Controls.Add(note, 0, 0);
            Button close = new ModernButton { Text = "关闭", Width = 90, Height = 36, Dock = DockStyle.Top, Margin = new Padding(8, 2, 0, 0), DialogResult = DialogResult.OK };
            footer.Controls.Add(close, 1, 0); root.Controls.Add(footer, 0, 5); CancelButton = close;
            _timer.Interval = 1000; _timer.Tick += delegate { RefreshData(); };
            Shown += delegate { RefreshData(); _timer.Start(); };
            FormClosed += delegate { _closing = true; _generation++; _timer.Stop(); _monitor.Reset(); };
        }
        private static Label Label(string text, float size, Color color)
        { return new Label { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0), Font = UiPalette.Body(size), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }; }
        private static Control Metric(string title, Label value, string caption)
        {
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), BackColor = UiPalette.Surface, ColumnCount = 1, RowCount = 3 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            panel.Controls.Add(Label(title, 9, UiPalette.Muted), 0, 0); panel.Controls.Add(value, 0, 1); panel.Controls.Add(Label(caption, 8.3f, UiPalette.Muted), 0, 2); return panel;
        }
        private void AddColumn(string name, string title, float weight, int minimum)
        { _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable }); }
        private bool ParentPaused() { try { return _mainPaused != null && _mainPaused(); } catch (Exception) { return true; } }
        private void RefreshData()
        {
            if (_closing || IsDisposed || !IsHandleCreated) return;
            bool parentPaused = ParentPaused(), paused = _localPaused || parentPaused;
            _toggle.Enabled = !parentPaused; _toggle.Text = _localPaused ? "继续网卡" : "暂停网卡"; ((ModernButton)_toggle).IconName = _localPaused ? "play" : "pause";
            if (paused)
            {
                if (!_wasPaused) { _wasPaused = true; _generation++; _monitor.Reset(); }
                _state.Text = parentPaused ? "主窗口采样已暂停；返回主窗口继续采样后显示网卡速度。" : "网卡采样已暂停；继续后需要重新采样。";
                _state.ForeColor = UiPalette.Warn; _measured.Text = "—"; _capture.Text = "已暂停";
                foreach (DataGridViewRow row in _grid.Rows) { row.Cells["download"].Value = "—"; row.Cells["upload"].Value = "—"; }
                return;
            }
            if (_wasPaused) { _wasPaused = false; _generation++; _monitor.Reset(); }
            if (_busy) return;
            int version = _generation; _busy = true;
            Task.Run(delegate { return _monitor.Capture(); }).ContinueWith(delegate(Task<SystemTrafficSnapshot> task)
            {
                if (_closing || IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((Action)delegate
                {
                    if (_closing || IsDisposed) return;
                    _busy = false;
                    if (version != _generation || _localPaused || ParentPaused()) { RefreshData(); return; }
                    if (task.IsFaulted) RenderSnapshot(new SystemTrafficSnapshot { Error = "系统网卡读取失败" });
                    else if (!task.Result.Stale) RenderSnapshot(task.Result);
                }); } catch (InvalidOperationException) { }
            });
        }
        private void RenderSnapshot(SystemTrafficSnapshot snapshot)
        {
            bool failed = !String.IsNullOrWhiteSpace(snapshot.Error);
            _state.Text = failed ? snapshot.Error : "每张网卡单独计速 · 物理/虚拟由 Windows 标记 · 网卡流量不等于程序流量";
            _state.ForeColor = failed ? UiPalette.Warn : UiPalette.Muted; _tips.SetToolTip(_state, _state.Text);
            _active.Text = failed ? "—" : snapshot.Adapters.Count(x => x.Available).ToString("N0");
            _measured.Text = failed ? "—" : snapshot.Adapters.Count(x => x.Measured).ToString("N0");
            _capture.Text = failed ? "未获取" : snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss");
            string selected = _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as string;
            int top = _grid.FirstDisplayedScrollingRowIndex;
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                foreach (SystemAdapterTraffic adapter in snapshot.Adapters)
                {
                    int index = _grid.Rows.Add(adapter.Name, adapter.Kind, adapter.Measured ? Rate(adapter.DownloadPerSecond) : "—", adapter.Measured ? Rate(adapter.UploadPerSecond) : "—", adapter.Status);
                    _grid.Rows[index].Tag = adapter.Id; _grid.Rows[index].Cells["name"].ToolTipText = adapter.Description;
                    if (selected != null && String.Equals(selected, adapter.Id, StringComparison.OrdinalIgnoreCase)) { _grid.CurrentCell = _grid.Rows[index].Cells["name"]; _grid.Rows[index].Selected = true; }
                }
                if (top >= 0 && _grid.Rows.Count > 0) _grid.FirstDisplayedScrollingRowIndex = Math.Min(top, _grid.Rows.Count - 1);
            }
            finally { _grid.ResumeLayout(); }
            _empty.Text = failed ? snapshot.Error : "没有可显示的系统网卡"; _empty.Visible = _grid.Rows.Count == 0; if (_empty.Visible) _empty.BringToFront();
        }
        private static string Rate(double value)
        {
            double amount = Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 ? 0 : value; string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" }; int unit = 0;
            while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
            return amount.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }
        protected override void Dispose(bool disposing) { if (disposing) { _closing = true; _timer.Dispose(); _tips.Dispose(); } base.Dispose(disposing); }
        private sealed class AdapterGrid : DataGridView { public AdapterGrid() { DoubleBuffered = true; } }
    }
}
