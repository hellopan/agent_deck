using System.Text;
using System.Text.Json;

namespace AgentDeck.Tray;

// 任务 page: the queue on the left, one task's request and trace on the right.
// Approvals still happen on the phone — this side can look, and can stop a run,
// but cannot approve on the phone's behalf.
internal sealed partial class ReviewWindow
{
    // A FlowLayoutPanel rather than a TableLayoutPanel: an absolute-height row
    // inside an AutoScroll table still hands the leftover space to its last row,
    // which stretched the final task card to the bottom of the window.
    private readonly FlowLayoutPanel queueList = new()
    {
        Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(0, 0, 2, 2)
    };
    private readonly Label queueCount = DeckTheme.Label("队列为空", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label detailTitle = DeckTheme.Label("未选择任务", DeckTheme.Hero);
    private readonly Label detailState = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label detailMeta = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly TextBox detailPrompt = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.None,
        WordWrap = true, AccessibleName = "请求内容"
    };
    private readonly Label detailDirectory = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label detailSandbox = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label detailAdapter = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label detailTaskId = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label traceTitle = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly TraceView traceOutput = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label traceEmpty = DeckTheme.Label(
        "该任务还没有输出。任务开始运行后，事件会写在这里。", DeckTheme.Small, DeckTheme.Dim);
    private readonly Button follow = DeckTheme.Button("LIVE · 跟随中");
    private QueueItem? selected;
    private long traceLength = -1;
    private bool followTrace = true;

    private Control TasksPage()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(QueueCard(), 0, 0);
        body.Controls.Add(new Panel { BackColor = DeckTheme.Canvas }, 1, 0);
        body.Controls.Add(DetailCard(), 2, 0);
        return Page("任务", "查看这台电脑的任务队列与执行状态；审批在手机上完成。", body);
    }

    private Control QueueCard()
    {
        var card = Card("任务", out var cardBody, note: "按提交时间");
        DeckTheme.Row(cardBody, queueCount, 24);
        DeckTheme.Row(cardBody, queueList, 100, fill: true);
        queueList.SizeChanged += (_, _) => ResizeQueueRows();
        var actions = DeckTheme.Actions(DeckTheme.Surface);
        AddAction(actions, "刷新队列", RefreshInboxAsync, primary: true);
        DeckTheme.Row(cardBody, actions, 38);
        return card;
    }

    private Control DetailCard()
    {
        var card = Card("任务详情", out var body, note: "审批在手机上完成");
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailTitle.AutoEllipsis = true;
        head.Controls.Add(detailTitle, 0, 0);
        detailState.Margin = new Padding(10, 0, 0, 0);
        detailState.AutoSize = true;
        detailState.Dock = DockStyle.None;
        detailState.TextAlign = ContentAlignment.MiddleRight;
        head.Controls.Add(detailState, 1, 0);
        DeckTheme.Row(body, head, 30);
        detailMeta.AutoEllipsis = true;
        DeckTheme.Row(body, detailMeta, 20);
        DeckTheme.Row(body, DeckTheme.Caption("请求内容"), 24);
        DeckTheme.Row(body, DeckTheme.Reader(detailPrompt, "选择左侧任务后显示提交的提示词。"), 88);

        var facts = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 2
        };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        facts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        facts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        facts.Controls.Add(Fact("工作目录", detailDirectory), 0, 0);
        facts.Controls.Add(Fact("沙箱", detailSandbox), 1, 0);
        facts.Controls.Add(Fact("适配器", detailAdapter), 0, 1);
        facts.Controls.Add(Fact("任务 ID", detailTaskId), 1, 1);
        DeckTheme.Row(body, facts, 66);

        var traceHead = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 6, 0, 0)
        };
        traceHead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        traceHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        traceHead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        traceHead.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caption = DeckTheme.Caption("执行轨迹");
        caption.AutoSize = true;
        caption.Dock = DockStyle.None;
        traceHead.Controls.Add(caption, 0, 0);
        traceTitle.AutoEllipsis = true;
        traceTitle.Margin = new Padding(10, 0, 0, 0);
        traceHead.Controls.Add(traceTitle, 1, 0);
        follow.AutoSize = true;
        follow.Dock = DockStyle.None;
        follow.MinimumSize = new Size(0, 22);
        follow.Padding = new Padding(8, 0, 8, 0);
        follow.Font = DeckTheme.Tiny;
        follow.Margin = Padding.Empty;
        follow.Click += (_, _) =>
        {
            followTrace = !followTrace;
            follow.Text = followTrace ? "LIVE · 跟随中" : "已暂停";
            follow.ForeColor = followTrace ? DeckTheme.Good : DeckTheme.Warning;
            if (followTrace) LoadTrace(force: true);
        };
        follow.ForeColor = DeckTheme.Good;
        traceHead.Controls.Add(follow, 2, 0);
        DeckTheme.Row(body, traceHead, 30);
        var traceFrame = new Panel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12), BackColor = DeckTheme.Well
        };
        traceEmpty.TextAlign = ContentAlignment.MiddleCenter;
        traceFrame.Controls.Add(traceOutput);
        traceFrame.Controls.Add(traceEmpty);
        traceEmpty.BringToFront();
        DeckTheme.Row(body, traceFrame, 100, fill: true);

        var actions = DeckTheme.Actions(DeckTheme.Surface);
        AddAction(actions, "叫停任务", CancelAsync, danger: true);
        DeckTheme.Row(body, actions, 38);
        return card;
    }

    private static Control Fact(string caption, Label value)
    {
        var table = DeckTheme.Table(background: DeckTheme.Surface);
        DeckTheme.Row(table, DeckTheme.Caption(caption), 15);
        value.AutoEllipsis = true;
        DeckTheme.Row(table, value, 17);
        return table;
    }

    private async Task RefreshInboxAsync()
    {
        var output = await RunAsync("inbox", "-task-inbox", RequirePath(InboxPath, "任务收件箱"),
            "-projects", RequirePath(ProjectsPath, "项目授权清单"), "-task-logs", RequirePath(LogsPath, "任务日志目录"));
        var items = (JsonSerializer.Deserialize<QueueView>(output, Json)?.Items ?? [])
            .OrderBy(item => item.Status is "queued" or "running" ? 0 : 1)
            .ThenByDescending(item => item.SubmittedAt)
            .ToArray();
        var previous = selected?.TaskId;
        queueList.SuspendLayout();
        queueList.Controls.Clear();
        var active = items.Count(item => item.Status is "queued" or "running");
        queueCount.Text = items.Length == 0 ? "队列为空" : $"{active} 条未结束 · 共 {items.Length} 条";
        statusPending.Text = $"待处理 {active}";
        asidePending.Text = active.ToString();
        pages[1].Tab.Badge = active;
        var archived = false;
        foreach (var item in items)
        {
            bool final = item.Status is not ("queued" or "running");
            if (final && !archived)
            {
                archived = true;
                var separator = DeckTheme.Label($"已处理 · {items.Length - active}", DeckTheme.Small, DeckTheme.Dim);
                separator.AutoSize = false;
                separator.Height = 26;
                separator.Padding = new Padding(10, 0, 0, 0);
                queueList.Controls.Add(separator);
            }
            var row = new QueueRow(item) { Height = 78 };
            row.Click += (_, _) => SelectTask(row);
            queueList.Controls.Add(row);
        }
        if (items.Length == 0)
        {
            var empty = DeckTheme.Label("还没有任务。手机端提交后会出现在这里。", DeckTheme.Small, DeckTheme.Dim);
            empty.AutoSize = false;
            empty.Height = 34;
            empty.Padding = new Padding(12, 10, 0, 0);
            queueList.Controls.Add(empty);
        }
        ResizeQueueRows();
        queueList.ResumeLayout();
        var keep = queueList.Controls.OfType<QueueRow>().FirstOrDefault(row => row.Item.TaskId == previous)
            ?? queueList.Controls.OfType<QueueRow>().FirstOrDefault();
        if (keep is not null) SelectTask(keep);
        else ClearDetail();
        status.Text = items.Length == 0 ? "队列为空。" : $"已加载 {items.Length} 条任务。";
    }

    // FlowLayoutPanel never stretches a child, so each row is told how wide the
    // viewport is whenever it changes; otherwise long summaries clip at 100px.
    private void ResizeQueueRows()
    {
        int width = Math.Max(40, queueList.ClientSize.Width - (queueList.VerticalScroll.Visible ? 18 : 2));
        foreach (Control row in queueList.Controls) row.Width = width;
    }

    private void SelectTask(QueueRow row)
    {
        selected = row.Item;
        foreach (var other in queueList.Controls.OfType<QueueRow>()) other.Selected = other == row;
        detailTitle.Text = string.IsNullOrWhiteSpace(row.Item.Summary) ? "未命名任务" : row.Item.Summary;
        detailState.Text = StatusWord(row.Item.Status);
        detailState.ForeColor = StatusColor(row.Item.Status);
        detailMeta.Text = $"{row.Item.ProjectId} · 提交于 {row.Item.SubmittedAt.ToLocalTime():MM-dd HH:mm}";
        detailPrompt.Text = row.Item.Prompt;
        detailDirectory.Text = row.Item.Directory;
        detailSandbox.Text = "read-only";
        detailAdapter.Text = codexVersion is { Length: > 0 } version ? $"Codex {version}" : "Codex";
        detailTaskId.Text = row.Item.TaskId[..Math.Min(12, row.Item.TaskId.Length)];
        traceTitle.Text = $"task {row.Item.TaskId[..Math.Min(8, row.Item.TaskId.Length)]}";
        traceLength = -1;
        traceOutput.Clear();
        traceEmpty.Visible = false;
        traceOutput.Visible = true;
        followTrace = true;
        follow.Text = "LIVE · 跟随中";
        follow.ForeColor = DeckTheme.Good;
        LoadTrace(force: true);
    }

    private void ClearDetail()
    {
        selected = null;
        detailTitle.Text = "未选择任务";
        detailState.Text = "";
        detailMeta.Text = "";
        detailPrompt.Text = "";
        detailDirectory.Text = detailSandbox.Text = detailAdapter.Text = detailTaskId.Text = "—";
        traceTitle.Text = "";
        traceOutput.Clear();
        traceEmpty.Text = "选择一条任务，查看它的执行轨迹。";
        traceEmpty.Visible = true;
        traceOutput.Visible = false;
        traceLength = -1;
    }

    // Reads the journal tail. A journal is JSON lines; anything that fails to
    // parse is shown verbatim rather than dropped, so a corrupt line is visible
    // instead of silently shortening the trace.
    private void LoadTrace(bool force)
    {
        if (selected is null) return;
        var path = Path.Combine(RequirePath(LogsPath, "任务日志目录"), selected.TaskId + ".log");
        long length;
        try { length = File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { length = 0; }
        if (!force && (!followTrace || length == traceLength)) return;
        traceLength = length;
        traceOutput.Clear();
        if (!File.Exists(path))
        {
            traceEmpty.Text = "该任务还没有输出。任务开始运行后，事件会写在这里。";
            traceEmpty.Visible = true;
            traceOutput.Visible = false;
            return;
        }
        traceOutput.Visible = true;
        traceEmpty.Visible = false;
        string text;
        try
        {
            const int limit = 256 * 1024;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var start = Math.Max(0, file.Length - limit);
            file.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[limit];
            int read = file.ReadAtLeast(bytes, bytes.Length, false);
            if (start > 0)
            {
                int offset = Array.IndexOf(bytes, (byte)'\n', 0, read) + 1;
                text = Encoding.UTF8.GetString(bytes, offset, read - offset);
                AppendTrace("仅显示日志末尾。", DeckTheme.Dim);
            }
            else
            {
                text = Encoding.UTF8.GetString(bytes, 0, read);
            }
        }
        catch (IOException error)
        {
            AppendTrace($"无法读取日志：{error.Message}", DeckTheme.Danger);
            return;
        }
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            RenderEvent(trimmed);
        }
        traceOutput.Follow();
    }

    private void RenderEvent(string line)
    {
        string type = "", text = "", at = "", state = "";
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            text = root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";
            at = root.TryGetProperty("at", out var a) ? a.GetString() ?? "" : "";
            state = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            AppendTrace(line, DeckTheme.Dim);
            return;
        }
        var time = DateTimeOffset.TryParse(at, out var moment) ? moment.ToLocalTime().ToString("HH:mm:ss") : "        ";
        var (label, color) = type switch
        {
            "approval/requested" => ("审批 · ", DeckTheme.Warning),
            "approval/declined" => ("拒绝 · ", DeckTheme.Danger),
            "approval/accepted" => ("允许 · ", DeckTheme.Good),
            "task/notice" => ("提示 · ", DeckTheme.Muted),
            "task/finished" => ("结束 · ", state == "completed" ? DeckTheme.Good : DeckTheme.Danger),
            _ => ("", DeckTheme.Dim)
        };
        var detail = text.Length > 0 ? text : type;
        if (type == "task/finished" && state.Length > 0) detail = $"{StatusWord(state)} {text}".Trim();
        traceOutput.Append(time, label, detail, color);
    }

    private void AppendTrace(string text, Color color) => traceOutput.Append("", "", text, color);

    private static string StatusWord(string status) => status switch
    {
        "queued" => "排队中",
        "running" => "执行中",
        "completed" => "已完成",
        "failed" => "失败",
        "interrupted" => "已中断",
        "canceled" => "已叫停",
        _ => status
    };

    private static Color StatusColor(string status) => status switch
    {
        "queued" => DeckTheme.Dim,
        "running" => DeckTheme.Accent,
        "completed" => DeckTheme.Good,
        "failed" => DeckTheme.Danger,
        "interrupted" => DeckTheme.Warning,
        "canceled" => DeckTheme.Dim,
        _ => DeckTheme.Dim
    };

    private async Task CancelAsync()
    {
        if (selected is null) throw new InvalidOperationException("请先选择一条任务。");
        var item = selected;
        if (MessageBox.Show(this, $"叫停这个任务？\r\n\r\n{item.Summary}\r\n\r\n已开始的命令不会回滚。",
                "确认叫停", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            status.Text = "已取消。";
            return;
        }
        await RunAsync("cancel-task", "-task-inbox", RequirePath(InboxPath, "任务收件箱"),
            "-projects", RequirePath(ProjectsPath, "项目授权清单"), "-project-id", item.ProjectId,
            "-task-id", item.TaskId, "-task-logs", RequirePath(LogsPath, "任务日志目录"));
        status.Text = "已请求叫停，等待 Agent 收尾。";
        await RefreshInboxAsync();
    }

    // One queue card. Painted rather than composed from labels: a task list can
    // hold dozens of rows, and every extra control costs a layout pass.
    private sealed class QueueRow : Control
    {
        internal QueueItem Item { get; }
        private bool hover;
        private bool selected;

        internal QueueRow(QueueItem item)
        {
            Item = item;
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = DeckTheme.Surface;
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Selected
        {
            get => selected;
            set
            {
                if (selected == value) return;
                selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var background = new SolidBrush(Selected ? DeckTheme.Surface2 : hover ? DeckTheme.Line2 : DeckTheme.Surface))
                g.FillRectangle(background, ClientRectangle);
            using (var pen = new Pen(DeckTheme.Line2))
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            if (Selected)
            {
                using var accent = new SolidBrush(DeckTheme.Accent);
                g.FillRectangle(accent, 0, 6, 2, Height - 12);
            }
            int left = 12;
            TextRenderer.DrawText(g, Item.ProjectId, DeckTheme.Monospace,
                new Point(left, 8), DeckTheme.Dim, TextFormatFlags.NoPadding);
            var time = Item.SubmittedAt.ToLocalTime().ToString("HH:mm");
            var timeWidth = TextRenderer.MeasureText(g, time, DeckTheme.Monospace, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, time, DeckTheme.Monospace,
                new Point(Width - timeWidth - 10, 8), DeckTheme.Dim, TextFormatFlags.NoPadding);
            var title = string.IsNullOrWhiteSpace(Item.Summary) ? Item.Prompt : Item.Summary;
            TextRenderer.DrawText(g, title, DeckTheme.Body,
                new Rectangle(left, 26, Math.Max(10, Width - left - 12), 20), DeckTheme.Ink,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            var word = StatusWord(Item.Status);
            var color = StatusColor(Item.Status);
            int x = left;
            using (var dot = new SolidBrush(color))
                g.FillEllipse(dot, x, Height - 24, 5, 5);
            TextRenderer.DrawText(g, word, DeckTheme.Small,
                new Point(x + 10, Height - 31), color, TextFormatFlags.NoPadding);
            var id = Item.TaskId.Length > 8 ? Item.TaskId[..8] : Item.TaskId;
            var idWidth = TextRenderer.MeasureText(g, id, DeckTheme.Monospace, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, id, DeckTheme.Monospace,
                new Point(Width - idWidth - 10, Height - 30), DeckTheme.Dim, TextFormatFlags.NoPadding);
        }
    }
}
