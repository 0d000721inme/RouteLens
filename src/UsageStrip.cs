using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RouteLens
{
    internal sealed class UsageStrip : TableLayoutPanel
    {
        private readonly UsageButton _plan = new UsageButton();
        private readonly UsageButton _sites = new UsageButton();
        private readonly ToolTip _tips = new ToolTip();
        private readonly DailySiteTraffic _daily;
        private SubscriptionQuota[] _plans = new SubscriptionQuota[0];
        private string _chosenId;
        private bool _paused, _clash;
        private DailySiteTrafficReport _report;

        internal UsageStrip(DailySiteTraffic daily)
        {
            _daily = daily;
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            Padding = new Padding(26, 3, 26, 2);
            BackColor = UiPalette.Surface;
            RowCount = 1; ColumnCount = 2;
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            _plan.Margin = new Padding(0, 0, 18, 0);
            _sites.Margin = new Padding(18, 0, 0, 0);
            _plan.Click += delegate { ShowPlans(); };
            _sites.Click += delegate { ShowSites(); };
            _tips.SetToolTip(_sites, "查看今天本次运行监测到的站点，以及直连、代理与未判定流量。");
            Controls.Add(_plan, 0, 0); Controls.Add(_sites, 1, 0);
            UpdateUsage(_plans, false, true);
        }

        internal void UpdateUsage(SubscriptionQuota[] plans, bool paused, bool clash)
        {
            _plans = plans; _paused = paused; _clash = clash;
            UpdatePlan();
            _report = _daily.Read(DateTime.Now);
            double total = (double)_report.UploadBytes + _report.DownloadBytes;
            List<UsageSegment> segments = new List<UsageSegment>();
            double accounted = 0;
            foreach (DailySiteTrafficEntry site in _report.Sites.Take(8))
            {
                if (total <= 0 || site.TotalBytes <= 0) break;
                double part = site.TotalBytes / total;
                segments.Add(new UsageSegment(BrandColor(site.Host), part, site.Host + "  " + part.ToString("0%")));
                accounted += part;
            }
            if (total > 0 && accounted < 0.999999) segments.Add(new UsageSegment(UiPalette.Muted, 1 - accounted));
            string legend = String.Join("     ", _report.Sites.Take(3).Select(s => s.Host + "  " + (s.TotalBytes / Math.Max(1, total)).ToString("0%")).ToArray());
            if (legend.Length == 0) legend = !clash ? "Windows 连接未提供字节计数" : paused ? "采样已暂停" : "等待可比较的 Clash 流量样本";
            string scope = (paused ? "已暂停 · " : "") + "本次运行 · 站点详情  ›";
            _sites.SetContent("今日监测  " + Format(total) + "  ·  代理 " + Format(_report.ProxyBytes), scope, legend, segments);
        }

        private SubscriptionQuota SelectedPlan()
        {
            SubscriptionQuota chosen = _plans.FirstOrDefault(p => p.Id == _chosenId);
            if (chosen != null) return chosen;
            _chosenId = null;
            SubscriptionQuota[] active = _plans.Where(p => p.IsCurrent).ToArray();
            if (active.Length == 1) return active[0];
            return _plans.Length == 1 && _plans[0].Source == "Mihomo" ? _plans[0] : null;
        }

        private void UpdatePlan()
        {
            SubscriptionQuota plan = SelectedPlan();
            if (plan == null)
            {
                _tips.SetToolTip(_plan, "点击选择本机可读取的订阅套餐。");
                _plan.SetContent("订阅套餐  ▾", _plans.Length == 0 ? "暂无额度" : "选择套餐", _plans.Length == 0 ?
                    "等待本机 Clash 提供套餐额度" : "已读取 " + _plans.Length + " 个套餐", new List<UsageSegment>());
                return;
            }
            double fraction = (double)plan.UsedBytes / plan.TotalBytes;
            _plan.SetContent((plan.IsCurrent ? "当前套餐" : "订阅套餐") + "  ·  " + plan.Name + "  ▾",
                "剩余 " + Format(plan.RemainingBytes), "已用 " + Format(plan.UsedBytes) + " / 总量 " + Format(plan.TotalBytes) + "   ·   " + fraction.ToString("0.#%"),
                new List<UsageSegment> { new UsageSegment(UiPalette.Accent, Math.Min(1, fraction)) });
            _tips.SetToolTip(_plan, plan.Source + "  ·  " + (plan.UpdatedAtUtc.HasValue ?
                "订阅数据更新于 " + plan.UpdatedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "未提供订阅更新时间") +
                (plan.ExpiresAtUtc.HasValue ? "\r\n到期 " + plan.ExpiresAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd") : ""));
        }

        private void ShowPlans()
        {
            ContextMenuStrip menu = CreatePlanMenu();
            menu.Closed += delegate { menu.Dispose(); };
            menu.Show(_plan, 0, _plan.Height);
        }

        internal ContextMenuStrip CreatePlanMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink,
                Renderer = new ToolStripProfessionalRenderer(new PlanMenuColors()), Font = Font };
            SubscriptionQuota current = SelectedPlan();
            if (_plans.Length == 0) menu.Items.Add("本机尚未提供套餐额度").Enabled = false;
            if (_chosenId != null && _plans.Any(x => x.IsCurrent))
                menu.Items.Add("跟随 Clash 当前套餐", null, delegate { _chosenId = null; UpdatePlan(); });
            foreach (SubscriptionQuota plan in _plans)
            {
                SubscriptionQuota choice = plan;
                string text = (plan.IsCurrent ? "当前 · " : "") + plan.Name + "   " + Format(plan.RemainingBytes) + " 剩余  ·  " + plan.Source;
                ToolStripMenuItem item = new ToolStripMenuItem(text) { Checked = current != null && current.Id == plan.Id, ForeColor = UiPalette.Ink };
                item.Click += delegate { _chosenId = choice.Id; UpdatePlan(); };
                menu.Items.Add(item);
            }
            return menu;
        }

        private void ShowSites()
        {
            using (Form dialog = new Form { Text = "今日监测 · 站点流量", StartPosition = FormStartPosition.CenterParent,
                AutoScaleMode = AutoScaleMode.Dpi, BackColor = UiPalette.Canvas, ForeColor = UiPalette.Ink, Font = UiPalette.Body(9.5f),
                ClientSize = new Size(910, 475), MinimumSize = new Size(760, 340), ShowInTaskbar = false, MinimizeBox = false, Padding = new Padding(18) })
            {
                TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                Label summary = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "监测统计范围" };
                DataGridView grid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = UiPalette.Surface, ForeColor = UiPalette.Ink,
                    BorderStyle = BorderStyle.None, RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                    ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    EnableHeadersVisualStyles = false, ColumnHeadersHeight = 32, GridColor = UiPalette.Border, AccessibleName = "站点流量明细" };
                grid.ColumnHeadersDefaultCellStyle.BackColor = UiPalette.Raised; grid.ColumnHeadersDefaultCellStyle.ForeColor = UiPalette.Ink;
                grid.DefaultCellStyle.BackColor = UiPalette.Surface; grid.DefaultCellStyle.ForeColor = UiPalette.Ink;
                grid.DefaultCellStyle.SelectionBackColor = UiPalette.AccentSoft; grid.DefaultCellStyle.SelectionForeColor = UiPalette.Ink;
                foreach (string column in new[] { "站点", "总流量", "占比", "直连", "代理", "未判定", "上传", "下载" })
                    grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column, FillWeight = column == "站点" ? 225 : column == "占比" ? 54 : 94,
                        SortMode = DataGridViewColumnSortMode.NotSortable });
                grid.RowTemplate.Height = 30;
                Action refresh = delegate
                {
                    UpdateUsage(_plans, _paused, _clash);
                    double total = (double)_report.UploadBytes + _report.DownloadBytes;
                    summary.Text = _report.Day.ToString("yyyy-MM-dd") + "  ·  本次运行累计 " + Format(total) + "    直连 " + Format(_report.DirectBytes) +
                        "    代理 " + Format(_report.ProxyBytes) + "    未判定 " + Format(_report.UnknownBytes) +
                        (total == 0 ? "    等待可比较的相邻样本" : "    截至 " + DateTime.Now.ToString("HH:mm:ss"));
                    grid.Rows.Clear();
                    foreach (DailySiteTrafficEntry entry in _report.Sites)
                        grid.Rows.Add(entry.Host, Format(entry.TotalBytes), (entry.TotalBytes / Math.Max(1, total)).ToString("0.#%"),
                            Format(entry.DirectBytes), Format(entry.ProxyBytes), Format(entry.UnknownBytes), Format(entry.UploadBytes), Format(entry.DownloadBytes));
                    if (_report.OverflowBytes > 0)
                        grid.Rows.Add("其他站点（超出保留上限）", Format(_report.OverflowBytes),
                            (_report.OverflowBytes / Math.Max(1, total)).ToString("0.#%"), "—", "—", "—", "—", "—");
                };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0) };
                ModernButton close = new ModernButton { Text = "关闭", Width = 80, Height = 32, DialogResult = DialogResult.Cancel };
                ModernButton reload = new ModernButton { Text = "刷新", Width = 75, Height = 32 };
                ModernButton clear = new ModernButton { Text = "清除本次统计", Width = 118, Height = 32 };
                reload.Click += delegate { refresh(); };
                clear.Click += delegate
                {
                    if (MessageBox.Show(dialog, "清除本次运行累计的站点流量？", "清除本次统计", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    { _daily.Clear(); refresh(); }
                };
                buttons.Controls.AddRange(new Control[] { close, reload, clear }); layout.Controls.Add(summary, 0, 0); layout.Controls.Add(grid, 0, 1);
                layout.Controls.Add(buttons, 0, 2); dialog.Controls.Add(layout); dialog.CancelButton = close; refresh(); dialog.ShowDialog(FindForm());
            }
        }

        internal static string Format(double value)
        {
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.##") + " " + units[unit];
        }

        internal static Color BrandColor(string host)
        {
            string domain = (host ?? "").TrimEnd('.').ToLowerInvariant();
            if (Matches(domain, "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com")) return Color.FromArgb(231, 48, 61);
            if (Matches(domain, "google.com", "googleapis.com", "gstatic.com")) return Color.FromArgb(66, 133, 244);
            if (Matches(domain, "bilibili.com", "bilivideo.com", "hdslb.com")) return Color.FromArgb(241, 101, 155);
            if (Matches(domain, "netflix.com", "nflxvideo.net")) return Color.FromArgb(193, 21, 46);
            if (Matches(domain, "openai.com", "chatgpt.com", "oaistatic.com")) return Color.FromArgb(16, 163, 127);
            if (Matches(domain, "microsoft.com", "live.com", "windowsupdate.com")) return Color.FromArgb(0, 161, 218);
            if (Matches(domain, "github.com", "githubusercontent.com")) return UiPalette.IsDark ? Color.FromArgb(174, 146, 225) : Color.FromArgb(112, 79, 154);
            Color[] palette = { Color.FromArgb(61, 155, 154), Color.FromArgb(143, 113, 190), Color.FromArgb(218, 147, 69),
                Color.FromArgb(118, 154, 76), Color.FromArgb(86, 126, 183), Color.FromArgb(186, 113, 132) };
            uint seed = 0;
            foreach (char c in domain) seed = unchecked(seed * 31 + c);
            return palette[seed % palette.Length];
        }

        private static bool Matches(string domain, params string[] suffixes)
        { return suffixes.Any(s => domain == s || domain.EndsWith("." + s, StringComparison.Ordinal)); }

        private sealed class PlanMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return UiPalette.Surface; } }
            public override Color ImageMarginGradientBegin { get { return UiPalette.Raised; } }
            public override Color ImageMarginGradientMiddle { get { return UiPalette.Raised; } }
            public override Color ImageMarginGradientEnd { get { return UiPalette.Raised; } }
            public override Color MenuItemSelected { get { return UiPalette.AccentSoft; } }
            public override Color MenuItemBorder { get { return UiPalette.Border; } }
            public override Color MenuBorder { get { return UiPalette.Border; } }
        }

        protected override void Dispose(bool disposing)
        { if (disposing) _tips.Dispose(); base.Dispose(disposing); }
    }

    internal struct UsageSegment
    {
        public Color Color;
        public double Fraction;
        public string Label;
        public UsageSegment(Color color, double fraction, string label = null) { Color = color; Fraction = fraction; Label = label; }
    }

    internal sealed class UsageButton : Button
    {
        private string _title = "", _metric = "", _caption = "";
        private List<UsageSegment> _segments = new List<UsageSegment>();
        private readonly Font _titleFont = UiPalette.Body(9.5f), _captionFont = UiPalette.Body(8.1f);
        internal UsageButton()
        {
            Dock = DockStyle.Fill; BackColor = UiPalette.Surface; Cursor = Cursors.Hand; DoubleBuffered = true;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
        }
        internal void SetContent(string title, string metric, string caption, List<UsageSegment> segments)
        {
            if (title == _title && metric == _metric && caption == _caption && segments.SequenceEqual(_segments)) return;
            _title = title; _metric = metric; _caption = caption; _segments = segments;
            AccessibleName = title + "，" + metric + "，" + caption; Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(UiPalette.Surface);
            float scale = g.DpiX / 96f;
            int gap = (int)Math.Round(7 * scale), inset = (int)Math.Round(2 * scale);
            TextFormatFlags common = UiPaint.TextFlags | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int titleHeight = (int)Math.Round(22 * scale), captionHeight = (int)Math.Round(19 * scale);
            int metricWidth = Math.Min(Width / 2, TextRenderer.MeasureText(g, _metric, _titleFont).Width + gap);
            TextRenderer.DrawText(g, _title, _titleFont, new Rectangle(inset, inset, Math.Max(0, Width - metricWidth - gap), titleHeight), UiPalette.Ink, common);
            TextRenderer.DrawText(g, _metric, _titleFont, new Rectangle(Width - metricWidth, inset, metricWidth, titleHeight), UiPalette.Accent, common | TextFormatFlags.Right);
            int captionY = inset + titleHeight;
            if (_segments.Count == 0 || _segments[0].Label == null)
                TextRenderer.DrawText(g, _caption, _captionFont, new Rectangle(inset, captionY, Width - inset * 2, captionHeight), UiPalette.Muted, common);
            else
            {
                int legendX = inset, dot = Math.Max(4, (int)Math.Round(6 * scale));
                foreach (UsageSegment segment in _segments.Take(3))
                {
                    if (segment.Label == null || legendX + gap * 8 >= Width) break;
                    int labelWidth = Math.Min(Width - legendX - dot - gap, Math.Min(Width * 2 / 5, TextRenderer.MeasureText(g, segment.Label, _captionFont).Width));
                    using (SolidBrush swatch = new SolidBrush(segment.Color))
                        g.FillEllipse(swatch, legendX, captionY + (captionHeight - dot) / 2, dot, dot);
                    TextRenderer.DrawText(g, segment.Label, _captionFont, new Rectangle(legendX + dot + inset, captionY, labelWidth, captionHeight), UiPalette.Muted, common);
                    legendX += dot + inset + labelWidth + gap;
                }
            }
            int y = Math.Min(Height - 4, captionY + captionHeight + inset), height = Math.Max(3, (int)Math.Round(6 * scale));
            int length = Math.Max(0, Width - inset * 2);
            using (SolidBrush track = new SolidBrush(UiPalette.Border)) g.FillRectangle(track, inset, y, length, height);
            int drawn = 0; double cumulative = 0;
            foreach (UsageSegment segment in _segments)
            {
                cumulative += segment.Fraction;
                int end = Math.Min(length, (int)Math.Round(length * cumulative));
                if (end > drawn) using (SolidBrush brush = new SolidBrush(segment.Color)) g.FillRectangle(brush, inset + drawn, y, end - drawn, height);
                drawn = end;
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle, UiPalette.Ink, UiPalette.Surface);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { _titleFont.Dispose(); _captionFont.Dispose(); } base.Dispose(disposing); }
    }
}
