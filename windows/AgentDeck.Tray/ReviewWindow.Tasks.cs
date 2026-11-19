using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentDeck.Tray;

// 任务 page: three cards, top to bottom — what is running, what is waiting for
// a human, and what finished. There is deliberately no approve button anywhere:
// the phone is the only place a command can be allowed, and the card footer says
// so. The desktop keeps "view trace" and "stop", nothing else.
internal sealed partial class ReviewWindow
{
    private readonly FlowLayoutPanel board = new()
    {
        Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = Padding.Empty
    };
    private readonly DeckTheme.PillLabel runningCount =
        DeckTheme.Pill("0 个", DeckTheme.PillTone.Neutral, dot: true);
    private readonly DeckTheme.PillLabel pendingCount =
        DeckTheme.Pill("0 条", DeckTheme.PillTone.Neutral);
    private readonly DataGridView runningGrid = Sheet(
        "现在没有任务在跑。手机提交后，电脑空闲时会在这里开始执行。");
    private readonly DataGridView pendingGrid = Sheet(
        "没有等待批准的请求。需要批准时，手机会收到通知。");
    private readonly DataGridView doneGrid = Sheet(
        "还没有结束的任务。完成、失败与叫停都会留在这里。");
    private Control runningCard = null!;
    private Control pendingCard = null!;
    private Control doneCard = null!;
    private List<QueueItem> boardItems = [];
    private bool boardBusy;
    private readonly System.Windows.Forms.Timer boardTimer = new() { Interval = 5000 };

    private Control TasksPage()
    {
        BuildBoard();
        boardTimer.Tick += (_, _) => _ = RefreshTaskBoardAsync();
        FormClosed += (_, _) => boardTimer.Dispose();
        var page = Page("任务", "看得到全部，只批不了任何一条。批准在手机上完成，电脑端只保留叫停。", board);
        // A process per refresh is not free: only poll while this page is up.
        page.VisibleChanged += (_, _) =>
        {
            if (page.Visible)
            {
                boardTimer.Start();
                _ = RefreshTaskBoardAsync();
            }
            else boardTimer.Stop();
        };
        return page;
    }

    private void BuildBoard()
    {
        runningGrid.Columns.Add(Column("项目", 120, mono: true));
        runningGrid.Columns.Add(Column("任务", 0, fill: true));
        runningGrid.Columns.Add(Column("已运行", 84, mono: true));
        runningGrid.Columns.Add(Column("最后一条事件", 190));
        runningGrid.Columns.Add(LinkColumn("轨迹", 58));
        runningGrid.Columns.Add(LinkColumn("叫停", 58));
        runningGrid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= boardItems.Count) return;
            var item = boardItems[e.RowIndex];
            if (e.ColumnIndex == 4) OpenTrace(item);
            else if (e.ColumnIndex == 5) _ = RunActionAsync(() => CancelAsync(item));
        };

        pendingGrid.Columns.Add(Column("项目", 120, mono: true));
        pendingGrid.Columns.Add(Column("请求内容", 0, fill: true, mono: true));
        pendingGrid.Columns.Add(Column("影响范围", 190));
        pendingGrid.Columns.Add(Column("已等待", 88, mono: true));
        pendingGrid.Columns.Add(Column("等待对象", 88, mono: true));

        doneGrid.Columns.Add(Column("项目", 120, mono: true));
        doneGrid.Columns.Add(Column("摘要", 0, fill: true));
        doneGrid.Columns.Add(Column("耗时", 84, mono: true));
        doneGrid.Columns.Add(Column("结束于", 96, mono: true));
        doneGrid.Columns.Add(Column("结果", 80));

        runningCard = TaskCard("正在执行", runningCount, runningGrid,
            "叫停会终止本次运行并保留已经产出的改动，二次确认后执行。");
        pendingCard = TaskCard("待批准", pendingCount, pendingGrid,
            "这里刻意不给批准按钮：让人离开工位去处理，正是这个产品存在的理由。要拒绝请在手机上操作，或直接叫停整个任务。");
        doneCard = TaskCard("最近完成", null, doneGrid, null, ShowLogs());
        board.Controls.Add(runningCard);
        board.Controls.Add(new Panel { Height = 14, BackColor = DeckTheme.Canvas, Margin = Padding.Empty });
        board.Controls.Add(pendingCard);
        board.Controls.Add(new Panel { Height = 14, BackColor = DeckTheme.Canvas, Margin = Padding.Empty });
        board.Controls.Add(doneCard);
        board.SizeChanged += (_, _) => SizeBoard();
    }

    private Button ShowLogs()
    {
        var link = DeckTheme.Link("在日志里看全部", DeckTheme.Dim, DeckTheme.Ink);
        link.Click += (_, _) => SelectPage(3);
        return link;
    }

    private Control TaskCard(string title, DeckTheme.PillLabel? count, DataGridView grid, string? note, Control? trailing = null)
    {
        var body = DeckTheme.Table(new Padding(0), DeckTheme.Surface);
        var extras = trailing is null ? Array.Empty<Control>() : new[] { trailing };
        Control[]? chips = count is null ? null : [count];
        var header = DeckTheme.CardHeaderWith(title, null, chips, extras);
        DeckTheme.Row(body, header, 42);
        DeckTheme.Row(body, grid, 100, fill: true);
        if (note is not null)
        {
            var hint = DeckTheme.Label(note, DeckTheme.Tiny, DeckTheme.Dim);
            hint.Padding = new Padding(16, 0, 16, 0);
            hint.ForeColor = DeckTheme.Dim;
            DeckTheme.Row(body, hint, 30);
        }
        var card = new DeckTheme.CardPanel { Margin = new Padding(0) };
        card.Controls.Add(body);
        return card;
    }

    private async Task RefreshTaskBoardAsync()
    {
        if (boardBusy || IsDisposed) return;
        boardBusy = true;
        try
        {
            var output = await RunAsync("inbox", "-task-inbox", RequirePath(InboxPath, "任务收件箱"),
                "-projects", RequirePath(ProjectsPath, "项目授权清单"),
                "-task-logs", RequirePath(LogsPath, "任务日志目录"));
            var items = (JsonSerializer.Deserialize<QueueView>(output, Json)?.Items ?? [])
                .OrderByDescending(item => item.SubmittedAt)
                .ToArray();
            RenderBoard(items);
        }
        catch (Exception error)
        {
            status.Text = $"任务列表读不出来：{error.Message}";
        }
        finally { boardBusy = false; }
    }

    private void RenderBoard(QueueItem[] items)
    {
        var running = items.Where(item => item.Status == "running").ToArray();
        var queued = items.Where(item => item.Status == "queued").ToArray();
        var done = items.Where(item => item.Status is not ("queued" or "running"))
            .Take(8).ToArray();

        var active = running.FirstOrDefault();
        var signals = items.ToDictionary(item => item.TaskId, item => ReadSignal(item.TaskId));
        var now = DateTimeOffset.Now;

        runningGrid.Rows.Clear();
        boardItems = running.ToList();
        if (active is not null)
        {
            var signal = signals[active.TaskId];
            runningGrid.Rows.Add(active.ProjectId, Title(active),
                Elapsed(signal.Start, now), LastEvent(signal), "查看轨迹", "叫停");
        }
        int activeCount = running.Length + queued.Length;
        SetCount(runningCount, $"{activeCount} 个",
            activeCount > 0 ? DeckTheme.PillTone.Good : DeckTheme.PillTone.Neutral);

        // Only one task runs at a time, so at most one approval can be waiting;
        // the row still comes from the task journal rather than a second source.
        var pending = active is not null ? signals[active.TaskId].Pending : null;
        pendingGrid.Rows.Clear();
        if (active is not null && pending is not null)
        {
            pendingGrid.Rows.Add(active.ProjectId, pending.Text, "逐次批准 · 仅此目录",
                Elapsed(pending.At, now), WaitingFor());
        }
        SetCount(pendingCount, $"{pendingGrid.Rows.Count} 条",
            pendingGrid.Rows.Count > 0 ? DeckTheme.PillTone.Danger : DeckTheme.PillTone.Neutral);

        doneGrid.Rows.Clear();
        foreach (var item in done)
        {
            var signal = signals[item.TaskId];
            doneGrid.Rows.Add(item.ProjectId, Title(item), Elapsed(signal.Start, signal.End),
                signal.End is { } end ? end.ToLocalTime().ToString("HH:mm") : "—",
                StatusWord(item.Status));
        }

        // Rows are the reason the cards resized; without this the first card is
        // laid out for zero rows and the table is clipped after the first paint.
        ResizeCard(runningCard, runningGrid, running.Length);
        ResizeCard(pendingCard, pendingGrid, pendingGrid.Rows.Count);
        ResizeCard(doneCard, doneGrid, done.Length);
        SizeBoard();
    }

    private void SetCount(DeckTheme.PillLabel chip, string text, DeckTheme.PillTone tone)
    {
        chip.Text = text;
        chip.Tone = tone;
    }

    private static void ResizeCard(Control card, DataGridView grid, int rows)
    {
        int height = 42 + grid.ColumnHeadersHeight + rows * 33 + (rows == 0 ? 56 : 0) + 30;
        card.Height = height + 4;
    }

    private void SizeBoard()
    {
        int width = board.ClientSize.Width - (board.VerticalScroll.Visible ? 18 : 2);
        if (width <= 0) return;
        foreach (Control control in board.Controls)
        {
            if (control.Width != width) control.Width = width;
        }
    }

    private void OpenTrace(QueueItem item)
    {
        var path = Path.Combine(RequirePath(LogsPath, "任务日志目录"), item.TaskId + ".log");
        using var trace = new TaskTraceWindow(item.TaskId, Title(item), item.ProjectId,
            item.Directory, item.Status, path);
        trace.ShowDialog(this);
    }

    private async Task CancelAsync(QueueItem item)
    {
        if (MessageBox.Show(this, $"叫停这个任务？\r\n\r\n{Title(item)}\r\n\r\n已开始的命令不会回滚。",
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
        await RefreshTaskBoardAsync();
    }

    private string WaitingFor()
    {
        var phone = TrustedPhones().FirstOrDefault();
        return phone.Fingerprint is null ? "手机" : ShortFingerprint(phone.Fingerprint);
    }

    private static string Title(QueueItem item) =>
        string.IsNullOrWhiteSpace(item.Summary) ? item.Prompt : item.Summary;

    private static string Elapsed(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is null || to is null) return "—";
        var span = to.Value - from.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }

    private static string LastEvent(TaskSignal signal) => signal.End is { } at
        ? $"{at.ToLocalTime():HH:mm:ss} {EventWord(signal.LastType)}"
        : "尚无事件";

    private static string EventWord(string type) => type switch
    {
        "item/agentMessage/delta" => "消息",
        "item/commandExecution/outputDelta" => "命令输出",
        "approval/requested" => "审批",
        "approval/approved" => "已批准",
        "approval/declined" => "已拒绝",
        "turn/started" => "开始",
        "task/finished" => "结束",
        _ => type.Length > 0 ? type : "事件"
    };

    // The journal is the only local record of an approval. Its tail is enough:
    // events are written in order, and a request is pending only while the last
    // approval event is still "requested".
    private TaskSignal ReadSignal(string taskId)
    {
        var path = Path.Combine(RequirePath(LogsPath, "任务日志目录"), taskId + ".log");
        if (!File.Exists(path))
            return new TaskSignal(null, null, "", "", null);
        try
        {
            const int limit = 192 * 1024;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var start = Math.Max(0, file.Length - limit);
            file.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[Math.Min(limit, file.Length)];
            int read = file.ReadAtLeast(bytes, bytes.Length, false);
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            if (start > 0)
            {
                int offset = text.IndexOf('\n');
                if (offset >= 0) text = text[(offset + 1)..];
            }

            DateTimeOffset? first = null, last = null;
            string lastType = "", lastText = "";
            DateTimeOffset? pendingAt = null;
            string pendingText = "";
            bool pending = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    var at = root.TryGetProperty("at", out var a) &&
                             DateTimeOffset.TryParse(a.GetString(), out var when)
                        ? when : (DateTimeOffset?)null;
                    if (at is not null)
                    {
                        first ??= at;
                        last = at;
                    }
                    if (type.Length > 0)
                    {
                        lastType = type;
                        lastText = root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";
                    }
                    if (type is "approval/requested" or "approval/approved" or "approval/declined")
                    {
                        pending = type == "approval/requested";
                        pendingAt = at;
                        pendingText = lastText;
                    }
                }
                catch (JsonException) { }
            }
            // An expired request is no longer waiting: the Agent denies it after
            // five minutes, and the journal may not have the denial yet.
            if (pending && pendingAt is { } since && DateTimeOffset.Now - since < TimeSpan.FromMinutes(5))
                return new TaskSignal(first, last, lastType, lastText,
                    new PendingApproval(pendingText, since));
            return new TaskSignal(first, last, lastType, lastText, null);
        }
        catch (IOException)
        {
            return new TaskSignal(null, null, "", "", null);
        }
    }

    private sealed record TaskSignal(DateTimeOffset? Start, DateTimeOffset? End,
        string LastType, string LastText, PendingApproval? Pending);

    private sealed record PendingApproval(string Text, DateTimeOffset At);

    private static DataGridViewLinkColumn LinkColumn(string name, int width) => new()
    {
        HeaderText = "",
        Text = name,
        Width = width,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        LinkColor = DeckTheme.LinkColor,
        ActiveLinkColor = DeckTheme.Ink,
        VisitedLinkColor = DeckTheme.LinkColor,
        TrackVisitedState = false,
        LinkBehavior = LinkBehavior.NeverUnderline,
        DefaultCellStyle = new DataGridViewCellStyle
        {
            Font = DeckTheme.Small,
            Padding = new Padding(6, 0, 0, 0)
        }
    };

    private static string StatusWord(string status) => status switch
    {
        "queued" => "排队中",
        "running" => "执行中",
        "completed" => "完成",
        "failed" => "失败",
        "interrupted" => "已中断",
        "canceled" => "已取消",
        _ => status
    };
}
