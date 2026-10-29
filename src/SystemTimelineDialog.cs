using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class SystemTimelineDialog : Form
    {
        private readonly Func<CancellationToken, SystemTimelineReport> _read;
        private readonly Label _status;
        private readonly TextBox _details, _sources;
        private readonly DataGridView _grid;
        private readonly Button _start, _cancel;
        private CancellationTokenSource _running;
        private SystemTimelineReport _report;

        public SystemTimelineDialog() : this(SystemTimeline.Read) { }

        internal SystemTimelineDialog(Func<CancellationToken, SystemTimelineReport> reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            _read = reader;
            Text = "系统事件时间线 · RouteLens"; StartPosition = FormStartPosition.CenterParent;
            Size = new Size(970, 730); MinimumSize = new Size(760, 590); ShowInTaskbar = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f); Padding = new Padding(22, 16, 22, 14);
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 7 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 42)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); Controls.Add(root);
            TableLayoutPanel heading = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 2 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heading.Controls.Add(TextLabel("系统事件时间线", 19, UiPalette.Ink), 0, 0);
            heading.Controls.Add(TextLabel("手动读取已有日志，帮助查看程序、服务与计划任务的变化", 9, UiPalette.Muted), 0, 1); root.Controls.Add(heading, 0, 0);
            Label explanation = TextLabel("点击读取后查看最近 24 小时已有 Sysmon、系统服务及任务日志，每个来源最多 100 条。日志可能未开启、已清理或无权限；没有记录不代表没有发生。\r\n事件描述可能含程序路径、命令行或网络地址，仅在此窗口内存中展示，不上传或保存。事件关联与恶意性需要人工核实。", 9, UiPalette.Muted);
            root.Controls.Add(explanation, 0, 1);
            _status = TextLabel("尚未读取 · 点击按钮后才访问本机事件日志", 9, UiPalette.Accent); _status.TextAlign = ContentAlignment.MiddleLeft; root.Controls.Add(_status, 0, 2);

            RoundedPanel listCard = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(1), Radius = 14 };
            _grid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = UiPalette.Surface, BorderStyle = BorderStyle.None, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 34,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, GridColor = Color.FromArgb(238, 241, 243), AccessibleName = "系统事件列表，选择一行查看事件原始描述" };
            _grid.RowTemplate.Height = 34;
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Raised, ForeColor = UiPalette.Muted, Font = UiPalette.Body(9), SelectionBackColor = UiPalette.Raised, SelectionForeColor = UiPalette.Ink, Padding = new Padding(7, 0, 0, 0) };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink, Font = UiPalette.Body(9), Padding = new Padding(7, 0, 0, 0), NullValue = "—" };
            AddColumn("time", "时间（本机）", 27, 165); AddColumn("source", "来源", 24, 130); AddColumn("event", "事件 ID", 16, 80); AddColumn("provider", "提供方", 33, 170);
            _grid.SelectionChanged += delegate { ShowSelection(); };
            listCard.Controls.Add(_grid); root.Controls.Add(listCard, 0, 3);
            RoundedPanel detailCard = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(12), Radius = 14 };
            _details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Font = UiPalette.Body(9.5f), WordWrap = true, AccessibleName = "所选事件描述",
                Text = "读取后选择一条事件查看详情。\r\n\r\n事件仅说明系统记录了什么，不能单独证明进程恶意或与网络行为存在因果关系。" };
            detailCard.Controls.Add(_details); root.Controls.Add(detailCard, 0, 4);
            _sources = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = UiPalette.Canvas, ForeColor = UiPalette.Muted, Font = UiPalette.Body(8.5f), WordWrap = true, AccessibleName = "各系统日志来源的读取状态",
                Text = "各来源的权限、缺失或截断状态将在这里显示。" }; root.Controls.Add(_sources, 0, 5);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 7, 0, 0), Margin = new Padding(0) };
            _start = new ModernButton { Text = "读取已有事件", Width = 150, Height = 38, Primary = true, Margin = new Padding(0, 0, 10, 0), AccessibleName = "手动读取最近 24 小时已有系统事件" };
            _cancel = new ModernButton { Text = "取消读取", Width = 110, Height = 38, Enabled = false, Margin = new Padding(0, 0, 10, 0) };
            Button startup = new ModernButton { Text = "启动项快照", Width = 128, Height = 38, Margin = new Padding(0, 0, 10, 0), AccessibleName = "手动建立并比较启动项快照" };
            startup.Click += delegate { using (StartupSnapshotDialog dialog = new StartupSnapshotDialog()) dialog.ShowDialog(this); };
            Button close = new ModernButton { Text = "关闭", Width = 88, Height = 38, Margin = new Padding(0), DialogResult = DialogResult.Cancel };
            actions.Controls.Add(_start); actions.Controls.Add(_cancel); actions.Controls.Add(startup); actions.Controls.Add(close); root.Controls.Add(actions, 0, 6); CancelButton = close;
            _start.Click += StartClicked;
            _cancel.Click += delegate { if (_running != null) { _status.Text = "正在取消读取…"; _running.Cancel(); } };
            FormClosing += delegate { if (_running != null) _running.Cancel(); };
            Shown += delegate { _start.Focus(); };
        }

        private static Label TextLabel(string text, float size, Color color)
        { return new Label { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0), Font = UiPalette.Body(size), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft }; }
        private void AddColumn(string name, string title, float weight, int minimum)
        { _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable }); }

        private async void StartClicked(object sender, EventArgs e)
        {
            if (_running != null) return;
            CancellationTokenSource running = new CancellationTokenSource(); _running = running;
            _start.Enabled = false; _cancel.Enabled = true; _status.Text = "正在读取本机已有日志…"; _status.ForeColor = UiPalette.Accent;
            _report = null; _grid.Rows.Clear(); _details.Text = "正在读取…"; _sources.Text = "等待各来源返回。";
            try
            {
                SystemTimelineReport report = await Task.Run(delegate { return _read(running.Token); });
                if (IsDisposed || Disposing) return;
                Display(report);
            }
            catch (Exception)
            {
                if (!IsDisposed && !Disposing) { _status.Text = "读取失败，无法判断。"; _status.ForeColor = UiPalette.Warn; _details.Text = "无法读取系统事件，请检查系统事件日志是否可用。"; }
            }
            finally
            {
                if (Object.ReferenceEquals(_running, running)) _running = null;
                running.Dispose();
                if (!IsDisposed && !Disposing) { _start.Enabled = true; _cancel.Enabled = false; }
            }
        }

        private void Display(SystemTimelineReport report)
        {
            if (report == null) { _status.Text = "没有取得结果，无法判断。"; _status.ForeColor = UiPalette.Warn; return; }
            _report = report;
            _status.Text = (report.Cancelled ? "读取已取消，结果不完整" : "读取完成") + " · " + report.Entries.Count + " 条匹配事件 · 结果仅覆盖读取到的日志";
            _status.ForeColor = report.Cancelled ? UiPalette.Warn : UiPalette.Accent;
            StringBuilder sources = new StringBuilder();
            foreach (SystemTimelineChannelResult source in report.Sources) sources.AppendLine(source.Source + "：" + source.Status);
            if (report.Cancelled) sources.AppendLine("未完成的来源尚未检查。");
            _sources.Text = sources.ToString();
            _grid.SuspendLayout();
            try
            {
                foreach (SystemTimelineEntry entry in report.Entries)
                {
                    int row = _grid.Rows.Add(entry.TimeUtc.HasValue ? entry.TimeUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "未知", entry.Source, entry.EventId.ToString(), entry.Provider);
                    _grid.Rows[row].Tag = entry;
                }
            }
            finally { _grid.ResumeLayout(); }
            if (_grid.Rows.Count > 0) { _grid.ClearSelection(); _grid.Rows[0].Selected = true; ShowSelection(); }
            else _details.Text = "没有可展示的匹配事件。请查看各来源的读取状态；没有记录不代表没有发生。";
        }

        private void ShowSelection()
        {
            if (_grid.CurrentRow == null) return;
            SystemTimelineEntry entry = _grid.CurrentRow.Tag as SystemTimelineEntry;
            if (entry == null) return;
            StringBuilder text = new StringBuilder();
            text.AppendLine((entry.Source ?? "未知来源") + " · 事件 " + entry.EventId).AppendLine();
            List<SystemTimelineEntry> related = SystemTimeline.RelatedEntries(entry, _report == null ? null : _report.Entries);
            if (entry.ProcessGuid.HasValue && entry.ProcessGuid.Value != Guid.Empty && String.Equals(entry.Source, "Sysmon", StringComparison.Ordinal) && String.Equals(entry.Provider, "Microsoft-Windows-Sysmon", StringComparison.OrdinalIgnoreCase))
            {
                text.AppendLine("同一 Sysmon 进程实例：本次读取中另有 " + related.Count + " 条事件（依据 ProcessGuid）。");
                for (int i = 0; i < Math.Min(related.Count, 10); i++)
                    text.AppendLine("  " + (related[i].TimeUtc.HasValue ? related[i].TimeUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "时间未知") + "  ·  事件 " + related[i].EventId);
                if (related.Count > 10) text.AppendLine("  仅列出前 10 条；总数限于本次已读取的记录。");
            }
            else text.AppendLine("未提供可确认的 Sysmon ProcessGuid；没有根据 PID 或时间猜测进程关联。");
            text.AppendLine("同一进程的事件不证明因果关系或恶意；日志未启用、清理或截断时可能遗漏。").AppendLine();
            text.AppendLine(entry.Description ?? "事件描述不可用。");
            _details.Text = text.ToString();
            _details.SelectionStart = 0; _details.SelectionLength = 0;
        }
    }
}
