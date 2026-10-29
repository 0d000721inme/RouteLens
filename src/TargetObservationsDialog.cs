using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class TargetObservation
    {
        public string Name, Program;
        public DateTime LastSeen, LastUpload;
    }

    // An ephemeral, bounded view of targets observed while the traffic window is open.
    public sealed class TargetObservationsDialog : Form
    {
        private readonly Func<List<TargetObservation>> _provider;
        private readonly ComboBox _view;
        private readonly DataGridView _grid;
        private readonly Label _status;
        private readonly Timer _timer = new Timer();

        public TargetObservationsDialog(Func<List<TargetObservation>> provider)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            _provider = provider; Text = "目标观察 · RouteLens"; StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = false; Size = new Size(900, 620); MinimumSize = new Size(720, 500);
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); Padding = new Padding(22, 16, 22, 14);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(layout);
            Label title = Label("目标观察", 19, UiPalette.Ink); layout.Controls.Add(title, 0, 0);
            Label note = Label("仅记录窗口打开期间按当时筛选观察到的目标，最多保留 5 分钟和 200 项，关闭后清除。\r\n上传依据 Clash 两次连续计数的正增量；程序为内核提供的分组，短连接可能遗漏。", 9, UiPalette.Muted);
            note.AutoEllipsis = false; layout.Controls.Add(note, 0, 1);
            FlowLayoutPanel choices = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = UiPalette.Canvas };
            _view = new ComboBox { Width = 220, Height = 32, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 16, 0), AccessibleName = "目标观察分类" };
            _view.Items.AddRange(new object[] { "最近活动目标", "最近有上传增量的目标" }); _view.SelectedIndex = 0;
            _status = new Label { AutoSize = false, Width = 350, Height = 34, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiPalette.Muted, Font = UiPalette.Body(9) };
            choices.Controls.Add(_view); choices.Controls.Add(_status); layout.Controls.Add(choices, 0, 2);
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Radius = 12, Padding = new Padding(1), Margin = new Padding(0) };
            _grid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = UiPalette.Surface, BorderStyle = BorderStyle.None, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, ReadOnly = true, MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false, ColumnHeadersHeight = 38, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                GridColor = Color.FromArgb(238, 241, 243), AccessibleName = "最近目标观察列表" };
            _grid.RowTemplate.Height = 38;
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Raised, ForeColor = UiPalette.Muted, Font = UiPalette.Body(8.8f), SelectionBackColor = UiPalette.Raised, SelectionForeColor = UiPalette.Ink, Padding = new Padding(8, 0, 0, 0) };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink, Font = UiPalette.Body(9), Padding = new Padding(8, 0, 0, 0), NullValue = "—" };
            AddColumn("program", "程序", 25, 125); AddColumn("target", "目标", 37, 180); AddColumn("seen", "最近活动", 19, 105); AddColumn("upload", "最近确认上传增量", 19, 120);
            card.Controls.Add(_grid); layout.Controls.Add(card, 0, 3);
            FlowLayoutPanel footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0), Margin = new Padding(0) };
            Button close = new ModernButton { Text = "关闭", Width = 90, Height = 36, DialogResult = DialogResult.OK, Margin = new Padding(0) };
            footer.Controls.Add(close); layout.Controls.Add(footer, 0, 4); CancelButton = close;
            _view.SelectedIndexChanged += delegate { RefreshList(); };
            _timer.Interval = 1000; _timer.Tick += delegate { RefreshList(); };
            Shown += delegate { RefreshList(); _timer.Start(); }; FormClosed += delegate { _timer.Stop(); };
        }
        private static Label Label(string text, float size, Color color)
        { return new Label { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0), ForeColor = color, Font = UiPalette.Body(size), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }; }
        private void AddColumn(string key, string title, float weight, int min)
        { _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, HeaderText = title, FillWeight = weight, MinimumWidth = min, SortMode = DataGridViewColumnSortMode.NotSortable }); }
        private void RefreshList()
        {
            List<TargetObservation> entries;
            try { entries = _provider() ?? new List<TargetObservation>(); }
            catch { entries = new List<TargetObservation>(); }
            DateTime now = DateTime.UtcNow;
            if (_view.SelectedIndex == 1) entries = entries.Where(x => x.LastUpload != default(DateTime) && now >= x.LastUpload && now - x.LastUpload <= TimeSpan.FromMinutes(5)).ToList();
            _status.Text = entries.Count + " 项 · 历史不代表当前仍在上传";
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                foreach (TargetObservation item in entries)
                    _grid.Rows.Add(item.Program ?? "未识别程序", item.Name ?? "目标未提供", TimeText(item.LastSeen), TimeText(item.LastUpload));
            }
            finally { _grid.ResumeLayout(); }
        }
        private static string TimeText(DateTime value) { return value == default(DateTime) ? "未确认" : value.ToLocalTime().ToString("HH:mm:ss"); }
        protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
    }
}
