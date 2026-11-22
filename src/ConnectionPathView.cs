using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;

namespace RouteLens
{
    internal sealed class ConnectionPathView : UserControl
    {
        private readonly WorldMapControl map = new WorldMapControl { Dock = DockStyle.Fill, ShowExpandButton = false };
        private readonly ComboBox origin = Choice("本地位置未设", "南通市");
        private readonly ComboBox remote = Choice("按地址定位", "手动标注中国香港");
        private readonly Label heading = Caption(16), metadata = Caption(9), status = Caption(9);
        private readonly ListView hops = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = false, AccessibleName = "本机 ICMP 逐跳结果" };
        private readonly ModernButton measure = new ModernButton { Text = "测量本机路径", AutoSize = false, Width = 124, Height = 32 };
        private readonly List<WorldMapRoutePoint> hopPlaces = new List<WorldMapRoutePoint>();
        private readonly Func<string, GeoResult> lookup;
        private readonly Func<ConnectionInfo, TrafficReport> getTraffic;
        private readonly Func<NetworkSnapshot> getSnapshot;
        private readonly Func<bool> isPaused;
        private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 900 };
        private CancellationTokenSource measuring;
        private ConnectionInfo pinned;
        private string source;
        private string traceStatus = "";
        private WorldMapRoutePoint customOrigin;

        public ConnectionPathView(Func<NetworkSnapshot> snapshot, Func<ConnectionInfo, TrafficReport> traffic,
            Func<string, GeoResult> geo, Func<bool> paused, Action back)
        {
            getSnapshot = snapshot; getTraffic = traffic; lookup = geo; isPaused = paused;
            Dock = DockStyle.Fill; BackColor = UiPalette.Canvas; Padding = new Padding(16, 4, 16, 8);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
            foreach (int height in new[] { 38, 48, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 72)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
            Controls.Add(layout);
            layout.Controls.Add(heading, 0, 0); layout.Controls.Add(metadata, 0, 1);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            origin.AccessibleName = "本地地图起点（手动设置）"; remote.AccessibleName = "远端地图标注";
            origin.Width = 150; remote.Width = 172; origin.Items.Add("自定义经纬度…");
            origin.SelectedIndexChanged += delegate
            {
                if (origin.SelectedIndex == 2)
                {
                    WorldMapRoutePoint position = EditOrigin();
                    if (position == null) { origin.SelectedIndex = customOrigin == null ? 0 : 3; return; }
                    customOrigin = position;
                    if (origin.Items.Count > 3) origin.Items.RemoveAt(3);
                    origin.Items.Add(position.Label); origin.SelectedIndex = 3;
                }
                UpdateView();
            };
            remote.SelectedIndexChanged += delegate { UpdateView(); };
            measure.Click += async delegate { await MeasureAsync(); };
            ModernButton close = new ModernButton { Text = "国家分布", Width = 98, Height = 32 };
            close.Click += delegate { back(); };
            actions.Controls.AddRange(new Control[] { origin, remote, measure, close });
            layout.Controls.Add(actions, 0, 2); layout.Controls.Add(map, 0, 3); layout.Controls.Add(status, 0, 4);
            hops.Columns.Add("跳数", 58); hops.Columns.Add("响应地址", 270); hops.Columns.Add("耗时", 78);
            hops.Columns.Add("IP 归属", 145); hops.Columns.Add("探测结果", 240); layout.Controls.Add(hops, 0, 5);
            refresh.Tick += delegate { UpdateView(); };
            VisibleChanged += delegate
            {
                if (IsDisposed || Disposing) return;
                if (Visible && pinned != null) { UpdateView(); refresh.Start(); }
                else { refresh.Stop(); StopTrace(); map.SetRouteTraffic(false, false); }
            };
        }

        private static ComboBox Choice(params string[] entries)
        {
            ComboBox box = new ModernCombo { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 5, 9, 0) };
            box.Items.AddRange(entries); box.SelectedIndex = 0; return box;
        }
        private static Label Caption(float size)
        {
            return new Label { Dock = DockStyle.Fill, Font = UiPalette.Body(size), ForeColor = UiPalette.Ink,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
        }
        private WorldMapRoutePoint EditOrigin()
        {
            using (Form dialog = new Form { Text = "本地地图标注", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(330,165),
                BackColor = UiPalette.Canvas, ForeColor = UiPalette.Ink, Font = UiPalette.Body(9.5f) })
            {
                TextBox name = new TextBox { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Text = customOrigin == null ? "本地" : customOrigin.Label, MaxLength = 36 };
                NumericUpDown lon = new NumericUpDown { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Minimum = -180, Maximum = 180, DecimalPlaces = 3 };
                NumericUpDown lat = new NumericUpDown { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Minimum = -60, Maximum = 85, DecimalPlaces = 3 };
                if (customOrigin != null) { lon.Value = (decimal)customOrigin.LongitudeLatitude.Value.X; lat.Value = (decimal)customOrigin.LongitudeLatitude.Value.Y; }
                TableLayoutPanel form = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 4, BackColor = UiPalette.Canvas };
                form.Controls.Add(new Label { Text = "地点", ForeColor = UiPalette.Ink, BackColor = Color.Transparent }, 0, 0); form.Controls.Add(name, 1, 0);
                form.Controls.Add(new Label { Text = "经度", ForeColor = UiPalette.Ink, BackColor = Color.Transparent }, 0, 1); form.Controls.Add(lon, 1, 1);
                form.Controls.Add(new Label { Text = "纬度", ForeColor = UiPalette.Ink, BackColor = Color.Transparent }, 0, 2); form.Controls.Add(lat, 1, 2);
                Button ok = new ModernButton { Text = "应用", DialogResult = DialogResult.OK };
                form.Controls.Add(ok, 1, 3); dialog.Controls.Add(form); dialog.AcceptButton = ok;
                return dialog.ShowDialog(FindForm()) == DialogResult.OK ? new WorldMapRoutePoint(
                    name.Text.Trim().Length == 0 ? "本地" : name.Text.Trim(), "", new PointF((float)lon.Value, (float)lat.Value)) : null;
            }
        }
        public void Pin(ConnectionInfo connection, NetworkSnapshot snapshot)
        {
            ClearPin(); pinned = connection; source = Source(snapshot); remote.SelectedIndex = 0;
            UpdateView(); if (Visible) refresh.Start();
        }

        internal void ClearPin()
        {
            refresh.Stop(); StopTrace(); pinned = null; source = "";
            traceStatus = ""; hops.Items.Clear(); hopPlaces.Clear(); map.SetRouteHops(null);
            map.ClearRoute();
        }
        private static string Source(NetworkSnapshot snapshot)
        { return snapshot == null ? "" : snapshot.SourceName + "\n" + snapshot.ControllerSummary; }
        private bool Proxied { get { return pinned.RouteType == "代理" || String.Equals(pinned.RouteType, "PROXY", StringComparison.OrdinalIgnoreCase); } }
        private string ProbeTarget { get { return Proxied ? pinned.OutboundAddress : pinned.RemoteAddress; } }
        private bool CanTrace(out IPAddress address)
        {
            address = null;
            return pinned != null && pinned.RouteType != "拒绝" && pinned.RouteType != "未知" &&
                !String.Equals(pinned.RouteType, "REJECT", StringComparison.OrdinalIgnoreCase) &&
                IpScope.Classify(ProbeTarget) != "虚拟地址" && ConnectionTrace.TryGetTarget(ProbeTarget, out address);
        }
        private WorldMapRoutePoint Locate(string ip)
        {
            if (!IpScope.IsPublic(ip)) return null;
            GeoResult geo = lookup(ip);
            return geo != null && geo.Success ? new WorldMapRoutePoint(geo.CountryCode == "HK" ? "中国香港" : geo.CountryName, geo.CountryCode) : null;
        }
        private ConnectionInfo CurrentConnection()
        {
            NetworkSnapshot snapshot = getSnapshot();
            return pinned == null || snapshot == null || snapshot.Connections == null || source != Source(snapshot) ||
                !String.IsNullOrEmpty(snapshot.Error) ? null : snapshot.Connections.FirstOrDefault(c => c != null && c.Id == pinned.Id && c.Host == pinned.Host &&
                c.RemoteAddress == pinned.RemoteAddress && c.RemotePort == pinned.RemotePort && c.LocalPort == pinned.LocalPort &&
                c.Protocol == pinned.Protocol && c.LocalAddress == pinned.LocalAddress && c.RouteType == pinned.RouteType &&
                c.ProxyChain == pinned.ProxyChain && c.OutboundAddress == pinned.OutboundAddress);
        }
        internal void UpdateView()
        {
            if (pinned == null || Disposing || IsDisposed) return;
            ConnectionInfo live = CurrentConnection();
            bool paused = isPaused();
            if (paused || live == null) StopTrace();
            TrafficReport traffic = live == null || paused ? null : getTraffic(live);
            string target = String.IsNullOrEmpty(pinned.Host) ? pinned.RemoteAddress : pinned.Host;
            heading.Text = (String.IsNullOrEmpty(target) ? "目标未提供" : target) + "   ·   连接路径";
            metadata.Text = (pinned.ProcessName ?? "未识别程序") + "   →   " + (String.IsNullOrEmpty(pinned.Inbound) ? (pinned.DataSource ?? "Windows") : pinned.Inbound)
                + "   →   " + (String.IsNullOrEmpty(pinned.ProxyChain) ? "策略链未提供" : pinned.ProxyChain) + "   →   " + (target ?? "目标未知")
                + "\r\n" + (Proxied ? "内核报告出站端点 " + (String.IsNullOrEmpty(pinned.OutboundAddress) ? "未知" : pinned.OutboundAddress) + "   ·   隧道内部不可见" : "系统或内核报告目标 " + (pinned.RemoteAddress ?? "未知")) + "   ·   " + (pinned.Protocol ?? "");
            WorldMapRoutePoint from = origin.SelectedIndex == 1 ? new WorldMapRoutePoint("南通市（手动）", "CN", new PointF(120.894f,31.981f)) : origin.SelectedIndex == 3 ? customOrigin : null;
            WorldMapRoutePoint to = remote.SelectedIndex == 1 ? new WorldMapRoutePoint("中国香港（手动）", "HK") : Locate(ProbeTarget);
            map.SetRoute(from, to); map.SetRouteHops(hopPlaces);
            map.SetRouteTraffic(Visible && traffic != null && traffic.Measuring && traffic.UploadPerSecond > 0,
                Visible && traffic != null && traffic.Measuring && traffic.DownloadPerSecond > 0);
            IPAddress address;
            bool canMeasure = CanTrace(out address);
            measure.Enabled = !paused && live != null && (measuring != null || canMeasure);
            measure.AccessibleDescription = paused ? "采样已暂停" : live == null ? "当前快照未见此连接" :
                canMeasure ? "从本机向 " + address + " 发送 ICMP 探测" : "缺少可测量的出站 IP";
            string activity = paused ? "采样已暂停 · 显示固定快照" : live == null ? "当前快照未见此连接 · 显示固定快照" : traffic == null || !traffic.Measuring ? "暂无可比较的流量样本" :
                String.Format("↑ {0:0.#} KiB/s    ↓ {1:0.#} KiB/s", traffic.UploadPerSecond / 1024, traffic.DownloadPerSecond / 1024);
            status.Text = activity + "     ·     " + (traceStatus.Length > 0 ? traceStatus : canMeasure ? "本机 ICMP → " + address : "没有可测量的远端 IP");
        }
        private void StopTrace()
        {
            CancellationTokenSource run = measuring;
            if (run == null) return;
            measuring = null;
            run.Cancel();
            traceStatus = "测量已停止"; measure.Text = "测量本机路径";
        }
        private async System.Threading.Tasks.Task MeasureAsync()
        {
            if (measuring != null) { StopTrace(); UpdateView(); return; }
            IPAddress target; if (!Visible || isPaused() || CurrentConnection() == null || !CanTrace(out target)) return;
            CancellationTokenSource run = new CancellationTokenSource(); measuring = run;
            hopPlaces.Clear(); hops.Items.Clear(); map.SetRouteHops(null);
            traceStatus = "正在从本机测量 " + target; measure.Text = "停止测量"; UpdateView();
            Action<TraceHop> showHop = delegate(TraceHop hop)
            {
                if (run != measuring || run.IsCancellationRequested || IsDisposed || !Visible) return;
                WorldMapRoutePoint place = Locate(hop.Address); hopPlaces.Add(place);
                hops.Items.Add(new ListViewItem(new[] { hop.Number.ToString(), hop.Address.Length == 0 ? "—" : hop.Address,
                    hop.RoundtripMs.HasValue ? hop.RoundtripMs + " ms" : "—", place == null ? "—" : place.Label, hop.StatusText }));
                map.SetRouteHops(hopPlaces);
            };
            string outcome = "";
            try
            {
                // Deliver the row before the worker finishes, including fast loopback replies.
                TraceOutcome result = await ConnectionTrace.RunAsync(target, delegate(TraceHop hop)
                {
                    if (!run.IsCancellationRequested) Invoke(showHop, hop);
                }, run.Token);
                outcome = result == TraceOutcome.Reached ? "本机 ICMP 已到达 " + target : result == TraceOutcome.HopLimit ? "已达跳数上限" : "目标不可达";
            }
            catch (OperationCanceledException) { outcome = "测量已停止"; }
            catch (PingException) { outcome = "系统未能完成 ICMP 探测"; }
            catch (InvalidOperationException) { outcome = "系统无法返回逐跳结果"; }
            catch (System.Net.Sockets.SocketException) { outcome = "系统网络接口不可用"; }
            catch (System.Security.SecurityException) { outcome = "当前权限无法进行 ICMP 探测"; }
            finally
            {
                if (run == measuring)
                {
                    measuring = null;
                    if (!IsDisposed && !Disposing) { traceStatus = outcome; measure.Text = "测量本机路径"; UpdateView(); }
                }
                run.Dispose();
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { refresh.Dispose(); StopTrace(); }
            base.Dispose(disposing);
        }
    }
}
