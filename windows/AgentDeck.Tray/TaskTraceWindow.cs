using System.Text;
using System.Text.Json;

namespace AgentDeck.Tray;

// One task's journal in its own window. The desktop only reads the file the
// Agent already writes, so opening this cannot disturb a running task, and the
// tail is re-read on a timer instead of tailing an open handle.
internal sealed class TaskTraceWindow : Form
{
    private readonly TraceView output = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label follow = DeckTheme.Label("跟随中", DeckTheme.Tiny, DeckTheme.Good);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1500 };
    private readonly string path;
    private long length = -1;
    private bool paused;

    internal TaskTraceWindow(string taskId, string summary, string project, string directory,
        string status, string logPath)
    {
        path = logPath;
        DeckTheme.Form(this, "任务轨迹");
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var body = DeckTheme.Table(new Padding(20, 16, 20, 16), DeckTheme.Canvas);
        var head = DeckTheme.Table(background: DeckTheme.Canvas);
        var title = DeckTheme.Label(summary, DeckTheme.Hero, DeckTheme.Ink);
        title.AutoEllipsis = true;
        DeckTheme.Row(head, title, 28);
        var meta = DeckTheme.Label(
            $"{project}   ·   {directory}   ·   {Short(taskId)}   ·   {Word(status)}",
            DeckTheme.MonoSmall, DeckTheme.Dim);
        meta.AutoEllipsis = true;
        DeckTheme.Row(head, meta, 20);
        DeckTheme.Row(body, head, 50);

        var frame = new Panel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12, 10, 12, 10),
            BackColor = DeckTheme.Well
        };
        frame.Controls.Add(output);
        DeckTheme.Round(frame, 6);
        DeckTheme.Row(body, frame, 100, fill: true);

        var foot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 6, 0, 0)
        };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var note = DeckTheme.Label("日志来自任务收件箱旁边的 -task-logs 目录，只追加不改写。",
            DeckTheme.Tiny, DeckTheme.Dim);
        note.AutoEllipsis = true;
        foot.Controls.Add(note, 0, 0);
        follow.Margin = new Padding(12, 0, 12, 0);
        foot.Controls.Add(follow, 1, 0);
        var toggle = DeckTheme.Button("暂停");
        toggle.MinimumSize = new Size(0, 24);
        toggle.Padding = new Padding(9, 0, 9, 0);
        toggle.Font = DeckTheme.Tiny;
        toggle.Margin = Padding.Empty;
        toggle.Click += (_, _) =>
        {
            paused = !paused;
            toggle.Text = paused ? "继续" : "暂停";
            follow.Text = paused ? "已暂停" : "跟随中";
            follow.ForeColor = paused ? DeckTheme.Warning : DeckTheme.Good;
            if (!paused) Reload(force: true);
        };
        foot.Controls.Add(toggle, 2, 0);
        DeckTheme.Row(body, foot, 30);
        Controls.Add(body);

        Reload(force: true);
        timer.Tick += (_, _) => Reload(force: false);
        Shown += (_, _) => timer.Start();
        FormClosed += (_, _) => timer.Dispose();
    }

    private void Reload(bool force)
    {
        if (IsDisposed) return;
        long size;
        try { size = File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { size = 0; }
        if (!force && (paused || size == length)) return;
        length = size;
        output.Clear();
        if (!File.Exists(path))
        {
            output.Append("", "", "任务还没有开始，或日志尚未写入。", DeckTheme.Dim);
            return;
        }
        string text;
        try
        {
            const int limit = 256 * 1024;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var start = Math.Max(0, file.Length - limit);
            file.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[Math.Min(limit, file.Length)];
            int read = file.ReadAtLeast(bytes, bytes.Length, false);
            text = Encoding.UTF8.GetString(bytes, 0, read);
            if (start > 0)
            {
                int offset = text.IndexOf('\n');
                if (offset >= 0) text = text[(offset + 1)..];
                output.Append("", "", "仅显示日志末尾 256 KB。", DeckTheme.Dim);
            }
        }
        catch (IOException error)
        {
            output.Append("", "", $"无法读取日志：{error.Message}", DeckTheme.Danger);
            return;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            Render(line);
        }
        output.Follow();
    }

    private void Render(string line)
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
            output.Append("", "", line, DeckTheme.Dim);
            return;
        }
        var time = DateTimeOffset.TryParse(at, out var moment)
            ? moment.ToLocalTime().ToString("HH:mm:ss") : "        ";
        var (label, color) = type switch
        {
            "approval/requested" => ("审批 · ", DeckTheme.Warning),
            "approval/declined" => ("拒绝 · ", DeckTheme.Danger),
            "approval/approved" => ("允许 · ", DeckTheme.Good),
            "task/notice" => ("提示 · ", DeckTheme.Muted),
            "task/finished" => ("结束 · ", state == "completed" ? DeckTheme.Good : DeckTheme.Danger),
            _ => ("", DeckTheme.Dim)
        };
        var detail = text.Length > 0 ? text : type;
        if (type == "task/finished" && state.Length > 0) detail = $"{Word(state)} {text}".Trim();
        output.Append(time, label, detail, color);
    }

    private static string Word(string status) => status switch
    {
        "queued" => "排队中",
        "running" => "执行中",
        "completed" => "已完成",
        "failed" => "失败",
        "interrupted" => "已中断",
        "canceled" => "已叫停",
        _ => status
    };

    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}
