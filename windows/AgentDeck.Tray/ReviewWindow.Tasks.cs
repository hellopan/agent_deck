using System.Text;
using System.Text.Json;

namespace AgentDeck.Tray;

// 任务 page: a queue pane on the left and one task's request plus trace on the
// right. Both live inside a single rounded shell with a hairline split, which
// reads as one workspace; two separate cards made the same data look like two
// unrelated panels. Approvals still happen on the phone — this side can look,
// and can stop a run, but cannot approve on the phone's behalf.
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
    private readonly Label queueChip = DeckTheme.Chip("0", DeckTheme.Accent);
    private readonly Label queueCount = DeckTheme.Label("队列为空", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label detailTitle = DeckTheme.Label("未选择任务", DeckTheme.Hero);
    private readonly Label detailState = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label detailMeta = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly TextBox detailPrompt = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.None,
        WordWrap = true, AccessibleName = "请求内容"
    };
    private readonly Label detailDirectory = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label detailSandbox = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label detailAdapter = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label detailTaskId = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label traceTitle = DeckTheme.Label("", DeckTheme.MonoSmall, DeckTheme.Dim);
    private readonly TraceView traceOutput = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly TableLayoutPanel traceEmpty = DeckTheme.Table(background: DeckTheme.Well);
    private readonly Label traceEmptyTitle = DeckTheme.Label("未选择任务", DeckTheme.Body, DeckTheme.Muted);
    private readonly Label traceEmptyHint = DeckTheme.Label("从左侧队列选择一条任务查看轨迹。",
        DeckTheme.Small, DeckTheme.Dim);
    private readonly Button follow = DeckTheme.Button("LIVE");
    private readonly Button cancel = DeckTheme.Button("叫停任务", danger: true);
    private QueueItem? selected;
    private long traceLength = -1;
    private bool followTrace = true;

    private Control TasksPage()
    {
        var card = new DeckTheme.CardPanel();
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.Controls.Add(QueuePane(), 0, 0);
        split.Controls.Add(new Panel { BackColor = DeckTheme.Line, Margin = Padding.Empty }, 1, 0);
        split.Controls.Add(DetailPane(), 2, 0);
        card.Controls.Add(split);
        return Page("任务", "查看这台电脑的任务队列与执行状态；审批在手机上完成。", card);
    }

    private Control QueuePane()
    {
        var pane = DeckTheme.Table(background: DeckTheme.Surface);
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1, Padding = new Padding(16, 0, 14, 0)
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = DeckTheme.Label("任务", DeckTheme.Section);
        title.AutoSize = true;
        title.Dock = DockStyle.None;
        head.Controls.Add(title, 0, 0);
        queueChip.Margin = new Padding(9, 0, 0, 0);
        queueChip.BackColor = DeckTheme.Tint(DeckTheme.Accent, 0.14);
        queueChip.ForeColor = DeckTheme.Accent;
        head.Controls.Add(queueChip, 1, 0);
        var order = DeckTheme.Label("按提交时间", DeckTheme.Tiny, DeckTheme.Dim);
        order.TextAlign = ContentAlignment.MiddleRight;
        head.Controls.Add(order, 2, 0);
        DeckTheme.Row(pane, head, 62);
        DeckTheme.Row(pane, DeckTheme.Divider(DeckTheme.Line2), 1);
        DeckTheme.Row(pane, queueList, 100, fill: true);
        queueList.SizeChanged += (_, _) => ResizeQueueRows();

        DeckTheme.Row(pane, DeckTheme.Divider(), 1);
        var foot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1, Padding = new Padding(16, 0, 12, 0)
        };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        queueCount.AutoEllipsis = true;
        foot.Controls.Add(queueCount, 0, 0);
        var refresh = DeckTheme.Button("刷新");
        refresh.MinimumSize = new Size(56, 26);
        refresh.Padding = new Padding(8, 0, 8, 0);
        refresh.Font = DeckTheme.Small;
        refresh.Margin = Padding.Empty;
        refresh.Anchor = AnchorStyles.None;
        refresh.Click += async (_, _) => await RunActionAsync(RefreshInboxAsync);
        foot.Controls.Add(refresh, 1, 0);
        DeckTheme.Row(pane, foot, 46);
        return pane;
    }

    private Control DetailPane()
    {
        var pane = DeckTheme.Table(background: DeckTheme.Surface);
        var head = DeckTheme.Table(new Padding(20, 14, 20, 12), DeckTheme.Surface);
        var titleLine = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        titleLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailTitle.AutoEllipsis = true;
        titleLine.Controls.Add(detailTitle, 0, 0);
        detailState.AutoSize = true;
        detailState.Dock = DockStyle.None;
        detailState.Anchor = AnchorStyles.Right;
        detailState.Padding = new Padding(8, 3, 8, 3);
        detailState.Margin = new Padding(12, 0, 0, 0);
        detailState.Visible = false;
        DeckTheme.Round(detailState, 3);
        titleLine.Controls.Add(detailState, 1, 0);
        DeckTheme.Row(head, titleLine, 30);
        detailMeta.AutoEllipsis = true;
        DeckTheme.Row(head, detailMeta, 20);
        DeckTheme.Row(pane, head, 62);
        DeckTheme.Row(pane, DeckTheme.Divider(), 1);

        var body = DeckTheme.Table(new Padding(20, 0, 20, 0), DeckTheme.Surface);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 14);
        DeckTheme.Row(body, DeckTheme.Caption("请求内容"), 22);
        var promptFrame = new Panel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(14, 10, 14, 8),
            BackColor = DeckTheme.Well
        };
        promptFrame.Controls.Add(detailPrompt);
        promptFrame.Paint += (_, e) =>
        {
            using var accent = new SolidBrush(DeckTheme.Accent);
            e.Graphics.FillRectangle(accent, 0, 8, 2, Math.Max(0, promptFrame.Height - 16));
        };
        DeckTheme.Round(promptFrame, 6);
        detailPrompt.BackColor = DeckTheme.Well;
        detailPrompt.ForeColor = DeckTheme.Muted;
        detailPrompt.Font = DeckTheme.Body;
        detailPrompt.BorderStyle = BorderStyle.None;
        DeckTheme.Row(body, promptFrame, 74);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 14);
        DeckTheme.Row(body, FactsRow(), 58);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 14);
        DeckTheme.Row(body, TraceHead(), 30);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 6);

        var traceFrame = new Panel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12, 8, 12, 8),
            BackColor = DeckTheme.Well
        };
        traceEmptyTitle.TextAlign = ContentAlignment.MiddleCenter;
        traceEmptyHint.TextAlign = ContentAlignment.MiddleCenter;
        DeckTheme.Row(traceEmpty, new Panel { BackColor = DeckTheme.Well }, 100, fill: true);
        DeckTheme.Row(traceEmpty, traceEmptyTitle, 24);
        DeckTheme.Row(traceEmpty, traceEmptyHint, 20);
        DeckTheme.Row(traceEmpty, new Panel { BackColor = DeckTheme.Well }, 100, fill: true);
        traceFrame.Controls.Add(traceOutput);
        traceFrame.Controls.Add(traceEmpty);
        traceEmpty.BringToFront();
        DeckTheme.Round(traceFrame, 6);
        DeckTheme.Row(body, traceFrame, 100, fill: true);
        DeckTheme.Row(pane, body, 100, fill: true);

        DeckTheme.Row(pane, DeckTheme.Divider(), 1);
        var foot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1, Padding = new Padding(20, 0, 16, 0)
        };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var hint = DeckTheme.Label("审批在手机上完成；这里只能查看与叫停。", DeckTheme.Tiny, DeckTheme.Dim);
        hint.AutoEllipsis = true;
        foot.Controls.Add(hint, 0, 0);
        cancel.Margin = Padding.Empty;
        cancel.Anchor = AnchorStyles.None;
        cancel.MinimumSize = new Size(0, 30);
        cancel.Enabled = false;
        cancel.Click += async (_, _) => await RunActionAsync(CancelAsync);
        foot.Controls.Add(cancel, 1, 0);
        DeckTheme.Row(pane, foot, 50);
        return pane;
    }

    private Control FactsRow()
    {
        var facts = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 7, RowCount = 1
        };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        facts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        facts.Controls.Add(Fact("工作目录", detailDirectory), 0, 0);
        facts.Controls.Add(ColumnRule(), 1, 0);
        facts.Controls.Add(Fact("沙箱", detailSandbox), 2, 0);
        facts.Controls.Add(ColumnRule(), 3, 0);
        facts.Controls.Add(Fact("适配器", detailAdapter), 4, 0);
        facts.Controls.Add(ColumnRule(), 5, 0);
        facts.Controls.Add(Fact("任务 ID", detailTaskId), 6, 0);
        return facts;
    }

    private static Control ColumnRule()
    {
        var rule = DeckTheme.Divider();
        rule.Margin = new Padding(0, 6, 0, 6);
        return rule;
    }

    private static Control Fact(string caption, Label value)
    {
        var table = DeckTheme.Table(background: DeckTheme.Surface);
        DeckTheme.Row(table, DeckTheme.Caption(caption), 16);
        value.AutoEllipsis = true;
        value.Font = DeckTheme.MonoSmall;
        DeckTheme.Row(table, value, 20);
        table.Margin = new Padding(12, 0, 6, 0);
        return table;
    }

    private Control TraceHead()
    {
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caption = DeckTheme.Caption("执行轨迹");
        caption.AutoSize = true;
        caption.Dock = DockStyle.None;
        head.Controls.Add(caption, 0, 0);
        traceTitle.AutoEllipsis = true;
        traceTitle.Font = DeckTheme.MonoMicro;
        traceTitle.Margin = new Padding(10, 0, 0, 0);
        head.Controls.Add(traceTitle, 1, 0);

        follow.Font = DeckTheme.MonoMicro;
        follow.MinimumSize = new Size(0, 20);
        follow.Padding = new Padding(7, 0, 7, 0);
        follow.Margin = Padding.Empty;
        follow.Anchor = AnchorStyles.Right;
        follow.FlatAppearance.BorderSize = 0;
        follow.BackColor = DeckTheme.Tint(DeckTheme.Good, 0.14);
        follow.ForeColor = DeckTheme.Good;
        follow.FlatAppearance.MouseOverBackColor = DeckTheme.Tint(DeckTheme.Good, 0.24);
        DeckTheme.Round(follow, 3);
        follow.Click += (_, _) =>
        {
            followTrace = !followTrace;
            follow.Text = followTrace ? "LIVE" : "暂停";
            follow.ForeColor = followTrace ? DeckTheme.Good : DeckTheme.Warning;
            follow.BackColor = DeckTheme.Tint(followTrace ? DeckTheme.Good : DeckTheme.Warning, 0.14);
            if (followTrace) LoadTrace(force: true);
        };
        head.Controls.Add(follow, 2, 0);
        return head;
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
        queueChip.Text = active.ToString();
        queueChip.ForeColor = active > 0 ? DeckTheme.Accent : DeckTheme.Dim;
        queueChip.BackColor = active > 0 ? DeckTheme.Tint(DeckTheme.Accent, 0.14) : DeckTheme.Surface2;
        queueCount.Text = items.Length == 0 ? "队列为空" : $"{active} 条未结束 · 共 {items.Length} 条";
        statusPending.Text = $"待处理 {active}";
        rail.Badge = active;
        var archived = false;
        foreach (var item in items)
        {
            bool final = item.Status is not ("queued" or "running");
            if (final && !archived)
            {
                archived = true;
                var separator = DeckTheme.Label($"已处理 · {items.Length - active}", DeckTheme.Tiny, DeckTheme.Dim);
                separator.AutoSize = false;
                separator.Height = 28;
                separator.Padding = new Padding(16, 0, 0, 0);
                queueList.Controls.Add(separator);
            }
            var row = new QueueRow(item) { Height = 84 };
            row.Click += (_, _) => SelectTask(row);
            queueList.Controls.Add(row);
        }
        if (items.Length == 0)
        {
            var empty = DeckTheme.Label("还没有任务。手机端提交后会出现在这里。", DeckTheme.Small, DeckTheme.Dim);
            empty.AutoSize = false;
            empty.Height = 40;
            empty.Padding = new Padding(16, 12, 0, 0);
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
        detailState.BackColor = DeckTheme.Tint(StatusColor(row.Item.Status));
        detailState.Visible = true;
        detailMeta.Text = $"{row.Item.ProjectId}   ·   提交于 {row.Item.SubmittedAt.ToLocalTime():MM-dd HH:mm}" +
                          $"   ·   {ShortId(row.Item.TaskId)}";
        detailPrompt.Text = row.Item.Prompt;
        SetFact(detailDirectory, row.Item.Directory);
        SetFact(detailSandbox, row.Item.Sandbox == "workspace-write" ? "可写" : "只读");
        SetFact(detailAdapter, codexVersion is { Length: > 0 } version ? $"Codex {version}" : "Codex");
        SetFact(detailTaskId, ShortId(row.Item.TaskId));
        traceTitle.Text = $"task {ShortId(row.Item.TaskId)}";
        traceLength = -1;
        traceOutput.Clear();
        traceEmpty.Visible = false;
        traceOutput.Visible = true;
        followTrace = true;
        follow.Text = "LIVE";
        follow.ForeColor = DeckTheme.Good;
        follow.BackColor = DeckTheme.Tint(DeckTheme.Good, 0.14);
        cancel.Enabled = row.Item.Status is "queued" or "running";
        LoadTrace(force: true);
    }

    private static string ShortId(string id) => id[..Math.Min(8, id.Length)];

    private static void SetFact(Label label, string text)
    {
        label.Text = text;
        label.ForeColor = text is "—" or "" ? DeckTheme.Dim : DeckTheme.Ink;
    }

    private void ClearDetail()
    {
        selected = null;
        detailTitle.Text = "未选择任务";
        detailState.Text = "";
        detailState.Visible = false;
        detailMeta.Text = "从左侧队列选择一条任务";
        detailPrompt.Text = "";
        foreach (var fact in new[] { detailDirectory, detailSandbox, detailAdapter, detailTaskId }) SetFact(fact, "—");
        traceTitle.Text = "";
        traceOutput.Clear();
        traceEmptyTitle.Text = "未选择任务";
        traceEmptyHint.Text = "从左侧队列选择一条任务查看轨迹。";
        traceEmpty.Visible = true;
        traceOutput.Visible = false;
        follow.Enabled = false;
        cancel.Enabled = false;
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
            traceEmptyTitle.Text = "尚无输出";
            traceEmptyHint.Text = "任务开始运行后，事件会实时写在这里。";
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
            using (var background = new SolidBrush(Selected ? DeckTheme.Selected : hover ? DeckTheme.Line2 : DeckTheme.Surface))
                g.FillRectangle(background, ClientRectangle);
            using (var pen = new Pen(DeckTheme.Hairline))
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            if (Selected)
            {
                using var accent = new SolidBrush(DeckTheme.Accent);
                g.FillRectangle(accent, 0, 0, 2, Height);
            }
            const int left = 16;
            int right = Width - 14;
            TextRenderer.DrawText(g, Item.ProjectId, DeckTheme.MonoMicro,
                new Point(left, 10), DeckTheme.Dim, TextFormatFlags.NoPadding);
            var time = Item.SubmittedAt.ToLocalTime().ToString("HH:mm");
            var timeWidth = TextRenderer.MeasureText(g, time, DeckTheme.MonoMicro, Size.Empty,
                TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, time, DeckTheme.MonoMicro,
                new Point(right - timeWidth, 10), DeckTheme.Dim, TextFormatFlags.NoPadding);
            var title = string.IsNullOrWhiteSpace(Item.Summary) ? Item.Prompt : Item.Summary;
            TextRenderer.DrawText(g, title, DeckTheme.Body,
                new Rectangle(left, 31, Math.Max(10, right - left), 20), DeckTheme.Ink,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            var word = StatusWord(Item.Status);
            var color = StatusColor(Item.Status);
            using (var dot = new SolidBrush(color))
                g.FillEllipse(dot, left, Height - 25, 5, 5);
            TextRenderer.DrawText(g, word, DeckTheme.Small,
                new Point(left + 11, Height - 32), color, TextFormatFlags.NoPadding);
            int wordWidth = TextRenderer.MeasureText(g, word, DeckTheme.Small, Size.Empty,
                TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, "Codex", DeckTheme.Tiny,
                new Point(left + 11 + wordWidth + 8, Height - 31), DeckTheme.Dim, TextFormatFlags.NoPadding);
            var id = Item.TaskId.Length > 8 ? Item.TaskId[..8] : Item.TaskId;
            var idWidth = TextRenderer.MeasureText(g, id, DeckTheme.MonoMicro, Size.Empty,
                TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, id, DeckTheme.MonoMicro,
                new Point(right - idWidth, Height - 31), DeckTheme.Dim, TextFormatFlags.NoPadding);
        }
    }
}
