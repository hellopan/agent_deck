using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AgentDeck.Tray;

// Colours, fonts and small controls shared by every window. Values mirror
// design/v3-tokens.md: a neutral canvas, white cards, one-pixel hairlines, and
// near-black as the single action colour. Green, amber and red are status only —
// a coloured primary button reads as a second accent and pulls the eye away from
// whatever actually needs attention. Amber survives as `Warning`/`Link`, because
// in this product "needs a human" and "clickable" are the same kind of attention.
internal static class DeckTheme
{
    internal static readonly Color Canvas = Color.FromArgb(245, 246, 247);   // bg
    internal static readonly Color Bar = Color.FromArgb(250, 251, 252);      // bg-alt: rail, table head, footer
    internal static readonly Color Surface = Color.FromArgb(255, 255, 255);  // card
    internal static readonly Color Surface2 = Color.FromArgb(250, 251, 252); // bg-alt inside a card
    internal static readonly Color Field = Color.FromArgb(255, 255, 255);    // input: white + 1px border
    internal static readonly Color Well = Color.FromArgb(250, 251, 252);     // code, output, read-only
    internal static readonly Color Line = Color.FromArgb(227, 230, 234);
    internal static readonly Color Line2 = Color.FromArgb(238, 240, 242);
    internal static readonly Color Hairline = Color.FromArgb(238, 240, 242);
    internal static readonly Color Ink = Color.FromArgb(27, 31, 36);
    internal static readonly Color Muted = Color.FromArgb(90, 97, 107);
    internal static readonly Color Dim = Color.FromArgb(139, 146, 156);
    internal static readonly Color Accent = Color.FromArgb(27, 31, 36);      // act
    internal static readonly Color AccentInk = Color.FromArgb(255, 255, 255); // on-act
    internal static readonly Color AccentHover = Color.FromArgb(43, 49, 56);
    internal static readonly Color AccentDown = Color.FromArgb(17, 21, 26);
    internal static readonly Color Active = Color.FromArgb(240, 242, 244);   // hover
    internal static readonly Color Good = Color.FromArgb(21, 128, 61);
    internal static readonly Color Warning = Color.FromArgb(161, 98, 7);
    internal static readonly Color LinkColor = Color.FromArgb(161, 98, 7);   // == Warning, on purpose
    internal static readonly Color Danger = Color.FromArgb(180, 35, 24);
    internal static readonly Color DangerLine = Color.FromArgb(246, 211, 208);
    internal static readonly Color Selected = Color.FromArgb(233, 236, 239); // nav-active
    internal static readonly Color Badge = Color.FromArgb(230, 233, 236);    // neutral count pill

    // The design is a browser: its stack is -apple-system, "Segoe UI", "Microsoft
    // YaHei". Chrome on Windows therefore draws Latin in Segoe UI and hands CJK to
    // YaHei through font fallback. Pinning YaHei UI for everything put its Latin
    // glyphs — DESKTOP-8F2A, 28ms, wss://… — on screen, which is why the app read
    // heavier and sharper than the mock. Asking for Segoe UI first reproduces the
    // browser's split: GDI font linking still resolves CJK to YaHei.
    private static readonly string UiFamily = PickFamily("Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei");
    private static readonly string MonoFamily = PickFamily("Cascadia Mono", "Cascadia Code", "Consolas");

    // Sizes are the design's pixel values divided by 1.333 (1px = 0.75pt), so a
    // 13px paragraph really measures 13px on a 96dpi screen instead of 12.7.
    internal static readonly Font Body = new(UiFamily, 9.75F);        // 13px
    internal static readonly Font Small = new(UiFamily, 9.375F);      // 12.5px
    internal static readonly Font Tiny = new(UiFamily, 8.625F);       // 11.5px
    internal static readonly Font PillFace = new(UiFamily, 8.25F);    // 11px
    internal static readonly Font Brand = new(UiFamily, 9.75F, FontStyle.Bold);
    internal static readonly Font Heading = new(UiFamily, 13.5F, FontStyle.Bold);  // 18px
    internal static readonly Font Hero = new(UiFamily, 12F, FontStyle.Bold);       // 16px
    internal static readonly Font Section = new(UiFamily, 9.75F, FontStyle.Bold);  // 13px card title
    internal static readonly Font ButtonFace = new(UiFamily, 9.375F);              // 12.5px
    internal static readonly Font ButtonFaceBold = new(UiFamily, 9.375F, FontStyle.Bold);
    internal static readonly Font Monospace = new(MonoFamily, 9F);                 // 12px
    internal static readonly Font MonoSmall = new(MonoFamily, 8.625F);             // 11.5px
    internal static readonly Font MonoMicro = new(MonoFamily, 8.25F);              // 11px
    internal static readonly Font MonoHero = new(MonoFamily, 10.5F);
    internal static readonly Icon AppIcon = CreateAppIcon();

    private static string PickFamily(params string[] candidates)
    {
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
            if (names.Contains(candidate)) return candidate;
        return "Segoe UI";
    }

    internal static void Form(Form form, string title)
    {
        form.Text = title;
        form.Icon = AppIcon;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = Body;
        form.BackColor = Canvas;
        form.ForeColor = Ink;
        form.HandleCreated += (_, _) =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || SystemInformation.HighContrast)
                return;
            // 0 = light title bar. This has to flip to 1 when the dark theme lands;
            // a dark title bar over a #f5f6f7 window looks like a rendering bug.
            int enabled = 0;
            DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private static Icon CreateAppIcon()
    {
        // The real mark lives inside the exe twice: as the Win32 icon group
        // (ApplicationIcon) and as this embedded stream. Pick the 48px frame —
        // the tray needs 16-32px at 100-200% DPI, and downscaling 48 is
        // cleaner than upscaling the old hand-drawn 32px bitmap.
        using var stream = typeof(DeckTheme).Assembly
            .GetManifestResourceStream("AgentDeck.AppIcon.ico");
        if (stream is not null)
        {
            try { return new Icon(stream, 48, 48); }
            catch { /* broken resource: keep the old drawing below */ }
        }
        return DrawLegacyIcon();
    }

    /// <summary>Fallback only, when a build somehow lost the embedded icon.</summary>
    private static Icon DrawLegacyIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Accent);
        using var stroke = new Pen(AccentInk, 2.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.FillRectangle(background, 2, 2, 28, 28);
        graphics.DrawLines(stroke, [new Point(9, 10), new Point(15, 16), new Point(9, 22)]);
        graphics.DrawLine(stroke, 18, 22, 23, 22);
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }
        float diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Clips a control to rounded corners. Child controls are clipped by the
    /// parent region too, which is the only way a WinForms card can keep square
    /// child panels from poking through its corners.
    /// </summary>
    internal static void Round(Control control, float radius)
    {
        void Apply()
        {
            if (control.Width <= 0 || control.Height <= 0) return;
            var previous = control.Region;
            using var path = Rounded(new RectangleF(0, 0, control.Width, control.Height), radius);
            control.Region = new Region(path);
            previous?.Dispose();
        }
        control.SizeChanged += (_, _) => Apply();
        Apply();
    }

    internal static Color Tint(Color color, double amount = 0.16) => Color.FromArgb(
        (int)Math.Round(Surface.R + (color.R - Surface.R) * amount),
        (int)Math.Round(Surface.G + (color.G - Surface.G) * amount),
        (int)Math.Round(Surface.B + (color.B - Surface.B) * amount));

    // Containers cannot take a transparent BackColor in WinForms, so callers say
    // which surface they sit on. Card bodies pass Surface, pages pass Canvas.
    internal static TableLayoutPanel Table(Padding padding = default, Color? background = null) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        Margin = Padding.Empty,
        Padding = padding,
        BackColor = background ?? Canvas
    };

    internal static void Row(TableLayoutPanel table, Control content, int height, bool fill = false)
    {
        table.RowStyles.Add(new RowStyle(fill ? SizeType.Percent : SizeType.Absolute, height));
        table.Controls.Add(content, 0, table.RowCount++);
    }

    internal static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        Font = font ?? Body,
        ForeColor = color ?? Ink,
        BackColor = Color.Transparent,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    internal static Button Button(string title, bool primary = false, bool danger = false)
    {
        var button = new Button
        {
            Text = title,
            Font = primary ? ButtonFaceBold : ButtonFace,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(76, 30),
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(12, 0, 12, 0),
            BackColor = primary ? Accent : Surface2,
            ForeColor = primary ? AccentInk : danger ? Danger : Muted,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : danger ? DangerLine : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Active;
        button.FlatAppearance.MouseDownBackColor = primary ? AccentDown : Surface2;
        Round(button, 5);
        return button;
    }

    internal static FlowLayoutPanel Actions(Color? background = null) => new()
    {
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        WrapContents = false,
        BackColor = background ?? Canvas
    };

    // A text button for secondary, in-row actions. A filled button per row would
    // outweigh the row it belongs to, but a bare Label cannot be focused or
    // reached with the keyboard, and this one destroys state.
    internal static Button Link(string title, Color? color = null, Color? hover = null)
    {
        var button = new Button
        {
            Text = title,
            Font = ButtonFace,
            ForeColor = color ?? Dim,
            BackColor = Surface,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = Size.Empty,
            Margin = Padding.Empty,
            // The design's links are bare text separated by a 12px gap; 6px of
            // padding on each side doubled that gap and pushed row actions apart.
            Padding = new Padding(2, 0, 2, 0),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            TabStop = true
        };
        button.FlatAppearance.BorderSize = 0;
        // Same colour as the card, so hover only changes the text, not the row.
        button.FlatAppearance.MouseOverBackColor = Surface;
        button.FlatAppearance.MouseDownBackColor = Surface;
        var rest = color ?? Dim;
        var over = hover ?? Danger;
        button.MouseEnter += (_, _) => button.ForeColor = over;
        button.MouseLeave += (_, _) => button.ForeColor = rest;
        return button;
    }

    internal static Control Input(TextBox field, string name)
    {
        field.BorderStyle = BorderStyle.None;
        field.BackColor = Field;
        field.ForeColor = Ink;
        field.Font = Body;
        field.Dock = DockStyle.Fill;
        field.AccessibleName = name;
        var frame = new Panel
        {
            BackColor = Field,
            Padding = new Padding(11, 7, 11, 5),
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        frame.Controls.Add(field);
        frame.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(new RectangleF(0.5f, 0.5f, frame.Width - 1, frame.Height - 1), 5);
            using var pen = new Pen(field.Focused ? Accent : Line);
            e.Graphics.DrawPath(pen, path);
        };
        field.GotFocus += (_, _) => frame.Invalidate();
        field.LostFocus += (_, _) => frame.Invalidate();
        Round(frame, 5);
        return frame;
    }

    internal static Control Reader(Control output, string placeholder)
    {
        output.BackColor = Well;
        output.ForeColor = Ink;
        output.Font = Monospace;
        output.Margin = Padding.Empty;
        if (output is TextBox box)
        {
            box.BorderStyle = BorderStyle.None;
            if (box.TextLength == 0) box.Text = placeholder;
        }
        else if (output is RichTextBox rich)
        {
            rich.BorderStyle = BorderStyle.None;
            rich.ReadOnly = true;
            rich.WordWrap = false;
            rich.DetectUrls = false;
            if (rich.TextLength == 0) rich.Text = placeholder;
        }
        var frame = new Panel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12), BackColor = Well
        };
        frame.Controls.Add(output);
        Round(frame, 6);
        return frame;
    }

    internal enum PillTone { Neutral, Good, Warning, Danger }

    /// <summary>
    /// The design's <c>.pill</c>: 20px tall, 5px radius, 1px border, 11px text and
    /// an optional 5px leading dot. A Label cannot draw the border, and a Panel
    /// with a child Label puts the text on the panel's centre line rather than the
    /// text's own optical centre, so the pill paints itself and measures its own
    /// width from the string.
    /// </summary>
    internal sealed class PillLabel : Control
    {
        private string text;
        private PillTone tone;
        private bool dot;

        internal PillLabel(string text, PillTone tone, bool dot = false)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.text = text;
            this.tone = tone;
            this.dot = dot;
            Font = PillFace;
            BackColor = Color.Transparent;
            Margin = Padding.Empty;
            Size = Preferred();
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string Text
        {
            get => text;
            set
            {
                var next = value ?? string.Empty;
                if (next == text) return;
                text = next;
                Size = Preferred();
                Invalidate();
            }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal PillTone Tone
        {
            get => tone;
            set
            {
                if (tone == value) return;
                tone = value;
                Invalidate();
            }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Dot
        {
            get => dot;
            set
            {
                if (dot == value) return;
                dot = value;
                Size = Preferred();
                Invalidate();
            }
        }

        internal Color Foreground => tone switch
        {
            PillTone.Good => Good,
            PillTone.Warning => Warning,
            PillTone.Danger => Danger,
            _ => Muted
        };

        private Color Background => tone switch
        {
            PillTone.Good => Color.FromArgb(236, 247, 239),     // green-bg
            PillTone.Warning => Color.FromArgb(253, 247, 230),  // amber-bg
            PillTone.Danger => Color.FromArgb(253, 236, 235),   // red-bg
            _ => Color.FromArgb(246, 247, 248)                  // plain-bg
        };

        private Color BorderColor => tone switch
        {
            PillTone.Good => Color.FromArgb(207, 232, 216),     // green-line
            PillTone.Warning => Color.FromArgb(240, 226, 189),  // amber-line
            PillTone.Danger => Color.FromArgb(246, 211, 208),   // red-line
            _ => Line
        };

        private Size Preferred()
        {
            int textWidth = TextRenderer.MeasureText(text, Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            // 7px padding either side, 5px dot plus 4px gap when a dot is present.
            return new Size(textWidth + 14 + (dot ? 9 : 0), 20);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (var path = Rounded(bounds, 5))
            {
                using var fill = new SolidBrush(Background);
                g.FillPath(fill, path);
                using var pen = new Pen(BorderColor);
                g.DrawPath(pen, path);
            }
            int left = 7;
            if (dot)
            {
                using var brush = new SolidBrush(Foreground);
                g.FillEllipse(brush, left, Height / 2f - 2.5f, 5, 5);
                left += 9;
            }
            var area = new Rectangle(left, 0, Width - left - 7, Height);
            TextRenderer.DrawText(g, text, Font, area, Foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    internal static PillLabel Pill(string text, PillTone tone = PillTone.Neutral, bool dot = false)
        => new(text, tone, dot);

    // The 6px status dot used by the design: a solid core plus a faint halo, so
    // the colour reads even on a near-black background. StatusDot keeps a
    // settable colour because every dot on screen changes with connection state.
    internal sealed class StatusDot : Control
    {
        private Color color;
        private readonly int size;

        internal StatusDot(Color color, int size = 6)
        {
            this.color = color;
            this.size = size;
            SetStyle(ControlStyles.SupportsTransparentBackColor |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Color Color
        {
            get => color;
            set
            {
                if (color == value) return;
                color = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var halo = new SolidBrush(Color.FromArgb(38, color));
            e.Graphics.FillEllipse(halo, 1, 1, size + 4, size + 4);
            using var core = new SolidBrush(color);
            e.Graphics.FillEllipse(core, 3, 3, size, size);
        }
    }

    internal static StatusDot Dot(Color color, int size = 6) => new(color, size)
    {
        Width = size + 6, Height = size + 6, Margin = Padding.Empty
    };

    internal static Control Caption(string text) => new Label
    {
        Text = text,
        Font = Tiny,
        ForeColor = Dim,
        BackColor = Color.Transparent,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    internal static Panel Divider(Color? color = null) => new()
    {
        Dock = DockStyle.Fill, Height = 1, Margin = Padding.Empty, BackColor = color ?? Hairline
    };

    // A card header: title on the left, optional pills beside it, an optional
    // right-aligned note, then caller-supplied links or buttons. Everything is
    // centred on the 42px band — a TableLayoutPanel cell anchors a Dock=None child
    // to its top-left by default, which is why the title used to sit high.
    internal static Control CardHeader(string title, string? count = null, string? note = null,
        params Control[] trailing)
    {
        Control[]? chips = count is null ? null : [Pill(count)];
        return CardHeaderWith(title, note, chips, trailing);
    }

    internal static Control CardHeaderWith(string title, string? note, Control[]? chips,
        params Control[] trailing)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Surface,
            ColumnCount = 4, RowCount = 1, Padding = new Padding(16, 0, 16, 0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleLabel = Label(title, Section);
        titleLabel.AutoSize = true;
        titleLabel.Dock = DockStyle.None;
        titleLabel.Margin = Padding.Empty;
        var left = new List<Control> { titleLabel };
        if (chips is not null) left.AddRange(chips);
        var group = Inline(left);
        // Anchor=Left without Top/Bottom centres the group in the 42px cell.
        group.Anchor = AnchorStyles.Left;
        header.Controls.Add(group, 0, 0);
        header.SetColumnSpan(group, 2);

        if (note is not null)
        {
            var right = Label(note, Small, Dim);
            right.TextAlign = ContentAlignment.MiddleRight;
            right.AutoEllipsis = true;
            header.Controls.Add(right, 2, 0);
        }
        // One cell holds one control, so several actions go into a strip. Adding
        // them straight to the cell stacked them on top of each other.
        if (trailing.Length > 0)
        {
            var actions = Inline(trailing, gap: 10);
            actions.Anchor = AnchorStyles.Right;
            // Without this the right-hand note and the first action run together:
            // "事件流在任务页看" followed by "0" reads as "…看 0".
            actions.Margin = new Padding(12, 0, 0, 0);
            header.Controls.Add(actions, 3, 0);
        }
        var frame = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Surface };
        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Hairline };
        frame.Controls.Add(header);
        frame.Controls.Add(line);
        return frame;
    }

    /// <summary>
    /// A row of controls that keeps each one on the group's centre line. Heights
    /// differ inside a header (a 20px pill next to a 19px title, a 28px button next
    /// to a 24px link), and a FlowLayoutPanel pins every child to the top of its
    /// tallest sibling.
    /// </summary>
    internal static TableLayoutPanel Inline(IReadOnlyList<Control> items, int gap = 8)
    {
        var strip = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = items.Count, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty,
            BackColor = Surface, Dock = DockStyle.None
        };
        strip.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int index = 0; index < items.Count; index++)
        {
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var item = items[index];
            item.Margin = new Padding(index == 0 ? 0 : gap, 0, 0, 0);
            item.Anchor = AnchorStyles.None;
            strip.Controls.Add(item, index, 0);
        }
        return strip;
    }

    /// <summary>
    /// A card with an 8px radius. The region clip is what makes the radius real:
    /// child panels in WinForms paint square corners over whatever the parent
    /// drew, so rounded corners have to be enforced by clipping, not painting.
    /// </summary>
    internal sealed class CardPanel : Panel
    {
        private readonly float radius;

        internal CardPanel(float radius = 8)
        {
            this.radius = radius;
            BackColor = Surface;
            Margin = Padding.Empty;
            Padding = new Padding(1);
            Dock = DockStyle.Fill;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Round(this, radius);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius);
            using var pen = new Pen(Line, 1.4f);
            e.Graphics.DrawPath(pen, path);
        }
    }
}
