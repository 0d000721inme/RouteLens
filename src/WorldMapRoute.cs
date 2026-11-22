using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class WorldMapRoutePoint
    {
        public WorldMapRoutePoint(string label, string countryCode, PointF? longitudeLatitude = null)
        {
            Label = label ?? "";
            CountryCode = (countryCode ?? "").Trim();
            LongitudeLatitude = longitudeLatitude;
        }

        public string Label { get; private set; }
        public string CountryCode { get; private set; }
        // X is longitude and Y is latitude, both in degrees.
        public PointF? LongitudeLatitude { get; private set; }
    }

    public sealed partial class WorldMapControl
    {
        private static Color UploadColor { get { return UiPalette.Accent; } }
        private static Color DownloadColor { get { return UiPalette.Success; } }
        private static Color HopColor { get { return UiPalette.IsDark ? Color.FromArgb(195, 166, 242) : Color.FromArgb(112, 91, 154); } }
        private readonly Timer _routeTimer = new Timer { Interval = 16 };
        private readonly Timer _demoTimer = new Timer { Interval = 6500 };
        private readonly Stopwatch _routeElapsed = new Stopwatch();
        private long _routeNextMotionCheck;
        private readonly List<PointF?> _routeHops = new List<PointF?>();
        private readonly List<PointF?> _projectedHops = new List<PointF?>();
        private WorldMapRoutePoint _routeSource, _routeDestination;
        private PointF? _routeSourcePosition, _routeDestinationPosition;
        private RouteCurve? _routeCurve;
        private Rectangle _routeDirtyBounds;
        private bool _routeMode, _routeGeometryDirty, _routeUploading, _routeDownloading, _routeDemo;
        private float _distributionZoom, _distributionX, _distributionY;

        private struct RouteCurve
        {
            public PointF Start, Control, End;

            public PointF At(float t)
            {
                float u = 1f - t;
                return new PointF(u * u * Start.X + 2f * u * t * Control.X + t * t * End.X,
                    u * u * Start.Y + 2f * u * t * Control.Y + t * t * End.Y);
            }
        }

        public void PlayDemoRoute()
        { PlayDemoRoute(true); }

        internal void PlayDemoRoute(bool clearAutomatically)
        {
            ClearRoute();
            SetRoute(new WorldMapRoutePoint("演示·南通", "CN", new PointF(120.894f, 31.981f)),
                new WorldMapRoutePoint("演示·香港", "HK", new PointF(114.17f, 22.32f)));
            _routeDemo = true;
            _demoTimer.Stop();
            if (clearAutomatically) _demoTimer.Start();
            SetRouteTraffic(true, true);
            UpdateRouteAccessibility();
            Invalidate();
        }

        public void SetRoute(WorldMapRoutePoint source, WorldMapRoutePoint destination)
        {
            PointF? sourcePosition = RoutePosition(source), destinationPosition = RoutePosition(destination);
            bool refit = !_routeMode || sourcePosition != _routeSourcePosition || destinationPosition != _routeDestinationPosition;
            bool newRoute = !_routeMode || !SameEndpoint(source, _routeSource) || !SameEndpoint(destination, _routeDestination);
            if (!_routeMode)
            {
                _distributionZoom = _zoom;
                _distributionX = _centerX;
                _distributionY = _centerY;
            }
            if (_routeMode && !refit && SameLabel(source, _routeSource) && SameLabel(destination, _routeDestination)) return;
            ClearResizePreviewLayer();
            _routeMode = true;
            _routeSource = source;
            _routeDestination = destination;
            _routeSourcePosition = sourcePosition;
            _routeDestinationPosition = destinationPosition;
            if (newRoute) { _routeHops.Clear(); _projectedHops.Clear(); }
            _routeGeometryDirty = true;
            _expandButton.Hide();
            _tips.SetToolTip(this, null);
            if (refit) FitRoute();
            UpdateRouteAccessibility();
            UpdateRouteTimer();
            Invalidate();
        }

        public void SetRouteHops(IEnumerable<WorldMapRoutePoint> hops)
        {
            if (!_routeMode) return;
            List<PointF?> next = new List<PointF?>();
            if (hops != null)
                foreach (WorldMapRoutePoint hop in hops)
                {
                    if (next.Count == 64) break;
                    next.Add(CountryPosition(hop));
                }
            bool changed = next.Count != _routeHops.Count;
            for (int i = 0; !changed && i < next.Count; i++) changed = next[i] != _routeHops[i];
            if (!changed) return;
            _routeHops.Clear();
            _routeHops.AddRange(next);
            _routeGeometryDirty = true;
            UpdateRouteAccessibility();
            Invalidate();
        }

        public void SetRouteTraffic(bool uploading, bool downloading)
        {
            bool changed = _routeUploading != uploading || _routeDownloading != downloading;
            _routeUploading = uploading;
            _routeDownloading = downloading;
            UpdateRouteTimer();
            if (changed && _routeMode) { UpdateRouteAccessibility(); Invalidate(); }
        }

        public void ClearRoute()
        {
            _demoTimer.Stop();
            _routeDemo = false;
            if (!_routeMode) return;
            _routeMode = false;
            _routeSource = _routeDestination = null;
            _routeSourcePosition = _routeDestinationPosition = null;
            _routeHops.Clear();
            _projectedHops.Clear();
            _routeCurve = null;
            _routeUploading = _routeDownloading = false;
            _markersDirty = true;
            _expandButton.Visible = _showExpandButton;
            UpdateRouteTimer();
            ApplyView(_distributionZoom, _distributionX, _distributionY);
            UpdateAccessibility();
            Invalidate();
        }

        private static PointF? RoutePosition(WorldMapRoutePoint point)
        {
            if (point == null) return null;
            PointF? explicitPosition = point.LongitudeLatitude;
            if (explicitPosition.HasValue && IsMapPosition(explicitPosition.Value.X, explicitPosition.Value.Y)) return explicitPosition;
            return CountryPosition(point);
        }

        private static PointF? CountryPosition(WorldMapRoutePoint point)
        {
            MapCountry country;
            if (point != null && SharedMap.TryGetValue(point.CountryCode, out country) && IsMapPosition(country.Longitude, country.Latitude))
                return new PointF(country.Longitude, country.Latitude);
            return null;
        }

        private static bool SameLabel(WorldMapRoutePoint a, WorldMapRoutePoint b)
        {
            return (a == null ? "" : a.Label) == (b == null ? "" : b.Label) &&
                (a == null || a.LongitudeLatitude == null) == (b == null || b.LongitudeLatitude == null);
        }

        private static bool SameEndpoint(WorldMapRoutePoint a, WorldMapRoutePoint b)
        {
            return SameLabel(a, b) && (a == null ? "" : a.CountryCode) == (b == null ? "" : b.CountryCode) &&
                (a == null ? (PointF?)null : a.LongitudeLatitude) == (b == null ? (PointF?)null : b.LongitudeLatitude);
        }

        private void FitRoute()
        {
            List<PointF?> all = new List<PointF?> { _routeSourcePosition, _routeDestinationPosition };
            all.AddRange(_routeHops);
            float left = 1f, right = 0f, top = 1f, bottom = 0f;
            foreach (PointF? position in all)
            {
                if (!position.HasValue) continue;
                float x = (position.Value.X + 180f) / 360f;
                float y = (North - position.Value.Y) / (North - South);
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            if (left > right) { ApplyView(1f, .5f, .5f); return; }
            float span = Math.Max(right - left, bottom - top);
            float zoom = Math.Max(1f, Math.Min(8f, .62f / Math.Max(.04f, span)));
            ApplyView(zoom, (left + right) / 2f, (top + bottom) / 2f);
        }

        private void ApplyView(float zoom, float centerX, float centerY)
        {
            float oldZoom = _zoom, oldX = _centerX, oldY = _centerY;
            _zoom = zoom; _centerX = centerX; _centerY = centerY;
            ClampCenter();
            if (Math.Abs(oldZoom - _zoom) < .0001f && Math.Abs(oldX - _centerX) < .0001f && Math.Abs(oldY - _centerY) < .0001f) return;
            ClearProjection();
            Invalidate();
            Action<float> handler = ZoomChanged;
            if (handler != null) handler(_zoom);
        }

        private void UpdateRouteTimer()
        {
            if (!_routeMode || !_routeSourcePosition.HasValue || !_routeDestinationPosition.HasValue ||
                _routeSourcePosition == _routeDestinationPosition || (!_routeUploading && !_routeDownloading) ||
                !Visible || IsDisposed || Disposing || !IsHandleCreated || _resizePreview || !PageTransition.AnimationsEnabled)
            {
                StopRouteTimer();
                return;
            }
            if (_routeTimer.Enabled) return;
            _routeElapsed.Restart();
            _routeNextMotionCheck = 1000;
            _routeTimer.Start();
        }

        private void StopRouteTimer()
        {
            if (IsDisposed || Disposing) return;
            _routeTimer.Stop();
            _routeElapsed.Reset();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateRouteTimer();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            StopRouteTimer();
            base.OnHandleDestroyed(e);
        }

        private void AdvanceRouteTraffic(object sender, EventArgs e)
        {
            long elapsed = _routeElapsed.ElapsedMilliseconds;
            if (elapsed >= _routeNextMotionCheck)
            {
                _routeNextMotionCheck = elapsed + 1000;
                if (!PageTransition.AnimationsEnabled) StopRouteTimer();
            }
            if (!_routeDirtyBounds.IsEmpty) Invalidate(_routeDirtyBounds);
        }

        private void BuildRouteGeometry(float scale)
        {
            _routeCurve = null;
            _routeDirtyBounds = Rectangle.Empty;
            _projectedHops.Clear();
            foreach (PointF? hop in _routeHops) _projectedHops.Add(ProjectRoutePoint(hop));
            PointF? start = ProjectRoutePoint(_routeSourcePosition), end = ProjectRoutePoint(_routeDestinationPosition);
            if (start.HasValue && end.HasValue && DistanceSquared(start.Value, end.Value) > 1f)
            {
                float dx = end.Value.X - start.Value.X, dy = end.Value.Y - start.Value.Y;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                float arc = Math.Min(S(68, scale), distance * .31f);
                RouteCurve curve = new RouteCurve { Start = start.Value, End = end.Value,
                    Control = new PointF((start.Value.X + end.Value.X) / 2f - dy / distance * arc,
                        (start.Value.Y + end.Value.Y) / 2f - Math.Abs(dx) / distance * arc) };
                _routeCurve = curve;
                RectangleF dirty = RectangleF.FromLTRB(Math.Min(curve.Control.X, Math.Min(curve.Start.X, curve.End.X)),
                    Math.Min(curve.Control.Y, Math.Min(curve.Start.Y, curve.End.Y)), Math.Max(curve.Control.X, Math.Max(curve.Start.X, curve.End.X)),
                    Math.Max(curve.Control.Y, Math.Max(curve.Start.Y, curve.End.Y)));
                _routeDirtyBounds = Rectangle.Ceiling(RectangleF.Intersect(_mapBounds, RectangleF.Inflate(dirty, S(12, scale), S(12, scale))));
            }
            _routeGeometryDirty = false;
        }

        private PointF? ProjectRoutePoint(PointF? point)
        {
            return point.HasValue ? Project(point.Value.X, point.Value.Y) : (PointF?)null;
        }

        private void DrawRoute(Graphics graphics, float scale, Rectangle clip)
        {
            if (_routeGeometryDirty) BuildRouteGeometry(scale);
            GraphicsState state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.SetClip(_mapBounds, CombineMode.Intersect);
            DrawRouteHops(graphics, scale);
            if (_routeCurve.HasValue) DrawTrafficCurve(graphics, scale, _routeCurve.Value);
            PointF? start = ProjectRoutePoint(_routeSourcePosition), end = ProjectRoutePoint(_routeDestinationPosition);
            if (start.HasValue) DrawRouteEndpoint(graphics, scale, start.Value, _routeSource, "起点", UploadColor, true);
            if (end.HasValue) DrawRouteEndpoint(graphics, scale, end.Value, _routeDestination, "终点", DownloadColor, false);
            graphics.Restore(state);
            if (clip.Top <= S(57, scale) || clip.Bottom >= Height - S(32, scale)) DrawRouteLegend(graphics, scale);
        }

        private void DrawRouteHops(Graphics graphics, float scale)
        {
            using (Pen pen = new Pen(Color.FromArgb(186, HopColor), 1.5f * scale))
            using (Brush brush = new SolidBrush(HopColor))
            {
                PointF? previous = null;
                foreach (PointF? hop in _projectedHops)
                {
                    if (hop.HasValue)
                    {
                        PointF point = hop.Value;
                        if (previous.HasValue && previous.Value != point) graphics.DrawLine(pen, previous.Value, point);
                        graphics.FillEllipse(brush, point.X - 3f * scale, point.Y - 3f * scale, 6f * scale, 6f * scale);
                    }
                    previous = hop;
                }
            }
        }

        private void DrawTrafficCurve(Graphics graphics, float scale, RouteCurve curve)
        {
            PointF c1 = new PointF(curve.Start.X + (curve.Control.X - curve.Start.X) * 2f / 3f, curve.Start.Y + (curve.Control.Y - curve.Start.Y) * 2f / 3f);
            PointF c2 = new PointF(curve.End.X + (curve.Control.X - curve.End.X) * 2f / 3f, curve.End.Y + (curve.Control.Y - curve.End.Y) * 2f / 3f);
            using (Pen pen = new Pen(Color.FromArgb(116, UploadColor), 1.6f * scale) { DashStyle = DashStyle.Dash })
                graphics.DrawBezier(pen, curve.Start, c1, c2, curve.End);
            double phase = (_routeElapsed.Elapsed.TotalMilliseconds % 1800d) / 1800d;
            if (_routeUploading) DrawRouteDirection(graphics, scale, curve, phase, false);
            if (_routeDownloading) DrawRouteDirection(graphics, scale, curve, phase, true);
        }

        private void DrawRouteDirection(Graphics graphics, float scale, RouteCurve curve, double phase, bool reverse)
        {
            Color color = reverse ? DownloadColor : UploadColor;
            if (!_routeTimer.Enabled)
            {
                DrawDirectionArrow(graphics, scale, curve, reverse ? .62f : .38f, reverse, color);
                return;
            }

            for (int i = 0; i < 3; i++)
            {
                float t = (float)((phase + i / 3d + (reverse ? .17d : 0d)) % 1d);
                float opacity = Math.Min(1f, Math.Min(t, 1f - t) * 8f);
                float eased = t * t * (3f - 2f * t);
                float position = reverse ? 1f - eased : eased;
                DrawDirectionTrail(graphics, scale, curve, position, reverse, color, opacity);
                DrawDirectionArrow(graphics, scale, curve, position, reverse, Color.FromArgb((int)(255 * opacity), color));
            }
        }

        private static PointF TrafficPosition(RouteCurve curve, float t, float scale, bool reverse, out PointF direction)
        {
            PointF position = curve.At(t);
            PointF earlier = curve.At(Math.Max(0, t - .012f)), later = curve.At(Math.Min(1, t + .012f));
            float dx = later.X - earlier.X, dy = later.Y - earlier.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            float signedLength = reverse ? -length : length;
            direction = length > .001f ? new PointF(dx / signedLength, dy / signedLength) : PointF.Empty;
            position.X += direction.Y * 3.5f * scale;
            position.Y -= direction.X * 3.5f * scale;
            return position;
        }

        private static void DrawDirectionTrail(Graphics graphics, float scale, RouteCurve curve, float t, bool reverse, Color color, float opacity)
        {
            if (opacity <= 0) return;
            PointF direction;
            PointF previous = TrafficPosition(curve, t, scale, reverse, out direction);
            float step = (reverse ? 1f : -1f) * Math.Min(.04f, 5f * scale / (float)Math.Sqrt(DistanceSquared(curve.Start, curve.End)));
            using (Pen pen = new Pen(color, 2.2f * scale) { EndCap = LineCap.Round, StartCap = LineCap.Round })
            {
                for (int segment = 1; segment <= 3; segment++)
                {
                    float trailPosition = t + step * segment;
                    if (trailPosition < 0 || trailPosition > 1) break;
                    PointF next = TrafficPosition(curve, trailPosition, scale, reverse, out direction);
                    pen.Color = Color.FromArgb((int)((150 - 37 * (segment - 1)) * opacity), color);
                    graphics.DrawLine(pen, previous, next);
                    previous = next;
                }
            }
        }

        private static void DrawDirectionArrow(Graphics graphics, float scale, RouteCurve curve, float t, bool reverse, Color color)
        {
            PointF direction;
            PointF position = TrafficPosition(curve, t, scale, reverse, out direction);
            float ux = direction.X, uy = direction.Y;
            using (Brush brush = new SolidBrush(color))
                graphics.FillPolygon(brush, new[] {
                    new PointF(position.X + ux * 4f * scale, position.Y + uy * 4f * scale),
                    new PointF(position.X + (-ux * 3f + uy * 2.7f) * scale, position.Y + (-uy * 3f - ux * 2.7f) * scale),
                    new PointF(position.X + (-ux * 3f - uy * 2.7f) * scale, position.Y + (-uy * 3f + ux * 2.7f) * scale) });
        }

        private void DrawRouteEndpoint(Graphics graphics, float scale, PointF point, WorldMapRoutePoint endpoint, string fallback, Color color, bool source)
        {
            if (!_mapBounds.Contains(point)) return;
            float radius = 4.5f * scale;
            using (Brush halo = new SolidBrush(Color.FromArgb(42, color)))
                graphics.FillEllipse(halo, point.X - radius * 2, point.Y - radius * 2, radius * 4, radius * 4);
            using (Brush fill = new SolidBrush(UiPalette.Surface))
            using (Pen pen = new Pen(color, 2f * scale))
            {
                graphics.FillEllipse(fill, point.X - radius, point.Y - radius, radius * 2, radius * 2);
                graphics.DrawEllipse(pen, point.X - radius, point.Y - radius, radius * 2, radius * 2);
            }
            string label = RouteLabel(endpoint, fallback);
            int textHeight = S(19, scale), gap = S(10, scale), x = (int)point.X, y = (int)point.Y;
            int maxTextWidth = Math.Min(S(160, scale), Math.Max(S(32, scale), (int)_mapBounds.Width / 3));
            int textWidth = Math.Min(maxTextWidth, TextRenderer.MeasureText(label, _bodyFont).Width + S(10, scale));
            int labelX = source ? x + gap : x - gap - textWidth;
            int labelY = source ? y - gap - textHeight : y + gap;
            labelX = Math.Max((int)_mapBounds.Left + 1, Math.Min((int)_mapBounds.Right - textWidth, labelX));
            labelY = Math.Max((int)_mapBounds.Top + 1, Math.Min((int)_mapBounds.Bottom - textHeight, labelY));
            Rectangle bounds = new Rectangle(labelX, labelY, textWidth, textHeight);
            UiPaint.RoundRect(graphics, bounds, S(4, scale), Color.FromArgb(238, UiPalette.Surface));
            DrawText(graphics, label, _bodyFont, Rectangle.Inflate(bounds, -S(4, scale), 0), UiPalette.Ink, TextFormatFlags.Left);
        }

        private static string RouteLabel(WorldMapRoutePoint endpoint, string fallback)
        {
            return endpoint == null || String.IsNullOrWhiteSpace(endpoint.Label) ? fallback : endpoint.Label;
        }

        private void DrawRouteLegend(Graphics graphics, float scale)
        {
            int inset = S(20, scale), width = Math.Max(0, Width - 2 * inset);
            UiPaint.DrawIcon(graphics, "globe", new Rectangle(inset, S(14, scale), S(18, scale), S(18, scale)), UiPalette.Accent, 1.5f);
            DrawText(graphics, "连接路径地图", _titleFont, new Rectangle(inset + S(27, scale), S(13, scale), width, S(22, scale)), UiPalette.Ink, TextFormatFlags.Left);
            string coverage = _routeDemo ? "示例动画 · 演示路线和流量，不代表实际连接" :
                !_routeSourcePosition.HasValue && !_routeDestinationPosition.HasValue ? "起点与终点均未定位" :
                !_routeSourcePosition.HasValue ? "起点未定位" : !_routeDestinationPosition.HasValue ? "终点未定位" : "虚线为端点示意";
            if (!PageTransition.AnimationsEnabled && (_routeUploading || _routeDownloading))
                coverage += " · 系统动画已关闭，方向箭头静态显示";
            DrawText(graphics, coverage, _bodyFont, new Rectangle(inset, S(38, scale), width, S(19, scale)), UiPalette.Muted, TextFormatFlags.Left);
            int top = Height - S(28, scale);
            DrawText(graphics, _routeUploading ? (_routeDemo ? "↑ 示例上传" : "↑ 上传") : "上传空闲", _bodyFont,
                new Rectangle(inset, top, S(82, scale), S(19, scale)), _routeUploading ? UploadColor : UiPalette.Muted, TextFormatFlags.Left);
            DrawText(graphics, _routeDownloading ? (_routeDemo ? "↓ 示例下载" : "↓ 下载") : "下载空闲", _bodyFont,
                new Rectangle(inset + S(86, scale), top, S(82, scale), S(19, scale)), _routeDownloading ? DownloadColor : UiPalette.Muted, TextFormatFlags.Left);
            if (_routeHops.Count > 0)
                DrawText(graphics, "逐跳 · 国家代表点", _bodyFont, new Rectangle(inset + S(174, scale), top,
                    Math.Max(0, width - S(174, scale)), S(19, scale)), HopColor, TextFormatFlags.Left);
        }

        private void UpdateRouteAccessibility()
        {
            string motionStatus = !PageTransition.AnimationsEnabled && (_routeUploading || _routeDownloading)
                ? "系统动画已关闭，上传和下载方向以静态箭头表示。" : "";
            AccessibleDescription = "连接路径地图。起点 " + RouteLabel(_routeSource, "未知") + "，终点 " + RouteLabel(_routeDestination, "未知") +
                "。虚线为端点示意，逐跳图层使用国家代表点。" + (_routeUploading ? "正在上传。" : "上传空闲。") +
                (_routeDownloading ? "正在下载。" : "下载空闲。") + motionStatus +
                (_routeDemo ? " 当前是演示路线与方向动画，不代表实际连接或流量。" : "");
        }
    }
}
