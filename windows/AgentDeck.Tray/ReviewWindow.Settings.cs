using System.Diagnostics;
using System.Text.Json;

namespace AgentDeck.Tray;

// 设置 page: what this machine is allowed to touch, whether it starts with
// Windows, and which editors were found. There is no path to type: Codex is
// detected, the project policy is a list, and the only editable value is where
// task logs go.
internal sealed partial class ReviewWindow
{
    private readonly WhitelistView projectTable = new()
    {
        Dock = DockStyle.Fill, Margin = Padding.Empty
    };
    private readonly Label projectSummary = DeckTheme.Label("0 个目录", DeckTheme.MonoMicro, DeckTheme.Dim);
    private readonly DeckTheme.StatusDot serviceDot = DeckTheme.Dot(DeckTheme.Dim);
    private readonly Label serviceTitle = DeckTheme.Label("开机自动运行 · 未安装", DeckTheme.Body, DeckTheme.Muted);
    private readonly Label serviceDetail = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label serviceMode = DeckTheme.Label("托盘进程", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Button serviceAction = DeckTheme.Button("安装服务");
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
    private TableLayoutPanel? projectBody;
    private int projectRulesRow = -1;

    private Control SettingsPage()
    {
        var body = DeckTheme.Table(background: DeckTheme.Canvas);
        // Fixed rows plus scrolling: at the 900x660 minimum the log field would
        // otherwise be pushed out of the window, which is exactly the defect the
        // old five-page settings screen had.
        body.AutoScroll = true;
        DeckTheme.Row(body, ProjectCard(), 100, fill: true);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Canvas }, 12);
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.Controls.Add(ServiceCard(), 0, 0);
        bottom.Controls.Add(new Panel { BackColor = DeckTheme.Canvas }, 1, 0);
        bottom.Controls.Add(DetectionCard(), 2, 0);
        // 216, not 204: three 44px editor rows plus the header, the spacers and
        // the hint below them. At 204 the hint spilled out of the card and the
        // page grew a scrollbar for twelve pixels nobody could see.
        DeckTheme.Row(body, bottom, 216);        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Canvas }, 12);
        DeckTheme.Row(body, LogsCard(), 54);
        var page = Page("设置", "只在这几个目录里工作。其余路径一律拒绝。", body);
        page.VisibleChanged += (_, _) => { if (page.Visible) RefreshSettings(); };
        return page;
    }

    private Control ProjectCard()
    {
        projectSummary.BackColor = DeckTheme.Surface2;
        projectSummary.Padding = new Padding(7, 2, 7, 2);
        projectSummary.AutoSize = true;
        projectSummary.Dock = DockStyle.None;
        projectSummary.Anchor = AnchorStyles.Right;
        DeckTheme.Round(projectSummary, 9);
        var card = Card("项目白名单", out var body, note: "添加后默认停用", action: projectSummary);
        projectBody = body;
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 4);
        DeckTheme.Row(body, projectTable, 100, fill: true);

        // The allow/deny rules are onboarding text: they matter when the list is
        // empty and cost 59px the rest of the time. Keeping them in their own row
        // lets RefreshProjects collapse it, which is what gives the table enough
        // height to show three projects instead of slicing the second one.
        var rulesBlock = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 1, RowCount = 4
        };
        rulesBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        rulesBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        rulesBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        rulesBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        rulesBlock.Controls.Add(new Panel { BackColor = DeckTheme.Surface, Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
        rulesBlock.Controls.Add(DeckTheme.Divider(DeckTheme.Line), 0, 1);
        rulesBlock.Controls.Add(new Panel { BackColor = DeckTheme.Surface, Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 2);

        var rules = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 4, 0, 0)
        };
        rules.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        rules.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rules.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        rules.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        rules.Controls.Add(RuleKey("允许", DeckTheme.Good), 0, 0);
        rules.Controls.Add(RuleText("只在启用的目录内读写，子目录继承父目录权限"), 1, 0);
        rules.Controls.Add(RuleKey("拒绝", DeckTheme.Danger), 0, 1);
        rules.Controls.Add(RuleText("符号链接、目录联接、含 .. 的越界路径，以及未在清单中的任何路径"), 1, 1);
        rulesBlock.Controls.Add(rules, 0, 3);
        projectRulesRow = body.RowCount;
        DeckTheme.Row(body, rulesBlock, 59);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 6, 0, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var hint = DeckTheme.Label("改动对下一次任务生效；写入仍会逐条请求手机批准", DeckTheme.Tiny, DeckTheme.Dim);
        hint.AutoEllipsis = true;
        actions.Controls.Add(hint, 0, 0);
        var buttons = DeckTheme.Actions(DeckTheme.Surface);
        buttons.AutoSize = true;
        buttons.Dock = DockStyle.None;
        buttons.Anchor = AnchorStyles.Right;
        AddAction(buttons, "管理项目…", ManageProjectsAsync, primary: true);
        AddAction(buttons, "刷新", () =>
        {
            RefreshProjects();
            return Task.CompletedTask;
        });
        actions.Controls.Add(buttons, 1, 0);
        DeckTheme.Row(body, actions, 42);
        return card;
    }

    private static Control RuleKey(string text, Color color) =>
        DeckTheme.Label(text, DeckTheme.Small, color);

    private static Control RuleText(string text)
    {
        var label = DeckTheme.Label(text, DeckTheme.Small, DeckTheme.Dim);
        label.AutoEllipsis = true;
        return label;
    }

    private Control ServiceCard()
    {
        serviceAction.Margin = Padding.Empty;
        serviceAction.Click += async (_, _) => await RunActionAsync(ToggleServiceAsync);
        var card = Card("常驻与自启", out var body, action: serviceAction);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 6);
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        serviceDot.Dock = DockStyle.None;
        serviceDot.Anchor = AnchorStyles.Left;
        serviceDot.Margin = new Padding(0, 1, 0, 0);
        head.Controls.Add(serviceDot, 0, 0);
        serviceTitle.AutoEllipsis = true;
        head.Controls.Add(serviceTitle, 1, 0);
        DeckTheme.Row(body, head, 26);
        serviceDetail.AutoEllipsis = true;
        serviceDetail.ForeColor = DeckTheme.Muted;
        DeckTheme.Row(body, serviceDetail, 20);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 8);
        var note1 = DeckTheme.Label("服务模式在登录前就能连接手机；", DeckTheme.Small, DeckTheme.Dim);
        note1.AutoEllipsis = true;
        DeckTheme.Row(body, note1, 20);
        var note2 = DeckTheme.Label("托盘模式需要先登录 Windows。", DeckTheme.Small, DeckTheme.Dim);
        note2.AutoEllipsis = true;
        DeckTheme.Row(body, note2, 20);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 8);
        var facts = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 2
        };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        facts.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        facts.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        facts.Controls.Add(DeckTheme.Label("当前方式", DeckTheme.Small, DeckTheme.Dim), 0, 0);
        serviceMode.TextAlign = ContentAlignment.MiddleRight;
        facts.Controls.Add(serviceMode, 1, 0);
        facts.Controls.Add(DeckTheme.Label("服务名", DeckTheme.Small, DeckTheme.Dim), 0, 1);
        var serviceName = DeckTheme.Label("AgentDeckLab", DeckTheme.MonoSmall, DeckTheme.Muted);
        serviceName.TextAlign = ContentAlignment.MiddleRight;
        facts.Controls.Add(serviceName, 1, 1);
        DeckTheme.Row(body, facts, 44);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        return card;
    }

    private Control DetectionCard()
    {
        var redetect = DeckTheme.Button("重新检测");
        redetect.MinimumSize = new Size(0, 24);
        redetect.Padding = new Padding(9, 0, 9, 0);
        redetect.Font = DeckTheme.Tiny;
        redetect.Margin = Padding.Empty;
        redetect.Anchor = AnchorStyles.Right;
        redetect.Click += async (_, _) => await RunActionAsync(() => DetectToolsAsync(fill: true));
        var card = Card("编辑器检测", out var body, action: redetect);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 4);
        DeckTheme.Row(body, EditorRow(codexDot, "Codex", codexLine, codexPathLine, "只读 · 写 · 审批 · 续接"), 44);
        DeckTheme.Row(body, EditorRow(cursorDot, "Cursor", cursorLine, cursorPathLine, "未接入"), 44);
        DeckTheme.Row(body, EditorRow(buddyDot, "WorkBuddy", buddyLine, buddyPathLine, "未接入"), 44);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 4);
        var hint = DeckTheme.Label("路径由 AgentDeck 自动发现，不支持手动指定。", DeckTheme.Tiny, DeckTheme.Dim);
        DeckTheme.Row(body, hint, 20);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        return card;
    }

    private Control LogsCard()
    {
        var card = new DeckTheme.CardPanel();
        var line = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1, Padding = new Padding(16, 0, 14, 0)
        };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        line.Controls.Add(DeckTheme.Label("任务日志", DeckTheme.Small, DeckTheme.Dim), 0, 0);
        var field = DeckTheme.Input(logs, "任务日志目录");
        field.Margin = new Padding(0, 9, 10, 9);
        line.Controls.Add(field, 1, 0);
        var browse = DeckTheme.Button("浏览…");
        browse.MinimumSize = new Size(0, 28);
        browse.Padding = new Padding(10, 0, 10, 0);
        browse.Font = DeckTheme.Small;
        browse.Margin = Padding.Empty;
        browse.Anchor = AnchorStyles.None;
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "选择任务日志目录", SelectedPath = LogsPath };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            logs.Text = picker.SelectedPath;
            SavePaths();
        };
        line.Controls.Add(browse, 2, 0);
        card.Controls.Add(line);
        return card;
    }

    private static Control EditorRow(DeckTheme.StatusDot dot, string name, Label version, Label path,
        string capabilities)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 2
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
        dot.Dock = DockStyle.None;
        dot.Anchor = AnchorStyles.Left;
        dot.Margin = new Padding(0, 1, 0, 0);
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
        version.Font = DeckTheme.MonoMicro;
        head.Controls.Add(version, 1, 0);
        row.Controls.Add(head, 1, 0);
        path.AutoEllipsis = true;
        path.Font = DeckTheme.Small;
        row.Controls.Add(path, 1, 1);
        var caps = DeckTheme.Label(capabilities, DeckTheme.Tiny, DeckTheme.Dim);
        // Row 0 only, with AutoSize kept on so the AutoSize column can measure
        // the text: a Dock=Fill label reports the width it was handed, and the
        // capability list was clipped to "只读 · 写 · 审批 · 续".
        caps.AutoSize = true;
        caps.Dock = DockStyle.None;
        caps.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        caps.TextAlign = ContentAlignment.MiddleRight;
        caps.Margin = new Padding(12, 2, 0, 0);
        row.Controls.Add(caps, 2, 0);
        return row;
    }

    private void RefreshSettings()
    {
        if (logs.TextLength == 0) logs.Text = LogsPath;
        RefreshProjects();
        var service = ServiceControl.Query();
        serviceDot.Color = service.Running ? DeckTheme.Good : service.Installed ? DeckTheme.Warning : DeckTheme.Dim;
        serviceTitle.Text = service.Running ? "开机自动运行 · 服务运行中"
            : service.Installed ? "开机自动运行 · 已安装，未运行" : "开机自动运行 · 未安装";
        serviceTitle.ForeColor = service.Installed ? DeckTheme.Ink : DeckTheme.Muted;
        serviceDetail.Text = service.Running ? "由 Windows 服务托管 · 登录前即可连接"
            : service.Installed ? "服务已注册但没有运行，重启服务或重新安装" : "以托盘方式运行，登录后启动";
        serviceMode.Text = service.Installed ? "Windows 服务" : "托盘进程";
        serviceAction.Text = service.Installed ? "卸载服务" : "安装服务";
        serviceAction.Enabled = service.Installed || DeviceRegistered;
        if (codexLine.Text == "尚未检测") _ = DetectToolsAsync(fill: true);
    }

    // Installing a service needs an elevated process, so the tray only builds
    // the command and lets Windows raise the prompt. Every path the service
    // needs is passed explicitly: it has to start with the identity, pairing
    // folder and project policy the tray already has, not with defaults.
    private async Task ToggleServiceAsync()
    {
        if (ServiceControl.Query().Installed)
        {
            var confirm = MessageBox.Show(this,
                "卸载后开机不再自动运行 Agent，需要登录 Windows 后由托盘启动。\n\n确认卸载？",
                "卸载开机自启服务", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.OK) return;
            var code = await RunElevatedAsync("service-uninstall", "-name", ServiceControl.Name);
            if (ServiceControl.Query().Installed)
                throw new InvalidOperationException($"服务没有卸载成功（退出码 {code}）。");
            StopAgent();
            RecordEvent("已卸载开机自启服务");
            if (DeviceIdText() is { Length: > 0 } id) StartAgent(id);
            SetStatus("服务已卸载，Agent 改由托盘启动。");
            RefreshStatePage();
            return;
        }
        var device = DeviceIdText();
        if (device.Length == 0) throw new InvalidOperationException("请先配置云服务器，再安装开机自启服务。");
        var proceed = MessageBox.Show(this,
            "安装需要管理员权限，Windows 会弹出确认框。\n\n安装后 Agent 由服务启动，未登录也能连接手机。",
            "安装开机自启服务", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (proceed != DialogResult.OK) return;
        var arguments = new List<string>
        {
            "service-install", "-name", ServiceControl.Name, "-exe", ExecutablePath,
            "-config", AgentConfig, "-pairing-dir", PairingDirectory, "-device", device,
            "-log", Path.Combine(DeviceDirectory, "service.log"),
            "-task-logs", LogsPath, "-task-inbox", InboxPath, "-projects", ProjectsPath
        };
        if (codexPath.Trim().Length > 0) arguments.AddRange(["-codex", RequirePath(codexPath, "Codex 可执行文件")]);
        var exit = await RunElevatedAsync([.. arguments]);
        if (!ServiceControl.Query().Installed)
            throw new InvalidOperationException($"服务没有安装成功（退出码 {exit}）。如果 Windows 提示被取消，请重试。");
        // The service owns the relay connection now. A second agent on the same
        // device identity would take turns answering the phone.
        StopAgent();
        RecordEvent("已安装开机自启服务");
        SetStatus("服务已安装并启动，登录前也能连接。");
        RefreshStatePage();
    }

    // runas needs UseShellExecute, which means no output pipes, so the result is
    // the exit code plus a fresh look at the service manager.
    private async Task<int> RunElevatedAsync(params string[] args)
    {
        var start = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = true, Verb = "runas", CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("无法启动安装命令。");
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消管理员授权，服务没有改动。", error);
        }
        using (process)
        {
            await process.WaitForExitAsync(lifetime.Token);
            return process.ExitCode;
        }
    }

    private void SetProjectRows(List<(string Directory, string Id, bool Enabled, string Mode)> rows)
    {
        projectTable.SetRows(rows);
        if (projectBody is null || projectRulesRow < 0 || projectRulesRow >= projectBody.RowStyles.Count)
            return;
        // Rules are onboarding text: shown when the list is empty, collapsed as
        // soon as it has entries so the table can show three projects instead of
        // slicing the second one.
        projectBody.RowStyles[projectRulesRow] =
            new RowStyle(SizeType.Absolute, rows.Count == 0 ? 59 : 0);
        projectBody.PerformLayout();
    }

    private void RefreshProjects()
    {
        var path = ProjectsPath;
        if (!File.Exists(path))
        {
            SetProjectRows([]);
            projectSummary.Text = "0 个目录";
            projectSummary.ForeColor = DeckTheme.Dim;
            projectSummary.BackColor = DeckTheme.Surface2;
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("projects", out var projects) ||
                projects.ValueKind != JsonValueKind.Array)
            {
                SetProjectRows([]);
                projectSummary.Text = "清单格式不正确";
                projectSummary.ForeColor = DeckTheme.Danger;
                projectSummary.BackColor = DeckTheme.Tint(DeckTheme.Danger);
                return;
            }
            var rows = new List<(string Directory, string Id, bool Enabled, string Mode)>();
            foreach (var project in projects.EnumerateArray())
            {
                var id = project.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                var directory = project.TryGetProperty("directory", out var d) ? d.GetString() ?? "" : "";
                var on = project.TryGetProperty("enabled", out var e) && e.GetBoolean();
                var mode = project.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
                rows.Add((directory, id, on, mode));
            }
            SetProjectRows(rows);
            int enabled = rows.Count(row => row.Enabled);
            projectSummary.Text = $"{rows.Count} 个目录";
            projectSummary.ForeColor = enabled > 0 ? DeckTheme.Good : DeckTheme.Dim;
            projectSummary.BackColor = enabled > 0 ? DeckTheme.Tint(DeckTheme.Good) : DeckTheme.Surface2;
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            SetProjectRows([]);
            projectSummary.Text = "读取失败";
            projectSummary.ForeColor = DeckTheme.Danger;
            projectSummary.BackColor = DeckTheme.Tint(DeckTheme.Danger);
            status.Text = $"读取授权清单失败：{error.Message}";
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

    /// <summary>
    /// Fills the detection card from what is really installed. A "found but
    /// unusable" result is a warning, not a success — the stripped Codex copy
    /// starts fine and then fails every shell tool, which is worse than missing.
    /// </summary>
    private async Task DetectToolsAsync(bool fill, bool quiet = false)
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
                if (!usable && !quiet)
                    status.Text = $"Codex 不可用：{(issue.Length > 0 ? issue : "未安装")}";
            }
        }
        catch (Exception error)
        {
            if (!quiet) status.Text = $"检测失败。{error.Message}";
        }
    }

    // The whitelist is painted rather than a DataGridView: the grid's header
    // chrome and cell padding cannot be fully themed, and at this size the
    // default look reads as an editable text box rather than a table.
    private sealed class WhitelistView : ScrollableControl
    {
        private readonly List<(string Directory, string Id, bool Enabled, string Mode)> rows = [];

        internal WhitelistView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = DeckTheme.Surface;
            AutoScroll = true;
        }

        internal void SetRows(IEnumerable<(string Directory, string Id, bool Enabled, string Mode)> value)
        {
            rows.Clear();
            rows.AddRange(value);
            AutoScrollMinSize = new Size(0, 28 + rows.Count * 36);
            AutoScrollPosition = Point.Empty;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var background = new SolidBrush(DeckTheme.Surface)) g.FillRectangle(background, ClientRectangle);
            const int left = 5;
            int right = Width - 4 - (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            const int idWidth = 118;
            const int modeWidth = 56;
            const int onWidth = 60;
            int headerHeight = 28;

            if (rows.Count == 0)
            {
                // No header row for an empty list: column titles over nothing are
                // noise, and dropping them is what lets both onboarding lines fit
                // in the space the card can spare.
                TextRenderer.DrawText(g, "还没有授权清单", DeckTheme.Small,
                    new Point(left, 4), DeckTheme.Muted, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "点「管理项目…」添加第一个目录", DeckTheme.Small,
                    new Point(left, 26), DeckTheme.Dim, TextFormatFlags.NoPadding);
                return;
            }

            using (var line = new Pen(DeckTheme.Line2))
                g.DrawLine(line, 0, headerHeight - 1, Width, headerHeight - 1);
            int idLeft = right - idWidth - modeWidth - onWidth;
            TextRenderer.DrawText(g, "本机目录", DeckTheme.Tiny, new Point(left, 8), DeckTheme.Dim,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "项目 ID", DeckTheme.Tiny, new Point(idLeft, 8),
                DeckTheme.Dim, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "权限", DeckTheme.Tiny, new Point(idLeft + idWidth, 8),
                DeckTheme.Dim, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "启用", DeckTheme.Tiny, new Point(right - onWidth + 6, 8),
                DeckTheme.Dim, TextFormatFlags.NoPadding);

            var state = g.Save();
            g.SetClip(new Rectangle(0, headerHeight, Width, Height - headerHeight));
            int y = headerHeight + AutoScrollPosition.Y;
            foreach (var (directory, id, enabled, mode) in rows)
            {
                if (y > Height) break;
                if (y + 36 < headerHeight)
                {
                    y += 36;
                    continue;
                }
                int cellY = y + 11;
                int directoryWidth = Math.Max(40, Width - idWidth - modeWidth - onWidth - left - 12);
                TextRenderer.DrawText(g, directory, DeckTheme.MonoSmall,
                    new Rectangle(left, cellY, directoryWidth, 18), DeckTheme.Muted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, id, DeckTheme.MonoMicro,
                    new Rectangle(idLeft, cellY + 1, idWidth, 18), DeckTheme.Dim,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, mode == "workspace-write" ? "可写" : "只读", DeckTheme.Small,
                    new Rectangle(idLeft + idWidth, cellY, modeWidth, 18),
                    mode == "workspace-write" ? DeckTheme.Warning : DeckTheme.Dim, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, enabled ? "启用" : "停用", DeckTheme.Small,
                    new Rectangle(right - onWidth + 6, cellY, onWidth - 8, 18),
                    enabled ? DeckTheme.Good : DeckTheme.Dim, TextFormatFlags.NoPadding);
                using (var line = new Pen(DeckTheme.Line2))
                    g.DrawLine(line, 0, y + 35, Width, y + 35);
                y += 36;
            }
            g.Restore(state);
        }
    }
}
