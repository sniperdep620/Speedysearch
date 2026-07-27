using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Speedysearch.Windows
{
    internal static class LauncherTheme
    {
        public static readonly Color Surface = Color.FromArgb(24, 30, 32);
        public static readonly Color Input = Color.FromArgb(10, 14, 15);
        public static readonly Color Card = Color.FromArgb(39, 47, 49);
        public static readonly Color SelectedCard = Color.FromArgb(62, 78, 77);
        public static readonly Color Foreground = Color.FromArgb(239, 245, 244);
        public static readonly Color Muted = Color.FromArgb(145, 158, 158);
        public static readonly Color Dimmed = Color.FromArgb(105, 105, 105);
        public static readonly Color Accent = Color.FromArgb(126, 231, 209);
        public static readonly Color AccentText = Color.FromArgb(10, 35, 31);
        public static readonly Color Border = Color.FromArgb(48, 55, 56);
        public static readonly Color CardBorder = Color.FromArgb(51, 59, 61);
        public static readonly Color SelectedBorder = Color.FromArgb(88, 135, 127);

        public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(1, radius * 2);
            Rectangle arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static StringFormat EllipsisFormat(StringAlignment alignment)
        {
            return new StringFormat
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        private int _radius;
        private Color _fillColor;
        private Color _borderColor;

        public RoundedPanel()
        {
            _radius = 12;
            _fillColor = LauncherTheme.Card;
            _borderColor = LauncherTheme.Border;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
        }

        public int Radius
        {
            get { return _radius; }
            set { _radius = value; Invalidate(); }
        }

        public Color FillColor
        {
            get { return _fillColor; }
            set { _fillColor = value; BackColor = value; Invalidate(); }
        }

        public Color BorderColor
        {
            get { return _borderColor; }
            set { _borderColor = value; Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = LauncherTheme.RoundedRectangle(bounds, _radius))
            using (SolidBrush fill = new SolidBrush(_fillColor))
            using (Pen border = new Pen(_borderColor))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }
    }

    internal sealed class FilterChip : Control
    {
        private bool _selected;
        private bool _hovered;

        public FilterChip()
        {
            Cursor = Cursors.Hand;
            Size = new Size(68, 30);
            TabStop = false;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
        }

        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value)
                {
                    return;
                }
                _selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fillColor = _selected
                ? LauncherTheme.Accent
                : (_hovered ? Color.FromArgb(45, 55, 56) : LauncherTheme.Surface);
            Color borderColor = _selected ? LauncherTheme.Accent : LauncherTheme.Border;
            Color textColor = _selected ? LauncherTheme.AccentText : LauncherTheme.Muted;
            using (GraphicsPath path = LauncherTheme.RoundedRectangle(bounds, 9))
            using (SolidBrush fill = new SolidBrush(fillColor))
            using (Pen border = new Pen(borderColor))
            using (SolidBrush text = new SolidBrush(textColor))
            using (Font font = new Font("Segoe UI", 9.0f))
            using (StringFormat format = LauncherTheme.EllipsisFormat(StringAlignment.Center))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
                e.Graphics.DrawString(Text, font, text, bounds, format);
            }
        }
    }

    internal sealed class IconButton : Control
    {
        private bool _hovered;

        public IconButton()
        {
            Cursor = Cursors.Hand;
            Size = new Size(28, 24);
            TabStop = false;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            if (_hovered)
            {
                using (GraphicsPath path = LauncherTheme.RoundedRectangle(bounds, 7))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(48, 58, 59)))
                {
                    e.Graphics.FillPath(fill, path);
                }
            }
            using (SolidBrush text = new SolidBrush(_hovered
                ? LauncherTheme.Foreground
                : LauncherTheme.Muted))
            using (Font font = new Font("Segoe UI", 12.0f))
            using (StringFormat format = LauncherTheme.EllipsisFormat(StringAlignment.Center))
            {
                e.Graphics.DrawString(Text, font, text, bounds, format);
            }
        }
    }

    internal sealed class SpinnerControl : Control
    {
        private int _phase;

        public SpinnerControl()
        {
            Size = new Size(22, 22);
            TabStop = false;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
        }

        public void Advance()
        {
            _phase = (_phase + 30) % 360;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle arc = new Rectangle(4, 4, Width - 9, Height - 9);
            using (Pen track = new Pen(Color.FromArgb(55, 66, 67), 2.0f))
            using (Pen active = new Pen(LauncherTheme.Accent, 2.0f))
            {
                active.StartCap = LineCap.Round;
                active.EndCap = LineCap.Round;
                e.Graphics.DrawEllipse(track, arc);
                e.Graphics.DrawArc(active, arc, _phase, 105);
            }
        }
    }

    internal sealed class ResultCardControl : Control
    {
        private readonly Font _nameFont;
        private readonly Font _pathFont;
        private readonly Font _symbolFont;
        private readonly Font _kindFont;
        private bool _selected;

        public ResultCardControl(SearchResult result)
        {
            Result = result;
            Height = 66;
            Margin = new Padding(0, 0, 0, 5);
            Cursor = Cursors.Hand;
            TabStop = false;
            _nameFont = new Font("Segoe UI Semibold", 11.0f);
            _pathFont = new Font("Segoe UI", 8.5f);
            _symbolFont = new Font("Segoe UI Symbol", 14.0f);
            _kindFont = new Font("Segoe UI Semibold", 8.0f);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
        }

        public SearchResult Result { get; private set; }

        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value)
                {
                    return;
                }
                _selected = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fillColor = _selected ? LauncherTheme.SelectedCard : LauncherTheme.Card;
            Color borderColor = _selected ? LauncherTheme.SelectedBorder : LauncherTheme.CardBorder;
            using (GraphicsPath path = LauncherTheme.RoundedRectangle(bounds, 12))
            using (SolidBrush fill = new SolidBrush(fillColor))
            using (Pen border = new Pen(borderColor))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }

            Rectangle iconBounds = new Rectangle(12, 12, 42, 42);
            using (GraphicsPath iconPath = LauncherTheme.RoundedRectangle(iconBounds, 9))
            using (SolidBrush iconFill = new SolidBrush(Color.FromArgb(36, 54, 52)))
            using (SolidBrush accent = new SolidBrush(LauncherTheme.Accent))
            using (StringFormat centered = LauncherTheme.EllipsisFormat(StringAlignment.Center))
            {
                e.Graphics.FillPath(iconFill, iconPath);
                e.Graphics.DrawString(
                    EntrySymbol(Result.Entry),
                    _symbolFont,
                    accent,
                    iconBounds,
                    centered);
            }

            int kindWidth = 78;
            Rectangle nameBounds = new Rectangle(66, 8, Math.Max(20, Width - 66 - kindWidth - 14), 28);
            Rectangle pathBounds = new Rectangle(66, 33, Math.Max(20, Width - 66 - kindWidth - 14), 23);
            Rectangle kindBounds = new Rectangle(Width - kindWidth - 12, 0, kindWidth, Height);
            using (SolidBrush nameBrush = new SolidBrush(LauncherTheme.Foreground))
            using (SolidBrush pathBrush = new SolidBrush(LauncherTheme.Muted))
            using (SolidBrush kindBrush = new SolidBrush(_selected
                ? LauncherTheme.Accent
                : LauncherTheme.Dimmed))
            using (StringFormat left = LauncherTheme.EllipsisFormat(StringAlignment.Near))
            using (StringFormat right = LauncherTheme.EllipsisFormat(StringAlignment.Far))
            {
                e.Graphics.DrawString(Result.Entry.Name, _nameFont, nameBrush, nameBounds, left);
                e.Graphics.DrawString(DisplayPath(Result.Entry), _pathFont, pathBrush, pathBounds, left);
                e.Graphics.DrawString(DisplayKind(Result.Entry), _kindFont, kindBrush, kindBounds, right);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _nameFont.Dispose();
                _pathFont.Dispose();
                _symbolFont.Dispose();
                _kindFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private static string EntrySymbol(IndexEntry entry)
        {
            if (entry.EntryType == EntryType.App)
            {
                return "\u25C8";
            }
            if (entry.EntryType == EntryType.File && entry.Metadata.IsDirectory)
            {
                return "\u25A1";
            }
            if (entry.EntryType == EntryType.File)
            {
                return "\u25C7";
            }
            if (entry.EntryType == EntryType.Setting)
            {
                return "\u2301";
            }
            return ">_";
        }

        private static string DisplayKind(IndexEntry entry)
        {
            if (entry.EntryType == EntryType.File && entry.Metadata.IsDirectory)
            {
                return "FOLDER";
            }
            return entry.EntryType.ToString().ToUpperInvariant();
        }

        private static string DisplayPath(IndexEntry entry)
        {
            if (entry.EntryType == EntryType.App || entry.EntryType == EntryType.Setting)
            {
                return String.IsNullOrWhiteSpace(entry.Metadata.Category)
                    ? entry.Path
                    : entry.Metadata.Category;
            }
            try
            {
                string parent = Path.GetDirectoryName(entry.Path);
                return String.IsNullOrWhiteSpace(parent) ? entry.Path : parent;
            }
            catch
            {
                return entry.Path;
            }
        }
    }

    internal sealed class EmptyStateControl : Control
    {
        private readonly Font _symbolFont;
        private readonly Font _titleFont;
        private readonly Font _detailFont;
        private readonly Font _hintFont;

        public EmptyStateControl()
        {
            _symbolFont = new Font("Segoe UI Symbol", 20.0f);
            _titleFont = new Font("Segoe UI Semibold", 12.0f);
            _detailFont = new Font("Segoe UI", 9.5f);
            _hintFont = new Font("Segoe UI", 8.5f);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
        }

        public string TitleText { get; set; }
        public string DetailText { get; set; }
        public string HintText { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(LauncherTheme.Surface);
            int center = Height / 2;
            Rectangle symbol = new Rectangle(0, center - 70, Width, 38);
            Rectangle title = new Rectangle(20, center - 29, Math.Max(0, Width - 40), 28);
            Rectangle detail = new Rectangle(20, center + 1, Math.Max(0, Width - 40), 25);
            Rectangle hint = new Rectangle(20, center + 32, Math.Max(0, Width - 40), 24);
            using (StringFormat format = LauncherTheme.EllipsisFormat(StringAlignment.Center))
            using (SolidBrush accent = new SolidBrush(LauncherTheme.Accent))
            using (SolidBrush foreground = new SolidBrush(LauncherTheme.Foreground))
            using (SolidBrush muted = new SolidBrush(LauncherTheme.Muted))
            using (SolidBrush dimmed = new SolidBrush(LauncherTheme.Dimmed))
            {
                e.Graphics.DrawString("\u2726", _symbolFont, accent, symbol, format);
                e.Graphics.DrawString(TitleText ?? String.Empty, _titleFont, foreground, title, format);
                e.Graphics.DrawString(DetailText ?? String.Empty, _detailFont, muted, detail, format);
                e.Graphics.DrawString(HintText ?? String.Empty, _hintFont, dimmed, hint, format);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _symbolFont.Dispose();
                _titleFont.Dispose();
                _detailFont.Dispose();
                _hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
