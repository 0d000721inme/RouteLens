using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class PrivacyDnsDialog : Form
    {
        private readonly Label _status;
        private readonly TextBox _details;
        private readonly Button _start, _cancel;
        private CancellationTokenSource _cancellation;

        public PrivacyDnsDialog()
        {
            Text = "主动 DNS 测试"; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Size = new Size(830, 650); MinimumSize = new Size(700, 520); Padding = new Padding(22); MinimizeBox = false;

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(layout);
            Label title = new Label { Text = "主动 DNS 测试", Dock = DockStyle.Fill, ForeColor = UiPalette.Ink, Font = UiPalette.Body(19), Margin = new Padding(0), AutoEllipsis = true };
            layout.Controls.Add(title, 0, 0);
            Label description = new Label { Text = "点击开始后，使用 bash.ws 测试域名和 HTTPS 接口，观察本次请求路径使用的 DNS 解析服务器。检测服务会看到本次请求的公网 IP 和发起查询的解析服务器；不会发送你的浏览历史。\r\n结果只反映本次请求路径，其他应用、浏览器 DoH 和直连规则可能不同。", Dock = DockStyle.Fill,
                ForeColor = UiPalette.Muted, Font = UiPalette.Body(9), Margin = new Padding(0, 0, 0, 5), AutoEllipsis = false };
            layout.Controls.Add(description, 0, 1);
            _status = new Label { Text = "尚未开始 · 点击按钮后才连接检测服务", Dock = DockStyle.Fill, Margin = new Padding(0), ForeColor = UiPalette.Accent, Font = UiPalette.Body(9), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            layout.Controls.Add(_status, 0, 2);
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(15), Radius = 14 };
            _details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Font = UiPalette.Body(10), WordWrap = true, AccessibleName = "本次主动测试的 DNS 解析服务器和出口证据",
                Text = "等待开始测试。\r\n\r\n本测试会显示服务观察到的 DNS 解析服务器及本次请求的出口 IP。\r\n\r\n未观察到解析服务器时，结果为无法判断；地区不同或相同都不能单独证明是否泄露。" };
            card.Controls.Add(_details); layout.Controls.Add(card, 0, 3);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 10, 0, 0), Margin = new Padding(0) };
            _start = new ModernButton { Text = "开始主动测试", Width = 150, Height = 38, Primary = true, Margin = new Padding(0, 0, 10, 0), AccessibleName = "联系 bash.ws 开始主动 DNS 测试" };
            _cancel = new ModernButton { Text = "取消测试", Width = 110, Height = 38, Enabled = false, Margin = new Padding(0, 0, 10, 0) };
            Button close = new ModernButton { Text = "关闭", Width = 88, Height = 38, Margin = new Padding(0), DialogResult = DialogResult.Cancel };
            actions.Controls.Add(_start); actions.Controls.Add(_cancel); actions.Controls.Add(close); layout.Controls.Add(actions, 0, 4); CancelButton = close;
            _start.Click += StartClicked;
            _cancel.Click += delegate { if (_cancellation != null) { _status.Text = "正在取消测试…"; _cancellation.Cancel(); } };
            FormClosing += delegate { if (_cancellation != null) _cancellation.Cancel(); };
            Shown += delegate { _details.SelectionLength = 0; _start.Focus(); };
        }

        private async void StartClicked(object sender, EventArgs e)
        {
            if (_cancellation != null) return;
            CancellationTokenSource running = new CancellationTokenSource();
            _cancellation = running; running.CancelAfter(TimeSpan.FromSeconds(30));
            _start.Enabled = false; _cancel.Enabled = true;
            _details.Text = "正在检测，请稍候…";
            try
            {
                PrivacyDnsReport report = await PrivacyDnsTester.RunAsync(running.Token, SetProgress);
                if (IsDisposed || Disposing) return;
                _details.Text = FormatReport(report);
                _status.Text = report.Cancelled ? "测试已取消或超时" : !String.IsNullOrEmpty(report.Error) ? "测试受限 · 无法判断" : "测试完成 · 请结合解析服务器和代理设置查看";
                _status.ForeColor = report.Cancelled || !String.IsNullOrEmpty(report.Error) ? UiPalette.Warn : UiPalette.Accent;
            }
            finally
            {
                if (Object.ReferenceEquals(_cancellation, running)) _cancellation = null;
                running.Dispose();
                if (!IsDisposed && !Disposing) { _start.Enabled = true; _cancel.Enabled = false; }
            }
        }

        private void SetProgress(string text)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action<string>)SetProgress, text); } catch (InvalidOperationException) { }
                return;
            }
            _status.ForeColor = UiPalette.Accent; _status.Text = text;
        }

        public static string FormatReport(PrivacyDnsReport report)
        {
            if (report == null) return "尚未取得结果，无法判断。";
            StringBuilder text = new StringBuilder();
            text.AppendLine("DNS 解析服务器（测试服务实际观察到）");
            if (report.Resolvers.Count == 0) text.AppendLine("没有可用记录。\r\n");
            else foreach (PrivacyDnsEntry entry in report.Resolvers) text.AppendLine(Describe(entry));
            text.AppendLine(); text.AppendLine("本次 HTTPS 请求出口（测试服务观察到）");
            if (report.Exits.Count == 0) text.AppendLine("未提供。\r\n");
            else foreach (PrivacyDnsEntry entry in report.Exits) text.AppendLine(Describe(entry));
            if (!String.IsNullOrEmpty(report.Error)) { text.AppendLine(); text.AppendLine("当前限制：" + report.Error); }
            text.AppendLine(); text.AppendLine("判断说明");
            text.AppendLine("本次测试无法覆盖所有应用：浏览器或程序可能使用各自的 DoH、独立代理或直连规则。解析服务器与出口地区不同不自动意味着泄露；没有观察到异常也不能保证安全。请结合代理配置及被动 DNS 观察共同判断。");
            text.AppendLine(); text.AppendLine("本测试向 bash.ws 发起随机测试域名与 HTTPS 请求；结果仅在当前窗口内存中显示，不会写入历史日志。关闭窗口会取消正在进行的请求。");
            return text.ToString();
        }

        private static string Describe(PrivacyDnsEntry entry)
        {
            if (entry == null) return "未提供";
            string location = String.Join(" / ", new[] { entry.Country, entry.Region, entry.City, entry.Organization }.WhereNotEmpty());
            return (entry.Address ?? "未提供") + (location.Length == 0 ? "" : "  ·  " + location);
        }
    }

    internal static class PrivacyDnsFormatting
    {
        public static string[] WhereNotEmpty(this string[] values)
        {
            System.Collections.Generic.List<string> result = new System.Collections.Generic.List<string>();
            foreach (string value in values) if (!String.IsNullOrWhiteSpace(value)) result.Add(value);
            return result.ToArray();
        }
    }
}
