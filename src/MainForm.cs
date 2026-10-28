using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class MainForm : Form
    {
        private readonly GeoService _geo;
        private readonly ClashConnectionReader _clash = new ClashConnectionReader();
        private readonly DnsLeakMonitor _dns = new DnsLeakMonitor();
        private readonly TrafficTracker _traffic = new TrafficTracker();
        private readonly HistoryStore _history;
        private readonly ActivityAlerts _alerts = new ActivityAlerts();
        private List<ConnectionInfo> _trafficConnections = new List<ConnectionInfo>();
        private MapExplorerSnapshot _mapSnapshot = new MapExplorerSnapshot { Countries = new List<CountryStat>(), Connections = new List<MapConnection>(), SourceUnavailable = true, SourceStatus = "正在读取连接" };
        private readonly Timer _sampleTimer = new Timer();
        private readonly Timer _viewTimer = new Timer();
        private readonly Dictionary<string, DataGridViewRow> _rows = new Dictionary<string, DataGridViewRow>();
        private readonly Dictionary<string, RowData> _rowData = new Dictionary<string, RowData>();
        private readonly ToolTip _tips = new ToolTip();
        private NetworkSnapshot _snapshot;
        private bool _reading, _paused, _closing, _updatingRows;
        private bool _inspectorRequested = true;
        private string _countryCode;
        private int _sourceGeneration;
        private ComboBox _sourcePicker;
        private TextBox _search;
        private CheckBox _publicOnly, _includeUdp, _online;
        private Button _pause, _refresh, _countryFilter, _detailsButton, _copyDetail;
        private Button _dnsStatusButton, _trafficButton;
        private NotifyIcon _dnsNotification, _activityNotification;
        private bool _dnsNotificationsEnabled = true;
        private DateTime _lastDnsNotificationAt = DateTime.MinValue;
        private Label _live, _exitCountry, _exitIp, _exitDetail, _counts, _status, _capture, _proxy, _empty;
        private Label _metricConnections, _metricProcesses, _metricCountries;
        private PictureBox _exitFlag, _detailFlag;
        private Label _detailProcess, _detailMeta, _detailCountry, _detailTarget, _detailLocal, _detailNetwork, _detailState;
        private Label _detailHost, _detailRoute, _detailRule, _detailAttribution, _detailTraffic, _detailPath, _detailInbound;
        private TableLayoutPanel _workspace;
        private TableLayoutPanel _rootLayout;
        private RoundedPanel _inspector;
        private CountryBreakdown _countryChart;
        private WorldMapControl _worldMap;
        private ConnectionGrid _grid;
        private float _dpiScale = 1f;

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, string lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
        [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

        public MainForm(GeoService geo)
        {
            if (geo == null) throw new ArgumentNullException("geo");
            _geo = geo; _history = new HistoryStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
            _alerts.Alert += ShowActivityNotification; Text = "RouteLens · 网络观察";
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(10);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.None; ClientSize = new Size(1380, 900); MinimumSize = new Size(1040, 720);
            StartPosition = FormStartPosition.CenterScreen; DoubleBuffered = true; KeyPreview = true;
            BuildInterface();
            _sampleTimer.Interval = 1000; _sampleTimer.Tick += async delegate { await SampleAsync(); };
            _viewTimer.Interval = 1000; _viewTimer.Tick += delegate { RenderView(); };
            Shown += async delegate
            {
                using (Graphics graphics = CreateGraphics()) _dpiScale = graphics.DpiX / 96f;
                if (StartPosition != FormStartPosition.Manual)
                {
                    Rectangle area = Screen.FromHandle(Handle).WorkingArea;
                    MinimumSize = new Size(Math.Min((int)(1040 * _dpiScale), area.Width), Math.Min((int)(720 * _dpiScale), area.Height));
                    if (Width > area.Width || Height > area.Height)
                        Bounds = new Rectangle(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2), Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                }
                AdaptWorkspace(); _sampleTimer.Start(); _viewTimer.Start(); _geo.RefreshEgress(); _dns.Start(); RenderView(); await SampleAsync();
            };
            Resize += delegate { AdaptWorkspace(); Invalidate(); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { _search.Clear(); ClearCountry(); e.SuppressKeyPress = true; }
            };
        }

        private void BuildInterface()
        {
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
                Padding = new Padding(26, 0, 26, 12), Margin = new Padding(0), BackColor = UiPalette.Canvas };
            _rootLayout = root;
            foreach (int height in new[] { 58, 58, 300, 56, 42 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            Controls.Add(root); root.Controls.Add(BuildTitleBar(), 0, 0);

            TableLayoutPanel intro = Table(2);
            intro.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); intro.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 296));
            Panel titles = new Panel { Dock = DockStyle.Fill };
            Label title = LabelAt("网络概览", 21, FontStyle.Bold, UiPalette.Ink); title.SetBounds(0, 2, 400, 35);
            Label subtitle = LabelAt("查看程序访问的网站、分流规则与实际代理链", 9.5f, FontStyle.Regular, UiPalette.Muted); subtitle.SetBounds(1, 36, 530, 20);
            titles.Controls.AddRange(new Control[] { title, subtitle }); intro.Controls.Add(titles, 0, 0);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
            _pause = ButtonAt("暂停采样", "pause", 116, 38, false); _pause.Click += PauseClicked;
            _live = LabelAt("实时监测", 10, FontStyle.Regular, UiPalette.Accent); _live.TextAlign = ContentAlignment.MiddleRight; _live.Size = new Size(142, 38); _live.Margin = new Padding(0, 0, 16, 0);
            actions.Controls.Add(_pause); actions.Controls.Add(_live); intro.Controls.Add(actions, 1, 0); root.Controls.Add(intro, 0, 1);

            TableLayoutPanel overview = Table(2); overview.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 304)); overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            RoundedPanel exit = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 9, 0) };
            Label exitTitle = LabelAt("当前出口", 10, FontStyle.Bold, UiPalette.Ink); exitTitle.SetBounds(20, 13, 84, 24);
            Label exitContext = LabelAt("本软件请求", 8, FontStyle.Regular, UiPalette.Muted); exitContext.SetBounds(105, 15, 110, 21);
            _refresh = ButtonAt("", "refresh", 36, 34, false); _refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right; _refresh.Location = new Point(exit.Width - 56, 12); _refresh.AccessibleName = "重新探测出口";
            _refresh.Click += delegate { _geo.RefreshEgress(); RenderEgress(); }; _tips.SetToolTip(_refresh, "重新探测本软件的公网出口，自动每30秒探测。");
            _exitFlag = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(21, 49, 34, 24), AccessibleName = "出口国家国旗" };
            _exitCountry = LabelAt("正在探测", 20, FontStyle.Bold, UiPalette.Ink); _exitCountry.SetBounds(66, 40, 207, 39);
            _exitIp = LabelAt("等待公网 IP", 11, FontStyle.Regular, UiPalette.Ink); _exitIp.Font = UiPalette.Mono(11); _exitIp.SetBounds(22, 81, 252, 23);
            _exitDetail = LabelAt("正在发起公网探测请求", 8, FontStyle.Regular, UiPalette.Muted); _exitDetail.SetBounds(21, 107, 252, 21);
            _exitCountry.AutoEllipsis = true; _exitIp.AutoEllipsis = true; _exitDetail.AutoEllipsis = true;
            exit.Controls.AddRange(new Control[] { exitTitle, exitContext, _refresh, _exitFlag, _exitCountry, _exitIp, _exitDetail });
            TableLayoutPanel metrics = Table(3); metrics.Dock = DockStyle.None; metrics.SetBounds(20, 140, 255, 54);
            for (int i = 0; i < 3; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            _metricConnections = Metric(metrics, 0, "连接"); _metricProcesses = Metric(metrics, 1, "程序"); _metricCountries = Metric(metrics, 2, "目标国家");
            _countryChart = new CountryBreakdown { Bounds = new Rectangle(14, 188, 267, 108), Margin = new Padding(0) };
            _countryChart.CountrySelected += delegate(string code) { SelectCountry(code); };
            exit.Controls.Add(metrics); exit.Controls.Add(_countryChart);
            exit.Resize += delegate
            {
                _refresh.Left = exit.Width - (int)(56 * _dpiScale);
                _exitCountry.Width = Math.Max(80, exit.Width - _exitCountry.Left - (int)(24 * _dpiScale));
                _exitIp.Width = Math.Max(80, exit.Width - _exitIp.Left - (int)(24 * _dpiScale));
                _exitDetail.Width = Math.Max(80, exit.Width - (int)(44 * _dpiScale));
                bool showRanking = exit.Height / _dpiScale >= 280;
                metrics.SetBounds((int)(20 * _dpiScale), (int)((showRanking ? 129 : 140) * _dpiScale), Math.Max(80, exit.Width - (int)(40 * _dpiScale)), (int)(54 * _dpiScale));
                _countryChart.Visible = showRanking;
                _countryChart.SetBounds((int)(14 * _dpiScale), (int)(188 * _dpiScale), Math.Max(80, exit.Width - (int)(28 * _dpiScale)), Math.Max(1, exit.Height - (int)(194 * _dpiScale)));
            };
            overview.Controls.Add(exit, 0, 0);
            RoundedPanel mapCard = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(9, 0, 0, 0), Padding = new Padding(12, 1, 12, 1) };
            _worldMap = new WorldMapControl { Dock = DockStyle.Fill, Margin = new Padding(0), AccessibleName = "全球目标分布地图" };
            _worldMap.CountrySelected += delegate(string code) { SelectCountry(code); };
            _worldMap.ExpandRequested += delegate { ShowMapExplorer(); };
            mapCard.Controls.Add(_worldMap); overview.Controls.Add(mapCard, 1, 0); root.Controls.Add(overview, 0, 2);

            TableLayoutPanel toolbar = Table(4); toolbar.Padding = new Padding(0, 14, 0, 6);
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 316)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            FlowLayoutPanel tableHeading = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            Label connectionsTitle = LabelAt("连接列表", 13, FontStyle.Bold, UiPalette.Ink); connectionsTitle.Size = new Size(104, 30);
            _counts = LabelAt("正在读取", 9, FontStyle.Regular, UiPalette.Muted); _counts.Size = new Size(190, 30); _counts.TextAlign = ContentAlignment.MiddleLeft;
            tableHeading.Controls.Add(connectionsTitle); tableHeading.Controls.Add(_counts); toolbar.Controls.Add(tableHeading, 0, 0);
            _dnsStatusButton = ButtonAt("DNS · 正在检查", "globe", 157, 36, false); _dnsStatusButton.Dock = DockStyle.Fill;
            _dnsStatusButton.Margin = new Padding(0, 0, 15, 0); _dnsStatusButton.AccessibleName = "DNS 泄露风险状态，点击查看监测详情";
            _dnsStatusButton.Click += delegate { ShowDnsDetails(); }; toolbar.Controls.Add(_dnsStatusButton, 1, 0);
            RoundedPanel searchPanel = new RoundedPanel { Dock = DockStyle.Fill, Radius = 8, Padding = new Padding(34, 9, 12, 7), Margin = new Padding(0, 0, 15, 0) };
            searchPanel.Paint += delegate(object sender, PaintEventArgs e) { UiPaint.DrawIcon(e.Graphics, "search", new Rectangle((int)(11 * _dpiScale), (int)(12 * _dpiScale), (int)(16 * _dpiScale), (int)(16 * _dpiScale)), UiPalette.Muted, 1.6f); };
            _search = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = UiPalette.Ink, Font = UiPalette.Body(10), AccessibleName = "搜索程序、网站域名、IP、代理链或规则" };
            _search.HandleCreated += delegate { SendMessage(_search.Handle, 0x1501, new IntPtr(1), "搜索程序、网站、节点或规则"); };
            _search.TextChanged += delegate { RenderConnections(); }; searchPanel.Controls.Add(_search); toolbar.Controls.Add(searchPanel, 2, 0);
            _tips.SetToolTip(_search, "搜索程序、域名、IP、国家、代理链、规则或 PID；Ctrl+F 聚焦，Esc 清空");
            _online = new ModernCheck { Text = "联网查国家", Checked = true, Width = 143, Height = 36, Location = new Point(0, 1), Margin = new Padding(0) };
            _online.CheckedChanged += delegate { _geo.Enabled = _online.Checked; RenderView(); }; _tips.SetToolTip(_online, "只将公网IP发给国家查询服务，关闭后保留已有缓存。");
            Panel onlineHolder = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }; onlineHolder.Controls.Add(_online);
            toolbar.Controls.Add(onlineHolder, 3, 0); root.Controls.Add(toolbar, 0, 3);

            TableLayoutPanel filters = Table(2); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            FlowLayoutPanel filterLeft = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            Label sourceLabel = LabelAt("数据源", 9, FontStyle.Regular, UiPalette.Muted); sourceLabel.Size = new Size(52, 32); sourceLabel.TextAlign = ContentAlignment.MiddleLeft;
            _sourcePicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = UiPalette.Ink, Font = UiPalette.Body(9.5f), Width = 190, Margin = new Padding(0, 3, 18, 0), AccessibleName = "连接数据源" };
            _sourcePicker.Items.AddRange(new object[] { "Clash 原始连接", "Windows 系统连接" }); _sourcePicker.SelectedIndex = 0;
            _tips.SetToolTip(_sourcePicker, "Clash 原始连接显示程序、网站和分流；Windows 系统连接显示电脑的外层连接，不包含代理内部的网站信息。");
            _publicOnly = new ModernCheck { Text = "仅公网目标", Checked = true, Width = 130, Height = 32, Margin = new Padding(0, 0, 8, 0) };
            _includeUdp = new ModernCheck { Text = "UDP 连接", Checked = true, Width = 112, Height = 32, Margin = new Padding(0, 0, 10, 0) };
            _publicOnly.CheckedChanged += delegate { RenderConnections(); }; _includeUdp.CheckedChanged += delegate { RenderConnections(); };
            _tips.SetToolTip(_publicOnly, "隐藏没有域名的非公网目标；Clash 已提供域名但未提供实际 IP 的连接仍保留。Windows UDP 本地端点由 UDP 开关控制。"); _tips.SetToolTip(_includeUdp, "Clash UDP 可显示真实目标与域名；Windows UDP 只能显示本地端点。");
            _countryFilter = ButtonAt("清除国家筛选", "close", 160, 32, false); _countryFilter.Visible = false; _countryFilter.Click += delegate { ClearCountry(); };
            filterLeft.Controls.AddRange(new Control[] { sourceLabel, _sourcePicker, _publicOnly, _includeUdp, _countryFilter }); filters.Controls.Add(filterLeft, 0, 0);
            _sourcePicker.SelectedIndexChanged += async delegate { await ChangeSourceAsync(); };
            _detailsButton = ButtonAt("连接详情", "filter", 110, 32, false); _detailsButton.Click += delegate { if (ClientSize.Width / _dpiScale < 1280) ShowConnectionDetails(); else { _inspectorRequested = !_inspectorRequested; AdaptWorkspace(); } };
            filters.Controls.Add(_detailsButton, 1, 0); root.Controls.Add(filters, 0, 4);
            _workspace = Table(2); _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 294));
            RoundedPanel gridArea = new RoundedPanel { Dock = DockStyle.Fill, Radius = 18, Padding = new Padding(1), Margin = new Padding(0) };
            BuildGrid(); gridArea.Controls.Add(_grid);
            _empty = LabelAt("正在读取系统连接…", 12, FontStyle.Regular, UiPalette.Muted); _empty.BackColor = Color.White; _empty.TextAlign = ContentAlignment.MiddleCenter;
            gridArea.Controls.Add(_empty); _empty.BringToFront(); gridArea.Resize += delegate { _empty.Bounds = new Rectangle(20, Math.Max(50, gridArea.Height / 2 - 38), Math.Max(80, gridArea.Width - 40), 90); };
            _workspace.Controls.Add(gridArea, 0, 0); _inspector = BuildInspector(); _workspace.Controls.Add(_inspector, 1, 0); root.Controls.Add(_workspace, 0, 5);
            TableLayoutPanel statusRow = Table(2); statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
            _status = LabelAt("读取系统连接中", 9, FontStyle.Regular, UiPalette.Muted); _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft; _status.AutoEllipsis = true;
            _capture = LabelAt("每 2 秒采样", 9, FontStyle.Regular, UiPalette.Muted); _capture.Dock = DockStyle.Fill; _capture.TextAlign = ContentAlignment.MiddleRight;
            statusRow.Controls.Add(_status, 0, 0); statusRow.Controls.Add(_capture, 1, 0); root.Controls.Add(statusRow, 0, 6);
            TableLayoutPanel footer = Table(2); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            Label notice = LabelAt("国家为目标IP归属；域名与分流取自Clash，不含HTTPS页面路径。", 8.5f, FontStyle.Regular, UiPalette.Muted); notice.Dock = DockStyle.Fill; notice.TextAlign = ContentAlignment.MiddleLeft;
            _proxy = LabelAt("系统代理：读取中", 8.5f, FontStyle.Regular, UiPalette.Muted); _proxy.Dock = DockStyle.Fill; _proxy.TextAlign = ContentAlignment.MiddleRight; _proxy.AutoEllipsis = true;
            footer.Controls.Add(notice, 0, 0); footer.Controls.Add(_proxy, 1, 0); root.Controls.Add(footer, 0, 7); AdaptWorkspace();
        }

        private Control BuildTitleBar()
        {
            TableLayoutPanel bar = Table(3); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            Panel brand = new Panel { Dock = DockStyle.Fill };
            BrandMark mark = new BrandMark { Bounds = new Rectangle(0, 13, 31, 31) };
            Label name = LabelAt("RouteLens", 13, FontStyle.Bold, UiPalette.Ink); name.Font = UiPalette.Heading(13); name.SetBounds(42, 15, 108, 29);
            Label descriptor = LabelAt("网络观察", 9, FontStyle.Regular, UiPalette.Muted); descriptor.SetBounds(156, 19, 64, 23);
            brand.Controls.AddRange(new Control[] { mark, name, descriptor });
            MouseEventHandler drag = delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
            brand.MouseDown += drag; mark.MouseDown += drag; name.MouseDown += drag; descriptor.MouseDown += drag;
            brand.DoubleClick += delegate { ToggleMaximize(); }; name.DoubleClick += delegate { ToggleMaximize(); };
            FlowLayoutPanel window = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
            ModernButton min = (ModernButton)ButtonAt("", "minus", 40, 36, false); min.Quiet = true; min.AccessibleName = "最小化"; min.Click += delegate { WindowState = FormWindowState.Minimized; };
            ModernButton max = (ModernButton)ButtonAt("", "maximize", 40, 36, false); max.Quiet = true; max.AccessibleName = "最大化或还原"; max.Click += delegate { ToggleMaximize(); };
            ModernButton close = (ModernButton)ButtonAt("", "close", 40, 36, false); close.Quiet = true; close.AccessibleName = "关闭"; close.Click += delegate { Close(); };
            Panel navHolder = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            RoundedPanel navSurface = new RoundedPanel { Width = 466, Height = 42, Radius = 15, SurfaceColor = UiPalette.Raised, BorderColor = UiPalette.Raised, Padding = new Padding(4), Margin = new Padding(0) };
            FlowLayoutPanel nav = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = UiPalette.Raised, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            ModernButton connections = NavButton("连接", 58, false); connections.Click += delegate { if (_grid != null) _grid.Focus(); };
            _trafficButton = NavButton("实时流量", 68, true); _trafficButton.Click += delegate { ShowTrafficRanking(); };
            ModernButton history = NavButton("历史流量", 68, true); history.Click += delegate { ShowHistory(); };
            ModernButton map = NavButton("世界地图", 68, true); map.Click += delegate { ShowMapExplorer(); };
            ModernButton privacy = NavButton("隐私检测", 68, true); privacy.Click += delegate { ShowPrivacyCheck(); };
            ModernButton offline = NavButton("离线地区", 68, true); offline.Click += delegate { ShowOfflineGeo(); };
            ModernButton alerts = NavButton("提醒", 58, true); alerts.Click += delegate { ShowActivitySettings(); };
            _tips.SetToolTip(_trafficButton, "查看按程序汇总的实时下载和上传速度");
            _tips.SetToolTip(history, "按程序、域名和节点查看本机流量历史");
            _tips.SetToolTip(privacy, "打开主动 DNS 检测；未点击开始时不会发起测试");
            _tips.SetToolTip(offline, "管理本地 IP 地理位置数据库");
            nav.Controls.AddRange(new Control[] { connections, _trafficButton, history, map, privacy, offline, alerts }); navSurface.Controls.Add(nav); navHolder.Controls.Add(navSurface);
            navHolder.Resize += delegate { navSurface.Location = new Point(Math.Max(0, (navHolder.Width - navSurface.Width) / 2), Math.Max(0, (navHolder.Height - navSurface.Height) / 2)); };
            window.Controls.AddRange(new Control[] { min, max, close }); bar.Controls.Add(brand, 0, 0); bar.Controls.Add(navHolder, 1, 0); bar.Controls.Add(window, 2, 0); return bar;
        }
        private static ModernButton NavButton(string text, int width, bool quiet)
        { return new ModernButton { Text = text, Width = width, Height = 34, Quiet = quiet, CornerRadius = 11, Margin = new Padding(0), Padding = new Padding(4, 0, 4, 0), Font = UiPalette.Body(9), AccessibleName = text }; }
        private void ToggleMaximize() { MaximizedBounds = Screen.FromHandle(Handle).WorkingArea; WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }

        private void BuildGrid()
        {
            _grid = new ConnectionGrid { Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, ReadOnly = true, MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false, AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ColumnHeadersHeight = 42,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                GridColor = Color.FromArgb(238, 241, 243), EnableHeadersVisualStyles = false };
            _grid.RowTemplate.Height = 43; _grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Raised, ForeColor = UiPalette.Muted, Font = UiPalette.Body(9), SelectionBackColor = UiPalette.Raised, SelectionForeColor = UiPalette.Ink, Padding = new Padding(12, 0, 0, 0) };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = UiPalette.Ink, SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink, Font = UiPalette.Body(10), Padding = new Padding(12, 0, 0, 0), NullValue = "—" };
            AddTextColumn("process", "程序", 17, 126); AddTextColumn("pid", "PID", 7, 70); _grid.Columns["pid"].Visible = false;
            AddTextColumn("host", "网站域名", 27, 180); _grid.Columns["host"].DefaultCellStyle.Font = new Font("Segoe UI", 10);
            _grid.Columns["host"].ToolTipText = "Clash 连接元数据中的目标域名；HTTPS 页面路径不可见。";
            AddTextColumn("ip", "目标 IP", 19, 145); _grid.Columns["ip"].DefaultCellStyle.Font = UiPalette.Mono(9.5f);
            AddTextColumn("port", "端口", 8, 60); _grid.Columns["port"].DefaultCellStyle.Font = UiPalette.Mono(9.5f); _grid.Columns["port"].Visible = false;
            _grid.Columns.Add(new DataGridViewImageColumn { Name = "flag", Visible = false, ImageLayout = DataGridViewImageCellLayout.Zoom }); _grid.Columns["flag"].DefaultCellStyle.NullValue = null;
            AddTextColumn("country", "目标国家", 15, 120); AddTextColumn("org", "网络 / 运营商", 23, 130); _grid.Columns["org"].Visible = false;
            AddTextColumn("route", "走向", 8, 68); AddTextColumn("chain", "实际代理链", 22, 190);
            _grid.Columns["chain"].ToolTipText = "Clash 实际连接返回的组和最终节点，直连显示 DIRECT；不能从全局出口推断这条链。";
            AddTextColumn("protocol", "协议", 6, 52); _grid.Columns["protocol"].DefaultCellStyle.Font = UiPalette.Mono(9);
            AddTextColumn("state", "状态", 13, 87); _grid.Columns["state"].Visible = false;
            _grid.CellPainting += PaintGridCell; _grid.SelectionChanged += delegate { if (!_updatingRows) RenderInspector(); };
            _grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            ContextMenuStrip menu = new ContextMenuStrip(); menu.Items.Add("复制目标 IP", null, delegate { CopySelectedIp(); }); menu.Items.Add("复制连接信息", null, delegate { CopySelectedConnection(); }); _grid.ContextMenuStrip = menu;
            _grid.CellMouseDown += delegate(object sender, DataGridViewCellMouseEventArgs e) { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { _grid.ClearSelection(); _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0]; _grid.Rows[e.RowIndex].Selected = true; } };
            _grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) ShowConnectionDetails(); };
            _grid.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.C) { CopySelectedConnection(); e.SuppressKeyPress = true; } };
        }

        private RoundedPanel BuildInspector()
        {
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Radius = 18, Margin = new Padding(14, 0, 0, 0), AutoScroll = true, AutoScrollMinSize = new Size(0, 842) };
            Label heading = LabelAt("连接详情", 11, FontStyle.Bold, UiPalette.Ink); heading.SetBounds(19, 16, 210, 25);
            _detailProcess = LabelAt("选择一条连接", 13, FontStyle.Bold, UiPalette.Ink); _detailProcess.SetBounds(19, 48, 238, 29); _detailProcess.AutoEllipsis = true;
            _detailMeta = LabelAt("查看程序、网站与分流", 9, FontStyle.Regular, UiPalette.Muted); _detailMeta.SetBounds(20, 80, 238, 23);
            Label hostLabel = LabelAt("网站域名", 8.5f, FontStyle.Regular, UiPalette.Muted); hostLabel.SetBounds(20, 110, 230, 20);
            _detailHost = LabelAt("—", 11, FontStyle.Bold, UiPalette.Ink); _detailHost.SetBounds(20, 132, 238, 45); _detailHost.AutoEllipsis = true;
            _detailFlag = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(20, 187, 30, 20) };
            _detailCountry = LabelAt("尚未选择", 11, FontStyle.Bold, UiPalette.Ink); _detailCountry.SetBounds(60, 181, 196, 31);
            Label targetLabel = LabelAt("目标地址", 8.5f, FontStyle.Regular, UiPalette.Muted); targetLabel.SetBounds(20, 220, 230, 20);
            _detailTarget = LabelAt("—", 9.5f, FontStyle.Regular, UiPalette.Ink); _detailTarget.Font = UiPalette.Mono(9.5f); _detailTarget.SetBounds(20, 242, 238, 36);
            Label routeLabel = LabelAt("走向与实际代理链", 8.5f, FontStyle.Regular, UiPalette.Muted); routeLabel.SetBounds(20, 288, 230, 20);
            _detailRoute = LabelAt("—", 10, FontStyle.Regular, UiPalette.Ink); _detailRoute.SetBounds(20, 310, 238, 58); _detailRoute.AutoEllipsis = true;
            Label ruleLabel = LabelAt("命中规则", 8.5f, FontStyle.Regular, UiPalette.Muted); ruleLabel.SetBounds(20, 378, 230, 20);
            _detailRule = LabelAt("—", 9, FontStyle.Regular, UiPalette.Ink); _detailRule.SetBounds(20, 400, 238, 40); _detailRule.AutoEllipsis = true;
            Label attributionLabel = LabelAt("程序归因", 8.5f, FontStyle.Regular, UiPalette.Muted); attributionLabel.SetBounds(20, 450, 230, 20);
            _detailAttribution = LabelAt("—", 9, FontStyle.Regular, UiPalette.Muted); _detailAttribution.SetBounds(20, 472, 238, 40); _detailAttribution.AutoEllipsis = true;
            Label trafficLabel = LabelAt("这条连接的累计流量", 8.5f, FontStyle.Regular, UiPalette.Muted); trafficLabel.SetBounds(20, 522, 230, 20);
            _detailTraffic = LabelAt("—", 9.5f, FontStyle.Regular, UiPalette.Ink); _detailTraffic.Font = UiPalette.Mono(9.5f); _detailTraffic.SetBounds(20, 544, 238, 24);
            Label localLabel = LabelAt("本地地址 / 入站", 8.5f, FontStyle.Regular, UiPalette.Muted); localLabel.SetBounds(20, 578, 230, 20);
            _detailLocal = LabelAt("—", 9, FontStyle.Regular, UiPalette.Ink); _detailLocal.Font = UiPalette.Mono(9); _detailLocal.SetBounds(20, 600, 238, 35);
            _detailInbound = LabelAt("", 9, FontStyle.Regular, UiPalette.Muted); _detailInbound.SetBounds(20, 638, 238, 23); _detailInbound.AutoEllipsis = true;
            _detailPath = LabelAt("", 8.5f, FontStyle.Regular, UiPalette.Muted); _detailPath.SetBounds(20, 670, 238, 46); _detailPath.AutoEllipsis = true;
            _detailNetwork = LabelAt("", 8.5f, FontStyle.Regular, UiPalette.Muted); _detailNetwork.SetBounds(20, 722, 238, 30); _detailNetwork.AutoEllipsis = true;
            _detailState = LabelAt("", 9, FontStyle.Regular, UiPalette.Accent); _detailState.SetBounds(20, 758, 240, 22);
            _copyDetail = ButtonAt("复制连接信息", "copy", 238, 35, false); _copyDetail.Location = new Point(20, 796); _copyDetail.Enabled = false; _copyDetail.Click += delegate { CopySelectedConnection(); };
            card.Controls.AddRange(new Control[] { heading, _detailProcess, _detailMeta, hostLabel, _detailHost, _detailFlag, _detailCountry, targetLabel, _detailTarget, routeLabel, _detailRoute, ruleLabel, _detailRule, attributionLabel, _detailAttribution, trafficLabel, _detailTraffic, localLabel, _detailLocal, _detailInbound, _detailPath, _detailNetwork, _detailState, _copyDetail }); return card;
        }

        private void AdaptWorkspace()
        {
            if (_workspace == null || _inspector == null || _grid == null) return;
            if (_rootLayout != null)
            {
                float logicalHeight = ClientSize.Height / _dpiScale;
                float overviewHeight = logicalHeight >= 840 ? 300 : logicalHeight >= 760 ? 260 : 216;
                float pixels = overviewHeight * _dpiScale;
                if (Math.Abs(_rootLayout.RowStyles[2].Height - pixels) > 0.1f) _rootLayout.RowStyles[2].Height = pixels;
            }
            float logicalWidth = ClientSize.Width / _dpiScale; bool show = _inspectorRequested && logicalWidth >= 1280;
            _workspace.ColumnStyles[1].Width = show ? 294 * _dpiScale : 0; _inspector.Visible = show; _detailsButton.Enabled = true;
            _tips.SetToolTip(_detailsButton, logicalWidth < 1280 ? "打开所选连接的完整信息，双击列表行也可查看。" : "展开或收起所选连接详情；双击列表行可打开完整信息。");
        }
        private static TableLayoutPanel Table(int columns) { TableLayoutPanel table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent }; table.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); return table; }
        private static Label LabelAt(string text, float size, FontStyle style, Color color) { return new Label { Text = text, ForeColor = color, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", size, style), Margin = new Padding(0), UseMnemonic = false }; }
        private static Button ButtonAt(string text, string icon, int width, int height, bool primary) { return new ModernButton { Text = text, IconName = icon, Width = width, Height = height, Primary = primary, Margin = new Padding(0), Font = UiPalette.Body(9.5f), Cursor = Cursors.Hand }; }
        private static Label Metric(TableLayoutPanel parent, int column, string text)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }; Label value = LabelAt("—", 20, FontStyle.Bold, UiPalette.Ink); value.Font = UiPalette.Heading(20); value.SetBounds(0, 0, 88, 31); value.AutoEllipsis = true;
            Label caption = LabelAt(text, 8.5f, FontStyle.Regular, UiPalette.Muted); caption.SetBounds(1, 32, 88, 20); panel.Controls.Add(value); panel.Controls.Add(caption); parent.Controls.Add(panel, column, 0); return value;
        }
        private void AddTextColumn(string name, string heading, float weight, int minimum) { _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = heading, FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.Automatic }); }
        private async Task SampleAsync()
        {
            if (_closing || _paused || _reading) return; _reading = true;
            int generation = _sourceGeneration; bool clash = UseClash;
            try
            {
                NetworkSnapshot next = await Task.Run(delegate
                {
                    NetworkSnapshot windows = ConnectionReader.Read();
                    if (clash)
                    {
                        NetworkSnapshot core = _clash.Read(windows);
                        _dns.UpdateContext(windows, core.ClashConnected && core.Connections != null && core.Connections.Any(x => x.RouteType == "代理" || String.Equals(x.RouteType, "PROXY", StringComparison.OrdinalIgnoreCase)));
                        return core;
                    }
                    _dns.UpdateContext(windows, false);
                    windows.SourceName = "Windows 系统连接";
                    windows.SourceStatus = "系统外层连接，不提供代理内的网站、节点链或命中规则";
                    windows.ClashConnected = false; return windows;
                });
                if (!_closing && !_paused && generation == _sourceGeneration) { AcceptSnapshot(next); RenderView(); }
            }
            catch (Exception ex)
            {
                if (!_closing && !_paused && generation == _sourceGeneration)
                {
                    _snapshot = new NetworkSnapshot { Connections = new List<ConnectionInfo>(), CapturedAt = DateTime.Now, Error = ex.Message, SourceName = clash ? "Clash 原始连接" : "Windows 系统连接", SourceStatus = "读取失败：" + ex.Message };
                    _traffic.Update(_snapshot);
                    _history.ResetBaseline();
                    _alerts.ResetBaseline();
                    RenderView();
                }
            }
            finally { _reading = false; }
            if (!_closing && !_paused && generation != _sourceGeneration) await SampleAsync();
        }
        private bool UseClash { get { return _sourcePicker == null || _sourcePicker.SelectedIndex == 0; } }
        private async Task ChangeSourceAsync()
        {
            _sourceGeneration++; _snapshot = null; _countryCode = null; _traffic.Reset(); _history.ResetBaseline(); _alerts.ResetBaseline();
            _sampleTimer.Interval = UseClash ? 1000 : 2000;
            _includeUdp.Text = UseClash ? "UDP 连接" : "UDP 端点"; _includeUdp.Checked = UseClash;
            RenderView();
            if (!_paused) await SampleAsync();
        }
        private async void PauseClicked(object sender, EventArgs e)
        {
            _paused = !_paused; _sourceGeneration++; _traffic.Reset(); _history.ResetBaseline(); _alerts.ResetBaseline(); _pause.Text = _paused ? "继续采样" : "暂停采样"; ((ModernButton)_pause).IconName = _paused ? "play" : "pause";
            if (_paused && _snapshot != null) _traffic.Update(_snapshot);
            _live.Text = _paused ? "采样已暂停" : "实时监测"; _live.ForeColor = _paused ? UiPalette.Muted : UiPalette.Accent;
            if (_paused) _sampleTimer.Stop(); else { _sampleTimer.Start(); await SampleAsync(); } RenderView();
        }
        private void ClearCountry() { _countryCode = null; if (_countryChart != null) _countryChart.SelectedCode = null; RenderConnections(); }
        private void SelectCountry(string code) { _countryCode = String.IsNullOrEmpty(code) || String.Equals(_countryCode, code, StringComparison.OrdinalIgnoreCase) ? null : code; RenderConnections(); }
        private void RenderView()
        {
            if (_closing) return; RenderEgress(); RenderConnections(); RenderDnsStatus(_dns.GetStatus()); _status.ForeColor = UiPalette.Muted;
            if (_snapshot != null && !String.IsNullOrEmpty(_snapshot.Error)) { _status.Text = "连接读取提示：" + _snapshot.Error; _status.ForeColor = UiPalette.Warn; }
            else if (_snapshot != null && UseClash && !_snapshot.ClashConnected) { _status.Text = _snapshot.SourceStatus ?? "尚未连接 Clash 控制器"; _status.ForeColor = UiPalette.Warn; }
            else _status.Text = (_paused ? "采样已暂停" : (UseClash ? "每1秒采样" : "每2秒采样")) + (_snapshot == null ? "  /  正在读取" + (UseClash ? " Clash 原始连接" : " Windows 系统连接") : "  /  " + _snapshot.SourceStatus) + (_geo.PendingCount > 0 ? "  /  " + _geo.PendingCount + " 个 IP 待识别" : "");
            _capture.Text = _snapshot == null ? "等待第一次采样" : "最近更新  " + _snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss");
            _proxy.Text = _snapshot == null ? (UseClash ? "本地只读连接 Clash 控制器" : "Windows 系统连接") : (UseClash ? _snapshot.ControllerSummary : _snapshot.ProxySummary);
            _tips.SetToolTip(_status, _status.Text); _tips.SetToolTip(_proxy, _proxy.Text);
        }

        private void RenderDnsStatus(DnsLeakStatus status)
        {
            if (_dnsStatusButton == null || status == null) return;
            string label;
            switch (status.State)
            {
                case "warning": label = "DNS · 外发风险"; break;
                case "observed": label = "DNS · 明文外发"; break;
                case "monitoring": label = "DNS · 正在监测"; break;
                case "limited": label = "DNS · 检测受限"; break;
                case "stopped": label = "DNS · 已停止"; break;
                default: label = "DNS · 正在检查"; break;
            }
            _dnsStatusButton.Text = label; _dnsStatusButton.ForeColor = DnsDetailsDialog.StatusColor(status.State);
            string explanation = (status.Summary ?? "") + "\n" + (status.Coverage ?? "") + "\n点击查看 DNS 服务器、监测依据和限制。";
            _tips.SetToolTip(_dnsStatusButton, explanation); _dnsStatusButton.AccessibleDescription = explanation;
            if ((status.State != "warning" || !_dnsNotificationsEnabled) && _dnsNotification != null) _dnsNotification.Visible = false;
            if (!_dnsNotificationsEnabled || status.State != "warning" || !status.ProxyExpected || !status.IsMonitoring || status.Events == null || status.Events.Count == 0) return;
            DnsLeakEvent latest = status.Events.Where(x => x.ProxyExpectedAtObservation).OrderByDescending(x => x.LastSeen).FirstOrDefault();
            if (latest == null) return;
            DateTime now = DateTime.UtcNow;
            if (now - latest.LastSeen.ToUniversalTime() > TimeSpan.FromSeconds(10) || now - _lastDnsNotificationAt < TimeSpan.FromMinutes(5)) return;
            _lastDnsNotificationAt = now;
            try
            {
                if (_dnsNotification == null)
                {
                    _dnsNotification = new NotifyIcon { Icon = SystemIcons.Warning, Text = "RouteLens · DNS 外发提醒" };
                    _dnsNotification.BalloonTipClicked += delegate { ShowDnsDetails(); };
                    _dnsNotification.DoubleClick += delegate { ShowDnsDetails(); };
                }
                _dnsNotification.Visible = true;
                _dnsNotification.ShowBalloonTip(7000, "疑似 DNS 泄露风险", DnsNotificationText(latest), ToolTipIcon.Warning);
            }
            catch (ExternalException) { /* The persistent in-app status remains visible. */ }
            catch (ArgumentException) { }
        }

        private static string DnsNotificationText(DnsLeakEvent item)
        {
            return "存在代理依据时，物理接口观察到明文 DNS 请求。\nDNS：" + item.ResolverAddress + ":" + item.ResolverPort +
                " · " + item.AdapterName + "\n点击查看证据；该观察不等于已确认泄露。";
        }

        private void ShowDnsDetails()
        {
            if (_closing) return;
            using (DnsDetailsDialog dialog = new DnsDetailsDialog(_dns, delegate { return _dns.GetStatus(); }, RestartDnsAsAdministrator,
                delegate { return _dnsNotificationsEnabled; }, delegate(bool enabled) { _dnsNotificationsEnabled = enabled; RenderDnsStatus(_dns.GetStatus()); }))
                dialog.ShowDialog(this);
            RenderDnsStatus(_dns.GetStatus());
        }

        private void RestartDnsAsAdministrator()
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) };
                using (Process elevated = Process.Start(start))
                {
                    if (elevated == null) throw new InvalidOperationException("未能启动管理员实例。");
                }
                BeginInvoke(new Action(Close));
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                MessageBox.Show(this, exception.NativeErrorCode == 1223 ? "已取消管理员重启，当前实例继续以受限模式运行。" : "未能启动管理员检测：" + exception.Message, "DNS 监测", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (InvalidOperationException exception) { MessageBox.Show(this, exception.Message, "DNS 监测", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        private void RenderEgress()
        {
            EgressResult exit = _geo.Egress; _refresh.Enabled = _online.Checked && !exit.IsChecking; GeoResult location = exit.Location; bool hasIp = !String.IsNullOrEmpty(exit.Ip);
            _exitCountry.Text = location != null && location.Success ? CountryLabel(location) : (hasIp ? "等待国家识别" : (!_online.Checked ? "联网查询已关闭" : (exit.IsChecking ? "正在探测" : "暂未获取出口")));
            _exitFlag.Image = location != null && location.Success ? Flags.Get(location.CountryCode) : null; _exitIp.Text = hasIp ? exit.Ip : "等待公网 IP";
            string stamp = exit.CheckedAt == DateTime.MinValue ? "" : exit.CheckedAt.ToLocalTime().ToString("HH:mm:ss") + " 探测";
            _exitDetail.ForeColor = UiPalette.Muted;
            if (!String.IsNullOrEmpty(exit.Error)) { _exitDetail.Text = (hasIp ? "保留上次结果  /  " : "") + exit.Error; _exitDetail.ForeColor = UiPalette.Warn; }
            else if (!_online.Checked) _exitDetail.Text = "联网查询已关闭" + (stamp.Length > 0 ? "  /  " + stamp + "（缓存或离线数据库）" : "");
            else if (exit.IsChecking) _exitDetail.Text = (hasIp ? "保留上次结果  /  " : "") + "正在探测当前出口";
            else _exitDetail.Text = stamp + (location != null && !String.IsNullOrEmpty(location.Organization) ? "  /  " + location.Organization : "") + (exit.CheckedAt != DateTime.MinValue && DateTime.UtcNow - exit.CheckedAt.ToUniversalTime() > TimeSpan.FromMinutes(3) ? "（结果可能已变化）" : "");
            _tips.SetToolTip(_exitDetail, _exitDetail.Text); _tips.SetToolTip(_exitIp, _exitIp.Text);
        }

        private void RenderConnections()
        {
            if (_closing || _grid == null) return;
            if (_snapshot == null)
            {
                _trafficConnections = new List<ConnectionInfo>();
                _mapSnapshot = new MapExplorerSnapshot { Countries = new List<CountryStat>(), Connections = new List<MapConnection>(), SourceName = UseClash ? "Clash 原始连接" : "Windows 系统连接", IsPaused = _paused, SourceUnavailable = true, SourceStatus = _paused ? "采样已暂停，等待连接数据" : "正在读取连接" };
                _updatingRows = true;
                try { _grid.Rows.Clear(); _rows.Clear(); _rowData.Clear(); }
                finally { _updatingRows = false; }
                _countryChart.SetData(new List<CountryStat>(), 0, 0); _countryChart.SelectedCode = null; _countryFilter.Visible = false;
                _worldMap.SetData(new List<CountryStat>(), 0, 0, null);
                _metricConnections.Text = "—"; _metricProcesses.Text = "—"; _metricCountries.Text = "—"; _counts.Text = "正在读取";
                _empty.Visible = true; _empty.Text = (_paused ? "采样已暂停\n继续采样后读取" : "正在读取") + (UseClash ? " Clash 原始连接…" : " Windows 系统连接…"); RenderInspector(); return;
            }
            List<ConnectionInfo> all = _snapshot.Connections ?? new List<ConnectionInfo>(); string query = _search.Text.Trim(); List<RowData> visible = new List<RowData>();
            Dictionary<string, CountryStat> distribution = new Dictionary<string, CountryStat>(StringComparer.OrdinalIgnoreCase);
            List<MapConnection> mapConnections = new List<MapConnection>();
            HashSet<string> processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase); HashSet<string> countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int publicTotal = 0, knownTotal = 0;
            foreach (ConnectionInfo c in all)
            {
                bool udp = String.Equals(c.Protocol, "UDP", StringComparison.OrdinalIgnoreCase); if (udp && !_includeUdp.Checked) continue;
                bool isPublic = !String.IsNullOrEmpty(c.RemoteAddress) && IpScope.IsPublic(c.RemoteAddress);
                bool hasRemote = HasRemoteAddress(c);
                if (_publicOnly.Checked && !isPublic && !udp && String.IsNullOrWhiteSpace(c.Host)) continue;
                GeoResult geo = isPublic ? (_online.Checked ? _geo.GetOrQueue(c.RemoteAddress) : _geo.Peek(c.RemoteAddress)) : null;
                string country, organization = "—"; Image flag = null;
                if (!hasRemote) country = IsClashData(c) ? "未提供实际 IP" : (udp ? "远端不可见" : "无远端");
                else if (!isPublic) country = String.IsNullOrEmpty(c.Scope) ? IpScope.Classify(c.RemoteAddress) : c.Scope;
                else if (geo != null && geo.Success) { country = CountryLabel(geo); organization = String.IsNullOrEmpty(geo.Organization) ? "—" : geo.Organization; flag = Flags.Get(geo.CountryCode); }
                else if (!_online.Checked) country = "未查询"; else if (geo != null && !String.IsNullOrEmpty(geo.Error)) country = "暂未识别"; else country = "查询中";
                string process = String.IsNullOrWhiteSpace(c.ProcessName) ? "未识别程序" : c.ProcessName;
                string searchable = process + " " + c.ProcessId + " " + c.ProcessPath + " " + c.Host + " " + c.RemoteAddress + " " + country + " " + organization + " " + (geo == null ? "" : geo.CountryCode) + " " + c.ProxyChain + " " + RouteLabel(c) + " " + c.Rule + " " + c.RulePayload + " " + c.ProcessAttribution;
                if (query.Length > 0 && searchable.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                // Chart uses search/protocol scope before its own country filter so other countries remain selectable.
                if (isPublic)
                {
                    publicTotal++;
                    if (geo != null && geo.Success) { knownTotal++; CountryStat stat; if (!distribution.TryGetValue(geo.CountryCode, out stat)) { stat = new CountryStat { Code = geo.CountryCode, Name = country }; distribution.Add(geo.CountryCode, stat); } stat.Count++; }
                    mapConnections.Add(new MapConnection { CountryCode = geo != null && geo.Success ? geo.CountryCode : null, Process = process, Host = c.Host, RemoteAddress = c.RemoteAddress, ProxyChain = c.ProxyChain, RouteType = RouteLabel(c) });
                }
                if (_countryCode != null && (geo == null || !geo.Success || !String.Equals(geo.CountryCode, _countryCode, StringComparison.OrdinalIgnoreCase))) continue;
                if (geo != null && geo.Success) countries.Add(geo.CountryCode);
                if (process != "未识别程序") processes.Add(!String.IsNullOrWhiteSpace(c.ProcessPath) ? c.ProcessPath : process);
                string id = !String.IsNullOrEmpty(c.Id) ? c.Id : c.Protocol + "|" + c.ProcessId + "|" + c.LocalAddress + ":" + c.LocalPort + "|" + c.RemoteAddress + ":" + c.RemotePort;
                visible.Add(new RowData { Id = id, Connection = c, Process = process, Country = country, Organization = organization, Flag = flag, Geo = geo });
            }
            _countryChart.SelectedCode = _countryCode; _countryChart.SetData(distribution.Values.OrderByDescending(x => x.Count).ToList(), publicTotal, publicTotal - knownTotal);
            _worldMap.SetData(distribution.Values.OrderByDescending(x => x.Count).ToList(), publicTotal, publicTotal - knownTotal, _countryCode);
            _trafficConnections = visible.Select(x => x.Connection).ToList();
            _mapSnapshot = new MapExplorerSnapshot { Countries = distribution.Values.OrderByDescending(x => x.Count).ToList(), Connections = mapConnections, Total = publicTotal, Unknown = publicTotal - knownTotal, SelectedCode = _countryCode, SourceName = _snapshot.SourceName, IsPaused = _paused, SourceUnavailable = !String.IsNullOrEmpty(_snapshot.Error) || (UseClash && !_snapshot.ClashConnected), SourceStatus = !String.IsNullOrEmpty(_snapshot.Error) ? _snapshot.Error : _snapshot.SourceStatus };
            _countryFilter.Visible = _countryCode != null; _countryFilter.Text = _countryCode == null ? "清除国家筛选" : "国家 " + _countryCode + "  清除";
            string selectedId = _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as string;
            string topId = _grid.FirstDisplayedScrollingRowIndex >= 0 && _grid.FirstDisplayedScrollingRowIndex < _grid.Rows.Count ? _grid.Rows[_grid.FirstDisplayedScrollingRowIndex].Tag as string : null;
            HashSet<string> wanted = new HashSet<string>(visible.Select(x => x.Id)); _grid.SuspendLayout(); _updatingRows = true;
            try
            {
                foreach (string old in _rows.Keys.Where(key => !wanted.Contains(key)).ToArray()) { _grid.Rows.Remove(_rows[old]); _rows.Remove(old); _rowData.Remove(old); }
                bool added = false;
                foreach (RowData item in visible)
                {
                    DataGridViewRow row; if (!_rows.TryGetValue(item.Id, out row)) { int index = _grid.Rows.Add(); row = _grid.Rows[index]; row.Tag = item.Id; _rows.Add(item.Id, row); added = true; }
                    _rowData[item.Id] = item; ConnectionInfo c = item.Connection;
                    Set(row, "process", item.Process); Set(row, "pid", c.ProcessId > 0 ? c.ProcessId.ToString() : "—"); Set(row, "host", String.IsNullOrWhiteSpace(c.Host) ? "—" : c.Host); Set(row, "ip", HasRemoteAddress(c) ? c.RemoteAddress : "—");
                    int shownPort = HasRemoteAddress(c) ? c.RemotePort : (c.Protocol == "UDP" ? c.LocalPort : c.RemotePort); Set(row, "port", shownPort > 0 ? shownPort.ToString() : "—");
                    Set(row, "flag", item.Flag); Set(row, "country", item.Country); Set(row, "org", item.Organization); Set(row, "route", RouteLabel(c)); Set(row, "chain", String.IsNullOrWhiteSpace(c.ProxyChain) ? "—" : c.ProxyChain); Set(row, "protocol", c.Protocol); Set(row, "state", ConnectionState(c));
                    row.Cells["ip"].ToolTipText = "本地 " + Endpoint(c.LocalAddress, c.LocalPort) + "  →  " + (String.IsNullOrEmpty(c.RemoteAddress) ? "远端未知" : Endpoint(c.RemoteAddress, c.RemotePort));
                    row.Cells["port"].ToolTipText = c.Protocol == "UDP" && !HasRemoteAddress(c) ? "Windows UDP 为本地端口，没有远端信息" : "目标端口 " + c.RemotePort;
                    row.Cells["country"].ToolTipText = item.Geo != null && !String.IsNullOrEmpty(item.Geo.Error) ? item.Geo.Error : item.Country + (item.Geo == null || String.IsNullOrEmpty(item.Geo.City) ? "" : " / " + item.Geo.City);
                    row.Cells["process"].ToolTipText = item.Process + " / PID " + (c.ProcessId > 0 ? c.ProcessId.ToString() : "未提供") + "\n" + c.ProcessAttribution + (String.IsNullOrEmpty(c.ProcessPath) ? "" : "\n" + c.ProcessPath);
                    row.Cells["host"].ToolTipText = String.IsNullOrWhiteSpace(c.Host) ? (IsClashData(c) ? "Clash 未提供这条连接的目标域名" : "Windows 系统连接未提供域名，切换 Clash 原始连接可查看代理内的网站") : c.Host + "\n来自 Clash 元数据，不含 HTTPS 页面路径";
                    row.Cells["chain"].ToolTipText = "走向：" + RouteLabel(c) + "\n代理链：" + (String.IsNullOrWhiteSpace(c.ProxyChain) ? "数据源未提供" : c.ProxyChain) + "\n命中规则：" + RuleText(c);
                }
                if (added && _grid.SortedColumn != null) _grid.Sort(_grid.SortedColumn, _grid.SortOrder == SortOrder.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending);
                if (selectedId != null && _rows.ContainsKey(selectedId)) { DataGridViewColumn target = _grid.CurrentCell != null && _grid.CurrentCell.Visible ? _grid.CurrentCell.OwningColumn : _grid.Columns["process"]; _grid.CurrentCell = _rows[selectedId].Cells[target.Index]; _rows[selectedId].Selected = true; }
                if (topId != null && _rows.ContainsKey(topId)) _grid.FirstDisplayedScrollingRowIndex = _rows[topId].Index;
            }
            finally { _updatingRows = false; _grid.ResumeLayout(); }
            _metricConnections.Text = visible.Count.ToString(); _metricProcesses.Text = processes.Count.ToString(); _metricCountries.Text = countries.Count.ToString();
            _counts.Text = visible.Count + " 条连接" + (_countryCode == null ? "" : " / " + _countryCode); _empty.Visible = visible.Count == 0;
            if (visible.Count == 0)
            {
                if (UseClash && !_snapshot.ClashConnected) _empty.Text = "未能读取 Clash 原始连接\n" + (_snapshot.SourceStatus ?? _snapshot.Error ?? "请确认 Clash / Mihomo 正在运行并启用本地控制器");
                else if (!String.IsNullOrEmpty(_snapshot.Error)) _empty.Text = "无法读取连接\n" + _snapshot.Error;
                else if (query.Length > 0 || _countryCode != null) _empty.Text = "没有匹配的连接\n按 Esc 清除搜索和国家筛选";
                else if (all.Count > 0) _empty.Text = "当前筛选下暂无连接\n可取消“仅公网目标”或开启 UDP 连接";
                else _empty.Text = UseClash ? "Clash 当前没有可见连接\n打开网站或应用后查看程序、域名与实际代理链" : "Windows 当前没有可见连接";
            }
            _grid.InvalidateColumn(_grid.Columns["country"].Index); RenderInspector();
        }

        private void RenderInspector()
        {
            if (_detailProcess == null) return; string id = _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as string; RowData item;
            if (id == null || !_rowData.TryGetValue(id, out item))
            {
                _detailProcess.Text = "选择一条连接"; _detailMeta.Text = "查看程序、网站与分流"; _detailCountry.Text = "尚未选择"; _detailFlag.Image = null; _detailTarget.Text = "—"; _detailLocal.Text = "—"; _detailNetwork.Text = ""; _detailState.Text = "";
                _detailHost.Text = "—"; _detailRoute.Text = "—"; _detailRule.Text = "—"; _detailAttribution.Text = "—"; _detailTraffic.Text = "—"; _detailPath.Text = ""; _detailInbound.Text = ""; _copyDetail.Enabled = false; return;
            }
            ConnectionInfo c = item.Connection; _detailProcess.Text = item.Process; _detailMeta.Text = (c.ProcessId > 0 ? "PID " + c.ProcessId : "PID 未提供") + "    " + c.Protocol; _detailCountry.Text = item.Country; _detailFlag.Image = item.Flag;
            _detailHost.Text = String.IsNullOrWhiteSpace(c.Host) ? "未提供域名" : c.Host;
            _detailTarget.Text = HasRemoteAddress(c) ? Endpoint(c.RemoteAddress, c.RemotePort) : (IsClashData(c) ? "未提供实际 IP" : "远端不可见");
            _detailLocal.Text = Endpoint(c.LocalAddress, c.LocalPort);
            _detailRoute.Text = RouteLabel(c) + "\n" + (String.IsNullOrWhiteSpace(c.ProxyChain) ? "数据源未提供代理链" : c.ProxyChain);
            _detailRule.Text = RuleText(c);
            _detailAttribution.Text = String.IsNullOrWhiteSpace(c.ProcessAttribution) ? (IsClashData(c) ? "控制器未提供归因说明" : "Windows 系统进程信息") : c.ProcessAttribution;
            _detailTraffic.Text = IsClashData(c) && c.HasTrafficCounters != false ? "上传 " + FormatBytes(c.UploadBytes) + "  /  下载 " + FormatBytes(c.DownloadBytes) : "数据源未提供连接流量";
            _detailPath.Text = String.IsNullOrWhiteSpace(c.ProcessPath) ? "程序路径：未提供" : c.ProcessPath;
            _detailInbound.Text = "入站：" + (String.IsNullOrWhiteSpace(c.Inbound) ? "未提供" : c.Inbound);
            _detailNetwork.Text = item.Organization == "—" ? "目标网络：未识别" : item.Organization; _detailState.Text = ConnectionState(c) + "    " + (String.IsNullOrEmpty(c.DataSource) ? "Windows" : c.DataSource); _copyDetail.Enabled = true;
            foreach (Label label in new[] { _detailTarget, _detailLocal, _detailProcess, _detailHost, _detailRoute, _detailRule, _detailAttribution, _detailPath, _detailInbound, _detailNetwork }) _tips.SetToolTip(label, label.Text);
        }
        private void PaintGridCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return; string column = _grid.Columns[e.ColumnIndex].Name; if (column != "country" && column != "state" && column != "route") return;
            e.PaintBackground(e.CellBounds, true); Rectangle bounds = e.CellBounds;
            if (column == "country")
            {
                Image flag = _grid.Rows[e.RowIndex].Cells["flag"].Value as Image; int x = bounds.Left + (int)(12 * _dpiScale);
                if (flag != null) { int h = (int)(18 * _dpiScale), w = (int)(27 * _dpiScale); e.Graphics.DrawImage(flag, new Rectangle(x, bounds.Top + (bounds.Height - h) / 2, w, h)); x += (int)(36 * _dpiScale); }
                TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), e.CellStyle.Font, new Rectangle(x, bounds.Top, Math.Max(10, bounds.Right - x - 8), bounds.Height), UiPalette.Ink, UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                string text = Convert.ToString(e.FormattedValue); bool established = text == "已建立" || text == "代理"; Color back = established ? UiPalette.AccentSoft : UiPalette.Raised; Color ink = established ? UiPalette.Accent : UiPalette.Muted;
                if (text == "拒绝") { back = Color.FromArgb(250, 241, 224); ink = UiPalette.Warn; }
                RectangleF chip = new RectangleF(bounds.Left + 8 * _dpiScale, bounds.Top + 11 * _dpiScale, Math.Min(bounds.Width - 14 * _dpiScale, 66 * _dpiScale), bounds.Height - 22 * _dpiScale);
                UiPaint.RoundRect(e.Graphics, chip, 5 * _dpiScale, back); using (Font font = UiPalette.Body(8.5f)) TextRenderer.DrawText(e.Graphics, text, font, Rectangle.Round(chip), ink, UiPaint.TextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            using (Pen line = new Pen(Color.FromArgb(238, 241, 243))) e.Graphics.DrawLine(line, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1); e.Handled = true;
        }
        private static void Set(DataGridViewRow row, string name, object value) { if (!Object.Equals(row.Cells[name].Value, value)) row.Cells[name].Value = value; }
        private static string CountryLabel(GeoResult geo) { return String.IsNullOrEmpty(geo.CountryName) ? (String.IsNullOrEmpty(geo.CountryCode) ? "未知地区" : geo.CountryCode) : geo.CountryName; }
        private static string Endpoint(string address, int port) { return String.IsNullOrEmpty(address) ? "—" : (address.IndexOf(':') >= 0 ? "[" + address + "]" : address) + ":" + port; }
        private static bool HasRemoteAddress(ConnectionInfo connection) { return !String.IsNullOrWhiteSpace(connection.RemoteAddress) && IpScope.Classify(connection.RemoteAddress) != "无远端"; }
        private static bool IsClashData(ConnectionInfo connection) { return !String.IsNullOrEmpty(connection.DataSource) && (connection.DataSource.IndexOf("Clash", StringComparison.OrdinalIgnoreCase) >= 0 || connection.DataSource.IndexOf("Mihomo", StringComparison.OrdinalIgnoreCase) >= 0); }
        private static string RouteLabel(ConnectionInfo connection)
        {
            if (String.IsNullOrWhiteSpace(connection.RouteType)) return IsClashData(connection) ? "未知" : "未提供";
            switch (connection.RouteType.ToUpperInvariant()) { case "DIRECT": return "直连"; case "PROXY": return "代理"; case "REJECT": return "拒绝"; default: return connection.RouteType; }
        }
        private static string RuleText(ConnectionInfo connection)
        {
            return String.IsNullOrWhiteSpace(connection.Rule) ? "数据源未提供命中规则" : connection.Rule + (String.IsNullOrWhiteSpace(connection.RulePayload) ? "" : "  /  " + connection.RulePayload);
        }
        private static string ConnectionState(ConnectionInfo connection)
        {
            if (String.Equals(connection.State, "ACTIVE", StringComparison.OrdinalIgnoreCase)) return "活动连接";
            return connection.Protocol == "UDP" && HasRemoteAddress(connection) ? (String.IsNullOrWhiteSpace(connection.State) ? "活动连接" : connection.State) : StateLabel(connection.State, connection.Protocol);
        }
        private static string FormatBytes(long bytes)
        {
            double value = Math.Max(0, bytes); string[] units = { "B", "KB", "MB", "GB", "TB" }; int index = 0;
            while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
            return value.ToString(index == 0 ? "0" : "0.0") + " " + units[index];
        }
        private static string StateLabel(string state, string protocol)
        {
            if (protocol == "UDP") return "本地端点"; if (String.IsNullOrEmpty(state)) return "—";
            switch (state.Replace("_", "").ToLowerInvariant()) { case "established": return "已建立"; case "listen": case "listening": return "监听中"; case "timewait": case "closewait": return "待关闭"; case "synsent": case "synreceived": return "连接中"; case "closed": return "已关闭"; case "finwait1": case "finwait2": case "closing": case "lastack": return "关闭中"; default: return state; }
        }
        private void CopySelectedIp()
        {
            if (_grid.CurrentRow == null) return; string ip = Convert.ToString(_grid.CurrentRow.Cells["ip"].Value); if (String.IsNullOrEmpty(ip) || ip == "—") return;
            try { Clipboard.SetText(ip); _status.Text = "已复制目标 IP"; } catch (ExternalException) { _status.Text = "剪贴板暂不可用，请重试"; }
        }
        private void CopySelectedConnection()
        {
            string id = _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as string; RowData item; if (id == null || !_rowData.TryGetValue(id, out item)) return; ConnectionInfo c = item.Connection;
            try { Clipboard.SetText(ConnectionText(item)); _status.Text = "已复制连接信息"; } catch (ExternalException) { _status.Text = "剪贴板暂不可用，请重试"; }
        }
        private static string ConnectionText(RowData item)
        {
            ConnectionInfo c = item.Connection;
            return "程序：" + item.Process + "\r\nPID：" + (c.ProcessId > 0 ? c.ProcessId.ToString() : "未提供") + "\r\n网站域名：" + (String.IsNullOrWhiteSpace(c.Host) ? "未提供" : c.Host) +
                "\r\n目标：" + (HasRemoteAddress(c) ? Endpoint(c.RemoteAddress, c.RemotePort) : "未提供实际远端 IP") + "\r\n目标国家：" + item.Country + "\r\n走向：" + RouteLabel(c) +
                "\r\n实际代理链：" + (String.IsNullOrWhiteSpace(c.ProxyChain) ? "未提供" : c.ProxyChain) + "\r\n命中规则：" + RuleText(c) +
                "\r\n程序归因：" + (String.IsNullOrWhiteSpace(c.ProcessAttribution) ? "未提供" : c.ProcessAttribution) + "\r\n程序路径：" + (c.ProcessPath ?? "未提供") +
                "\r\n本地：" + Endpoint(c.LocalAddress, c.LocalPort) + "\r\n入站：" + (c.Inbound ?? "未提供") + "\r\n协议：" + c.Protocol + "\r\n状态：" + ConnectionState(c) +
                "\r\n累计流量：" + (IsClashData(c) && c.HasTrafficCounters != false ? "上传 " + FormatBytes(c.UploadBytes) + " / 下载 " + FormatBytes(c.DownloadBytes) : "数据源未提供") +
                "\r\n目标网络：" + item.Organization + "\r\n数据源：" + (c.DataSource ?? "Windows 系统连接") + "\r\n\r\n域名来自连接元数据，不包含 HTTPS 页面路径。目标国家是服务器 IP 归属，并非这条连接的代理出口。";
        }
        private void ShowConnectionDetails()
        {
            string id = _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as string; RowData item;
            if (id == null || !_rowData.TryGetValue(id, out item)) { _status.Text = "先从列表选择一条连接"; return; }
            using (Form details = new Form { Text = "连接详情 · " + item.Process, BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Font = UiPalette.Body(10), StartPosition = FormStartPosition.CenterParent, Size = new Size(680, 680), MinimumSize = new Size(520, 460), Padding = new Padding(22), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false })
            {
                TextBox text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Font = UiPalette.Body(10), Text = ConnectionText(item), WordWrap = true };
                Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(0, 14, 0, 0) };
                Button close = ButtonAt("关闭", "close", 90, 36, false); close.Dock = DockStyle.Right; close.DialogResult = DialogResult.OK;
                Button copy = ButtonAt("复制全部", "copy", 116, 36, false); copy.Dock = DockStyle.Left; copy.Click += delegate { try { Clipboard.SetText(text.Text); copy.Text = "已复制"; } catch (ExternalException) { copy.Text = "请重试"; } };
                bottom.Controls.Add(close); bottom.Controls.Add(copy); details.Controls.Add(text); details.Controls.Add(bottom); details.AcceptButton = close; details.CancelButton = close; details.ShowDialog(this);
            }
        }
        private void AcceptSnapshot(NetworkSnapshot snapshot)
        {
            _snapshot = snapshot; _traffic.Update(snapshot); _history.Update(snapshot);
            _alerts.Update(snapshot, delegate(string ip) { GeoResult result = _geo.Peek(ip); return result != null && result.Success ? result.CountryCode : null; });
        }
        public void SetSnapshotForTest(NetworkSnapshot snapshot) { AcceptSnapshot(snapshot); RenderView(); }

        private TrafficReport GetTrafficReport()
        {
            TrafficReport report = _traffic.Build(_trafficConnections, _paused);
            string scope = _countryCode == null ? "全部国家" : "国家 " + _countryCode;
            if (!String.IsNullOrWhiteSpace(_search.Text)) scope += " · 已按搜索筛选";
            report.Status = (UseClash ? "Clash 原始连接" : "Windows 系统连接") + " · " + scope + " · " + report.Status;
            return report;
        }
        private void ShowTrafficRanking()
        {
            using (TrafficRankingDialog dialog = new TrafficRankingDialog(GetTrafficReport, delegate { return _paused; })) dialog.ShowDialog(this);
        }
        private void ShowHistory() { using (HistoryDialog dialog = new HistoryDialog(_history)) dialog.ShowDialog(this); }
        private void ShowPrivacyCheck() { using (PrivacyDnsDialog dialog = new PrivacyDnsDialog()) dialog.ShowDialog(this); }
        private void ShowOfflineGeo() { using (OfflineCountryDialog dialog = new OfflineCountryDialog(_geo.Offline)) dialog.ShowDialog(this); RenderView(); }
        private void ShowActivitySettings() { using (ActivityAlertsDialog dialog = new ActivityAlertsDialog(_alerts)) dialog.ShowDialog(this); }
        private void ShowActivityNotification(string text)
        {
            if (_closing || String.IsNullOrWhiteSpace(text)) return;
            try
            {
                if (_activityNotification == null) _activityNotification = new NotifyIcon { Icon = SystemIcons.Information, Text = "RouteLens · 网络提醒" };
                _activityNotification.Visible = true; _activityNotification.ShowBalloonTip(5000, "RouteLens 网络提醒", text, ToolTipIcon.Info);
            }
            catch (ExternalException) { }
            catch (ArgumentException) { }
        }
        private void ShowMapExplorer()
        {
            using (MapExplorerDialog dialog = new MapExplorerDialog(delegate { return _mapSnapshot; }, delegate(string code)
            {
                _countryCode = String.IsNullOrWhiteSpace(code) ? null : code;
                RenderConnections();
            })) dialog.ShowDialog(this);
        }
        protected override CreateParams CreateParams { get { CreateParams p = base.CreateParams; p.Style |= 0x00080000 | 0x00020000 | 0x00010000; return p; } }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.Space))
            {
                Point where = PointToScreen(new Point(8, (int)(48 * _dpiScale))); IntPtr menu = GetSystemMenu(Handle, false);
                uint command = menu == IntPtr.Zero ? 0 : TrackPopupMenu(menu, 0x0100 | 0x0002, where.X, where.Y, 0, Handle, IntPtr.Zero);
                if (command != 0) SendMessage(Handle, 0x112, new IntPtr(command), IntPtr.Zero); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (Pen p = new Pen(UiPalette.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84 && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m); long packed = m.LParam.ToInt64();
                Point p = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff))); int edge = (int)(6 * _dpiScale);
                bool left = p.X < edge, right = p.X >= ClientSize.Width - edge, top = p.Y < edge, bottom = p.Y >= ClientSize.Height - edge;
                if (top && left) m.Result = new IntPtr(13); else if (top && right) m.Result = new IntPtr(14); else if (bottom && left) m.Result = new IntPtr(16); else if (bottom && right) m.Result = new IntPtr(17); else if (left) m.Result = new IntPtr(10); else if (right) m.Result = new IntPtr(11); else if (top) m.Result = new IntPtr(12); else if (bottom) m.Result = new IntPtr(15); return;
            }
            base.WndProc(ref m);
        }
        protected override void OnFormClosing(FormClosingEventArgs e) { _closing = true; _sourceGeneration++; _sampleTimer.Stop(); _viewTimer.Stop(); _sampleTimer.Dispose(); _viewTimer.Dispose(); _tips.Dispose(); if (_dnsNotification != null) _dnsNotification.Dispose(); if (_activityNotification != null) _activityNotification.Dispose(); _history.Dispose(); _dns.Dispose(); _clash.Dispose(); _geo.Dispose(); base.OnFormClosing(e); }
        private sealed class RowData { public string Id; public ConnectionInfo Connection; public string Process; public string Country; public string Organization; public Image Flag; public GeoResult Geo; }
        private sealed class ConnectionGrid : DataGridView { public ConnectionGrid() { DoubleBuffered = true; } }
    }
}
