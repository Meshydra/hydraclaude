// The two custom controls the manager window is built from.
//
// Both are fully owner-drawn: WinForms' stock ListView and Button cannot be themed, and the
// app has to follow the Windows light/dark setting. Drawing is kept cheap - double buffered,
// text through TextRenderer (GDI, no layout allocations), brushes taken from the baked palette,
// and hover only invalidates the row that changed rather than the whole control.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HydraClaude
{
    public static class Draw
    {
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s, f, r, c, flags);
        }
    }

    public enum ButtonKind { Primary, Ghost }

    // A button with every state designed - hover, pressed, disabled and a real focus ring
    // (craft R11). No gradient, no glow: emphasis is fill, weight and space.
    public class FlatButton : Control
    {
        public ButtonKind Kind = ButtonKind.Ghost;
        public int Radius = 6;

        bool _hover, _down;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.FontBodyBold;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Space || k == Keys.Enter || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                PerformClick();
            }
            base.OnKeyDown(e);
        }

        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.P;
            g.Clear(Parent != null ? Parent.BackColor : p.SurfaceBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill, text, border = Color.Empty;

            if (Kind == ButtonKind.Primary)
            {
                fill = !Enabled ? p.Hairline : _down ? p.AccentActive : _hover ? p.AccentHover : p.Accent;
                text = !Enabled ? p.TextMuted : p.AccentText;
            }
            else
            {
                fill = !Enabled ? Color.Transparent : _down ? p.RowSelected : _hover ? p.GhostHover : Color.Transparent;
                text = !Enabled ? p.TextMuted : p.Text;
                border = p.Hairline;
            }

            using (var path = Draw.RoundRect(r, Radius))
            {
                if (fill != Color.Transparent)
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != Color.Empty)
                    using (var pen = new Pen(border)) g.DrawPath(pen, path);

                if (Focused && ShowFocusCues)
                {
                    var fr = Rectangle.Inflate(r, -3, -3);
                    using (var fp = Draw.RoundRect(fr, Math.Max(2, Radius - 3)))
                    using (var pen = new Pen(Kind == ButtonKind.Primary ? p.AccentText : p.Accent))
                        g.DrawPath(pen, fp);
                }
            }

            g.SmoothingMode = SmoothingMode.Default;
            Draw.Text(g, Text, Font, text, new Rectangle(0, 0, Width, Height),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    public class ProfileRow
    {
        public ProfileEntry Entry;
        public bool Running;
        public string Path;
        public string When;
    }

    public class ProfileList : Control
    {
        public List<ProfileRow> Rows = new List<ProfileRow>();
        public event EventHandler Activated;          // double-click or Enter
        public event EventHandler SelectionChanged;
        public event MouseEventHandler RowContextMenu;

        int _selected = -1, _hover = -1, _scroll, _thumbGrab = -1;
        const int ScrollbarWidth = 8;

        public ProfileList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = true;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = Math.Max(-1, Math.Min(value, Rows.Count - 1));
                if (v == _selected) return;
                _selected = v;
                EnsureVisible(v);
                Invalidate();
                var h = SelectionChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public ProfileRow Selected
        {
            get { return _selected >= 0 && _selected < Rows.Count ? Rows[_selected] : null; }
        }

        int ContentHeight { get { return Rows.Count * Metrics.RowHeight; } }
        int MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }

        public void SetRows(List<ProfileRow> rows, string keepName)
        {
            Rows = rows;
            _selected = -1;
            if (keepName != null)
            {
                for (int i = 0; i < rows.Count; i++)
                    if (string.Equals(rows[i].Entry.Name, keepName, StringComparison.OrdinalIgnoreCase))
                        _selected = i;
            }
            if (_selected < 0 && rows.Count > 0) _selected = 0;
            _scroll = Math.Min(_scroll, MaxScroll);
            Invalidate();
            var h = SelectionChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        void EnsureVisible(int i)
        {
            if (i < 0) return;
            int top = i * Metrics.RowHeight, bottom = top + Metrics.RowHeight;
            if (top < _scroll) _scroll = top;
            else if (bottom > _scroll + Height) _scroll = bottom - Height;
            _scroll = Math.Max(0, Math.Min(_scroll, MaxScroll));
        }

        Rectangle RowRect(int i)
        {
            return new Rectangle(0, i * Metrics.RowHeight - _scroll, Width, Metrics.RowHeight);
        }

        int HitTest(int y)
        {
            if (Rows.Count == 0) return -1;
            int i = (y + _scroll) / Metrics.RowHeight;
            return i >= 0 && i < Rows.Count ? i : -1;
        }

        // ------------------------------------------------------------ input

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_thumbGrab >= 0) { DragThumb(e.Y); return; }
            int i = HitTest(e.Y);
            if (i != _hover)
            {
                // Repaint only the two rows that changed.
                if (_hover >= 0) Invalidate(RowRect(_hover));
                if (i >= 0) Invalidate(RowRect(i));
                _hover = i;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover >= 0) { Invalidate(RowRect(_hover)); _hover = -1; }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (MaxScroll > 0 && e.X >= Width - ScrollbarWidth)
            {
                var thumb = ThumbRect();
                if (thumb.Contains(e.X, e.Y)) { _thumbGrab = e.Y - thumb.Y; return; }
            }
            int i = HitTest(e.Y);
            if (i >= 0) SelectedIndex = i;
            if (e.Button == MouseButtons.Right && i >= 0)
            {
                var h = RowContextMenu;
                if (h != null) h(this, e);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { _thumbGrab = -1; base.OnMouseUp(e); }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HitTest(e.Y) >= 0) Fire();
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (MaxScroll <= 0) return;
            _scroll = Math.Max(0, Math.Min(_scroll - e.Delta / 120 * Metrics.RowHeight, MaxScroll));
            Invalidate();
        }

        protected override bool IsInputKey(Keys k)
        {
            switch (k)
            {
                case Keys.Up: case Keys.Down: case Keys.Enter:
                case Keys.Home: case Keys.End: return true;
            }
            return base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up: SelectedIndex = Math.Max(0, _selected - 1); e.Handled = true; break;
                case Keys.Down: SelectedIndex = Math.Min(Rows.Count - 1, _selected + 1); e.Handled = true; break;
                case Keys.Home: SelectedIndex = 0; e.Handled = true; break;
                case Keys.End: SelectedIndex = Rows.Count - 1; e.Handled = true; break;
                case Keys.Enter: Fire(); e.Handled = true; break;
            }
            base.OnKeyDown(e);
        }

        void Fire()
        {
            var h = Activated;
            if (h != null && Selected != null) h(this, EventArgs.Empty);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        // ------------------------------------------------------------ paint

        Rectangle ThumbRect()
        {
            int track = Height;
            int h = Math.Max(Metrics.S(28), (int)(track * (track / (float)ContentHeight)));
            int y = (int)((track - h) * (_scroll / (float)MaxScroll));
            return new Rectangle(Width - ScrollbarWidth + Metrics.S(2), y,
                                 ScrollbarWidth - Metrics.S(4), h);
        }

        void DragThumb(int mouseY)
        {
            int track = Height, h = ThumbRect().Height;
            float t = (mouseY - _thumbGrab) / (float)Math.Max(1, track - h);
            _scroll = Math.Max(0, Math.Min((int)(t * MaxScroll), MaxScroll));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.P;
            g.FillRectangle(p.BrSurface, ClientRectangle);

            if (Rows.Count == 0) { PaintEmpty(g, p); return; }

            int gutter = Metrics.Gutter;
            int textLeft = gutter + Metrics.S(16);
            int first = Math.Max(0, (_scroll + e.ClipRectangle.Top) / Metrics.RowHeight);
            int last = Math.Min(Rows.Count - 1,
                (_scroll + e.ClipRectangle.Bottom) / Metrics.RowHeight);

            for (int i = first; i <= last; i++)
            {
                var row = Rows[i];
                var r = RowRect(i);
                bool selected = i == _selected;
                int rightPad = MaxScroll > 0 ? ScrollbarWidth + Metrics.S(2) : 0;

                // Hover and selection are rounded cards rather than full-bleed bands: it is the
                // Windows 11 list idiom, and it means the row needs no separator rule to be read
                // as one unit (craft R8 - proximity does the grouping).
                if (selected || i == _hover)
                {
                    var card = new Rectangle(Metrics.S(10), r.Y + Metrics.S(3),
                                             Width - Metrics.S(20) - rightPad,
                                             r.Height - Metrics.S(6));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = Draw.RoundRect(card, Metrics.S(8)))
                    {
                        g.FillPath(selected ? p.BrRowSelected : p.BrRowHover, path);
                        if (selected && Focused)
                            using (var pen = new Pen(p.Accent))
                                g.DrawPath(pen, path);
                    }

                    if (selected)
                    {
                        var pill = new Rectangle(card.X + Metrics.S(4), r.Y + Metrics.S(16),
                                                 Metrics.S(3), r.Height - Metrics.S(32));
                        using (var path = Draw.RoundRect(pill, Metrics.S(2)))
                            g.FillPath(p.BrAccent, path);
                    }
                    g.SmoothingMode = SmoothingMode.Default;
                }

                if (row.Running)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    int d = Metrics.S(8);
                    g.FillEllipse(p.BrLiveText,
                        gutter, r.Y + (r.Height - d) / 2 - Metrics.S(8), d, d);
                    g.SmoothingMode = SmoothingMode.Default;
                }

                int rightEdge = Width - gutter - rightPad;

                // Right column first, so the name can be clipped against it rather than overlap.
                var stateRect = new Rectangle(rightEdge - Metrics.S(120), r.Y + Metrics.S(12),
                                              Metrics.S(120), Metrics.S(18));
                if (row.Running)
                    Draw.Text(g, "Running", Theme.FontChip, p.LiveText, stateRect,
                        TextFormatFlags.Right | TextFormatFlags.NoPrefix);

                var whenRect = new Rectangle(rightEdge - Metrics.S(160), r.Y + Metrics.S(32),
                                             Metrics.S(160), Metrics.S(18));
                if (!string.IsNullOrEmpty(row.When))
                    Draw.Text(g, row.When, Theme.FontBody, p.TextMuted, whenRect,
                        TextFormatFlags.Right | TextFormatFlags.NoPrefix);

                int nameWidth = stateRect.Left - textLeft - Metrics.S(12);
                Draw.Text(g, row.Entry.Name, Theme.FontRow, p.Text,
                    new Rectangle(textLeft, r.Y + Metrics.S(10), nameWidth, Metrics.S(20)),
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                Draw.Text(g, row.Path, Theme.FontBody, p.TextMuted,
                    new Rectangle(textLeft, r.Y + Metrics.S(31), whenRect.Left - textLeft - Metrics.S(12), Metrics.S(18)),
                    TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
            }

            if (MaxScroll > 0)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Draw.RoundRect(ThumbRect(), Metrics.S(2)))
                using (var b = new SolidBrush(p.Hairline))
                    g.FillPath(b, path);
                g.SmoothingMode = SmoothingMode.Default;
            }
        }

        // The empty state is designed, not defaulted (craft R11, Rams 8).
        void PaintEmpty(Graphics g, Palette p)
        {
            var r = new Rectangle(0, Height / 2 - Metrics.S(28), Width, Metrics.S(24));
            Draw.Text(g, "No profiles yet", Theme.FontRow, p.Text, r,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            r.Y += Metrics.S(24);
            Draw.Text(g, "Add one to run a second Claude login side by side.",
                Theme.FontBody, p.TextMuted, r,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
