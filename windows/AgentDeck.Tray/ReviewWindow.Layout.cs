using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace AgentDeck.Tray;

// Window shell and the status page. The shell is the three-part frame from
// design/ui-v2.html: a 44px header, a 56px icon rail and a 34px status bar.
// There are exactly three pages — 状态 / 任务 / 设置 — because every other
// split (connection, projects, logs) duplicated something already on one of
// them and cost the user a navigation step.
internal sealed partial class ReviewWindow
{
    private readonly Panel content = new()
    {
        Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = DeckTheme.Canvas
    };
    private readonly Label status = DeckTheme.Label("", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label statusService = DeckTheme.Label("服务 · 未运行", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label statusPending = DeckTheme.Label("待处理 0", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label statusCodex = DeckTheme.Label("CODEX —", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label statusInbox = DeckTheme.Label("INBOX 0", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label statusDevice = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label topState = DeckTheme.Label("未注册", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly DeckTheme.StatusDot topDot = DeckTheme.Dot(DeckTheme.Dim);
    private readonly Label hostName = DeckTheme.Label(Environment.MachineName, DeckTheme.Body, DeckTheme.Ink);
    private readonly Label hostId = DeckTheme.Label("未注册", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly List<(Control View, RailButton Tab)> pages = [];
    private readonly List<string> connectionEvents = [];
    private readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 6000 };
    private string? flashRestore;
    private Button pair = null!;
    private bool busy;

    private bool DeviceRegistered => File.Exists(Path.Combine(DeviceDirectory, "registered-device")) &&
        File.Exists(AgentConfig);

    private void BuildWorkspace()
    {
        DeckTheme.Form(this, "AgentDeck");
        MinimumSize = new Size(900, 660);
        ClientSize = new Size(1120, 720);
        StartPosition = FormStartPosition.CenterScreen;

        var shell = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(shell, TopBar(), 44);
        DeckTheme.Row(shell, Body(), 100, fill: true);
        DeckTheme.Row(shell, StatusBar(), 34);
        Controls.Add(shell);
        AddPage("状态", StatePage());
        AddPage("任务", TasksPage());
        AddPage("设置", SettingsPage());

        flashTimer.Tick += (_, _) =>
        {
            flashTimer.Stop();
            if (flashRestore is not null) status.Text = flashRestore;
            flashRestore = null;
            status.ForeColor = DeckTheme.Dim;
        };
        FormClosed += (_, _) => flashTimer.Dispose();

        var monitor = new System.Windows.Forms.Timer { Interval = 2000 };
        monitor.Tick += (_, _) => UpdateConnection();
        Shown += (_, _) => monitor.Start();
        FormClosed += (_, _) => monitor.Dispose();
        SelectPage(0);
    }

    private Control TopBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty };
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = DeckTheme.Line });
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1, Padding = new Padding(14, 0, 14, 0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var brand = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, BackColor = DeckTheme.Bar,
            Margin = Padding.Empty, Padding = new Padding(0, 12, 0, 0)
        };
        brand.Controls.Add(new MarkControl { Size = new Size(14, 14), Margin = new Padding(0, 0, 8, 0) });
        var name = DeckTheme.Label("AGENTDECK", DeckTheme.Brand);
        name.AutoSize = true;
        name.Dock = DockStyle.None;
        name.Margin = new Padding(0, 1, 0, 0);
        brand.Controls.Add(name);
        grid.Controls.Add(brand, 0, 0);

        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, BackColor = DeckTheme.Bar, Margin = new Padding(14, 0, 0, 0),
            ColumnCount = 3, RowCount = 1, Padding = new Padding(14, 0, 0, 0)
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // A character rather than a 1px Panel: a Panel inside an AutoSize row
        // collapses to zero height and its border lands above the text.
        host.Controls.Add(new Panel { BackColor = DeckTheme.Bar, Width = 1, Margin = Padding.Empty }, 0, 0);
        hostName.AutoSize = true;
        hostName.Dock = DockStyle.None;
        hostName.Margin = new Padding(0, 0, 8, 0);
        host.Controls.Add(hostName, 1, 0);
        hostId.AutoSize = true;
        hostId.Dock = DockStyle.None;
        hostId.Margin = new Padding(0, 1, 0, 0);
        host.Controls.Add(hostId, 2, 0);
        grid.Controls.Add(host, 1, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        topDot.Dock = DockStyle.None;
        topDot.Anchor = AnchorStyles.Right;
        topDot.Margin = new Padding(0, 0, 8, 0);
        right.Controls.Add(topDot, 0, 0);
        topState.TextAlign = ContentAlignment.MiddleRight;
        topState.AutoEllipsis = true;
        right.Controls.Add(topState, 1, 0);
        grid.Controls.Add(right, 2, 0);
        bar.Controls.Add(grid);
        return bar;
    }

    private Control Body()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(Rail(), 0, 0);
        body.Controls.Add(content, 1, 0);
        return body;
    }

    private Control Rail()
    {
        var rail = new Panel { Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty };
        rail.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 1, BackColor = DeckTheme.Line });
        var list = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty,
            ColumnCount = 1, RowCount = 1, Padding = new Padding(10, 9, 10, 6)
        };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRail(list, "状态", RailIcon.Status, out var stateTab);
        AddRail(list, "任务", RailIcon.Task, out var taskTab);
        AddRail(list, "设置", RailIcon.Settings, out var settingsTab);
        DeckTheme.Row(list, new Panel { BackColor = DeckTheme.Bar }, 100, fill: true);
        railStatus.Dock = DockStyle.None;
        railStatus.Anchor = AnchorStyles.None;
        railStatus.Margin = new Padding(0, 0, 0, 6);
        DeckTheme.Row(list, railStatus, 24);
        rail.Controls.Add(list);
        return rail;
    }

    private void AddRail(TableLayoutPanel rail, string title, RailIcon icon, out RailButton button)
    {
        var created = new RailButton(icon, title);
        created.Click += (_, _) => SelectPage(pages.FindIndex(p => p.Tab == created));
        var tip = new ToolTip();
        tip.SetToolTip(created, title);
        DeckTheme.Row(rail, created, 40);
        pages.Add((new Panel { Visible = false }, created));
        button = created;
    }

    private Control StatusBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty };
        bar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = DeckTheme.Line });
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty,
            ColumnCount = 4, RowCount = 1, Padding = new Padding(14, 0, 14, 0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        statusService.AutoSize = true;
        statusService.Dock = DockStyle.None;
        statusService.Margin = new Padding(0, 1, 18, 0);
        grid.Controls.Add(statusService, 0, 0);
        status.AutoEllipsis = true;
        status.Margin = new Padding(0, 1, 18, 0);
        grid.Controls.Add(status, 1, 0);
        statusPending.AutoSize = true;
        statusPending.Dock = DockStyle.None;
        statusPending.Margin = new Padding(0, 1, 18, 0);
        grid.Controls.Add(statusPending, 2, 0);

        var right = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, BackColor = DeckTheme.Bar,
            Margin = Padding.Empty, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 1, 0, 0)
        };
        foreach (var label in new[] { statusCodex, statusInbox, statusDevice })
        {
            label.AutoSize = true;
            label.Dock = DockStyle.None;
            label.Margin = new Padding(0, 0, 18, 0);
            right.Controls.Add(label);
        }
        grid.Controls.Add(right, 3, 0);
        bar.Controls.Add(grid);
        return bar;
    }

    private Control Page(string title, string description, Control body, Control? aside = null)
    {
        var page = DeckTheme.Table(new Padding(24, 18, 24, 0), DeckTheme.Canvas);
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var text = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(text, DeckTheme.Label(title, DeckTheme.Heading), 30);
        DeckTheme.Row(text, DeckTheme.Label(description, DeckTheme.Small, DeckTheme.Dim), 20);
        head.Controls.Add(text, 0, 0);
        if (aside is not null) head.Controls.Add(aside, 1, 0);
        DeckTheme.Row(page, head, 54);
        DeckTheme.Row(page, new Panel { BackColor = DeckTheme.Canvas }, 16);
        DeckTheme.Row(page, body, 100, fill: true);
        DeckTheme.Row(page, new Panel { BackColor = DeckTheme.Canvas }, 18);
        return page;
    }

    private void AddPage(string title, Control page)
    {
        int index = pages.FindIndex(p => p.Tab.Text == title);
        (Control View, RailButton Tab) entry = pages[index];
        pages[index] = (page, entry.Tab);
        content.Controls.Add(page);
        page.Visible = false;
    }

    private void SelectPage(int index)
    {
        content.SuspendLayout();
        for (int i = 0; i < pages.Count; i++)
        {
            var (view, tab) = pages[i];
            bool selected = i == index;
            view.Visible = selected;
            tab.Selected = selected;
        }
        content.ResumeLayout();
    }

    // One card: a one-pixel frame around a header row and whatever body rows the
    // caller appends. Cards never draw a drop shadow; separation comes from the
    // hairline and the 14px gap.
    private static Control Card(string title, out TableLayoutPanel body,
        string? count = null, string? note = null, Control? action = null)
    {
        body = DeckTheme.Table(new Padding(16, 0, 16, 12), DeckTheme.Surface);
        DeckTheme.Row(body, DeckTheme.CardHeader(title, count, note,
            action is null ? [] : [action]), 40);
        var frame = new Panel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Padding = new Padding(1), Margin = Padding.Empty
        };
        frame.Paint += (_, e) =>
        {
            using var pen = new Pen(DeckTheme.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, frame.Width - 1, frame.Height - 1);
        };
        frame.Controls.Add(body);
        return frame;
    }

    private static Control Metric(string caption, Label value)
    {
        var table = DeckTheme.Table(background: DeckTheme.Surface);
        DeckTheme.Row(table, DeckTheme.Caption(caption), 18);
        value.Font = DeckTheme.Monospace;
        DeckTheme.Row(table, value, 22);
        return table;
    }

    private static Control Legend(string key, Color color, string text)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var name = DeckTheme.Label(key, DeckTheme.Small, color);
        name.Margin = new Padding(0, 0, 8, 0);
        row.Controls.Add(name, 0, 0);
        var detail = DeckTheme.Label(text, DeckTheme.Small, DeckTheme.Dim);
        detail.AutoEllipsis = true;
        row.Controls.Add(detail, 1, 0);
        return row;
    }

    private sealed class MarkControl : Control
    {
        internal MarkControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new LinearGradientBrush(ClientRectangle,
                DeckTheme.Accent, Color.FromArgb(138, 101, 25), 45f);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    private enum RailIcon { Status, Task, Settings }

    private sealed class RailButton : Control
    {
        private readonly RailIcon icon;
        private bool selected;
        private int badge;

        internal RailButton(RailIcon icon, string text)
        {
            this.icon = icon;
            Text = text;
            AccessibleName = text;
            Margin = new Padding(0, 0, 0, 2);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = DeckTheme.Bar;
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Selected
        {
            get => selected;
            set { selected = value; Invalidate(); }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int Badge
        {
            get => badge;
            set { badge = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(selected ? DeckTheme.Surface2 : DeckTheme.Bar))
                g.FillRectangle(background, ClientRectangle);
            var color = selected ? DeckTheme.Ink : DeckTheme.Dim;
            var origin = new Point((Width - 20) / 2, (Height - 20) / 2);
            using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            switch (icon)
            {
                case RailIcon.Status:
                    g.DrawLines(pen,
                    [
                        new PointF(origin.X + 2, origin.Y + 11), new PointF(origin.X + 6, origin.Y + 11),
                        new PointF(origin.X + 8.5f, origin.Y + 4), new PointF(origin.X + 11.5f, origin.Y + 16),
                        new PointF(origin.X + 14, origin.Y + 9), new PointF(origin.X + 18, origin.Y + 9)
                    ]);
                    break;
                case RailIcon.Task:
                    for (int i = 0; i < 3; i++)
                    {
                        float y = origin.Y + 6 + i * 4.5f;
                        using var fill = new SolidBrush(color);
                        g.FillRectangle(fill, origin.X + 2, y - 1.4f, 2.6f, 2.6f);
                        g.DrawLine(pen, origin.X + 7.5f, y, origin.X + 18, y);
                    }
                    break;
                default:
                    var center = new PointF(origin.X + 10, origin.Y + 10);
                    g.DrawEllipse(pen, center.X - 5.4f, center.Y - 5.4f, 10.8f, 10.8f);
                    g.DrawEllipse(pen, center.X - 1.8f, center.Y - 1.8f, 3.6f, 3.6f);
                    for (int i = 0; i < 8; i++)
                    {
                        double angle = Math.PI * i / 4;
                        g.DrawLine(pen,
                            center.X + (float)(Math.Cos(angle) * 5.6), center.Y + (float)(Math.Sin(angle) * 5.6),
                            center.X + (float)(Math.Cos(angle) * 8.2), center.Y + (float)(Math.Sin(angle) * 8.2));
                    }
                    break;
            }
            if (selected)
            {
                using var accent = new SolidBrush(DeckTheme.Accent);
                g.FillRectangle(accent, 0, 9, 2, Height - 18);
            }
            if (badge > 0)
            {
                var text = badge > 9 ? "9+" : badge.ToString();
                var width = text.Length > 1 ? 17 : 13;
                using var fill = new SolidBrush(DeckTheme.Accent);
                g.FillRectangle(fill, Width - width - 2, 2, width, 13);
                using var font = new Font("Consolas", 7F, FontStyle.Bold);
                TextRenderer.DrawText(g, text, font, new Rectangle(Width - width - 2, 2, width, 13),
                    Color.FromArgb(23, 19, 10), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
