using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AgentDeck.Tray;

// Colours, fonts and small controls shared by every window. The values mirror
// design/ui-v2.html: a near-black canvas, one-pixel hairlines instead of heavy
// borders, and amber as the single accent — green and red are reserved for
// status, so a green "primary" button read as a second accent and pulled the
// eye away from whatever actually needed attention.
internal static class DeckTheme
{
    internal static readonly Color Canvas = Color.FromArgb(10, 13, 17);
    internal static readonly Color Bar = Color.FromArgb(12, 16, 21);
    internal static readonly Color Surface = Color.FromArgb(14, 19, 25);
    internal static readonly Color Surface2 = Color.FromArgb(20, 27, 35);
    internal static readonly Color Well = Color.FromArgb(9, 13, 18);
    internal static readonly Color Line = Color.FromArgb(35, 47, 59);
    internal static readonly Color Line2 = Color.FromArgb(22, 30, 38);
    // Between Line2 and Line: Line2 on Surface is a four-value step that reads
    // as a compression artifact, so every internal rule uses Hairline instead.
    internal static readonly Color Hairline = Color.FromArgb(30, 40, 51);
    internal static readonly Color Ink = Color.FromArgb(224, 232, 242);
    internal static readonly Color Muted = Color.FromArgb(170, 184, 199);
    internal static readonly Color Dim = Color.FromArgb(139, 154, 170);
    internal static readonly Color Accent = Color.FromArgb(217, 164, 65);
    internal static readonly Color AccentInk = Color.FromArgb(24, 19, 10);
    internal static readonly Color AccentHover = Color.FromArgb(232, 180, 84);
    internal static readonly Color Active = Color.FromArgb(26, 35, 45);
    internal static readonly Color Good = Color.FromArgb(70, 184, 92);
    internal static readonly Color Warning = Color.FromArgb(217, 164, 65);
    internal static readonly Color Danger = Color.FromArgb(229, 83, 75);
    internal static readonly Color DangerLine = Color.FromArgb(92, 43, 41);
    internal static readonly Color Selected = Color.FromArgb(21, 29, 38);

    private static readonly string UiFamily = PickFamily("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI");
    private static readonly string MonoFamily = PickFamily("Cascadia Mono", "Cascadia Code", "Consolas");

    internal static readonly Font Body = new(UiFamily, 9.5F);
    internal static readonly Font Small = new(UiFamily, 9F);
    internal static readonly Font Tiny = new(UiFamily, 8.25F);
    internal static readonly Font Brand = new(UiFamily, 9F, FontStyle.Bold);
    internal static readonly Font Heading = new(UiFamily, 15F, FontStyle.Bold);
    internal static readonly Font Hero = new(UiFamily, 13F, FontStyle.Bold);
    internal static readonly Font Section = new(UiFamily, 10F, FontStyle.Bold);
    internal static readonly Font ButtonFace = new(UiFamily, 9.5F);
    internal static readonly Font ButtonFaceBold = new(UiFamily, 9.5F, FontStyle.Bold);
    internal static readonly Font Monospace = new(MonoFamily, 9F);
    internal static readonly Font MonoSmall = new(MonoFamily, 8.5F);
    internal static readonly Font MonoMicro = new(MonoFamily, 8F);
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
            int enabled = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(32, 26, 16));
        using var stroke = new Pen(Accent, 2.6f)
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
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(200, 149, 55) : Surface2;
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

    internal static Control Input(TextBox field, string name)
    {
        field.BorderStyle = BorderStyle.None;
        field.BackColor = Well;
        field.ForeColor = Ink;
        field.Font = Body;
        field.Dock = DockStyle.Fill;
        field.AccessibleName = name;
        var frame = new Panel
        {
            BackColor = Well,
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

    /// <summary>Monospace pill used for counts and state words.</summary>
    internal static Label Chip(string text, Color color, Color? background = null)
    {
        var chip = new Label
        {
            Text = text,
            Font = MonoMicro,
            ForeColor = color,
            BackColor = background ?? Tint(color, 0.16),
            AutoSize = true,
            Padding = new Padding(7, 2, 7, 2),
            Margin = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleCenter
        };
        return chip;
    }

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

    // A card header: title on the left, an optional monospace count and a
    // right-aligned note. Fixed 42px so every card's body starts on the same line.
    internal static Control CardHeader(string title, string? count = null, string? note = null,
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
        var label = Label(title, Section);
        label.AutoSize = true;
        label.Dock = DockStyle.None;
        header.Controls.Add(label, 0, 0);
        if (count is not null)
        {
            var badge = Chip(count, Dim, Surface2);
            badge.Margin = new Padding(9, 0, 0, 0);
            header.Controls.Add(badge, 1, 0);
        }
        foreach (var extra in trailing) header.Controls.Add(extra, 3, 0);
        if (note is not null)
        {
            var right = Label(note, Small, Dim);
            right.TextAlign = ContentAlignment.MiddleRight;
            right.AutoEllipsis = true;
            header.Controls.Add(right, 2, 0);
        }
        var frame = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Surface };
        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Hairline };
        frame.Controls.Add(header);
        frame.Controls.Add(line);
        return frame;
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
