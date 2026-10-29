using System;
using System.Drawing;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class ActivityAlertsDialog : Form
    {
        private readonly ActivityAlerts _alerts;
        private readonly CheckBox _enabled, _programs, _countries, _traffic, _upload;
        private readonly NumericUpDown _threshold, _uploadThreshold, _uploadSeconds;
        public ActivityAlertsDialog(ActivityAlerts alerts)
        {
            if (alerts == null) throw new ArgumentNullException("alerts");
            _alerts = alerts; Text = "联网提醒"; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Size = new Size(720, 640); MinimumSize = new Size(650, 610); Padding = new Padding(22); MinimizeBox = false;

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            Controls.Add(layout);
            Label title = new Label { Text = "联网提醒", Dock = DockStyle.Fill, Font = UiPalette.Body(19), ForeColor = UiPalette.Ink, Margin = new Padding(0), AutoEllipsis = true };
            Label caption = new Label { Text = "首次采样用于建立基线，同类提醒最多每 5 分钟一次。流量提醒仅观察 Clash 已计速连接，不代表恶意行为。", Dock = DockStyle.Fill, Font = UiPalette.Body(9), ForeColor = UiPalette.Muted, Margin = new Padding(0), AutoEllipsis = false };
            layout.Controls.Add(title, 0, 0); layout.Controls.Add(caption, 0, 1);

            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), Margin = new Padding(0), Radius = 14 };
            TableLayoutPanel settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Margin = new Padding(0), BackColor = UiPalette.Surface };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _enabled = Check("启用联网提醒", alerts.Enabled); _enabled.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
            _programs = Check("新程序首次联网", alerts.NewProgramsEnabled);
            _countries = Check("新目标地区", alerts.NewCountriesEnabled);
            _traffic = Check("Clash 流量达到阈值", alerts.HighTrafficEnabled);
            settings.Controls.Add(_enabled, 0, 0); settings.Controls.Add(_programs, 0, 1); settings.Controls.Add(_countries, 0, 2); settings.Controls.Add(_traffic, 0, 3);
            FlowLayoutPanel thresholdRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(27, 0, 0, 0), WrapContents = false, BackColor = UiPalette.Surface };
            Label thresholdLabel = new Label { Text = "总速度阈值", Font = UiPalette.Body(9), ForeColor = UiPalette.Muted, Size = new Size(96, 30), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 8, 0) };
            _threshold = new NumericUpDown { Width = 100, Height = 30, Minimum = 1, Maximum = 10240, DecimalPlaces = 1, Increment = 1, Font = UiPalette.Body(9.5f), Margin = new Padding(0, 3, 7, 0), AccessibleName = "总速度提醒阈值，单位 MB 每秒" };
            _threshold.Value = Math.Max(_threshold.Minimum, Math.Min(_threshold.Maximum, (decimal)(alerts.HighTrafficBytesPerSecond / (1024d * 1024))));
            Label unit = new Label { Text = "MB/s", Font = UiPalette.Mono(9), ForeColor = UiPalette.Muted, Size = new Size(60, 30), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 0, 0) };
            thresholdRow.Controls.Add(thresholdLabel); thresholdRow.Controls.Add(_threshold); thresholdRow.Controls.Add(unit); settings.Controls.Add(thresholdRow, 0, 4);
            _upload = Check("持续上传观察", alerts.SustainedUploadEnabled); settings.Controls.Add(_upload, 0, 5);
            FlowLayoutPanel uploadRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(27, 0, 0, 0), WrapContents = false, BackColor = UiPalette.Surface };
            Label uploadLabel = new Label { Text = "上传速度至少", Font = UiPalette.Body(9), ForeColor = UiPalette.Muted, Size = new Size(96, 30), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 8, 0) };
            _uploadThreshold = new NumericUpDown { Width = 75, Height = 30, Minimum = .1m, Maximum = 10240, DecimalPlaces = 1, Increment = .1m, Font = UiPalette.Body(9.5f), Margin = new Padding(0, 3, 7, 0), AccessibleName = "持续上传速度阈值，单位 MiB 每秒" };
            _uploadThreshold.Value = Math.Max(_uploadThreshold.Minimum, Math.Min(_uploadThreshold.Maximum, (decimal)(alerts.SustainedUploadBytesPerSecond / (1024d * 1024))));
            Label uploadUnit = new Label { Text = "MiB/s  持续", Font = UiPalette.Body(9), ForeColor = UiPalette.Muted, Size = new Size(88, 30), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 0, 0) };
            _uploadSeconds = new NumericUpDown { Width = 65, Height = 30, Minimum = 1, Maximum = 3600, Font = UiPalette.Body(9.5f), Margin = new Padding(0, 3, 7, 0), AccessibleName = "持续上传所需秒数" };
            _uploadSeconds.Value = Math.Max(_uploadSeconds.Minimum, Math.Min(_uploadSeconds.Maximum, (decimal)alerts.SustainedUploadSeconds));
            Label secondsUnit = new Label { Text = "秒", Font = UiPalette.Body(9), ForeColor = UiPalette.Muted, Size = new Size(24, 30), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 0, 0) };
            uploadRow.Controls.Add(uploadLabel); uploadRow.Controls.Add(_uploadThreshold); uploadRow.Controls.Add(uploadUnit); uploadRow.Controls.Add(_uploadSeconds); uploadRow.Controls.Add(secondsUnit); settings.Controls.Add(uploadRow, 0, 6);
            card.Controls.Add(settings); layout.Controls.Add(card, 0, 2);
            Label note = new Label { Text = "默认关闭；仅在本次运行中生效，关闭后清除本次基线。系统通知不会包含域名、IP 或程序路径。", Dock = DockStyle.Fill, ForeColor = UiPalette.Muted, Font = UiPalette.Body(8.7f), Margin = new Padding(0, 8, 0, 0) };
            layout.Controls.Add(note, 0, 3);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 8, 0, 0) };
            Button save = new ModernButton { Text = "保存设置", Width = 120, Height = 38, Primary = true, Margin = new Padding(0, 0, 10, 0) };
            Button cancel = new ModernButton { Text = "取消", Width = 90, Height = 38, DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
            save.Click += delegate { Apply(); DialogResult = DialogResult.OK; Close(); };
            actions.Controls.Add(save); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 4); CancelButton = cancel;
            _enabled.CheckedChanged += delegate { SetOptionsEnabled(); }; _traffic.CheckedChanged += delegate { SetOptionsEnabled(); }; _upload.CheckedChanged += delegate { SetOptionsEnabled(); };
            SetOptionsEnabled();
        }

        private static CheckBox Check(string text, bool value)
        { return new ModernCheck { Text = text, Checked = value, Dock = DockStyle.Fill, Margin = new Padding(0), AccessibleName = text }; }
        private void SetOptionsEnabled()
        { _programs.Enabled = _countries.Enabled = _traffic.Enabled = _upload.Enabled = _enabled.Checked; _threshold.Enabled = _enabled.Checked && _traffic.Checked; _uploadThreshold.Enabled = _uploadSeconds.Enabled = _enabled.Checked && _upload.Checked; }
        private void Apply()
        {
            if (!_enabled.Checked) _alerts.Enabled = false;
            _alerts.NewProgramsEnabled = _programs.Checked; _alerts.NewCountriesEnabled = _countries.Checked; _alerts.HighTrafficEnabled = _traffic.Checked;
            _alerts.SustainedUploadEnabled = _upload.Checked; _alerts.SustainedUploadBytesPerSecond = (double)_uploadThreshold.Value * 1024 * 1024; _alerts.SustainedUploadSeconds = (double)_uploadSeconds.Value;
            _alerts.HighTrafficBytesPerSecond = (double)_threshold.Value * 1024 * 1024; _alerts.Enabled = _enabled.Checked;
        }
    }
}
