using System.Runtime.InteropServices;

namespace AgentDeck.Tray;

internal static class DeckTheme
{
    internal static readonly Color Canvas = Color.FromArgb(18, 21, 28);
    internal static readonly Color Rail = Color.FromArgb(22, 26, 34);
    internal static readonly Color Surface = Color.FromArgb(27, 33, 43);
    internal static readonly Color Well = Color.FromArgb(16, 20, 27);
    internal static readonly Color Line = Color.FromArgb(55, 66, 84);
    internal static readonly Color Ink = Color.FromArgb(240, 244, 250);
    internal static readonly Color Muted = Color.FromArgb(168, 181, 200);
    internal static readonly Color Accent = Color.FromArgb(165, 204, 255);
    internal static readonly Color Active = Color.FromArgb(37, 57, 83);
    internal static readonly Color Good = Color.FromArgb(147, 222, 185);
    internal static readonly Color Warning = Color.FromArgb(255, 191, 147);

    internal static readonly Font Body = new("Segoe UI", 10);
    internal static readonly Font Small = new("Segoe UI", 9);
    internal static readonly Font Brand = new("Segoe UI", 15, FontStyle.Bold);
    internal static readonly Font Heading = new("Segoe UI", 25, FontStyle.Bold);
    internal static readonly Font Section = new("Segoe UI", 14, FontStyle.Bold);
    internal static readonly Font Monospace = new("Consolas", 11);
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
        using var background = new SolidBrush(Active);
        using var stroke = new Pen(Accent, 2.8f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        graphics.FillEllipse(background, 1, 1, 30, 30);
        graphics.DrawLines(stroke, [new Point(9, 11), new Point(15, 16), new Point(9, 21)]);
        graphics.DrawLine(stroke, 18, 21, 23, 21);
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

    internal static TableLayoutPanel Table(Padding padding = default) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        Margin = Padding.Empty,
        Padding = padding
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
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    internal static Button Button(string title, bool primary = false)
    {
        var button = new Button
        {
            Text = title,
            Font = Body,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 36),
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(12, 0, 12, 0),
            BackColor = primary ? Accent : Surface,
            ForeColor = primary ? Well : Ink,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(195, 221, 255) : Active;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(136, 177, 229) : Rail;
        return button;
    }

    internal static FlowLayoutPanel Actions() => new()
    {
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        WrapContents = false
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
            Padding = new Padding(11, 9, 11, 5),
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

    internal static Control Reader(TextBox output, string placeholder)
    {
        output.BorderStyle = BorderStyle.None;
        output.BackColor = Well;
        output.ForeColor = Ink;
        output.Font = Monospace;
        output.Margin = Padding.Empty;
        // WinForms does not show placeholder text in a multi-line box.
        output.Text = placeholder;
        var frame = new Panel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(14), BackColor = Well
        };
        frame.Controls.Add(output);
        return frame;
    }
}
