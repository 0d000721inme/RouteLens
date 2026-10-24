using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class MapExplorerSnapshot
    {
        public List<CountryStat> Countries = new List<CountryStat>();
        public List<MapConnection> Connections = new List<MapConnection>();
        public int Total, Unknown;
        public string SelectedCode, SourceName, SourceStatus;
        public bool IsPaused, SourceUnavailable;
    }

    public sealed class MapConnection
    {
        public string CountryCode, Process, Host, RemoteAddress, ProxyChain, RouteType;
    }

    // This window consumes the parent's already-filtered data and never performs
    // DNS lookups, geolocation requests or additional connection sampling.
    public sealed class MapExplorerDialog : Form
    {
        private sealed class CountryChoice
        {
            public string Code, Name;
            public override string ToString() { return Name; }
        }

        private readonly Func<MapExplorerSnapshot> _provider;
        private readonly Action<string> _selectCountry;
        private readonly Timer _timer = new Timer();
        private readonly WorldMapControl _map;
        private readonly ComboBox _countryPicker;
        private readonly Label _connectionCount, _selectionName, _selectionMeta, _missingDomains, _status, _coverage;
        private readonly CountryRankList _programs, _domains;
        private bool _syncing;
        private string _selectedCode;

        public MapExplorerDialog(Func<MapExplorerSnapshot> provider, Action<string> selectCountry)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            _provider = provider; _selectCountry = selectCountry;
            Text = "世界地图与国家详情 · RouteLens";
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Size = new Size(1240, 790); MinimumSize = new Size(980, 640);
            Padding = new Padding(22); MinimizeBox = false;

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0),
                ColumnCount = 1, RowCount = 3 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(root);

            Panel heading = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            Label title = new Label { Text = "世界地图与国家详情", Dock = DockStyle.Top, Height = 38,
                Font = UiPalette.Body(20), ForeColor = UiPalette.Ink, AutoEllipsis = true };
            Label subtitle = new Label { Text = "选择国家，查看哪些程序和域名正在建立连接。", Dock = DockStyle.Bottom, Height = 31,
                Font = UiPalette.Body(9.5f), ForeColor = UiPalette.Muted, AutoEllipsis = true };
            heading.Controls.Add(title); heading.Controls.Add(subtitle); root.Controls.Add(heading, 0, 0);

            TableLayoutPanel content = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0),
                ColumnCount = 2, RowCount = 1 };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 326));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.Controls.Add(content, 0, 1);

            RoundedPanel mapCard = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(3), Margin = new Padding(0, 0, 14, 0) };
            _map = new WorldMapControl { Dock = DockStyle.Fill, ShowExpandButton = false, TabIndex = 0 };
            _map.CountrySelected += SetCountry;
            mapCard.Controls.Add(_map); content.Controls.Add(mapCard, 0, 0);

            RoundedPanel detailCard = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(17) };
            Panel detailScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0), BackColor = UiPalette.Surface };
            detailCard.Controls.Add(detailScroll); content.Controls.Add(detailCard, 1, 0);
            TableLayoutPanel detail = new TableLayoutPanel { Dock = DockStyle.Top, Height = 534, ColumnCount = 1, RowCount = 9,
                Margin = new Padding(0), Padding = new Padding(0), BackColor = UiPalette.Surface };
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int[] heights = { 27, 35, 83, 27, 112, 27, 112, 39, 72 };
            // The final note can scroll at the minimum window size; the map keeps
            // its available space rather than shrinking each ranking row.
            detail.Height = heights.Sum();
            foreach (int height in heights) detail.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            detailScroll.Controls.Add(detail);

            Label detailTitle = MakeLabel("国家详情", 10.5f, UiPalette.Ink); detail.Controls.Add(detailTitle, 0, 0);
            _countryPicker = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                Font = UiPalette.Body(9.5f), Margin = new Padding(0, 0, 0, 7), AccessibleName = "选择国家或查看全部国家", IntegralHeight = true, MaxDropDownItems = 14 };
            _countryPicker.SelectedIndexChanged += delegate
            {
                if (_syncing) return;
                CountryChoice choice = _countryPicker.SelectedItem as CountryChoice;
                SetCountry(choice == null ? null : choice.Code);
            };
            _countryPicker.DropDownClosed += delegate { RefreshSnapshot(); };
            detail.Controls.Add(_countryPicker, 0, 1);

            TableLayoutPanel metrics = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 3 };
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 19)); metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 19));
            _connectionCount = new Label { Text = "0", Font = UiPalette.Mono(28), ForeColor = UiPalette.Ink,
                Dock = DockStyle.Fill, Margin = new Padding(0), AutoEllipsis = true };
            _selectionName = MakeLabel("条连接 · 全部国家", 8.6f, UiPalette.Muted);
            _selectionMeta = MakeLabel("", 8.6f, UiPalette.Accent);
            metrics.Controls.Add(_connectionCount, 0, 0); metrics.Controls.Add(_selectionName, 0, 1); metrics.Controls.Add(_selectionMeta, 0, 2); detail.Controls.Add(metrics, 0, 2);

            detail.Controls.Add(RankHeading("程序排行"), 0, 3);
            _programs = new CountryRankList("当前国家暂无程序连接"); detail.Controls.Add(_programs, 0, 4);
            detail.Controls.Add(RankHeading("域名排行"), 0, 5);
            _domains = new CountryRankList("当前范围未提供域名"); detail.Controls.Add(_domains, 0, 6);
            _missingDomains = MakeLabel("未提供域名：0 条", 8.8f, UiPalette.Muted);
            _missingDomains.Margin = new Padding(0, 10, 0, 0); detail.Controls.Add(_missingDomains, 0, 7);
            Label note = MakeLabel("排行按当前连接数计算。域名来自连接元数据，未提供时不会用 IP 代替或猜测网站。", 8.6f, UiPalette.Muted);
            note.AutoEllipsis = false; detail.Controls.Add(note, 0, 8);

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0, 10, 0, 0), ColumnCount = 2, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.Controls.Add(footer, 0, 2);
            TableLayoutPanel state = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 2 };
            state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); state.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); state.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _status = MakeLabel("正在读取连接分布", 8.5f, UiPalette.Muted); _coverage = MakeLabel("", 8.5f, UiPalette.Muted);
            state.Controls.Add(_status, 0, 0); state.Controls.Add(_coverage, 0, 1); footer.Controls.Add(state, 0, 0);
            ModernButton close = new ModernButton { Text = "返回连接列表", IconName = "close", Dock = DockStyle.Fill, Margin = new Padding(0), DialogResult = DialogResult.OK };
            footer.Controls.Add(close, 1, 0); CancelButton = close;

            _timer.Interval = 1000; _timer.Tick += delegate { RefreshSnapshot(); };
            Shown += delegate { FitWorkingArea(); RefreshSnapshot(); _timer.Start(); };
            FormClosed += delegate { _timer.Stop(); };
            RefreshSnapshot();
        }

        private static Label MakeLabel(string text, float size, Color color)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0), Font = UiPalette.Body(size),
                ForeColor = color, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        }

        private static Control RankHeading(string text)
        {
            TableLayoutPanel heading = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 2, RowCount = 1 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
            heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heading.Controls.Add(MakeLabel(text, 9f, UiPalette.Muted), 0, 0);
            Label count = MakeLabel("连接数", 8.5f, UiPalette.Muted); count.TextAlign = ContentAlignment.MiddleRight; count.Padding = new Padding(0, 0, 9, 0);
            heading.Controls.Add(count, 1, 0); return heading;
        }

        private void FitWorkingArea()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (Width > area.Width || Height > area.Height)
            {
                MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
                Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            }
        }

        private static string NormalizeCode(string code)
        {
            return String.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        }

        private void SetCountry(string code)
        {
            if (_syncing) return;
            _selectedCode = NormalizeCode(code);
            if (_selectCountry != null) _selectCountry(_selectedCode);
            RefreshSnapshot();
        }

        private void RefreshSnapshot()
        {
            if (IsDisposed || Disposing) return;
            MapExplorerSnapshot snapshot;
            try { snapshot = _provider() ?? new MapExplorerSnapshot(); }
            catch
            {
                _status.Text = "暂时无法刷新地图，当前显示上次结果"; _status.ForeColor = UiPalette.Warn;
                return;
            }
            _selectedCode = NormalizeCode(snapshot.SelectedCode);
            List<CountryStat> countries = snapshot.Countries ?? new List<CountryStat>();
            List<MapConnection> connections = snapshot.Connections ?? new List<MapConnection>();
            _map.SetData(countries, snapshot.Total, snapshot.Unknown, _selectedCode);
            SyncCountryChoices(countries, _selectedCode);
            List<MapConnection> selected = connections.Where(x => x != null && (_selectedCode == null || NormalizeCode(x.CountryCode) == _selectedCode)).ToList();
            List<CountryRankItem> programs = GroupValues(selected.Select(x => String.IsNullOrWhiteSpace(x.Process) ? "未识别程序" : x.Process.Trim()));
            List<string> hosts = new List<string>(); int missingHosts = 0;
            foreach (MapConnection connection in selected)
            {
                string host = (connection.Host ?? "").Trim().TrimEnd('.'); IPAddress literal;
                if (String.IsNullOrEmpty(host) || IPAddress.TryParse(host.Trim('[', ']'), out literal)) missingHosts++;
                else hosts.Add(host.ToLowerInvariant());
            }
            List<CountryRankItem> domains = GroupValues(hosts);
            _connectionCount.Text = selected.Count.ToString("N0");
            CountryChoice choice = _countryPicker.SelectedItem as CountryChoice;
            string name = _selectedCode == null ? "全部国家 / 地区" : (choice == null ? _selectedCode : choice.Name);
            _selectionName.Text = "条连接 · " + name;
            _selectionMeta.Text = programs.Count.ToString("N0") + " 个程序组 · " + domains.Count.ToString("N0") + " 个域名";
            _programs.SetItems(programs); _domains.SetItems(domains);
            _missingDomains.Text = "未提供域名：" + missingHosts.ToString("N0") + " 条";
            _status.ForeColor = snapshot.IsPaused || snapshot.SourceUnavailable ? UiPalette.Warn : UiPalette.Accent;
            string source = String.IsNullOrWhiteSpace(snapshot.SourceName) ? "连接分布" : snapshot.SourceName;
            _status.Text = snapshot.SourceUnavailable
                ? "连接数据不可用 · " + source + (String.IsNullOrWhiteSpace(snapshot.SourceStatus) ? "" : " · " + snapshot.SourceStatus)
                : (snapshot.IsPaused ? "采样已暂停" : "实时更新") + " · " + source + " · 与主界面搜索范围同步";
            _coverage.Text = snapshot.Unknown > 0 ? "未识别国家 " + snapshot.Unknown.ToString("N0") + " 条，暂不在地图上放置标记" : "地图标记为国家代表点，不表示服务器或 VPN 节点的精确位置";
        }

        private void SyncCountryChoices(List<CountryStat> countries, string selectedCode)
        {
            // Never replace native drop-down items while the user is choosing.
            if (_countryPicker.DroppedDown) return;
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (CountryStat country in countries)
            {
                string code = country == null ? null : NormalizeCode(country.Code);
                if (code == null || names.ContainsKey(code)) continue;
                names.Add(code, String.IsNullOrWhiteSpace(country.Name) ? code : country.Name);
            }
            if (selectedCode != null && !names.ContainsKey(selectedCode)) names.Add(selectedCode, selectedCode + "（暂无连接）");
            List<CountryChoice> choices = names.Select(x => new CountryChoice { Code = x.Key, Name = x.Value }).OrderBy(x => x.Name, StringComparer.CurrentCulture).ToList();
            choices.Insert(0, new CountryChoice { Code = null, Name = "全部国家 / 地区" });
            bool different = _countryPicker.Items.Count != choices.Count;
            if (!different)
                for (int i = 0; i < choices.Count; i++)
                {
                    CountryChoice existing = _countryPicker.Items[i] as CountryChoice;
                    if (existing == null || existing.Code != choices[i].Code || existing.Name != choices[i].Name) { different = true; break; }
                }
            _syncing = true;
            try
            {
                if (different)
                {
                    _countryPicker.BeginUpdate();
                    try { _countryPicker.Items.Clear(); foreach (CountryChoice choice in choices) _countryPicker.Items.Add(choice); }
                    finally { _countryPicker.EndUpdate(); }
                }
                int selected = 0;
                for (int i = 0; i < _countryPicker.Items.Count; i++)
                    if (((CountryChoice)_countryPicker.Items[i]).Code == selectedCode) { selected = i; break; }
                _countryPicker.SelectedIndex = selected;
            }
            finally { _syncing = false; }
        }

        private static List<CountryRankItem> GroupValues(IEnumerable<string> values)
        {
            return values.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => new CountryRankItem { Name = x.Key, Count = x.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class CountryRankItem
    {
        public string Name;
        public int Count;
    }

    internal sealed class CountryRankList : ListBox
    {
        private readonly Font _numberFont = UiPalette.Mono(8.8f);
        private readonly ToolTip _tips = new ToolTip();
        private readonly string _emptyText;
        private readonly Label _emptyLabel;
        private string _tooltip;

        public CountryRankList(string emptyText)
        {
            _emptyText = emptyText;
            Dock = DockStyle.Fill; Margin = new Padding(0); BorderStyle = BorderStyle.None;
            BackColor = UiPalette.Raised; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9);
            DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 28; IntegralHeight = false;
            HorizontalScrollbar = false; SelectionMode = SelectionMode.One;
            AccessibleName = emptyText.IndexOf("程序", StringComparison.Ordinal) >= 0 ? "程序连接数量排行" : "域名连接数量排行";
            SetStyle(ControlStyles.ResizeRedraw, true);
            _emptyLabel = new Label { Text = emptyText, Font = Font, ForeColor = UiPalette.Muted,
                BackColor = UiPalette.Raised, Dock = DockStyle.Fill, Padding = new Padding(9, 0, 9, 0), AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, TabStop = false };
            Controls.Add(_emptyLabel);
        }

        public void SetItems(List<CountryRankItem> next)
        {
            bool changed = Items.Count != next.Count;
            if (!changed)
                for (int i = 0; i < next.Count; i++)
                {
                    CountryRankItem existing = (CountryRankItem)Items[i];
                    if (existing.Name != next[i].Name || existing.Count != next[i].Count) { changed = true; break; }
                }
            if (!changed) return;
            string selected = SelectedItem == null ? null : ((CountryRankItem)SelectedItem).Name;
            int top = TopIndex;
            BeginUpdate();
            try
            {
                Items.Clear(); foreach (CountryRankItem item in next) Items.Add(item);
                if (selected != null)
                    for (int i = 0; i < Items.Count; i++)
                        if (((CountryRankItem)Items[i]).Name == selected) { SelectedIndex = i; break; }
                if (Items.Count > 0) TopIndex = Math.Min(top, Items.Count - 1);
            }
            finally { EndUpdate(); }
            _emptyLabel.Visible = next.Count == 0;
            AccessibleDescription = next.Count == 0 ? _emptyText : next.Count.ToString("N0") + " 项，按当前连接数从多到少排列。";
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (Graphics graphics = CreateGraphics()) ItemHeight = Math.Max(28, (int)Math.Round(28 * graphics.DpiX / 96f));
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            CountryRankItem item = (CountryRankItem)Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush background = new SolidBrush(selected ? UiPalette.AccentSoft : UiPalette.Raised)) e.Graphics.FillRectangle(background, e.Bounds);
            float scale = e.Graphics.DpiX / 96f;
            int inset = (int)Math.Round(9 * scale), countWidth = (int)Math.Round(48 * scale);
            Rectangle name = new Rectangle(e.Bounds.Left + inset, e.Bounds.Top, Math.Max(0, e.Bounds.Width - countWidth - inset * 2), e.Bounds.Height);
            Rectangle count = new Rectangle(e.Bounds.Right - countWidth - inset, e.Bounds.Top, countWidth, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, item.Name, Font, name, UiPalette.Ink, UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(e.Graphics, item.Count.ToString("N0"), _numberFont, count, UiPalette.Accent, UiPaint.TextFlags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); int index = IndexFromPoint(e.Location);
            string next = index < 0 || index >= Items.Count ? null : ((CountryRankItem)Items[index]).Name + " · " + ((CountryRankItem)Items[index]).Count.ToString("N0") + " 条连接";
            if (next != _tooltip) { _tooltip = next; _tips.SetToolTip(this, next); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _numberFont.Dispose(); _tips.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
