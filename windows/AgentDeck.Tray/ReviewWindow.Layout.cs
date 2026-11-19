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

        // v3 layout: the rail runs the full height and carries the brand, while the
        // top bar spans only the main column. The old shell put a full-width bar
        // above a 56px icon strip, which read as a toolbar plus a mystery column.
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 198));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var main = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(main, TopBar(), 52);
        DeckTheme.Row(main, content, 100, fill: true);
        DeckTheme.Row(main, StatusBar(), 34);

        shell.Controls.Add(rail, 0, 0);
        shell.Controls.Add(main, 1, 0);
        Controls.Add(shell);
        AddPage(StatePage());
        AddPage(TasksPage());
        AddPage(ProjectsPage());
        AddPage(LogsPage());
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
        // Anchor=Left centres the label in the 34px bar; the default Top|Left
        // pinned it to the top edge, visibly above the status text next to it.
        statusService.Anchor = AnchorStyles.Left;
        statusService.Margin = new Padding(0, 0, 22, 0);
        grid.Controls.Add(statusService, 0, 0);
        status.AutoEllipsis = true;
        // A 34px bar holds one line. AutoSize let a long error wrap to a second
        // line that was then clipped by the bar itself, so the label is pinned
        // to the cell and ends in an ellipsis instead.
        status.AutoSize = false;
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        grid.Controls.Add(status, 1, 0);

        Control[] rightItems = [statusPending, statusCodex, statusDevice];
        foreach (var label in rightItems)
        {
            label.AutoSize = true;
            label.Dock = DockStyle.None;
        }
        var right = DeckTheme.Inline(rightItems, gap: 20);
        right.Anchor = AnchorStyles.Right;
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

    /// <summary>QA hook: jump straight to a page so a screenshot needs no click.</summary>
    internal void OpenPage(int index) => SelectPage(index);

    private void SelectPage(int index)
    {
        if (index < 0 || index >= pages.Count) return;
        // 项目与日志是磁盘的镜像。进页时重读一次，而不是常驻刷新：常驻刷新会让
        // 「刚在别处改过授权清单」这件事出现两个真相。
        if (index == 2) RefreshProjectsPage();
        else if (index == 3) RefreshLogsPage();
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

            // The brand lives in the rail now, where the design puts it. This bar
            // carries connection state on the left and machine identity on the
            // right; the old version ran logo + host + device + state across one
            // 44px strip, which made the top bar the busiest line in the window.
            int left = 18;
            if (state.Length > 0)
            {
                int cy = Height / 2;
                using (var halo = new SolidBrush(Color.FromArgb(38, stateColor)))
                    g.FillEllipse(halo, left - 1, cy - 5, 10, 10);
                using (var core = new SolidBrush(stateColor))
                    g.FillEllipse(core, left + 1, cy - 3, 6, 6);
                left += 15;
                TextRenderer.DrawText(g, state, DeckTheme.Body,
                    new Rectangle(left, 0, 280, Height), stateColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            var meta = device.Length == 0 ? host : host + " · " + device;
            if (meta.Length == 0) return;
            int metaWidth = TextRenderer.MeasureText(g, meta, DeckTheme.MonoSmall, Size.Empty,
                TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, meta, DeckTheme.MonoSmall,
                new Rectangle(Width - 18 - metaWidth, 0, metaWidth, Height), DeckTheme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // Painted sidebar. The design gives the rail the full window height and puts
    // the brand at its top, so this control owns the logo, five labelled items
    // and the count badge. Painted as one control on purpose: nested panels would
    // each paint their own background over the hover fill.
    private sealed class RailView : Control
    {
        private const int PadX = 10;
        private const int NavTop = 64;
        private const int ItemHeight = 33;
        private const int ItemGap = 2;
        private static readonly string[] Labels = ["主机", "任务", "项目", "日志", "设置"];
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
            for (int index = 0; index < Labels.Length; index++)
            {
                if (ItemBounds(index).Contains(point)) return index;
            }
            return -1;
        }

        private Rectangle ItemBounds(int index) =>
            new(PadX, NavTop + index * (ItemHeight + ItemGap), Width - PadX * 2, ItemHeight);

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

            DrawBrand(g);

            for (int index = 0; index < Labels.Length; index++)
            {
                var box = ItemBounds(index);
                if (index == selected || index == hover)
                {
                    using var path = DeckTheme.Rounded(box, 6);
                    // Bar and Surface2 are the same #fafbfc now, so a "surface" fill
                    // would make the selected row invisible against the rail.
                    using var fill = new SolidBrush(index == selected ? DeckTheme.Selected : DeckTheme.Active);
                    g.FillPath(fill, path);
                }

                var color = index == selected ? DeckTheme.Ink : DeckTheme.Muted;
                var icon = new Rectangle(box.X + 9, box.Y + (ItemHeight - 16) / 2, 16, 16);
                DrawIcon(g, index, icon, color);
                TextRenderer.DrawText(g, Labels[index], DeckTheme.Body,
                    new Rectangle(icon.Right + 9, box.Y, box.Width - 62, ItemHeight), color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.EndEllipsis);

                if (index == 1 && badge > 0)
                {
                    var text = badge > 9 ? "9+" : badge.ToString();
                    int width = text.Length > 1 ? 20 : 17;
                    var pill = new Rectangle(box.Right - width - 8, box.Y + (ItemHeight - 16) / 2, width, 16);
                    using var path = DeckTheme.Rounded(pill, 8);
                    using var fill = new SolidBrush(DeckTheme.Badge);
                    g.FillPath(fill, path);
                    TextRenderer.DrawText(g, text, DeckTheme.MonoMicro, pill, DeckTheme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }

            // Heartbeat only. The words already live in the top bar, and repeating
            // them down here would make one state look like two.
            int cy = Height - 20;
            int cx = PadX + 9;
            using (var halo = new SolidBrush(Color.FromArgb(38, statusColor)))
                g.FillEllipse(halo, cx - 1, cy - 4, 10, 10);
            using (var core = new SolidBrush(statusColor))
                g.FillEllipse(core, cx + 1, cy - 2, 6, 6);
        }

        private void DrawBrand(Graphics g)
        {
            var mark = new Rectangle(16, 16, 24, 24);
            using (var path = DeckTheme.Rounded(mark, 7))
            using (var fill = new SolidBrush(DeckTheme.Accent))
                g.FillPath(fill, path);
            // Same chevron as the tray icon: without it the mark reads as a
            // coloured placeholder square rather than a logo.
            using (var glyph = new Pen(DeckTheme.AccentInk, 2f)
                   {
                       StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
                   })
                g.DrawLines(glyph,
                [
                    new PointF(mark.X + 8.0f, mark.Y + 7.0f),
                    new PointF(mark.X + 14.0f, mark.Y + 12.0f),
                    new PointF(mark.X + 8.0f, mark.Y + 17.0f)
                ]);

            int x = mark.Right + 9;
            TextRenderer.DrawText(g, "AgentDeck", DeckTheme.Section,
                new Rectangle(x, 14, Width - x - 10, 18), DeckTheme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, "远程控制台", DeckTheme.Tiny,
                new Rectangle(x, 31, Width - x - 10, 15), DeckTheme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
                case 2:
                    // Folder: the tab on the top edge is what separates it from the
                    // task list at 16px. A plain rectangle reads as "page".
                    g.DrawLines(pen,
                    [
                        new PointF(cx - 8, cy + 5.5f), new PointF(cx - 8, cy - 5.5f),
                        new PointF(cx - 3, cy - 5.5f), new PointF(cx - 1, cy - 3),
                        new PointF(cx + 8, cy - 3), new PointF(cx + 8, cy + 5.5f),
                        new PointF(cx - 8, cy + 5.5f)
                    ]);
                    break;
                case 3:
                    // Document with ruled lines: same family as the task icon, but
                    // enclosed, so the two are told apart by silhouette.
                    g.DrawRectangle(pen, cx - 6, cy - 7, 12, 14);
                    g.DrawLine(pen, cx - 3, cy - 3.5f, cx + 3, cy - 3.5f);
                    g.DrawLine(pen, cx - 3, cy, cx + 3, cy);
                    g.DrawLine(pen, cx - 3, cy + 3.5f, cx + 1, cy + 3.5f);
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
