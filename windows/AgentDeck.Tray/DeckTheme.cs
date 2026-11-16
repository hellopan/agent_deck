using System.Runtime.InteropServices;

namespace AgentDeck.Tray;

// Colours, fonts and small controls shared by every window. The values mirror
// design/ui-v2.html: a near-black canvas, one-pixel hairlines instead of heavy
// borders, and amber as the only accent — anything brighter competes with the
// status dots that carry the real meaning.
internal static class DeckTheme
{
    internal static readonly Color Canvas = Color.FromArgb(10, 13, 17);
    internal static readonly Color Bar = Color.FromArgb(12, 16, 21);
    internal static readonly Color Surface = Color.FromArgb(14, 19, 25);
    internal static readonly Color Surface2 = Color.FromArgb(20, 27, 35);
    internal static readonly Color Well = Color.FromArgb(11, 16, 21);
    internal static readonly Color Line = Color.FromArgb(29, 38, 47);
    internal static readonly Color Line2 = Color.FromArgb(21, 29, 37);
    internal static readonly Color Ink = Color.FromArgb(222, 230, 240);
    internal static readonly Color Muted = Color.FromArgb(162, 176, 191);
    internal static readonly Color Dim = Color.FromArgb(132, 148, 164);
    internal static readonly Color Accent = Color.FromArgb(217, 164, 65);
    internal static readonly Color Active = Color.FromArgb(20, 27, 35);
    internal static readonly Color Good = Color.FromArgb(70, 184, 92);
    internal static readonly Color Warning = Color.FromArgb(217, 164, 65);
    internal static readonly Color Danger = Color.FromArgb(229, 83, 75);
    internal static readonly Color DangerLine = Color.FromArgb(74, 32, 32);

    internal static readonly Font Body = new("Segoe UI", 9F);
    internal static readonly Font Small = new("Segoe UI", 8.25F);
    internal static readonly Font Tiny = new("Segoe UI", 7.5F);
    internal static readonly Font Brand = new("Segoe UI", 9F, FontStyle.Bold);
    internal static readonly Font Heading = new("Segoe UI", 15F);
    internal static readonly Font Hero = new("Segoe UI", 12.75F);
    internal static readonly Font Section = new("Segoe UI", 9.75F, FontStyle.Bold);
    internal static readonly Font Monospace = new("Consolas", 8.5F);
    internal static readonly Icon AppIcon = CreateAppIcon();

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
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(32, 26, 16));
        using var stroke = new Pen(Accent, 2.6f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
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

    // Containers cannot take a transparent BackColor in WinForms, so callers say
    // which surface they sit on. Card bodies pass Surface, pages pass Canvas.
    internal static TableLayoutPanel Table(Padding padding = default, Color? background = null) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
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
            Font = Body,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(70, 30),
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(10, 0, 10, 0),
            BackColor = primary ? Color.FromArgb(35, 134, 54) : Surface,
            ForeColor = danger ? Danger : primary ? Color.White : Muted,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(35, 134, 54)
            : danger ? DangerLine : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(45, 154, 64)
            : Surface2;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(28, 108, 44)
            : Active;
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
            Padding = new Padding(10, 7, 10, 4),
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        frame.Controls.Add(field);
        frame.Paint += (_, e) =>
        {
            using var pen = new Pen(field.Focused ? Accent : Line);
            e.Graphics.DrawRectangle(pen, 0, 0, frame.Width - 1, frame.Height - 1);
        };
        field.GotFocus += (_, _) => frame.Invalidate();
        field.LostFocus += (_, _) => frame.Invalidate();
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
            // WinForms does not show placeholder text in a multi-line box.
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
        return frame;
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
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var halo = new SolidBrush(System.Drawing.Color.FromArgb(38, color));
            e.Graphics.FillEllipse(halo, 1, 1, size + 4, size + 4);
            using var core = new SolidBrush(color);
            e.Graphics.FillEllipse(core, 3, 3, size, size);
        }
    }

    internal static StatusDot Dot(Color color, int size = 6) => new(color, size)
    {
        Width = size + 6, Height = size + 6, Margin = Padding.Empty
    };

    // A one-line caption above a value, matching the "cap" blocks in the design.
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

    // A card header: title on the left, an optional monospace count and a
    // right-aligned note. Fixed 40px so every card's body starts on the same line.
    internal static Control CardHeader(string title, string? count = null, string? note = null,
        params Control[] trailing)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Surface,
            ColumnCount = 4, RowCount = 1, Padding = new Padding(14, 0, 14, 0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = Label(title, Section);
        label.AutoSize = true;
        label.Dock = DockStyle.None;
        label.Margin = new Padding(0, 0, 0, 0);
        header.Controls.Add(label, 0, 0);
        if (count is not null)
        {
            var badge = Label(count, Monospace, Dim);
            badge.AutoSize = true;
            badge.Dock = DockStyle.None;
            badge.BackColor = Surface2;
            badge.Padding = new Padding(6, 1, 6, 1);
            badge.Margin = new Padding(8, 0, 0, 0);
            header.Controls.Add(badge, 1, 0);
        }
        foreach (var extra in trailing) header.Controls.Add(extra, 3, 0);
        if (note is not null)
        {
            var right = Label(note, Small, Dim);
            right.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(right, 2, 0);
        }
        var frame = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Surface };
        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Line2 };
        frame.Controls.Add(header);
        frame.Controls.Add(line);
        return frame;
    }
}
