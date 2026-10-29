using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class ExternalEvidenceDialog : Form
    {
        private readonly DataGridView _grid;
        private readonly Label _status, _empty;
        private readonly Button _open;
        private readonly ToolTip _tips = new ToolTip();
        private bool _busy;

        public ExternalEvidenceDialog()
        {
            Text = "外部连接记录 · RouteLens"; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Size = new Size(1030, 690); MinimumSize = new Size(820, 570); MinimizeBox = false;
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); Padding = new Padding(22, 16, 22, 14);
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 5 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(root);
            root.Controls.Add(Label("外部连接记录", 20, UiPalette.Ink), 0, 0);
            Label info = Label("手动选择本地 Zeek conn.log 的 JSON 行格式。导入内容仅供查看，来源标记为“外部导入、未独立验证”。\r\n不自动关联本机程序，不根据记录推断攻击；关闭窗口后记录清除，不上传或写入历史。", 9, UiPalette.Muted);
            info.AutoEllipsis = false; root.Controls.Add(info, 0, 1);
            _status = Label("尚未导入 · 上限 8 MiB、5,000 条记录", 9, UiPalette.Accent);
            _status.AccessibleName = "外部日志导入状态"; root.Controls.Add(_status, 0, 2);
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(1), Radius = 12 };
            _grid = new ExternalGrid { Dock = DockStyle.Fill, BackgroundColor = UiPalette.Surface, BorderStyle = BorderStyle.None, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false, ColumnHeadersHeight = 38, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                GridColor = Color.FromArgb(238, 241, 243), AccessibleName = "Zeek 外部导入连接记录，未经独立验证" };
            _grid.RowTemplate.Height = 38;
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Raised, ForeColor = UiPalette.Muted, Font = UiPalette.Body(8.8f), SelectionBackColor = UiPalette.Raised, SelectionForeColor = UiPalette.Ink, Padding = new Padding(8, 0, 0, 0) };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink, Font = UiPalette.Body(9), Padding = new Padding(8, 0, 0, 0), NullValue = "未提供" };
            AddColumn("time", "记录时间 · UTC", 25, 180); AddColumn("origin", "发起端", 27, 155); AddColumn("responder", "响应端", 27, 155); AddColumn("protocol", "协议", 10, 70); AddColumn("state", "连接状态", 11, 75);
            _empty = Label("选择本地 Zeek JSON 日志后，在这里查看记录。", 10, UiPalette.Muted);
            _empty.Dock = DockStyle.None; _empty.TextAlign = ContentAlignment.MiddleCenter; _empty.BackColor = UiPalette.Surface;
            _grid.Controls.Add(_empty); _grid.Resize += delegate { _empty.Bounds = new Rectangle(0, _grid.ColumnHeadersHeight, _grid.ClientSize.Width, Math.Max(0, _grid.ClientSize.Height - _grid.ColumnHeadersHeight)); };
            card.Controls.Add(_grid); root.Controls.Add(card, 0, 3);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 9, 0, 0), Margin = new Padding(0) };
            _open = new ModernButton { Text = "选择日志", Primary = true, Width = 130, Height = 38, Margin = new Padding(0, 0, 10, 0), AccessibleName = "选择本地 Zeek JSON 连接日志" };
            Button close = new ModernButton { Text = "关闭", Width = 90, Height = 38, DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
            actions.Controls.Add(_open); actions.Controls.Add(close); root.Controls.Add(actions, 0, 4); CancelButton = close;
            _open.Click += OpenClicked;
        }

        private static Label Label(string text, float size, Color color)
        { return new Label { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0), Font = UiPalette.Body(size), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }; }
        private void AddColumn(string name, string caption, float width, int minimum)
        { _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = caption, FillWeight = width, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable }); }

        private async void OpenClicked(object sender, EventArgs e)
        {
            if (_busy) return;
            string path;
            using (OpenFileDialog chooser = new OpenFileDialog { Title = "选择本地 Zeek 连接日志（JSON 行）", Filter = "JSON 行或日志文件 (*.log;*.json;*.jsonl)|*.log;*.json;*.jsonl|所有文件 (*.*)|*.*", CheckFileExists = true, Multiselect = false, RestoreDirectory = true, DereferenceLinks = false })
            {
                if (chooser.ShowDialog(this) != DialogResult.OK) return;
                path = chooser.FileName;
            }
            _busy = true; _open.Enabled = false; _status.ForeColor = UiPalette.Accent; _status.Text = "正在读取所选文件…";
            try
            {
                ExternalEvidenceReport report = await Task.Run(delegate { return ExternalEvidence.LoadLocalFile(path); });
                if (IsDisposed || Disposing) return;
                ApplyReport(report);
            }
            catch (Exception)
            {
                if (!IsDisposed && !Disposing) ApplyReport(new ExternalEvidenceReport { Error = "读取失败，请检查文件后重试。" });
            }
            finally { _busy = false; if (!IsDisposed && !Disposing) _open.Enabled = true; }
        }

        internal void ApplyReport(ExternalEvidenceReport report)
        {
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                foreach (ExternalConnection row in report.Connections) _grid.Rows.Add(row.Time, row.Origin, row.Responder, row.Protocol, row.State);
            }
            finally { _grid.ResumeLayout(); }
            _empty.Visible = report.Connections.Count == 0;
            _empty.Text = report.Error != null ? report.Error : report.Connections.Count == 0 ? "没有可显示的 Zeek JSON 连接记录。" : "";
            if (_empty.Visible) _empty.BringToFront();
            if (report.Error != null) { _status.ForeColor = UiPalette.Warn; _status.Text = report.Error; _tips.SetToolTip(_status, _status.Text); return; }
            _status.ForeColor = report.Truncated || report.InvalidLines > 0 || report.LongLines > 0 || report.IncompleteRows > 0 ? UiPalette.Warn : UiPalette.Accent;
            _status.Text = "外部导入 · 未独立验证 · " + report.Connections.Count + " 条" +
                (report.Truncated ? " · 达到上限，后续未读取" : "") +
                (report.IncompleteRows > 0 ? " · " + report.IncompleteRows + " 条字段缺失或无效" : "") +
                (report.InvalidLines > 0 ? " · " + report.InvalidLines + " 行格式无效" : "") +
                (report.LongLines > 0 ? " · " + report.LongLines + " 行超长" : "");
            _tips.SetToolTip(_status, _status.Text); _status.AccessibleDescription = _status.Text;
        }
        protected override void Dispose(bool disposing) { if (disposing) _tips.Dispose(); base.Dispose(disposing); }
        private sealed class ExternalGrid : DataGridView { public ExternalGrid() { DoubleBuffered = true; } }
    }
}
