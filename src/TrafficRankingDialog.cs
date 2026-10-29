using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class TrafficRankingDialog : Form
    {
        private readonly Func<TrafficReport> _provider;
        private readonly Func<bool> _isMainPaused;
        private readonly Timer _timer = new Timer();
        private readonly ToolTip _tips = new ToolTip();
        private readonly Label _download;
        private readonly Label _upload;
        private readonly Label _programs;
        private readonly Label _coverage;
        private readonly Label _status;
        private readonly Label _empty;
        private readonly TextBox _path;
        private readonly ComboBox _sort;
        private readonly RankingGrid _grid;
        private readonly TrafficTrend _trend;
        private readonly Dictionary<string, DataGridViewRow> _rows = new Dictionary<string, DataGridViewRow>(StringComparer.Ordinal);
        private TrafficReport _report;
        private bool _updating;
        private readonly Dictionary<string, TargetObservation> _observedTargets = new Dictionary<string, TargetObservation>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastTargetSnapshot;

        public TrafficRankingDialog(Func<TrafficReport> provider, Func<bool> isMainPaused = null)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            _provider = provider;
            _isMainPaused = isMainPaused ?? delegate { return false; };
            Text = "实时流量排行 · RouteLens";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1000, 700);
            MinimumSize = new Size(820, 560);
            ShowInTaskbar = false;
            MinimizeBox = false;
            BackColor = UiPalette.Canvas;
            ForeColor = UiPalette.Ink;
            Font = UiPalette.Body(9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Padding = new Padding(22, 16, 22, 14);

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = new Padding(0), Padding = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            Controls.Add(layout);

            TableLayoutPanel heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heading.Controls.Add(TextLabel("实时流量排行", 20, UiPalette.Ink), 0, 0);
            heading.Controls.Add(TextLabel("按程序查看下载与上传 · 跟随主窗口的国家、搜索与连接筛选", 9, UiPalette.Muted), 0, 1);
            layout.Controls.Add(heading, 0, 0);

            RoundedPanel summary = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(18, 9, 18, 9), Radius = 12 };
            TableLayoutPanel metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0), BackColor = UiPalette.Surface };
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _download = TextLabel("—", 23, UiPalette.Accent);
            _upload = TextLabel("—", 23, UiPalette.Ink);
            _programs = TextLabel("0", 23, UiPalette.Ink);
            _coverage = TextLabel("尚未取得连接", 8.5f, UiPalette.Muted);
            metrics.Controls.Add(Metric("范围内下载速度", _download, TextLabel("按连接累计字节的变化计算", 8.5f, UiPalette.Muted)), 0, 0);
            metrics.Controls.Add(Metric("范围内上传速度", _upload, TextLabel("首个样本与新增连接需等待计速", 8.5f, UiPalette.Muted)), 1, 0);
            Control programMetric = Metric("程序分组", _programs, _coverage);
            _tips.SetToolTip(programMetric, "按同一程序路径合并；未能识别程序的连接归为一组。");
            _tips.SetToolTip(_programs, "按同一程序路径合并；未能识别程序的连接归为一组。");
            metrics.Controls.Add(programMetric, 2, 0);
            summary.Controls.Add(metrics);
            layout.Controls.Add(summary, 0, 1);

            _status = TextLabel("等待采样…", 9, UiPalette.Muted);
            _status.Margin = new Padding(2, 8, 2, 2);
            _status.AutoEllipsis = true;
            _status.AccessibleName = "流量数据来源、筛选范围与计速状态";
            layout.Controls.Add(_status, 0, 2);

            RoundedPanel trendCard = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(2), Radius = 18 };
            _trend = new TrafficTrend(); trendCard.Controls.Add(_trend); layout.Controls.Add(trendCard, 0, 3);

            TableLayoutPanel toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
            Label listHeading = TextLabel("程序流量", 11, UiPalette.Ink);
            listHeading.Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
            toolbar.Controls.Add(listHeading, 0, 0);
            ModernButton interfaces = new ModernButton { Text = "网卡流量", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 10, 4), AccessibleName = "查看 Windows 各网卡实时流量" };
            interfaces.Click += delegate { using (SystemTrafficDialog dialog = new SystemTrafficDialog(_isMainPaused)) dialog.ShowDialog(this); };
            _tips.SetToolTip(interfaces, "查看每张网卡的上传和下载速度；不推断程序归属，也不合计可能重复的网卡流量。");
            ModernButton targets = new ModernButton { Text = "目标观察", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 10, 4), AccessibleName = "查看最近活动目标和有上传增量的目标" };
            targets.Click += delegate { using (TargetObservationsDialog dialog = new TargetObservationsDialog(RecentTargets)) dialog.ShowDialog(this); };
            _tips.SetToolTip(targets, "只记录实时流量窗口打开期间观察到的目标；上传增量来自有效的 Clash 连接采样。");
            toolbar.Controls.Add(targets, 1, 0);
            toolbar.Controls.Add(interfaces, 2, 0);
            toolbar.Controls.Add(TextLabel("排序", 9, UiPalette.Muted), 3, 0);
            _sort = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = UiPalette.Body(9.5f), Margin = new Padding(0, 7, 0, 7), AccessibleName = "流量排行排序方式" };
            _sort.Items.AddRange(new object[] { "下载速度", "上传速度", "总速度" });
            _sort.SelectedIndex = 0;
            _sort.SelectedIndexChanged += delegate
            {
                if (_report == null) return;
                RenderReport(_report);
                if (_grid.Rows.Count > 0)
                {
                    _grid.CurrentCell = _grid.Rows[0].Cells["program"];
                    _grid.Rows[0].Selected = true;
                    _grid.FirstDisplayedScrollingRowIndex = 0;
                    UpdateSelection();
                }
            };
            toolbar.Controls.Add(_sort, 4, 0);
            layout.Controls.Add(toolbar, 0, 4);

            RoundedPanel tableCard = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Radius = 10, Padding = new Padding(1) };
            _grid = new RankingGrid { Dock = DockStyle.Fill, BackgroundColor = UiPalette.Surface, BorderStyle = BorderStyle.None, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false, AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeight = 38, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                GridColor = Color.FromArgb(238, 241, 243), EnableHeadersVisualStyles = false, AccessibleName = "按程序汇总的实时上传与下载流量" };
            _grid.RowTemplate.Height = 40;
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Raised, ForeColor = UiPalette.Muted, Font = UiPalette.Body(8.8f), SelectionBackColor = UiPalette.Raised, SelectionForeColor = UiPalette.Ink, Padding = new Padding(9, 0, 0, 0) };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink, Font = UiPalette.Body(9.5f), Padding = new Padding(9, 0, 0, 0), NullValue = "—", WrapMode = DataGridViewTriState.False };
            AddColumn("rank", "#", 5, 36);
            AddColumn("program", "程序", 27, 142);
            AddColumn("connections", "计速 / 连接", 13, 96);
            AddColumn("download", "下载速度", 16, 110);
            AddColumn("upload", "上传速度", 16, 110);
            AddColumn("bytes", "存活累计 ↓ / ↑", 23, 164);
            _grid.Columns["connections"].ToolTipText = "已取得有效速率的连接数 / 此程序在当前筛选范围内的存活连接数。";
            _grid.Columns["bytes"].ToolTipText = "当前存活连接的累计下载 / 上传；连接结束后不再计入，不是本次或整机总用量。";
            _grid.Columns["download"].DefaultCellStyle.ForeColor = UiPalette.Accent;
            foreach (string name in new[] { "download", "upload", "bytes" }) _grid.Columns[name].DefaultCellStyle.Font = UiPalette.Mono(9.5f);
            _grid.SelectionChanged += delegate { if (!_updating) UpdateSelection(); };
            _grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            _empty = TextLabel("正在等待流量数据", 11, UiPalette.Muted);
            _empty.BackColor = UiPalette.Surface;
            _empty.TextAlign = ContentAlignment.MiddleCenter;
            _empty.Dock = DockStyle.Fill;
            tableCard.Controls.Add(_grid);
            tableCard.Controls.Add(_empty);
            layout.Controls.Add(tableCard, 0, 5);

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0, 8, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _path = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ReadOnly = true, BackColor = UiPalette.Canvas, ForeColor = UiPalette.Ink, Font = UiPalette.Body(9), Text = "选择程序查看完整路径", Margin = new Padding(1, 2, 12, 0), AccessibleName = "选中程序的完整路径，可选择复制" };
            footer.Controls.Add(_path, 0, 0);
            Label note = TextLabel("累计仅含存活连接，结束后移出；* 为部分连接提供计数。\r\n速度“—”表示当前无法计速。", 8.5f, UiPalette.Muted);
            note.AutoEllipsis = true;
            footer.Controls.Add(note, 0, 1);
            ModernButton close = new ModernButton { Text = "关闭", Width = 90, Height = 36, Dock = DockStyle.Top, Margin = new Padding(4, 4, 0, 0), DialogResult = DialogResult.OK };
            footer.Controls.Add(close, 1, 0);
            footer.SetRowSpan(close, 2);
            CancelButton = close;
            layout.Controls.Add(footer, 0, 6);

            _timer.Interval = 1000;
            _timer.Tick += delegate { RefreshReport(); };
            Shown += delegate { RefreshReport(); _timer.Start(); };
            FormClosed += delegate { _timer.Stop(); };
            RefreshReport();
        }

        private static Label TextLabel(string text, float size, Color color)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0), ForeColor = color, Font = UiPalette.Body(size), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }

        private static Control Metric(string title, Label value, Label note)
        {
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(0), BackColor = UiPalette.Surface };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            panel.Controls.Add(TextLabel(title, 9, UiPalette.Muted), 0, 0);
            panel.Controls.Add(value, 0, 1);
            panel.Controls.Add(note, 0, 2);
            return panel;
        }

        private void AddColumn(string name, string heading, float weight, int minimum)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = heading, FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        private void RefreshReport()
        {
            if (IsDisposed || Disposing) return;
            TrafficReport report;
            try { report = _provider(); }
            catch { report = new TrafficReport { Status = "暂时无法读取流量数据，请等待下一次采样。" }; }
            report = report ?? new TrafficReport { Status = "等待连接采样。" };
            RecordTargets(report); _trend.Record(report); RenderReport(report);
        }

        private void RecordTargets(TrafficReport report)
        {
            if (!report.Fresh || report.SnapshotAt == default(DateTime) || report.SnapshotAt == _lastTargetSnapshot) return;
            _lastTargetSnapshot = report.SnapshotAt; DateTime now = DateTime.UtcNow;
            foreach (TargetTraffic target in report.Targets ?? new List<TargetTraffic>())
            {
                if (target == null || String.IsNullOrWhiteSpace(target.Name) || String.IsNullOrEmpty(target.Key)) continue;
                TargetObservation item;
                if (!_observedTargets.TryGetValue(target.Key, out item))
                { item = new TargetObservation { Name = target.Name, Program = target.Program }; _observedTargets[target.Key] = item; }
                item.LastSeen = now;
                if (target.PositiveUpload) item.LastUpload = now;
            }
            foreach (string key in _observedTargets.Where(x => now < x.Value.LastSeen || now - x.Value.LastSeen > TimeSpan.FromMinutes(5)).Select(x => x.Key).ToList()) _observedTargets.Remove(key);
            foreach (string key in _observedTargets.OrderByDescending(x => x.Value.LastSeen).Skip(200).Select(x => x.Key).ToList()) _observedTargets.Remove(key);
        }

        private List<TargetObservation> RecentTargets()
        {
            DateTime now = DateTime.UtcNow;
            return _observedTargets.Values.Where(x => now >= x.LastSeen && now - x.LastSeen <= TimeSpan.FromMinutes(5))
                .OrderByDescending(x => x.LastSeen).Select(x => new TargetObservation { Name = x.Name, Program = x.Program, LastSeen = x.LastSeen, LastUpload = x.LastUpload }).ToList();
        }

        private void RenderReport(TrafficReport report)
        {
            _report = report;
            List<ProgramTraffic> programs = (report.Programs ?? new List<ProgramTraffic>()).Where(x => x != null).ToList();
            bool measured = report.Available && report.Measuring;
            _download.Text = measured ? FormatRate(report.DownloadPerSecond) : "—";
            _upload.Text = measured ? FormatRate(report.UploadPerSecond) : "—";
            _programs.Text = programs.Count.ToString();
            _coverage.Text = "已计速 " + Math.Max(0, report.MeasuredConnectionCount) + " / " + Math.Max(0, report.ConnectionCount) + " 条已观察连接";
            _status.Text = String.IsNullOrWhiteSpace(report.Status) ? (report.Available ? "等待下一次采样后计速。" : "当前连接来源未提供流量计数。") : report.Status;
            _status.ForeColor = measured ? UiPalette.Muted : UiPalette.Warn;
            _tips.SetToolTip(_status, _status.Text);
            _tips.SetToolTip(_coverage, "分母是当前筛选范围内已观察到的 Clash 活动连接，不代表全机覆盖；短连接可能遗漏。只有取得连续计数的连接才可计速。");
            _tips.SetToolTip(_download, measured ? "已计速部分下载速度；不包含当前无法计速的连接。" : "等待有效采样；无法计速时不显示为 0。");
            _tips.SetToolTip(_upload, measured ? "已计速部分上传速度；不包含当前无法计速的连接。" : "等待有效采样；无法计速时不显示为 0。");

            int sort = _sort.SelectedIndex;
            programs = programs.OrderByDescending(x => measured && x.MeasuredConnections > 0)
                .ThenByDescending(x => sort == 1 ? SafeRate(x.UploadPerSecond) : sort == 2 ? SafeRate(x.DownloadPerSecond) + SafeRate(x.UploadPerSecond) : SafeRate(x.DownloadPerSecond))
                .ThenBy(x => x.Name ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Key ?? "", StringComparer.Ordinal).ToList();
            string selectedKey = _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as string;
            string topKey = _grid.FirstDisplayedScrollingRowIndex >= 0 ? _grid.Rows[_grid.FirstDisplayedScrollingRowIndex].Tag as string : null;
            HashSet<string> incoming = new HashSet<string>(StringComparer.Ordinal);
            _updating = true;
            _grid.SuspendLayout();
            try
            {
                int position = 0;
                foreach (ProgramTraffic item in programs)
                {
                    string key = item.Key ?? (item.Name ?? "未知程序") + "\n" + (item.Path ?? "");
                    if (!incoming.Add(key)) continue;
                    DataGridViewRow row;
                    if (!_rows.TryGetValue(key, out row))
                    {
                        row = new DataGridViewRow();
                        row.CreateCells(_grid);
                        row.Tag = key;
                        _rows.Add(key, row);
                    }
                    int old = row.Index;
                    if (old != position)
                    {
                        if (old >= 0) _grid.Rows.Remove(row);
                        _grid.Rows.Insert(position, row);
                        // The collection can share an inserted empty row (Index == -1).
                        // Retrieve it by position before reading or assigning cell values.
                        row = _grid.Rows[position];
                        _rows[key] = row;
                    }
                    bool rowMeasured = measured && item.MeasuredConnections > 0;
                    Put(row, "rank", (position + 1).ToString());
                    Put(row, "program", String.IsNullOrWhiteSpace(item.Name) ? "未识别程序" : item.Name);
                    Put(row, "connections", Math.Max(0, item.MeasuredConnections) + " / " + Math.Max(0, item.Connections));
                    Put(row, "download", rowMeasured ? FormatRate(item.DownloadPerSecond) : "—");
                    Put(row, "upload", rowMeasured ? FormatRate(item.UploadPerSecond) : "—");
                    bool hasCounters = report.Available && item.CounterConnections > 0;
                    Put(row, "bytes", hasCounters ? FormatBytes(item.DownloadBytes) + " / " + FormatBytes(item.UploadBytes) + (item.CounterConnections < item.Connections ? " *" : "") : "—");
                    string path = String.IsNullOrWhiteSpace(item.Path) ? "程序路径：未提供" : item.Path;
                    row.Cells["program"].ToolTipText = (item.Name ?? "未识别程序") + "\r\n" + path;
                    row.Cells["connections"].ToolTipText = "已计速 " + Math.Max(0, item.MeasuredConnections) + " 条 / 当前存活 " + Math.Max(0, item.Connections) + " 条";
                    row.Cells["download"].ToolTipText = rowMeasured ? "已计速部分的下载速度；0 B/s 表示本次样本没有下载增量。" : "当前无法计速，请查看上方采样状态。";
                    row.Cells["upload"].ToolTipText = rowMeasured ? "已计速部分的上传速度；0 B/s 表示本次样本没有上传增量。" : "当前无法计速，请查看上方采样状态。";
                    row.Cells["bytes"].ToolTipText = hasCounters ? "存活连接累计下载 " + FormatBytes(item.DownloadBytes) + " / 上传 " + FormatBytes(item.UploadBytes) + "；结束连接不计入。\r\n已提供累计 " + item.CounterConnections + " / " + item.Connections + " 条连接。" : "当前连接未提供字节计数。";
                    position++;
                }
                foreach (string key in _rows.Keys.Where(x => !incoming.Contains(x)).ToList())
                {
                    DataGridViewRow row = _rows[key];
                    if (row.Index >= 0) _grid.Rows.Remove(row);
                    _rows.Remove(key);
                }
                DataGridViewRow selected;
                if (selectedKey != null && _rows.TryGetValue(selectedKey, out selected))
                {
                    _grid.CurrentCell = selected.Cells["program"];
                    selected.Selected = true;
                }
                DataGridViewRow top;
                if (topKey != null && _rows.TryGetValue(topKey, out top) && top.Index >= 0)
                    _grid.FirstDisplayedScrollingRowIndex = top.Index;
            }
            finally { _grid.ResumeLayout(); _updating = false; }
            _empty.Visible = programs.Count == 0;
            _empty.Text = report.Available ? "当前筛选范围没有活跃程序\r\n可返回主窗口调整搜索或国家筛选" : "当前来源没有可用的流量排行\r\n请在主窗口选择并连接 Clash 原始连接";
            if (_empty.Visible) _empty.BringToFront();
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            string key = _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as string;
            ProgramTraffic program = _report == null || _report.Programs == null ? null : _report.Programs.FirstOrDefault(x => x != null && (x.Key ?? (x.Name ?? "未知程序") + "\n" + (x.Path ?? "")) == key);
            string text = program == null ? "选择程序查看完整路径" : String.IsNullOrWhiteSpace(program.Path) ? (program.Name ?? "未识别程序") + " · 程序路径未提供" : program.Path;
            if (_path.Text != text) _path.Text = text;
            _tips.SetToolTip(_path, text);
        }

        private static void Put(DataGridViewRow row, string column, string text)
        {
            if (!Object.Equals(row.Cells[column].Value, text)) row.Cells[column].Value = text;
        }

        private static double SafeRate(double value)
        {
            return Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 ? 0 : value;
        }

        private static string FormatRate(double value)
        {
            double bytes = SafeRate(value);
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return bytes.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }

        private static string FormatBytes(long value)
        {
            double bytes = Math.Max(0, value);
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return bytes.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _tips.Dispose(); }
            base.Dispose(disposing);
        }

        private sealed class RankingGrid : DataGridView
        {
            public RankingGrid() { DoubleBuffered = true; }
        }
    }
}
