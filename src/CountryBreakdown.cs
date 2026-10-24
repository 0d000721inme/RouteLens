using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class CountryStat
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
    }

    public sealed class CountryBreakdown : Control
    {
        private readonly List<CountryStat> _countries = new List<CountryStat>();
        private readonly Rectangle[] _rowBounds = new Rectangle[3];
        private readonly Font _bodyFont = UiPalette.Body(9.3f);
        private readonly Font _footerFont = UiPalette.Body(8.4f);
        private readonly Font _numberFont = UiPalette.Mono(10f);
        private readonly ToolTip _tips = new ToolTip();
        private string _selectedCode = "";
        private int _total;
        private int _unknown;
        private int _hoverIndex = -1;
        private int _focusIndex;

        public event Action<string> CountrySelected;

        public CountryBreakdown()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);
            BackColor = UiPalette.Surface;
            ForeColor = UiPalette.Ink;
            Size = new Size(420, 124);
            TabStop = true;
            AccessibleName = "连接目的地国家分布";
            AccessibleRole = AccessibleRole.List;
            _tips.InitialDelay = 400;
            _tips.ReshowDelay = 100;
            _tips.AutoPopDelay = 8000;
        }

        public string SelectedCode
        {
            get { return _selectedCode; }
            set
            {
                string code = (value ?? "").ToUpperInvariant();
                if (_selectedCode == code) return;
                _selectedCode = code;
                for (int i = 0; i < _countries.Count; i++)
                    if (_countries[i].Code == code) { _focusIndex = i; break; }
                Invalidate();
            }
        }

        public void SetData(List<CountryStat> countries, int total, int unknown)
        {
            string focusedCode = _focusIndex >= 0 && _focusIndex < _countries.Count ? _countries[_focusIndex].Code : null;
            List<CountryStat> previous = new List<CountryStat>(_countries);
            int previousTotal = _total;
            int previousUnknown = _unknown;
            _countries.Clear();
            if (countries != null)
            {
                foreach (CountryStat country in countries)
                {
                    if (country == null || country.Count <= 0 || String.IsNullOrWhiteSpace(country.Code)) continue;
                    string code = country.Code.Trim().ToUpperInvariant();
                    _countries.Add(new CountryStat
                    {
                        Code = code,
                        Name = String.IsNullOrWhiteSpace(country.Name) ? code : country.Name,
                        Count = country.Count
                    });
                }
            }
            _countries.Sort(delegate(CountryStat a, CountryStat b)
            {
                int byCount = b.Count.CompareTo(a.Count);
                return byCount != 0 ? byCount : String.CompareOrdinal(a.Code, b.Code);
            });
            if (_countries.Count > 3) _countries.RemoveRange(3, _countries.Count - 3);
            _total = Math.Max(0, total);
            _unknown = Math.Max(0, Math.Min(unknown, _total));
            _focusIndex = Math.Max(0, Math.Min(_focusIndex, _countries.Count - 1));
            if (focusedCode != null)
                for (int i = 0; i < _countries.Count; i++)
                    if (_countries[i].Code == focusedCode) { _focusIndex = i; break; }
            bool unchanged = previous.Count == _countries.Count && previousTotal == _total && previousUnknown == _unknown;
            if (unchanged)
                for (int i = 0; i < _countries.Count; i++)
                    if (previous[i].Code != _countries[i].Code || previous[i].Name != _countries[i].Name || previous[i].Count != _countries[i].Count)
                    { unchanged = false; break; }
            if (!unchanged)
            {
                _hoverIndex = -1;
                _tips.SetToolTip(this, "");
            }
            AccessibleDescription = FooterText() + "。" + UnknownText() + "。使用上下方向键选择国家，按回车筛选。";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            using (Brush background = new SolidBrush(BackColor)) graphics.FillRectangle(background, ClientRectangle);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = graphics.DpiX / 96f;
            int inset = Scale(6, scale);
            int rowHeight = Scale(28, scale);
            int top = Scale(2, scale);
            int footerTop = Height - Scale(36, scale);
            if (top + rowHeight * 3 > footerTop)
                rowHeight = Math.Max(1, (footerTop - top) / 3);
            for (int i = 0; i < _rowBounds.Length; i++) _rowBounds[i] = Rectangle.Empty;

            if (_countries.Count == 0)
            {
                UiPaint.DrawIcon(graphics, "globe", new Rectangle(inset + Scale(1, scale), top + Scale(16, scale), Scale(22, scale), Scale(22, scale)), UiPalette.Muted, 1.5f);
                TextRenderer.DrawText(graphics, _total == 0 ? "暂无公网目标连接" : "等待国家识别", _bodyFont,
                    new Rectangle(inset + Scale(34, scale), top + Scale(10, scale), Math.Max(0, Width - Scale(45, scale)), Scale(26, scale)),
                    UiPalette.Ink, UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(graphics, "识别完成后显示连接最多的 3 个国家", _footerFont,
                    new Rectangle(inset + Scale(34, scale), top + Scale(36, scale), Math.Max(0, Width - Scale(45, scale)), Scale(20, scale)),
                    UiPalette.Muted, UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
            else
            {
                for (int i = 0; i < _countries.Count; i++)
                {
                    Rectangle row = new Rectangle(0, top + i * rowHeight, Width, rowHeight);
                    _rowBounds[i] = row;
                    DrawCountry(graphics, _countries[i], row, i, scale);
                }
            }

            Rectangle footer = new Rectangle(inset, footerTop, Math.Max(0, Width - inset * 2), Scale(18, scale));
            TextRenderer.DrawText(graphics, FooterText(), _footerFont, footer, UiPalette.Muted,
                UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            footer.Y += Scale(17, scale);
            TextRenderer.DrawText(graphics, UnknownText(), _footerFont, footer, UiPalette.Muted,
                UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        private void DrawCountry(Graphics graphics, CountryStat country, Rectangle row, int index, float scale)
        {
            bool selected = country.Code == _selectedCode;
            if (selected || index == _hoverIndex)
            {
                Rectangle highlight = Rectangle.Inflate(row, -Scale(1, scale), -Scale(1, scale));
                UiPaint.RoundRect(graphics, highlight, Scale(4, scale), selected ? UiPalette.AccentSoft : UiPalette.Raised);
            }
            int inset = Scale(7, scale);
            int flagWidth = Scale(22, scale);
            int flagHeight = Scale(15, scale);
            Rectangle flagRect = new Rectangle(inset, row.Y + (row.Height - flagHeight) / 2, flagWidth, flagHeight);
            Image flag = Flags.Get(country.Code);
            if (flag != null)
            {
                graphics.DrawImage(flag, flagRect);
                using (Pen pen = new Pen(Color.FromArgb(32, UiPalette.Ink), 1f))
                    graphics.DrawRectangle(pen, flagRect.X, flagRect.Y, flagRect.Width - 1, flagRect.Height - 1);
            }
            else
            {
                UiPaint.RoundRect(graphics, flagRect, Scale(2, scale), UiPalette.Raised);
                TextRenderer.DrawText(graphics, country.Code, _footerFont, flagRect, UiPalette.Muted,
                    UiPaint.TextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }

            int nameLeft = flagRect.Right + Scale(11, scale);
            int countWidth = Scale(76, scale);
            int nameWidth = Math.Min(Scale(104, scale), Math.Max(Scale(60, scale), Width / 4));
            Rectangle nameRect = new Rectangle(nameLeft, row.Y, nameWidth, row.Height);
            TextRenderer.DrawText(graphics, country.Name, _bodyFont, nameRect, selected ? UiPalette.Accent : UiPalette.Ink,
                UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            int barLeft = nameRect.Right + Scale(10, scale);
            int barRight = Width - countWidth - Scale(14, scale);
            int barWidth = Math.Max(0, barRight - barLeft);
            int barHeight = Scale(5, scale);
            int barTop = row.Y + (row.Height - barHeight) / 2;
            if (barWidth > 0)
            {
                UiPaint.RoundRect(graphics, new Rectangle(barLeft, barTop, barWidth, barHeight), Scale(2, scale), UiPalette.Border);
                // Every bar uses all currently filtered public TCP connections as its denominator.
                double fraction = _total > 0 ? Math.Min(1, Math.Max(0, (double)country.Count / _total)) : 0;
                int filled = fraction > 0 ? Math.Max(Scale(3, scale), (int)Math.Round(barWidth * fraction)) : 0;
                if (filled > 0) UiPaint.RoundRect(graphics, new Rectangle(barLeft, barTop, Math.Min(barWidth, filled), barHeight), Scale(2, scale), UiPalette.Accent);
            }

            Rectangle numberRect = new Rectangle(Width - countWidth, row.Y, countWidth - Scale(25, scale), row.Height);
            TextRenderer.DrawText(graphics, country.Count.ToString("N0"), _numberFont, numberRect, UiPalette.Ink,
                UiPaint.TextFlags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(graphics, "条", _footerFont,
                new Rectangle(numberRect.Right + Scale(5, scale), row.Y, Scale(20, scale), row.Height), UiPalette.Muted,
                UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues && index == _focusIndex)
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(row, -Scale(2, scale), -Scale(2, scale)), UiPalette.Accent, BackColor);
        }

        private string FooterText()
        {
            return "搜索范围 · 已识别 " + (_total - _unknown).ToString("N0") + " / " + _total.ToString("N0") + " 条公网目标连接";
        }

        private string UnknownText()
        {
            return _unknown > 0 ? "其中 " + _unknown.ToString("N0") + " 条尚未识别" : (_total > 0 ? "当前连接均已识别 · 点击国家筛选" : "连接建立后会自动更新");
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTest(e.Location);
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            if (index == _hoverIndex) return;
            _hoverIndex = index;
            if (index >= 0)
            {
                CountryStat country = _countries[index];
                string percent = _total > 0 ? ((double)country.Count / _total).ToString("P1") : "0%";
                _tips.SetToolTip(this, country.Name + " · " + country.Count.ToString("N0") + " 条，占当前公网目标连接 " + percent + "。点击按国家筛选。");
            }
            else _tips.SetToolTip(this, "");
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            Cursor = Cursors.Default;
            _tips.SetToolTip(this, "");
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int index = HitTest(e.Location);
            if (index < 0) return;
            Focus();
            _focusIndex = index;
            SelectCountry(index);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_countries.Count == 0) return;
            if (e.KeyCode == Keys.Up) _focusIndex = Math.Max(0, _focusIndex - 1);
            else if (e.KeyCode == Keys.Down) _focusIndex = Math.Min(_countries.Count - 1, _focusIndex + 1);
            else if (e.KeyCode == Keys.Home) _focusIndex = 0;
            else if (e.KeyCode == Keys.End) _focusIndex = _countries.Count - 1;
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) SelectCountry(_focusIndex);
            else return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        private void SelectCountry(int index)
        {
            if (index >= 0 && index < _countries.Count)
            {
                Action<string> handler = CountrySelected;
                if (handler != null) handler(_countries[index].Code);
                Invalidate();
            }
        }

        private int HitTest(Point point)
        {
            for (int i = 0; i < _countries.Count; i++) if (_rowBounds[i].Contains(point)) return i;
            return -1;
        }

        private static int Scale(int value, float scale) { return Math.Max(1, (int)Math.Round(value * scale)); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tips.Dispose(); _bodyFont.Dispose(); _footerFont.Dispose(); _numberFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
