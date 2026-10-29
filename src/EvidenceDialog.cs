using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class EvidenceDialog : Form
    {
        private readonly EvidenceMonitor _monitor;
        private readonly Func<string> _selected;
        private readonly Label _status;
        private readonly DataGridView _grid;
        private readonly TextBox _detail;
        private readonly CheckBox _includeConnection;
        private readonly Timer _timer = new Timer();
        private EvidenceEvent _lastEvent;

        public EvidenceDialog(EvidenceMonitor monitor, Func<string> selected, Action showSystemTimeline, Action showAdvanced, Action showExternal = null)
        {
            _monitor = monitor; _selected = selected;
            Text = "证据与采集状态 · RouteLens"; StartPosition = FormStartPosition.CenterParent;
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(10);
            ClientSize = new Size(960, 700); MinimumSize = new Size(740, 560); Padding = new Padding(26, 18, 26, 20);
            ShowInTaskbar = false; MinimizeBox = false; AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int h in new[] { 40, 28, 46, 30 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(layout);
            layout.Controls.Add(TextLabel("证据与采集状态", 20, UiPalette.Ink), 0, 0);
            layout.Controls.Add(TextLabel("按来源查看观察和缺口；任何单条记录都不能证明感染或攻击。", 9, UiPalette.Muted), 0, 1);
            _status = TextLabel("等待采样", 10, UiPalette.Accent); layout.Controls.Add(_status, 0, 2);
            layout.Controls.Add(TextLabel("本次采集状态与提醒", 10, UiPalette.Ink), 0, 3);
            _grid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = UiPalette.Surface, BorderStyle = BorderStyle.None, RowHeadersVisible = false,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, ReadOnly = true, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false, ColumnHeadersHeight = 34, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = UiPalette.Border, AccessibleName = "采集状态事件列表" };
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Raised, ForeColor = UiPalette.Muted, Font = UiPalette.Body(9), Padding = new Padding(6, 0, 0, 0) };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink, Font = UiPalette.Body(9), Padding = new Padding(6, 0, 0, 0) };
            _grid.RowTemplate.Height = 34;
            AddColumn("时间", 15, 120); AddColumn("来源", 20, 110); AddColumn("状态", 18, 120); AddColumn("说明", 47, 200);
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0, 0, 0, 8), Radius = 14 }; card.Controls.Add(_grid); layout.Controls.Add(card, 0, 4);
            TableLayoutPanel context = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 2 };
            context.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); context.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); context.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            context.Controls.Add(TextLabel("主窗口所选连接 · 当前快照", 10, UiPalette.Ink), 0, 0);
            _detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = UiPalette.Surface,
                ForeColor = UiPalette.Ink, Font = UiPalette.Body(9), BorderStyle = BorderStyle.FixedSingle, AccessibleName = "所选连接的可见证据" };
            context.Controls.Add(_detail, 0, 1); layout.Controls.Add(context, 0, 5);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 9, 0, 0), Margin = new Padding(0) };
            Button timeline = ActionButton("系统变更", 100); timeline.Click += delegate { if (showSystemTimeline != null) showSystemTimeline(); RefreshData(); };
            Button advanced = ActionButton("全机观察", 100); advanced.Click += delegate
            {
                if (showAdvanced != null) showAdvanced();
                else MessageBox.Show(this, "全机逐进程 ETW 采集仍在实验和验证中，当前版本尚未启用。Windows 系统连接视图可查看现有端点，但不提供逐连接流量。", "全机观察", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshData();
            };
            Button export = ActionButton("导出状态", 100); export.Click += delegate { ExportStatus(); };
            Button external = ActionButton("外部证据", 100); external.Enabled = showExternal != null; external.Click += delegate { if (showExternal != null) showExternal(); };
            _includeConnection = new ModernCheck { Text = "包含所选连接", Width = 135, Height = 34, Margin = new Padding(4, 0, 0, 0), AccessibleName = "导出时包含所选连接中的域名、IP 和程序信息" };
            Button close = ActionButton("关闭", 85); close.DialogResult = DialogResult.OK;
            actions.Controls.AddRange(new Control[] { timeline, advanced, external, export, _includeConnection, close }); layout.Controls.Add(actions, 0, 6); CancelButton = close;
            _timer.Interval = 1000; _timer.Tick += delegate { RefreshData(); }; Shown += delegate { RefreshData(); _timer.Start(); }; FormClosed += delegate { _timer.Stop(); };
        }
        private static Label TextLabel(string text, float size, Color color)
        { return new Label { Text = text, Dock = DockStyle.Fill, Font = UiPalette.Body(size), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0), AutoEllipsis = true }; }
        private static Button ActionButton(string text, int width)
        { return new ModernButton { Text = text, Width = width, Height = 34, Margin = new Padding(0, 0, 8, 0) }; }
        private void AddColumn(string name, float weight, int minimum)
        { _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable }); }
        private void RefreshData()
        {
            _status.Text = "当前：" + _monitor.State + "  ·  来源：" + _monitor.Source + "  ·  全机覆盖率：未知";
            EvidenceEvent[] events = _monitor.Snapshot();
            EvidenceEvent newest = events.Length == 0 ? null : events[events.Length - 1];
            if (!Object.ReferenceEquals(newest, _lastEvent))
            {
                _grid.Rows.Clear();
                for (int i = events.Length - 1; i >= 0; i--) _grid.Rows.Add(events[i].TimeUtc.ToLocalTime().ToString("HH:mm:ss"), events[i].Source, events[i].State, events[i].Detail);
                _lastEvent = newest;
            }
            string selected = _selected == null ? null : _selected();
            string detail = String.IsNullOrEmpty(selected) ? "请在主窗口选择一条连接。\r\n\r\n连接快照不能证明程序是否可信或存在攻击；关闭窗口后状态事件不保留。" : selected + "\r\n\r\n仅为当前连接快照；不能据此证明感染或攻击。短连接及不可见的流量可能遗漏。";
            if (!_detail.Focused && _detail.Text != detail) _detail.Text = detail;
        }
        private void ExportStatus()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Title = "导出本次采集状态", Filter = "文本文件 (*.txt)|*.txt", FileName = "RouteLens-采集状态.txt", AddExtension = true, DefaultExt = "txt" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    StringBuilder text = new StringBuilder();
                    text.AppendLine("RouteLens 采集状态（本次运行）").AppendLine("导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
                    text.AppendLine("当前：" + _monitor.State + "；来源：" + _monitor.Source + "；全机覆盖率未知。");
                    text.AppendLine("连接是活动快照，短连接可能遗漏；状态不能证明感染或攻击。").AppendLine();
                    foreach (EvidenceEvent item in _monitor.Snapshot()) text.AppendLine(item.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") + "  " + item.Source + "  " + item.State + "  " + item.Detail);
                    if (_includeConnection.Checked) text.AppendLine().AppendLine("所选连接（可能包含程序、域名和 IP）：").AppendLine(_selected == null ? "未选择" : (_selected() ?? "未选择"));
                    File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
                    _status.Text = "已导出本次状态；请检查内容后再分享。";
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException)) throw;
                    MessageBox.Show(this, "无法导出：" + ex.Message, "导出状态", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
    }
}
