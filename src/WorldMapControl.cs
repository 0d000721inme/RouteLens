using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RouteLens
{
    // This is a country-level view. Its dots are representative country positions,
    // never inferred coordinates of the remote IP, VPN node, or local computer.
    public sealed class WorldMapControl : Control
    {
        private sealed class MapCountry
        {
            public string Code;
            public string Name;
            public float Longitude;
            public float Latitude;
            public readonly List<PointF[]> Polygons = new List<PointF[]>();
        }

        private sealed class ProjectedCountry : IDisposable
        {
            public MapCountry Source;
            public readonly GraphicsPath Path = new GraphicsPath(FillMode.Winding);
            public void Dispose() { Path.Dispose(); }
        }

        private sealed class MapMarker
        {
            public CountryStat Country;
            public PointF Anchor;
            public PointF Center;
            public RectangleF Bounds;
            public bool HasCount;
        }

        private static readonly object MapGate = new object();
        private static Dictionary<string, MapCountry> SharedMap;
        private static readonly Color LandColor = Color.FromArgb(238, 243, 240);
        private static readonly Color CoastColor = Color.FromArgb(204, 218, 211);
        private static readonly Color GridColor = Color.FromArgb(239, 244, 241);
        private static readonly Color ActiveLandColor = Color.FromArgb(205, 229, 216);
        private static readonly Color SelectedLandColor = Color.FromArgb(170, 213, 191);
        private static readonly Color QuietTextColor = Color.FromArgb(94, 116, 105);
        private const float North = 85f;
        private const float South = -60f;
        private readonly Font _titleFont = UiPalette.Body(10f);
        private readonly Font _bodyFont = UiPalette.Body(8.4f);
        private readonly Font _numberFont = UiPalette.Mono(8.5f);
        private readonly ToolTip _tips = new ToolTip();
        private readonly ModernButton _expandButton;
        private readonly List<CountryStat> _countries = new List<CountryStat>();
        private readonly List<ProjectedCountry> _projected = new List<ProjectedCountry>();
        private readonly Dictionary<string, ProjectedCountry> _byCode = new Dictionary<string, ProjectedCountry>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MapMarker> _markers = new List<MapMarker>();
        private Bitmap _baseLayer;
        private RectangleF _mapBounds;
        private float _cacheDpi;
        private int _total;
        private int _unknown;
        private int _mappedCount;
        private int _unmappedCount;
        private int _hoverIndex = -1;
        private int _focusIndex;
        private string _selectedCode;
        private bool _markersDirty = true;
        private bool _keyboardMode;
        private Point? _pointerLocation;
        private bool _showExpandButton = true;

        public WorldMapControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);
            BackColor = UiPalette.Surface;
            TabStop = true;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "世界连接地图";
            _tips.InitialDelay = 180;
            _tips.ReshowDelay = 70;
            _tips.AutoPopDelay = 10000;
            _expandButton = new ModernButton { Text = "放大地图", IconName = "maximize", Quiet = true,
                Font = UiPalette.Body(8.5f), AccessibleName = "放大地图并查看国家详情", TabIndex = 0 };
            _expandButton.Click += delegate { Action handler = ExpandRequested; if (handler != null) handler(); };
            Controls.Add(_expandButton);
            LayoutExpandButton();
            EnsureMapLoaded();
        }

        public event Action<string> CountrySelected;
        public event Action ExpandRequested;

        public bool ShowExpandButton
        {
            get { return _showExpandButton; }
            set { _showExpandButton = value; if (_expandButton != null) _expandButton.Visible = value; Invalidate(); }
        }

        public string SelectedCode { get { return _selectedCode; } }

        public void SetData(List<CountryStat> countries, int total, int unknown, string selectedCode)
        {
            string focusCode = _focusIndex >= 0 && _focusIndex < _markers.Count ? _markers[_focusIndex].Country.Code : null;
            List<CountryStat> next = new List<CountryStat>();
            Dictionary<string, CountryStat> nextCodes = new Dictionary<string, CountryStat>(StringComparer.OrdinalIgnoreCase);
            if (countries != null)
                foreach (CountryStat value in countries)
                {
                    if (value == null || value.Count <= 0) continue;
                    string code = (value.Code ?? "").Trim().ToUpperInvariant();
                    CountryStat item;
                    if (nextCodes.TryGetValue(code, out item)) item.Count += value.Count;
                    else
                    {
                        item = new CountryStat { Code = code, Name = String.IsNullOrWhiteSpace(value.Name) ? code : value.Name, Count = value.Count };
                        nextCodes.Add(code, item);
                        next.Add(item);
                    }
                }
            next.Sort(delegate(CountryStat a, CountryStat b) { int c = b.Count.CompareTo(a.Count); return c != 0 ? c : String.CompareOrdinal(a.Code, b.Code); });
            int safeTotal = Math.Max(0, total);
            int safeUnknown = Math.Max(0, Math.Min(unknown, safeTotal));
            string codeSelected = String.IsNullOrWhiteSpace(selectedCode) ? null : selectedCode.Trim().ToUpperInvariant();
            bool changed = safeTotal != _total || safeUnknown != _unknown || codeSelected != _selectedCode || next.Count != _countries.Count;
            if (!changed)
                for (int i = 0; i < next.Count; i++)
                    if (next[i].Code != _countries[i].Code || next[i].Name != _countries[i].Name || next[i].Count != _countries[i].Count)
                    { changed = true; break; }
            if (!changed) return;
            _countries.Clear();
            _countries.AddRange(next);
            _total = safeTotal;
            _unknown = safeUnknown;
            _selectedCode = codeSelected;
            _mappedCount = 0;
            _unmappedCount = 0;
            foreach (CountryStat item in _countries)
            {
                MapCountry shape;
                if (SharedMap.TryGetValue(item.Code, out shape) && IsMapPosition(shape.Longitude, shape.Latitude)) _mappedCount += item.Count;
                else _unmappedCount += item.Count;
            }
            _focusIndex = 0;
            _markersDirty = true;
            _hoverIndex = -1;
            // Preserve the country under keyboard focus when a live snapshot reorders counts.
            if (focusCode != null)
                for (int i = 0, mappedIndex = 0; i < _countries.Count; i++)
                {
                    MapCountry shape;
                    if (!SharedMap.TryGetValue(_countries[i].Code, out shape) || !IsMapPosition(shape.Longitude, shape.Latitude)) continue;
                    if (_countries[i].Code == focusCode) _focusIndex = mappedIndex;
                    mappedIndex++;
                }
            _markers.Clear();
            UpdateAccessibility();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            // FillRectangle deliberately retains the invalidation clip. Graphics.Clear
            // and unbounded GDI text would corrupt neighbouring native controls.
            using (Brush brush = new SolidBrush(BackColor)) graphics.FillRectangle(brush, ClientRectangle);
            EnsureProjection(graphics.DpiX, graphics.DpiY);
            if (_baseLayer != null) graphics.DrawImageUnscaled(_baseLayer, 0, 0);
            float scale = graphics.DpiX / 96f;
            GraphicsState state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.SetClip(_mapBounds, CombineMode.Intersect);
            foreach (MapMarker marker in _markers)
            {
                ProjectedCountry country;
                if (!_byCode.TryGetValue(marker.Country.Code, out country)) continue;
                bool selected = marker.Country.Code == _selectedCode;
                using (Brush brush = new SolidBrush(selected ? SelectedLandColor : ActiveLandColor)) graphics.FillPath(brush, country.Path);
                using (Pen pen = new Pen(selected ? Color.FromArgb(92, 157, 125) : Color.FromArgb(159, 195, 176), Math.Max(.65f, .7f * scale)))
                    graphics.DrawPath(pen, country.Path);
            }
            graphics.Restore(state);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Draw the least prominent marks first so the largest country counts stay legible.
            for (int i = _markers.Count - 1; i >= 0; i--) DrawMarker(graphics, _markers[i], i, scale);
            DrawHeading(graphics, scale);
            DrawFooter(graphics, scale);
        }

        private void DrawHeading(Graphics graphics, float scale)
        {
            int inset = S(20, scale);
            int titleY = S(13, scale);
            UiPaint.DrawIcon(graphics, "globe", new Rectangle(inset, titleY + S(1, scale), S(18, scale), S(18, scale)), UiPalette.Accent, 1.5f);
            DrawText(graphics, "世界连接地图", _titleFont, new Rectangle(inset + S(27, scale), titleY, S(140, scale), S(22, scale)), UiPalette.Ink, TextFormatFlags.Left);
            string status = _markers.Count.ToString("N0") + " 个国家 / 地区";
            int statusWidth = S(135, scale);
            int expandWidth = _showExpandButton ? S(115, scale) : 0;
            int statusRight = Width - inset - expandWidth;
            if (statusRight - statusWidth >= inset + S(175, scale))
                DrawText(graphics, status, _bodyFont, new Rectangle(statusRight - statusWidth, titleY + S(1, scale), statusWidth, S(21, scale)), UiPalette.Muted, TextFormatFlags.Right);
            string caption = "按目标 IP 归属展示 · 国家代表点，非精确位置";
            int active = _hoverIndex >= 0 ? _hoverIndex : (Focused && _keyboardMode ? _focusIndex : -1);
            if (active >= 0 && active < _markers.Count)
            {
                CountryStat country = _markers[active].Country;
                caption = country.Name + " · " + country.Count.ToString("N0") + " 条连接 · " + (country.Code == _selectedCode ? "再次选择可清除筛选" : "点击或 Enter 筛选");
            }
            DrawText(graphics, caption, _bodyFont, new Rectangle(inset, titleY + S(25, scale), Math.Max(0, Width - inset * 2), S(19, scale)), QuietTextColor, TextFormatFlags.Left);
            if (SharedMap.Count == 0 || _markers.Count == 0)
            {
                string empty = SharedMap.Count == 0 ? "地图数据未加载" : (_total == 0 ? "当前范围暂无可定位的公网目标" : (_unknown > 0 ? "等待国家识别，暂不放置连接标记" : "已识别目标暂缺国家代表点"));
                Rectangle badge = new Rectangle((Width - S(280, scale)) / 2, (int)_mapBounds.Y + (int)_mapBounds.Height / 2 - S(15, scale), S(280, scale), S(30, scale));
                UiPaint.RoundRect(graphics, badge, S(8, scale), Color.FromArgb(245, 249, 247), Color.FromArgb(223, 233, 227));
                DrawText(graphics, empty, _bodyFont, Rectangle.Inflate(badge, -S(8, scale), 0), UiPalette.Muted, TextFormatFlags.HorizontalCenter);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutExpandButton();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            LayoutExpandButton();
        }

        private void LayoutExpandButton()
        {
            if (_expandButton == null) return;
            float scale = IsHandleCreated ? DeviceDpiScale() : 1f;
            _expandButton.Bounds = new Rectangle(Math.Max(0, Width - S(126, scale)), S(7, scale), S(110, scale), S(32, scale));
        }

        private float DeviceDpiScale()
        {
            using (Graphics graphics = CreateGraphics()) return graphics.DpiX / 96f;
        }

        private void DrawFooter(Graphics graphics, float scale)
        {
            int inset = S(20, scale);
            int top = Height - S(30, scale);
            using (Brush brush = new SolidBrush(UiPalette.Accent)) graphics.FillEllipse(brush, inset, top + S(6, scale), S(6, scale), S(6, scale));
            string legend = Width < S(610, scale) ? "数字为连接数 · 点击筛选" : "数字为连接数 · 点击国家筛选 · Esc 清除";
            int legendWidth = S(275, scale);
            DrawText(graphics, legend, _bodyFont, new Rectangle(inset + S(14, scale), top, legendWidth, S(19, scale)), QuietTextColor, TextFormatFlags.Left);
            string coverage = "公网目标已定位 " + _mappedCount.ToString("N0") + " / " + _total.ToString("N0") + " 条";
            if (_unknown > 0) coverage += " · 待识别 " + _unknown.ToString("N0");
            if (_unmappedCount > 0) coverage += " · 未定位 " + _unmappedCount.ToString("N0");
            int available = Math.Max(0, Width - inset * 2 - legendWidth - S(20, scale));
            DrawText(graphics, coverage, _bodyFont, new Rectangle(Width - inset - available, top, available, S(19, scale)), UiPalette.Muted, TextFormatFlags.Right);
        }

        private void DrawMarker(Graphics graphics, MapMarker marker, int index, float scale)
        {
            bool selected = marker.Country.Code == _selectedCode;
            bool focused = Focused && ShowFocusCues && _keyboardMode && index == _focusIndex;
            bool active = index == _hoverIndex || focused || selected;
            float displacement = DistanceSquared(marker.Anchor, marker.Center);
            if (displacement > 9f * scale * scale)
            {
                using (Pen pen = new Pen(Color.FromArgb(active ? 150 : 100, UiPalette.Accent), Math.Max(.75f, scale)))
                    graphics.DrawLine(pen, marker.Anchor, marker.Center);
                using (Brush brush = new SolidBrush(UiPalette.Accent)) graphics.FillEllipse(brush, marker.Anchor.X - 2f * scale, marker.Anchor.Y - 2f * scale, 4f * scale, 4f * scale);
            }
            RectangleF bounds = marker.Bounds;
            if (active)
            {
                RectangleF halo = RectangleF.Inflate(bounds, 4f * scale, 4f * scale);
                UiPaint.RoundRect(graphics, halo, halo.Height / 2f, Color.FromArgb(selected ? 40 : 25, UiPalette.Accent));
            }
            UiPaint.RoundRect(graphics, RectangleF.Inflate(bounds, 1.6f * scale, 1.6f * scale), bounds.Height / 2f + 1.6f * scale, Color.White);
            UiPaint.RoundRect(graphics, bounds, bounds.Height / 2f, active ? Color.FromArgb(24, 100, 83) : UiPalette.Accent);
            if (marker.HasCount)
                DrawText(graphics, marker.Country.Count.ToString("N0"), _numberFont, Rectangle.Round(bounds), Color.White, TextFormatFlags.HorizontalCenter);
            if (focused)
            {
                Rectangle ring = Rectangle.Round(RectangleF.Inflate(bounds, 6f * scale, 6f * scale));
                ControlPaint.DrawFocusRectangle(graphics, ring, UiPalette.Accent, BackColor);
            }
        }

        private void EnsureProjection(float dpiX, float dpiY)
        {
            if (_baseLayer == null || _baseLayer.Width != Math.Max(1, Width) || _baseLayer.Height != Math.Max(1, Height) || Math.Abs(_cacheDpi - dpiX) > .1f)
            {
                ClearProjection();
                _cacheDpi = dpiX;
                float scale = dpiX / 96f;
                float availableWidth = Math.Max(1, Width - S(60, scale));
                float availableHeight = Math.Max(1, Height - S(99, scale));
                float unit = Math.Min(availableWidth / 360f, availableHeight / (North - South));
                float mapWidth = unit * 360f;
                float mapHeight = unit * (North - South);
                _mapBounds = new RectangleF((Width - mapWidth) / 2f, S(61, scale) + (availableHeight - mapHeight) / 2f, mapWidth, mapHeight);
                foreach (MapCountry country in SharedMap.Values)
                {
                    ProjectedCountry projected = new ProjectedCountry { Source = country };
                    foreach (PointF[] polygon in country.Polygons) AddProjectedRing(projected.Path, polygon);
                    _projected.Add(projected);
                    _byCode[country.Code] = projected;
                }
                _baseLayer = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
                _baseLayer.SetResolution(dpiX, dpiY);
                using (Graphics graphics = Graphics.FromImage(_baseLayer))
                {
                    using (Brush brush = new SolidBrush(BackColor)) graphics.FillRectangle(brush, 0, 0, _baseLayer.Width, _baseLayer.Height);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.SetClip(_mapBounds);
                    using (Pen pen = new Pen(GridColor, Math.Max(.6f, scale * .65f)))
                    {
                        for (int latitude = -30; latitude <= 60; latitude += 30)
                        { PointF left = Project(-180, latitude); PointF right = Project(180, latitude); graphics.DrawLine(pen, left, right); }
                        for (int longitude = -120; longitude <= 120; longitude += 60)
                        { PointF top = Project(longitude, North); PointF bottom = Project(longitude, South); graphics.DrawLine(pen, top, bottom); }
                    }
                    using (Brush brush = new SolidBrush(LandColor))
                    using (Pen pen = new Pen(CoastColor, Math.Max(.55f, .65f * scale)))
                        foreach (ProjectedCountry country in _projected)
                        {
                            graphics.FillPath(brush, country.Path);
                            graphics.DrawPath(pen, country.Path);
                        }
                }
                _markersDirty = true;
            }
            if (_markersDirty) BuildMarkers(dpiX / 96f);
        }

        private void AddProjectedRing(GraphicsPath target, PointF[] polygon)
        {
            if (polygon.Length < 3) return;
            PointF[] unwrapped = new PointF[polygon.Length];
            float previous = polygon[0].X;
            for (int i = 0; i < polygon.Length; i++)
            {
                float longitude = polygon[i].X;
                while (longitude - previous > 180f) longitude -= 360f;
                while (longitude - previous < -180f) longitude += 360f;
                unwrapped[i] = new PointF(longitude, polygon[i].Y);
                previous = longitude;
            }
            // Wrapped copies are clipped to the world extent; crossing the date line
            // must not create a false coast stretching through the entire map.
            for (int offset = -360; offset <= 360; offset += 360)
            {
                PointF[] points = new PointF[unwrapped.Length];
                float min = Single.MaxValue, max = Single.MinValue;
                for (int i = 0; i < points.Length; i++)
                {
                    points[i] = Project(unwrapped[i].X + offset, unwrapped[i].Y);
                    min = Math.Min(min, points[i].X); max = Math.Max(max, points[i].X);
                }
                if (max < _mapBounds.Left || min > _mapBounds.Right) continue;
                target.StartFigure();
                target.AddPolygon(points);
            }
        }

        private void BuildMarkers(float scale)
        {
            _markers.Clear();
            int maximum = _countries.Count > 0 ? _countries[0].Count : 1;
            foreach (CountryStat country in _countries)
            {
                MapCountry shape;
                if (!SharedMap.TryGetValue(country.Code, out shape) || !IsMapPosition(shape.Longitude, shape.Latitude)) continue;
                PointF anchor = Project(shape.Longitude, shape.Latitude);
                bool hasCount = _markers.Count < 16 || country.Code == _selectedCode;
                float height = hasCount ? (18f + Math.Min(4f, 4f * (float)Math.Sqrt((double)country.Count / maximum))) * scale : 7f * scale;
                float width = hasCount ? Math.Max(height, (country.Count.ToString("N0").Length * 6.3f + 10f) * scale) : height;
                PointF center = FindMarkerPosition(anchor, width, height, scale, hasCount);
                _markers.Add(new MapMarker
                {
                    Country = country, Anchor = anchor, Center = center, HasCount = hasCount,
                    Bounds = new RectangleF(center.X - width / 2, center.Y - height / 2, width, height)
                });
            }
            _focusIndex = Math.Max(0, Math.Min(_focusIndex, _markers.Count - 1));
            _markersDirty = false;
            RefreshHover(true);
            UpdateAccessibility();
        }

        private PointF FindMarkerPosition(PointF anchor, float width, float height, float scale, bool separate)
        {
            if (!separate) return anchor;
            float bestScore = Single.MaxValue;
            PointF best = anchor;
            for (int ring = 0; ring <= 4; ring++)
            {
                int steps = ring == 0 ? 1 : 16;
                for (int step = 0; step < steps; step++)
                {
                    double angle = (step * Math.PI * 2 / steps) - Math.PI / 2;
                    float distance = ring * 20f * scale;
                    PointF center = new PointF(anchor.X + (float)Math.Cos(angle) * distance, anchor.Y + (float)Math.Sin(angle) * distance);
                    center.X = Math.Max(_mapBounds.Left + width / 2, Math.Min(_mapBounds.Right - width / 2, center.X));
                    center.Y = Math.Max(_mapBounds.Top + height / 2, Math.Min(_mapBounds.Bottom - height / 2, center.Y));
                    RectangleF bounds = new RectangleF(center.X - width / 2, center.Y - height / 2, width, height);
                    RectangleF hitArea = RectangleF.Inflate(bounds, 4f * scale, 3f * scale);
                    float score = DistanceSquared(anchor, center) / (scale * scale);
                    foreach (MapMarker placed in _markers)
                    {
                        RectangleF overlap = RectangleF.Intersect(hitArea, placed.Bounds);
                        if (!overlap.IsEmpty) score += 100000f + overlap.Width * overlap.Height / (scale * scale);
                    }
                    if (score < bestScore) { bestScore = score; best = center; }
                    if (score < 100000f) return center;
                }
            }
            return best;
        }

        private PointF Project(float longitude, float latitude)
        {
            return new PointF(_mapBounds.X + (longitude + 180f) / 360f * _mapBounds.Width,
                _mapBounds.Y + (North - latitude) / (North - South) * _mapBounds.Height);
        }

        private static bool IsMapPosition(float longitude, float latitude)
        {
            return !Single.IsNaN(longitude) && !Single.IsNaN(latitude) && longitude >= -180 && longitude <= 180 && latitude >= South && latitude <= North;
        }

        private int HitTest(Point point)
        {
            int closest = -1;
            float closestDistance = Single.MaxValue;
            float scale = _cacheDpi > 0 ? _cacheDpi / 96f : 1f;
            for (int i = 0; i < _markers.Count; i++)
            {
                RectangleF hit = RectangleF.Inflate(_markers[i].Bounds, 5f * scale, 5f * scale);
                if (!hit.Contains(point)) continue;
                float distance = DistanceSquared(_markers[i].Center, point);
                if (distance < closestDistance) { closestDistance = distance; closest = i; }
            }
            if (closest >= 0 || !_mapBounds.Contains(point)) return closest;
            for (int i = 0; i < _markers.Count; i++)
            {
                ProjectedCountry country;
                if (_byCode.TryGetValue(_markers[i].Country.Code, out country) && country.Path.IsVisible(point)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _pointerLocation = e.Location;
            RefreshHover(false);
        }

        private void RefreshHover(bool force)
        {
            int index = _pointerLocation.HasValue ? HitTest(_pointerLocation.Value) : -1;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            if (index == _hoverIndex && !force) return;
            _hoverIndex = index;
            if (index >= 0)
            {
                CountryStat country = _markers[index].Country;
                string fraction = _total > 0 ? ((double)country.Count / _total).ToString("P1") : "0%";
                _tips.SetToolTip(this, country.Name + " · " + country.Count.ToString("N0") + " 条连接（" + fraction + "）\n目标 IP 的国家归属；标记使用国家代表点。\n" + (country.Code == _selectedCode ? "再次点击清除国家筛选。" : "点击按国家筛选。"));
            }
            else _tips.SetToolTip(this, "");
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _pointerLocation = null;
            RefreshHover(false);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int index = HitTest(e.Location);
            if (index < 0) return;
            Focus();
            _keyboardMode = false;
            _focusIndex = index;
            SelectCountry(index);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Action<string> handler = CountrySelected;
                if (handler != null) handler(null);
            }
            else if (_markers.Count == 0) return;
            else if (e.KeyCode == Keys.Home) _focusIndex = 0;
            else if (e.KeyCode == Keys.End) _focusIndex = _markers.Count - 1;
            else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) MoveFocus(e.KeyCode);
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) SelectCountry(_focusIndex);
            else return;
            _keyboardMode = true;
            e.Handled = true;
            e.SuppressKeyPress = true;
            UpdateAccessibility();
            Invalidate();
        }

        private void MoveFocus(Keys key)
        {
            if (_markers.Count < 2) return;
            PointF from = _markers[_focusIndex].Center;
            int best = -1;
            float bestScore = Single.MaxValue;
            for (int i = 0; i < _markers.Count; i++)
            {
                if (i == _focusIndex) continue;
                float dx = _markers[i].Center.X - from.X;
                float dy = _markers[i].Center.Y - from.Y;
                float along = key == Keys.Right ? dx : key == Keys.Left ? -dx : key == Keys.Down ? dy : -dy;
                if (along < .5f) continue;
                float across = key == Keys.Right || key == Keys.Left ? dy : dx;
                float score = along * along + across * across * 4f;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            _focusIndex = best >= 0 ? best : (_focusIndex + ((key == Keys.Right || key == Keys.Down) ? 1 : _markers.Count - 1)) % _markers.Count;
        }

        private void SelectCountry(int index)
        {
            if (index < 0 || index >= _markers.Count) return;
            string code = _markers[index].Country.Code;
            Action<string> handler = CountrySelected;
            if (handler != null) handler(code == _selectedCode ? null : code);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _keyboardMode = true; UpdateAccessibility(); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); ClearProjection(); Invalidate(); }

        private void UpdateAccessibility()
        {
            string detail = "";
            if (_focusIndex >= 0 && _focusIndex < _markers.Count)
                detail = "当前国家 " + _markers[_focusIndex].Country.Name + "，" + _markers[_focusIndex].Country.Count.ToString("N0") + " 条连接。";
            AccessibleDescription = "公网目标 IP 的国家级分布，标记为国家代表点，不是 IP 精确位置。已定位 " + _mappedCount.ToString("N0") + " 条，共 " + _total.ToString("N0") + " 条。" + detail + "方向键选择国家，Enter 或空格筛选，同国再次选择或 Esc 清除。";
        }

        private void ClearProjection()
        {
            if (_baseLayer != null) { _baseLayer.Dispose(); _baseLayer = null; }
            foreach (ProjectedCountry country in _projected) country.Dispose();
            _projected.Clear();
            _byCode.Clear();
            _markersDirty = true;
        }

        private static void EnsureMapLoaded()
        {
            lock (MapGate)
            {
                if (SharedMap != null) return;
                SharedMap = new Dictionary<string, MapCountry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    Stream stream = assembly.GetManifestResourceStream("Maps.world-map.json");
                    if (stream == null)
                        foreach (string name in assembly.GetManifestResourceNames())
                            if (name.EndsWith("world-map.json", StringComparison.OrdinalIgnoreCase)) { stream = assembly.GetManifestResourceStream(name); break; }
                    if (stream == null) return;
                    string json;
                    using (stream)
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) json = reader.ReadToEnd();
                    JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 128 };
                    Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                    object countries;
                    if (root == null || !root.TryGetValue("countries", out countries)) return;
                    foreach (object raw in Sequence(countries))
                    {
                        Dictionary<string, object> record = raw as Dictionary<string, object>;
                        if (record == null) continue;
                        string code = StringValue(record, "code").Trim().ToUpperInvariant();
                        float longitude, latitude;
                        if (code.Length != 2 || !FloatValue(record, "lon", out longitude) || !FloatValue(record, "lat", out latitude)) continue;
                        MapCountry country = new MapCountry { Code = code, Name = StringValue(record, "name"), Longitude = longitude, Latitude = latitude };
                        object polygons;
                        if (record.TryGetValue("polygons", out polygons))
                            foreach (object polygon in Sequence(polygons))
                            {
                                List<PointF> ring = new List<PointF>();
                                foreach (object rawPoint in Sequence(polygon))
                                {
                                    IList pair = rawPoint as IList;
                                    if (pair == null || pair.Count < 2) continue;
                                    float x, y;
                                    if (TryFloat(pair[0], out x) && TryFloat(pair[1], out y) && x >= -180.01f && x <= 180.01f && y >= -90 && y <= 90) ring.Add(new PointF(x, y));
                                }
                                if (ring.Count >= 3) country.Polygons.Add(ring.ToArray());
                            }
                        SharedMap[code] = country;
                    }
                }
                catch (IOException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (OverflowException) { }
            }
        }

        private static IEnumerable Sequence(object value)
        {
            IEnumerable sequence = value as IEnumerable;
            return sequence != null && !(value is string) ? sequence : new object[0];
        }
        private static string StringValue(Dictionary<string, object> record, string key)
        { object value; return record.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private static bool FloatValue(Dictionary<string, object> record, string key, out float number)
        { object value; number = Single.NaN; return record.TryGetValue(key, out value) && TryFloat(value, out number); }
        private static bool TryFloat(object value, out float number)
        { return Single.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !Single.IsNaN(number) && !Single.IsInfinity(number); }
        private static float DistanceSquared(PointF a, PointF b) { float x = a.X - b.X, y = a.Y - b.Y; return x * x + y * y; }
        private static int S(int value, float scale) { return Math.Max(1, (int)Math.Round(value * scale)); }
        private static void DrawText(Graphics graphics, string text, Font font, Rectangle bounds, Color color, TextFormatFlags alignment)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            TextRenderer.DrawText(graphics, text, font, bounds, color,
                UiPaint.TextFlags | alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClearProjection();
                _tips.Dispose();
                _titleFont.Dispose();
                _bodyFont.Dispose();
                _numberFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
