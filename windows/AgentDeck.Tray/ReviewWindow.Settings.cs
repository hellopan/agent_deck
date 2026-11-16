using System.Text.Json;
using Microsoft.Win32;

namespace AgentDeck.Tray;

// 设置 page: what this machine is allowed to touch, whether it starts with
// Windows, and which editors were found. There is no path to type: Codex is
// detected, the project policy is a list, and the only editable value is where
// task logs go.
internal sealed partial class ReviewWindow
{
    private readonly DataGridView projectGrid = new()
    {
        Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeRows = false, ReadOnly = true,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false, AccessibleName = "项目白名单",
        BackgroundColor = DeckTheme.Surface, GridColor = DeckTheme.Line2,
        BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
        ColumnHeadersHeight = 28, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        Margin = Padding.Empty
    };
    private readonly Label projectSummary = DeckTheme.Label("尚未加载授权清单。", DeckTheme.Small, DeckTheme.Dim);
    private readonly DeckTheme.StatusDot serviceDot = DeckTheme.Dot(DeckTheme.Dim);
    private readonly Label serviceTitle = DeckTheme.Label("开机自动运行", DeckTheme.Body, DeckTheme.Ink);
    private readonly Label serviceDetail = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly DeckTheme.StatusDot codexDot = DeckTheme.Dot(DeckTheme.Dim);
    private readonly DeckTheme.StatusDot cursorDot = DeckTheme.Dot(DeckTheme.Dim);
    private readonly DeckTheme.StatusDot buddyDot = DeckTheme.Dot(DeckTheme.Dim);
    private readonly Label codexLine = DeckTheme.Label("尚未检测", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label cursorLine = DeckTheme.Label("尚未检测", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label buddyLine = DeckTheme.Label("尚未检测", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label codexPathLine = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label cursorPathLine = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label buddyPathLine = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly TextBox logs = new();
    private string codexVersion = "";

    private Control SettingsPage()
    {
        var body = DeckTheme.Table(background: DeckTheme.Canvas);
        // Fixed rows plus scrolling: at the 900x660 minimum the log field would
        // otherwise be pushed out of the window, which is exactly the defect the
        // old five-page settings screen had.
        body.AutoScroll = true;
        DeckTheme.Row(body, ProjectCard(), 240);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Canvas }, 12);
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.Controls.Add(ServiceCard(), 0, 0);
        bottom.Controls.Add(new Panel { BackColor = DeckTheme.Canvas }, 1, 0);
        bottom.Controls.Add(DetectionCard(), 2, 0);
        DeckTheme.Row(body, bottom, 222);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Canvas }, 10);
        DeckTheme.Row(body, LogsRow(), 40);
        var page = Page("设置", "只在这几个目录里工作。其余路径一律拒绝。", body);
        page.VisibleChanged += (_, _) => { if (page.Visible) RefreshSettings(); };
        return page;
    }

    private Control ProjectCard()
    {
        var card = Card("项目白名单", out var body, note: "添加后默认停用");
        projectGrid.DefaultCellStyle.BackColor = DeckTheme.Surface;
        projectGrid.DefaultCellStyle.ForeColor = DeckTheme.Ink;
        projectGrid.DefaultCellStyle.SelectionBackColor = DeckTheme.Surface2;
        projectGrid.DefaultCellStyle.SelectionForeColor = DeckTheme.Ink;
        projectGrid.DefaultCellStyle.Font = DeckTheme.Small;
        projectGrid.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        projectGrid.ColumnHeadersDefaultCellStyle.BackColor = DeckTheme.Surface;
        projectGrid.ColumnHeadersDefaultCellStyle.ForeColor = DeckTheme.Dim;
        projectGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = DeckTheme.Surface;
        projectGrid.ColumnHeadersDefaultCellStyle.Font = DeckTheme.Tiny;
        projectGrid.RowTemplate.Height = 30;
        projectGrid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "本机目录", Name = "dir", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        projectGrid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "项目 ID", Name = "id", Width = 130 });
        projectGrid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "启用", Name = "enabled", Width = 58 });
        DeckTheme.Row(body, projectSummary, 24);
        DeckTheme.Row(body, projectGrid, 100, fill: true);

        var rules = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 2
        };
        rules.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        rules.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rules.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        rules.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        rules.Controls.Add(DeckTheme.Label("允许", DeckTheme.Small, DeckTheme.Good), 0, 0);
        rules.Controls.Add(DeckTheme.Label("只在启用的目录内读写，子目录继承父目录权限",
            DeckTheme.Small, DeckTheme.Dim), 1, 0);
        rules.Controls.Add(DeckTheme.Label("拒绝", DeckTheme.Small, DeckTheme.Danger), 0, 1);
        rules.Controls.Add(DeckTheme.Label("符号链接、目录联接，以及含 .. 的越界路径",
            DeckTheme.Small, DeckTheme.Dim), 1, 1);
        DeckTheme.Row(body, rules, 40);

        var actions = DeckTheme.Actions(DeckTheme.Surface);
        AddAction(actions, "管理项目…", ManageProjectsAsync, primary: true);
        AddAction(actions, "刷新", () =>
        {
            RefreshProjects();
            return Task.CompletedTask;
        });
        DeckTheme.Row(body, actions, 38);
        return card;
    }

    private Control ServiceCard()
    {
        var card = Card("常驻与自启", out var body);
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        serviceDot.Dock = DockStyle.None;
        serviceDot.Anchor = AnchorStyles.Left;
        serviceDot.Margin = new Padding(0, 0, 6, 0);
        head.Controls.Add(serviceDot, 0, 0);
        serviceTitle.AutoEllipsis = true;
        head.Controls.Add(serviceTitle, 1, 0);
        DeckTheme.Row(body, head, 26);
        serviceDetail.AutoEllipsis = true;
        serviceDetail.ForeColor = DeckTheme.Muted;
        DeckTheme.Row(body, serviceDetail, 20);
        DeckTheme.Row(body, DeckTheme.Label("以 Windows 服务方式启动时，登录前就能连接手机。",
            DeckTheme.Small, DeckTheme.Dim), 20);
        DeckTheme.Row(body, DeckTheme.Label("当前由托盘进程托管；服务模式安装后可在登录前连接。",
            DeckTheme.Small, DeckTheme.Dim), 20);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        return card;
    }

    private Control DetectionCard()
    {
        var redetect = DeckTheme.Button("重新检测");
        redetect.MinimumSize = new Size(0, 24);
        redetect.Padding = new Padding(8, 0, 8, 0);
        redetect.Font = DeckTheme.Tiny;
        redetect.Margin = Padding.Empty;
        redetect.Click += async (_, _) => await RunActionAsync(() => DetectToolsAsync(fill: true));
        var card = Card("编辑器检测", out var body, action: redetect);
        DeckTheme.Row(body, EditorRow(codexDot, "Codex", codexLine, codexPathLine, "只读 · 写 · 审批 · 续接"), 50);
        DeckTheme.Row(body, EditorRow(cursorDot, "Cursor", cursorLine, cursorPathLine, "未接入"), 50);
        DeckTheme.Row(body, EditorRow(buddyDot, "WorkBuddy", buddyLine, buddyPathLine, "未接入"), 50);
        var hint = DeckTheme.Label("路径由 AgentDeck 自动发现，不支持手动指定。", DeckTheme.Small, DeckTheme.Dim);
        DeckTheme.Row(body, hint, 18);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        return card;
    }

    private Control LogsRow()
    {
        var line = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        line.Controls.Add(DeckTheme.Label("任务日志目录", DeckTheme.Small, DeckTheme.Dim), 0, 0);
        line.Controls.Add(DeckTheme.Input(logs, "任务日志目录"), 1, 0);
        var browse = DeckTheme.Button("浏览…");
        browse.Margin = new Padding(8, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "选择任务日志目录", SelectedPath = LogsPath };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            logs.Text = picker.SelectedPath;
            SavePaths();
        };
        line.Controls.Add(browse, 2, 0);
        return line;
    }

    private static Control EditorRow(DeckTheme.StatusDot dot, string name, Label version, Label path,
        string capabilities)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 2
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        dot.Dock = DockStyle.None;
        dot.Anchor = AnchorStyles.Left;
        dot.Margin = new Padding(0, 2, 6, 0);
        row.SetRowSpan(dot, 2);
        row.Controls.Add(dot, 0, 0);
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = DeckTheme.Label(name, DeckTheme.Body, DeckTheme.Ink);
        title.AutoSize = true;
        title.Dock = DockStyle.None;
        title.Margin = new Padding(0, 0, 8, 0);
        head.Controls.Add(title, 0, 0);
        version.TextAlign = ContentAlignment.MiddleRight;
        head.Controls.Add(version, 1, 0);
        row.Controls.Add(head, 1, 0);
        path.AutoEllipsis = true;
        row.Controls.Add(path, 1, 1);
        var caps = DeckTheme.Label(capabilities, DeckTheme.Tiny, DeckTheme.Dim);
        caps.AutoSize = true;
        caps.Dock = DockStyle.None;
        caps.Anchor = AnchorStyles.Right;
        caps.TextAlign = ContentAlignment.MiddleRight;
        caps.Margin = new Padding(8, 0, 0, 0);
        row.SetRowSpan(caps, 2);
        row.Controls.Add(caps, 2, 0);
        return row;
    }

    private void RefreshSettings()
    {
        if (logs.TextLength == 0) logs.Text = LogsPath;
        RefreshProjects();
        var service = ServiceStatus();
        serviceDot.Color = service is null ? DeckTheme.Dim : DeckTheme.Good;
        serviceTitle.Text = service is null ? "开机自动运行 · 未安装" : "开机自动运行 · 已安装";
        serviceTitle.ForeColor = service is null ? DeckTheme.Muted : DeckTheme.Ink;
        serviceDetail.Text = service ?? "以托盘方式运行，登录后启动";
        if (codexLine.Text == "尚未检测") _ = DetectToolsAsync(fill: true);
    }

    private void RefreshProjects()
    {
        projectGrid.Rows.Clear();
        var path = ProjectsPath;
        if (!File.Exists(path))
        {
            projectSummary.Text = "还没有授权清单。点「管理项目…」添加第一个目录。";
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("projects", out var projects) ||
                projects.ValueKind != JsonValueKind.Array)
            {
                projectSummary.Text = "授权清单格式不正确。";
                return;
            }
            int enabled = 0;
            foreach (var project in projects.EnumerateArray())
            {
                var id = project.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                var directory = project.TryGetProperty("directory", out var d) ? d.GetString() ?? "" : "";
                var on = project.TryGetProperty("enabled", out var e) && e.GetBoolean();
                if (on) enabled++;
                int index = projectGrid.Rows.Add(directory, id, on ? "启用" : "停用");
                projectGrid.Rows[index].Cells[2].Style.ForeColor = on ? DeckTheme.Good : DeckTheme.Dim;
                projectGrid.Rows[index].Cells[1].Style.ForeColor = DeckTheme.Muted;
            }
            projectSummary.Text = $"{projectGrid.Rows.Count} 个目录 · {enabled} 个启用";
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            projectSummary.Text = $"读取授权清单失败：{error.Message}";
        }
    }

    private async Task ManageProjectsAsync()
    {
        using var editor = new ProjectEditor(ProjectsPath);
        if (editor.ShowDialog(this) == DialogResult.OK) status.Text = "项目授权已保存。";
        RefreshProjects();
        await Task.CompletedTask;
    }

    // Three trailing segments identify a build without overflowing the row: the
    // Codex path alone is longer than the card is wide.
    private static string CompactPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? path : "…\\" + string.Join('\\', parts[^3..]);
    }

    private static string? ServiceStatus()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\AgentDeckLab");
            if (key is null) return null;
            return key.GetValue("Start") is int start && start == 2 ? "已注册为服务 · 开机自启" : "已注册为服务 · 手动启动";
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fills the detection card from what is really installed. A "found but
    /// unusable" result is a warning, not a success — the stripped Codex copy
    /// starts fine and then fails every shell tool, which is worse than missing.
    /// </summary>
    private async Task DetectToolsAsync(bool fill)
    {
        try
        {
            using var data = JsonDocument.Parse(await RunAsync("detect"));
            foreach (var tool in data.RootElement.EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString() ?? "";
                var path = tool.TryGetProperty("path", out var rawPath) ? rawPath.GetString() ?? "" : "";
                var version = tool.TryGetProperty("version", out var rawVersion) ? rawVersion.GetString() ?? "" : "";
                var usable = tool.GetProperty("usable").GetBoolean();
                var issue = tool.TryGetProperty("problem", out var rawProblem) ? rawProblem.GetString() ?? "" : "";
                var (dot, line, pathLine) = name switch
                {
                    "codex" => (codexDot, codexLine, codexPathLine),
                    "cursor" => (cursorDot, cursorLine, cursorPathLine),
                    _ => (buddyDot, buddyLine, buddyPathLine)
                };
                dot.Color = usable ? DeckTheme.Good : path.Length > 0 ? DeckTheme.Warning : DeckTheme.Dim;
                line.ForeColor = usable ? DeckTheme.Muted : path.Length > 0 ? DeckTheme.Warning : DeckTheme.Dim;
                pathLine.ForeColor = DeckTheme.Dim;
                if (usable)
                {
                    line.Text = version.Length > 0 ? version : "已安装";
                    pathLine.Text = CompactPath(path);
                }
                else if (path.Length > 0)
                {
                    line.Text = "不可用";
                    pathLine.Text = issue;
                }
                else
                {
                    line.Text = "未安装";
                    pathLine.Text = "";
                }
                if (name != "codex") continue;
                codexVersion = usable ? version : "";
                statusCodex.Text = usable && version.Length > 0 ? $"CODEX {version}" : "CODEX —";
                if (usable && fill && codexPath.Length == 0) codexPath = path;
                if (!usable)
                    status.Text = $"Codex 不可用：{(issue.Length > 0 ? issue : "未安装")}";
            }
        }
        catch (Exception error) { status.Text = $"检测失败。{error.Message}"; }
    }
}
