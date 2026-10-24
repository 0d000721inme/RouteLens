using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class DnsDetailsDialog : Form
    {
        private readonly DnsLeakMonitor _monitor;
        private readonly Func<DnsLeakStatus> _getStatus;
        private readonly Label _summary;
        private readonly Label _coverage;
        private readonly TextBox _details;
        private readonly Button _startStop;
        private readonly Button _administrator;
        private readonly Timer _timer = new Timer();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        public DnsDetailsDialog(DnsLeakMonitor monitor, Func<DnsLeakStatus> getStatus, Action restartAdministrator,
            Func<bool> notificationsEnabled, Action<bool> setNotificationsEnabled)
        {
            _monitor = monitor; _getStatus = getStatus;
            Text = "DNS 泄露风险监测"; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            BackColor = UiPalette.Surface; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Size = new Size(780, 690); MinimumSize = new Size(720, 500); Padding = new Padding(22);
            MinimizeBox = false;
            TableLayoutPanel heading = new TableLayoutPanel { Dock = DockStyle.Top, Height = 104, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _summary = new Label { Dock = DockStyle.Fill, Margin = new Padding(0),
                Font = UiPalette.Body(16), ForeColor = UiPalette.Ink, AutoEllipsis = true };
            _coverage = new Label { Dock = DockStyle.Fill, Margin = new Padding(0, 1, 0, 7),
                ForeColor = UiPalette.Muted, Font = UiPalette.Body(9), AutoEllipsis = true };
            heading.Controls.Add(_summary, 0, 0); heading.Controls.Add(_coverage, 0, 1);
            _details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None, BackColor = UiPalette.Raised, ForeColor = UiPalette.Ink, Font = UiPalette.Body(9.5f),
                WordWrap = true, AccessibleName = "DNS 监测依据、网卡和本次内存记录" };
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 95 };
            CheckBox notify = new CheckBox { Text = "发现疑似风险时显示系统通知", Checked = notificationsEnabled(),
                Bounds = new Rectangle(0, 8, 310, 30), AutoSize = false, ForeColor = UiPalette.Ink };
            notify.CheckedChanged += delegate { setNotificationsEnabled(notify.Checked); };
            bottom.Controls.Add(notify);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, WrapContents = false };
            _startStop = ActionButton("停止检测", "pause", 108);
            _startStop.Click += delegate
            {
                DnsLeakStatus status = _getStatus();
                if (status.State == "stopped") _monitor.Start(); else _monitor.Stop();
                RefreshStatus();
            };
            Button clear = ActionButton("清空本次记录", "close", 134);
            clear.Click += delegate { _monitor.ClearEvents(); RefreshStatus(); };
            _administrator = ActionButton("以管理员重启检测", "refresh", 180);
            _administrator.Click += delegate { restartAdministrator(); };
            Button close = ActionButton("关闭", "close", 90); close.DialogResult = DialogResult.OK;
            actions.Controls.AddRange(new Control[] { _startStop, clear, _administrator, close }); bottom.Controls.Add(actions);
            Controls.Add(_details); Controls.Add(heading); Controls.Add(bottom); CancelButton = close;
            _timer.Interval = 1000; _timer.Tick += delegate { RefreshStatus(); };
            Shown += delegate { RefreshStatus(); _timer.Start(); };
            FormClosed += delegate { _timer.Stop(); };
            RefreshStatus();
        }

        private static Button ActionButton(string text, string icon, int width)
        {
            return new ModernButton { Text = text, IconName = icon, Width = width, Height = 36,
                Font = UiPalette.Body(9), Margin = new Padding(0, 0, 8, 0) };
        }

        private void RefreshStatus()
        {
            DnsLeakStatus status = _getStatus();
            _summary.Text = status.Title ?? "DNS 监测"; _summary.ForeColor = StatusColor(status.State);
            _coverage.Text = (status.Summary ?? "") + "\r\n" + (status.Coverage ?? "");
            _startStop.Text = status.State == "stopped" ? "开始检测" : "停止检测";
            ((ModernButton)_startStop).IconName = status.State == "stopped" ? "play" : "pause";
            _administrator.Visible = !status.IsAdministrator;
            string text = FormatDetails(status);
            // Let the user select/copy evidence without moving the selection each second.
            if (_details.SelectionLength == 0 && _details.Text != text)
            {
                int cursor = _details.SelectionStart;
                int firstLine = (int)SendMessage(_details.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero);
                _details.Text = text;
                _details.SelectionStart = Math.Min(cursor, _details.TextLength);
                int updatedLine = (int)SendMessage(_details.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero);
                SendMessage(_details.Handle, 0x00B6, IntPtr.Zero, new IntPtr(firstLine - updatedLine));
            }
        }

        public static Color StatusColor(string state)
        {
            if (state == "warning") return Color.FromArgb(167, 62, 49);
            if (state == "limited" || state == "observed") return UiPalette.Warn;
            if (state == "monitoring") return UiPalette.Accent;
            return UiPalette.Muted;
        }

        public static string FormatDetails(DnsLeakStatus status)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("监测依据");
            text.AppendLine(status.Detail ?? "尚未取得监测结果。");
            text.AppendLine(status.ProxyExpected ? "代理依据：系统代理已配置，或近期观察到 Clash 代理连接。" : "代理依据：当前没有取得代理启用依据；普通直连 DNS 不直接判为泄露。");
            text.AppendLine("本功能检查物理接口上的 IPv4 明文 UDP/TCP 53 请求。IPv6、DoH、DoT、分片及跨段 TCP DNS 不在本次覆盖范围内。");
            text.AppendLine("未发现请求不等于没有泄露；Clash 引导解析、节点解析或直连规则可能有意外发。观察到本机 IP 层请求，也不等于远端已收到。");
            if (!String.IsNullOrEmpty(status.Error)) text.AppendLine("当前限制：" + status.Error);
            text.AppendLine();
            text.AppendLine("接口与配置的 DNS（配置本身不是泄露证据）");
            if (status.Adapters == null || status.Adapters.Count == 0) text.AppendLine("尚未取得可用接口信息。");
            else foreach (DnsAdapterStatus adapter in status.Adapters)
            {
                text.AppendLine((adapter.Name ?? "未知接口") + (adapter.IsPhysical ? " · 已识别物理接口" : " · 未纳入物理接口捕获"));
                text.AppendLine("地址：" + (adapter.Addresses == null || adapter.Addresses.Count == 0 ? "未提供" : String.Join("、", adapter.Addresses.ToArray())));
                text.AppendLine("DNS：" + (adapter.DnsServers == null || adapter.DnsServers.Count == 0 ? "未提供" : String.Join("、", adapter.DnsServers.ToArray())));
                text.AppendLine("监测：" + (adapter.CaptureState ?? "未开启"));
                text.AppendLine();
            }
            text.AppendLine("本次观察（仅保存在内存）");
            if (status.Events == null || status.Events.Count == 0) text.AppendLine("尚无明文 DNS 外发记录。");
            else foreach (DnsLeakEvent item in status.Events.OrderByDescending(x => x.LastSeen))
            {
                text.AppendLine(item.LastSeen.ToLocalTime().ToString("HH:mm:ss") + " · " + (item.AdapterName ?? "未知接口") + " · " + item.Transport + " · " + item.Count + " 次");
                text.AppendLine("DNS 服务器：" + item.ResolverAddress + ":" + item.ResolverPort + "  ←  " + item.SourceAddress + ":" + item.SourcePort);
                text.AppendLine("查询域名：" + (String.IsNullOrEmpty(item.QueryName) ? "未提供" : item.QueryName));
                text.AppendLine("发包进程：" + (String.IsNullOrEmpty(item.ProcessName) ? "未知" : item.ProcessName) + (item.ProcessId > 0 ? " · PID " + item.ProcessId : " · PID 未提供"));
                text.AppendLine("观察时的代理依据：" + (item.ProxyExpectedAtObservation ? "系统代理已配置或近期有 Clash 代理活动" : "未取得；此条不判作代理泄露风险"));
                text.AppendLine();
            }
            text.AppendLine("发包进程可能是系统 DNS 服务，不能据此还原最初请求解析的应用。");
            text.AppendLine("监测与列表暂停互相独立；点击“停止检测”才会停止 DNS 监测。通知只显示 DNS 服务器和接口，不包含域名。不会自动修改 DNS、代理或防火墙设置，也不向地图或国家查询服务发送 DNS 记录。");
            return text.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
