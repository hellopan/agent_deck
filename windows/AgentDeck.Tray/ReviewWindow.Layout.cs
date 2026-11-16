using System.Drawing.Drawing2D;

namespace AgentDeck.Tray;

// Window shell and the shared page furniture. The shell is the three-part frame
// from design/ui-v2.html: a 44px header, a 56px icon rail and a 34px status bar.
// Header and rail are painted rather than composed from controls — every attempt
// to lay them out with nested TableLayoutPanels produced controls that drifted
// out of their cells, and painted text stays on the intended baseline.
internal sealed partial class ReviewWindow
{
    private readonly Panel content = new()
    {
        Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = DeckTheme.Canvas
    };
    private readonly TopBarView topBar = new() { Dock = DockStyle.Fill };
    private readonly RailView rail = new() { Dock = DockStyle.Fill };
    private readonly Label status = DeckTheme.Label("", DeckTheme.MonoSmall, DeckTheme.Dim);
    private readonly Label statusService = DeckTheme.Label("服务 · 未注册", DeckTheme.MonoSmall, DeckTheme.Dim);
    private readonly Label statusPending = DeckTheme.Label("待处理 0", DeckTheme.MonoSmall, DeckTheme.Dim);
    private readonly Label statusCodex = DeckTheme.Label("CODEX —", DeckTheme.MonoSmall, DeckTheme.Dim);
    private readonly Label statusDevice = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Dim);
    private readonly List<Control> pages = [];
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
        AddPage(StatePage());
        AddPage(TasksPage());
        AddPage(SettingsPage());
        rail.TabSelected += SelectPage;

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
        // Detect once at startup: the 状态 page shows the Codex version, and the
        // 任务 page labels every task with the adapter that runs it.
        _ = DetectToolsAsync(fill: true, quiet: true);
    }

    private Control TopBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty };
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = DeckTheme.Line });
        bar.Controls.Add(topBar);
        topBar.Host = Environment.MachineName;
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
        body.Controls.Add(rail, 0, 0);
        body.Controls.Add(content, 1, 0);
        return body;
    }

    private Control StatusBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty };
        bar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = DeckTheme.Line });
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Bar, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1, Padding = new Padding(16, 0, 16, 0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        statusService.AutoSize = true;
        statusService.Dock = DockStyle.None;
        statusService.Margin = new Padding(0, 0, 22, 0);
        grid.Controls.Add(statusService, 0, 0);
        status.AutoEllipsis = true;
        grid.Controls.Add(status, 1, 0);

        var right = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, BackColor = DeckTheme.Bar,
            Margin = Padding.Empty, FlowDirection = FlowDirection.LeftToRight
        };
        foreach (var label in new[] { statusPending, statusCodex, statusDevice })
        {
            label.AutoSize = true;
            label.Dock = DockStyle.None;
            label.Margin = new Padding(0, 0, 20, 0);
            right.Controls.Add(label);
        }
        grid.Controls.Add(right, 2, 0);
        bar.Controls.Add(grid);
        return bar;
    }

    private Control Page(string title, string description, Control body, Control? aside = null)
    {
        var page = DeckTheme.Table(new Padding(26, 20, 26, 18), DeckTheme.Canvas);
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var text = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(text, DeckTheme.Label(title, DeckTheme.Heading), 32);
        DeckTheme.Row(text, DeckTheme.Label(description, DeckTheme.Small, DeckTheme.Dim), 20);
        head.Controls.Add(text, 0, 0);
        if (aside is not null) head.Controls.Add(aside, 1, 0);
        DeckTheme.Row(page, head, 60);
        DeckTheme.Row(page, new Panel { BackColor = DeckTheme.Canvas }, 16);
        DeckTheme.Row(page, body, 100, fill: true);
        return page;
    }

    private void AddPage(Control page)
    {
        pages.Add(page);
        content.Controls.Add(page);
        page.Visible = false;
    }

    private void SelectPage(int index)
    {
        if (index < 0 || index >= pages.Count) return;
        content.SuspendLayout();
        for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == index;
        rail.SelectedIndex = index;
        content.ResumeLayout();
    }

    // One card: an 8px rounded shell around a 42px header and whatever body rows
    // the caller appends. Cards never draw a drop shadow; separation comes from
    // the hairline and the 16px gap.
    private static Control Card(string title, out TableLayoutPanel body,
        string? count = null, string? note = null, Control? action = null)
    {
        body = DeckTheme.Table(new Padding(16, 0, 16, 14), DeckTheme.Surface);
        DeckTheme.Row(body, DeckTheme.CardHeader(title, count, note,
            action is null ? [] : [action]), 42);
        var card = new DeckTheme.CardPanel();
        card.Controls.Add(body);
        return card;
    }

    private static Control Metric(string caption, Label value)
    {
        var table = DeckTheme.Table(new Padding(0, 10, 0, 0), DeckTheme.Surface);
        DeckTheme.Row(table, DeckTheme.Caption(caption), 18);
        value.Font = DeckTheme.Hero;
        value.ForeColor = DeckTheme.Muted;
        DeckTheme.Row(table, value, 26);
        return table;
    }

    private static Control Legend(string key, Color color, string text)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 15));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var dot = DeckTheme.Dot(color, 5);
        dot.Dock = DockStyle.None;
        dot.Anchor = AnchorStyles.Left;
        dot.Margin = new Padding(0, 0, 0, 1);
        row.Controls.Add(dot, 0, 0);
        var name = DeckTheme.Label(key, DeckTheme.Small, color);
        name.Margin = new Padding(0, 0, 10, 0);
        row.Controls.Add(name, 1, 0);
        var detail = DeckTheme.Label(text, DeckTheme.Small, DeckTheme.Dim);
        detail.AutoEllipsis = true;
        row.Controls.Add(detail, 2, 0);
        return row;
    }

    // A caption stacked over a monospace value, both right-aligned. Every label
    // is AutoSize with Dock=None: a Dock=Fill label inside an AutoSize table
    // reports the width it was given instead of the width it needs, which is how
    // the version string ended up clipped at "0.167.0-alpha".
    private static Control AsideItem(string caption, Label value)
    {
        var table = DeckTheme.Table(background: DeckTheme.Canvas);
        var cap = DeckTheme.Label(caption, DeckTheme.Tiny, DeckTheme.Dim);
        cap.AutoSize = true;
        cap.Dock = DockStyle.None;
        cap.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cap.TextAlign = ContentAlignment.BottomRight;
        DeckTheme.Row(table, cap, 17);
        value.AutoSize = true;
        value.Dock = DockStyle.None;
        value.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        value.TextAlign = ContentAlignment.TopRight;
        value.Font = DeckTheme.Monospace;
        value.ForeColor = DeckTheme.Muted;
        DeckTheme.Row(table, value, 19);
        table.Dock = DockStyle.None;
        table.AutoSize = true;
        table.MinimumSize = new Size(84, 36);
        table.Margin = new Padding(30, 0, 0, 0);
        return table;
    }

    // Painted header: mark, wordmark, machine name, connection state. Text is
    // measured and placed by hand so nothing can overlap at any window width.
    private sealed class TopBarView : Control
    {
        private string host = "";
        private string device = "";
        private string state = "";
        private Color stateColor = DeckTheme.Dim;

        internal TopBarView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = DeckTheme.Bar;
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal string Host
        {
            get => host;
            set { host = value; Invalidate(); }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal string Device
        {
            get => device;
            set { device = value; Invalidate(); }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal string State
        {
            get => state;
            set { state = value; Invalidate(); }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Color StateColor
        {
            get => stateColor;
            set { stateColor = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(DeckTheme.Bar)) g.FillRectangle(background, ClientRectangle);

            var mark = new Rectangle(16, (Height - 14) / 2, 14, 14);
            using (var path = DeckTheme.Rounded(mark, 3))
            using (var fill = new LinearGradientBrush(mark, DeckTheme.Accent, Color.FromArgb(138, 101, 25), 45f))
                g.FillPath(fill, path);
            // The same chevron the tray icon carries: without it the mark reads
            // as a coloured placeholder box rather than a logo.
            using (var glyph = new Pen(DeckTheme.AccentInk, 2f)
                   {
                       StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
                   })
                g.DrawLines(glyph,
                [
                    new PointF(mark.X + 5.0f, mark.Y + 4.2f),
                    new PointF(mark.X + 9.2f, mark.Y + 7.0f),
                    new PointF(mark.X + 5.0f, mark.Y + 9.8f)
                ]);

            int x = mark.Right + 9;
            TextRenderer.DrawText(g, "AGENTDECK", DeckTheme.Brand,
                new Rectangle(x, 0, 120, Height), DeckTheme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += TextRenderer.MeasureText(g, "AGENTDECK", DeckTheme.Brand, Size.Empty,
                TextFormatFlags.NoPadding).Width + 15;

            using (var pen = new Pen(DeckTheme.Line))
                g.DrawLine(pen, x, 13, x, Height - 13);
            x += 15;

            TextRenderer.DrawText(g, host, DeckTheme.Body,
                new Rectangle(x, 0, 240, Height), DeckTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis);
            x += TextRenderer.MeasureText(g, host, DeckTheme.Body, Size.Empty,
                TextFormatFlags.NoPadding).Width + 10;

            TextRenderer.DrawText(g, device, DeckTheme.MonoSmall,
                new Rectangle(x, 0, 120, Height), DeckTheme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (state.Length == 0) return;
            int stateWidth = TextRenderer.MeasureText(g, state, DeckTheme.MonoSmall, Size.Empty,
                TextFormatFlags.NoPadding).Width;
            int stateLeft = Width - 16 - stateWidth;
            TextRenderer.DrawText(g, state, DeckTheme.MonoSmall,
                new Rectangle(stateLeft, 0, stateWidth, Height), stateColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int cy = Height / 2;
            using (var halo = new SolidBrush(Color.FromArgb(40, stateColor)))
                g.FillEllipse(halo, stateLeft - 16, cy - 5, 10, 10);
            using (var core = new SolidBrush(stateColor))
                g.FillEllipse(core, stateLeft - 14, cy - 3, 6, 6);
        }
    }

    // Painted navigation rail. Three 36px icon buttons, an amber selection bar
    // and a badge, all positioned in code instead of through nested layouts.
    private sealed class RailView : Control
    {
        private const int PadTop = 12;
        private const int Step = 42;
        private const int Box = 36;
        private int selected;
        private int badge;
        private int hover = -1;
        private Color statusColor = DeckTheme.Dim;

        internal RailView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = DeckTheme.Bar;
            Cursor = Cursors.Hand;
        }

        internal event Action<int>? TabSelected;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int SelectedIndex
        {
            get => selected;
            set
            {
                if (selected == value) return;
                selected = value;
                Invalidate();
            }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int Badge
        {
            get => badge;
            set
            {
                if (badge == value) return;
                badge = value;
                Invalidate();
            }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Color StatusColor
        {
            get => statusColor;
            set
            {
                if (statusColor == value) return;
                statusColor = value;
                Invalidate();
            }
        }

        private int HitTest(Point point)
        {
            for (int index = 0; index < 3; index++)
            {
                var box = ItemBounds(index);
                if (box.Contains(point)) return index;
            }
            return -1;
        }

        private Rectangle ItemBounds(int index) =>
            new((Width - Box) / 2, PadTop + index * Step, Box, Box);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            if (hit != hover)
            {
                hover = hit;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            if (hit >= 0) TabSelected?.Invoke(hit);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(DeckTheme.Bar)) g.FillRectangle(background, ClientRectangle);
            using (var edge = new Pen(DeckTheme.Line))
                g.DrawLine(edge, Width - 1, 0, Width - 1, Height);

            for (int index = 0; index < 3; index++)
            {
                var box = ItemBounds(index);
                if (index == selected || index == hover)
                {
                    using var path = DeckTheme.Rounded(box, 8);
                    using var fill = new SolidBrush(index == selected ? DeckTheme.Surface2 : DeckTheme.Line2);
                    g.FillPath(fill, path);
                }
                if (index == selected)
                {
                    using var path = DeckTheme.Rounded(new RectangleF(4, box.Top + 9, 2, Box - 18), 1);
                    using var fill = new SolidBrush(DeckTheme.Accent);
                    g.FillPath(fill, path);
                }
                DrawIcon(g, index, box, index == selected ? DeckTheme.Ink : DeckTheme.Dim);
            }

            if (badge > 0)
            {
                var box = ItemBounds(1);
                var text = badge > 9 ? "9+" : badge.ToString();
                int width = text.Length > 1 ? 20 : 15;
                var pill = new Rectangle(box.Right - width + 3, box.Top - 3, width, 15);
                using var path = DeckTheme.Rounded(pill, 7);
                using var fill = new SolidBrush(DeckTheme.Accent);
                g.FillPath(fill, path);
                TextRenderer.DrawText(g, text, DeckTheme.MonoMicro, pill, DeckTheme.AccentInk,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            int cy = Height - 18;
            using (var halo = new SolidBrush(Color.FromArgb(40, statusColor)))
                g.FillEllipse(halo, Width / 2f - 5, cy - 4, 10, 10);
            using (var core = new SolidBrush(statusColor))
                g.FillEllipse(core, Width / 2f - 3, cy - 2, 6, 6);
        }

        private static void DrawIcon(Graphics g, int index, Rectangle box, Color color)
        {
            using var pen = new Pen(color, 1.45f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float cx = box.X + box.Width / 2f;
            float cy = box.Y + box.Height / 2f;
            switch (index)
            {
                case 0:
                    g.DrawLines(pen,
                    [
                        new PointF(cx - 8, cy + 1), new PointF(cx - 4, cy + 1),
                        new PointF(cx - 1.5f, cy - 6), new PointF(cx + 1.5f, cy + 6),
                        new PointF(cx + 4, cy - 1), new PointF(cx + 8, cy - 1)
                    ]);
                    break;
                case 1:
                    for (int line = 0; line < 3; line++)
                    {
                        float y = cy - 5 + line * 5;
                        using var dot = new SolidBrush(color);
                        g.FillEllipse(dot, cx - 8, y - 1.3f, 2.6f, 2.6f);
                        g.DrawLine(pen, cx - 3.4f, y, cx + 8, y);
                    }
                    break;
                default:
                    // Sliders, not a gear: a 12px gear needs eight teeth and a
                    // hub inside 18px, and at that size it renders as a sun.
                    float[] knobs = [cx - 3, cx + 4, cx - 1];
                    for (int rail = 0; rail < 3; rail++)
                    {
                        float y = cy - 6 + rail * 6;
                        float knob = knobs[rail];
                        g.DrawLine(pen, cx - 8.5f, y, knob - 3, y);
                        g.DrawLine(pen, knob + 3, y, cx + 8.5f, y);
                        g.DrawEllipse(pen, knob - 2.4f, y - 2.4f, 4.8f, 4.8f);
                    }
                    break;
            }
        }
    }
}
