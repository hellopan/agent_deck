using System.Diagnostics;
using System.Text.Json;

namespace AgentDeck.Tray;

// 设置 page: four tabs over the same cards the tray already owns — 接入 (relay and
// the Windows service), 引擎 (what is installed), 配对 (the phones that may talk
// to this machine) and 诊断 (where the logs are). The project whitelist is not
// here any more: it has its own page, and two editors for one file is how the
// screen and the disk start disagreeing.
internal sealed partial class ReviewWindow
{
    private readonly Panel settingsHost = new()
    {
        Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty
    };
    private readonly List<Control> settingsPanes = [];
    private SettingsTabsView settingsTabs = null!;
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
    private readonly Label settingsRelay = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label settingsDevice = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label settingsProfile = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label diagnosticsEvents = DeckTheme.Label("没有记录", DeckTheme.Small, DeckTheme.Muted);
    private readonly TextBox logs = new();
    private string codexVersion = "";

    private Control SettingsPage()
    {
        var root = DeckTheme.Table(background: DeckTheme.Canvas);
        settingsTabs = new SettingsTabsView();
        settingsTabs.Selected += OpenSettingsTab;
        DeckTheme.Row(root, settingsTabs, 46);
        DeckTheme.Row(root, new Panel { BackColor = DeckTheme.Canvas }, 14);

        settingsPanes.Add(AccessPane());
        settingsPanes.Add(DetectionPane());
        settingsPanes.Add(PairedCard());
        settingsPanes.Add(DiagnosticsPane());
        foreach (var pane in settingsPanes)
        {
            pane.Visible = false;
            settingsHost.Controls.Add(pane);
        }
        DeckTheme.Row(root, settingsHost, 100, fill: true);

        var page = Page("设置", "接入、引擎、配对与诊断。关闭窗口不影响后台服务，托盘仍在跑。", root);
        page.VisibleChanged += (_, _) => { if (page.Visible) RefreshSettings(); };
        OpenSettingsTab(0);
        return page;
    }

    internal void OpenSettingsTab(int index)
    {
        if (index < 0 || index >= settingsPanes.Count) return;
        for (int i = 0; i < settingsPanes.Count; i++) settingsPanes[i].Visible = i == index;
        settingsTabs.SelectedIndex = index;
        if (index == 0)
        {
            settingsRelay.Text = RelayEndpoint() is { Length: > 0 } relay ? relay : "尚未配置";
            settingsDevice.Text = DeviceIdText() is { Length: > 0 } id ? DeviceShort(id) : "未注册";
            settingsProfile.Text = AgentConfig;
        }
        if (index == 3) RefreshDiagnostics();
        RefreshSettings();
    }

    private Control AccessPane()
    {
        var pane = DeckTheme.Table(background: DeckTheme.Canvas);
        // A card that is stretched to the pane's height leaves a hand-sized empty
        // block under its last row; the mock sizes every card to its content.
        DeckTheme.Row(pane, ServiceCard(), 212);
        DeckTheme.Row(pane, new Panel { BackColor = DeckTheme.Canvas }, 14);
        DeckTheme.Row(pane, AccessFactsCard(), 150);
        DeckTheme.Row(pane, new Panel { BackColor = DeckTheme.Canvas }, 100, fill: true);
        return pane;
    }

    private Control AccessFactsCard()
    {
        var card = Card("本机身份", out var body, note: "手机用这些值认出这台电脑");
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 2);
        DeckTheme.Row(body, FactRow("Relay 地址", settingsRelay), 34);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        DeckTheme.Row(body, FactRow("设备 ID", settingsDevice), 34);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        DeckTheme.Row(body, FactRow("配置文件", settingsProfile), 34);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        return card;
    }

    private static Control FactRow(string caption, Label value)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.Controls.Add(DeckTheme.Label(caption, DeckTheme.Small, DeckTheme.Dim), 0, 0);
        value.AutoEllipsis = true;
        row.Controls.Add(value, 1, 0);
        return row;
    }

    private Control DiagnosticsPane()
    {
        var pane = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(pane, LogsCard(), 54);
        DeckTheme.Row(pane, new Panel { BackColor = DeckTheme.Canvas }, 14);
        var card = Card("运行记录", out var body, note: "只在本机");
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 2);
        DeckTheme.Row(body, FactRow("版本", DeckTheme.Label(AppVersion, DeckTheme.MonoSmall, DeckTheme.Muted)), 34);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        DeckTheme.Row(body, FactRow("日志目录", DeckTheme.Label(LogsPath, DeckTheme.MonoSmall, DeckTheme.Muted)), 34);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        var events = FactRow("最近事件", diagnosticsEvents);
        DeckTheme.Row(body, events, 34);
        var hint = DeckTheme.Label("诊断信息只包含连接、版本与错误摘要，不包含任务正文与凭据。",
            DeckTheme.Tiny, DeckTheme.Dim);
        DeckTheme.Row(body, hint, 30);
        // The spacer goes last: with a fill row before the hint, the hint was
        // pushed to the bottom of the card, a hundred pixels below the rows it
        // belongs to.
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        DeckTheme.Row(pane, card, 214);
        DeckTheme.Row(pane, new Panel { BackColor = DeckTheme.Canvas }, 100, fill: true);
        return pane;
    }

    private void RefreshDiagnostics()
    {
        diagnosticsEvents.Text = connectionEvents.FirstOrDefault() ?? "没有记录";
    }

    private static string AppVersion =>
        typeof(ReviewWindow).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.4.0";

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
        serviceDetail.ForeColor = DeckTheme.Dim;
        DeckTheme.Row(body, serviceDetail, 20);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 8);
        // These two sentences are a footnote, not a second paragraph: the design
        // sets them at 11.5px in t3, which is what keeps the card readable.
        var note1 = DeckTheme.Label("服务模式在登录前就能连接手机；托盘模式需要先登录 Windows。",
            DeckTheme.Tiny, DeckTheme.Dim);
        note1.AutoEllipsis = true;
        DeckTheme.Row(body, note1, 20);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 10);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        // Label over value, two columns: the mock's .kv. Right-aligning a value
        // against the card edge left it stranded a whole card-width from its key.
        var facts = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        facts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var serviceName = DeckTheme.Label("AgentDeckLab", DeckTheme.MonoSmall, DeckTheme.Muted);
        facts.Controls.Add(KvBlock("当前方式", serviceMode), 0, 0);
        facts.Controls.Add(KvBlock("服务名", serviceName), 1, 0);
        DeckTheme.Row(body, facts, 58);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        return card;
    }

    private static Control KvBlock(string caption, Label value)
    {
        var table = DeckTheme.Table(new Padding(0, 4, 12, 2), DeckTheme.Surface);
        DeckTheme.Row(table, DeckTheme.Caption(caption), 18);
        value.AutoEllipsis = true;
        value.Font = DeckTheme.Small;
        value.ForeColor = DeckTheme.Muted;
        DeckTheme.Row(table, value, 20);
        return table;
    }

    private Control DetectionPane()
    {
        var pane = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(pane, DetectionCard(), 236);
        DeckTheme.Row(pane, new Panel { BackColor = DeckTheme.Canvas }, 100, fill: true);
        return pane;
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
        var card = Card("引擎", out var body, note: "只探测本机安装与登录状态，不上传路径", action: redetect);
        DeckTheme.Row(body, EngineHead(), 26);
        DeckTheme.Row(body, EditorRow(codexDot, "Codex", codexLine, codexPathLine, "只读 · 写 · 审批 · 续接"), 40);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        DeckTheme.Row(body, EditorRow(cursorDot, "Cursor", cursorLine, cursorPathLine, "未接入"), 40);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        DeckTheme.Row(body, EditorRow(buddyDot, "WorkBuddy", buddyLine, buddyPathLine, "未接入"), 40);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 10);
        var hint = DeckTheme.Label("未就绪的引擎不会出现在手机端的工具列表里。", DeckTheme.Tiny, DeckTheme.Dim);
        DeckTheme.Row(body, hint, 20);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        return card;
    }

    // The engine list is a table in the mock: 引擎 · 版本 · 路径 · 能力 on one line.
    // Stacking name over path inside a 44px row left the three engines looking like
    // one paragraph, and put the version against the capability text.
    private static Control EngineHead()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 5, RowCount = 1
        };
        foreach (var width in new[] { 14, 92, 140 })
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // Column 0 is the status dot's slot. It stays empty here, but it has to be
        // there: a TableLayoutPanel drops an unoccupied leading column, which moved
        // every heading 14px left of the value it labels.
        row.Controls.Add(new Panel { BackColor = DeckTheme.Surface, Margin = Padding.Empty }, 0, 0);
        row.Controls.Add(DeckTheme.Label("引擎", DeckTheme.Tiny, DeckTheme.Dim), 1, 0);
        row.Controls.Add(DeckTheme.Label("版本", DeckTheme.Tiny, DeckTheme.Dim), 2, 0);
        row.Controls.Add(DeckTheme.Label("路径", DeckTheme.Tiny, DeckTheme.Dim), 3, 0);
        var caps = DeckTheme.Label("能力", DeckTheme.Tiny, DeckTheme.Dim);
        caps.TextAlign = ContentAlignment.MiddleRight;
        row.Controls.Add(caps, 4, 0);
        return row;
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
            ColumnCount = 5, RowCount = 1
        };
        foreach (var width in new[] { 14, 92, 140 })
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        dot.Dock = DockStyle.None;
        dot.Anchor = AnchorStyles.Left;
        dot.Margin = Padding.Empty;
        row.Controls.Add(dot, 0, 0);

        var title = DeckTheme.Label(name, DeckTheme.Body, DeckTheme.Ink);
        title.AutoEllipsis = true;
        row.Controls.Add(title, 1, 0);
        version.TextAlign = ContentAlignment.MiddleLeft;
        version.Font = DeckTheme.MonoMicro;
        version.AutoEllipsis = true;
        row.Controls.Add(version, 2, 0);
        path.AutoEllipsis = true;
        path.Font = DeckTheme.Small;
        row.Controls.Add(path, 3, 0);

        // AutoSize kept on so the AutoSize column can measure the text: a
        // Dock=Fill label reports the width it was handed, and the capability
        // list was clipped to "只读 · 写 · 审批 · 续".
        var caps = DeckTheme.Label(capabilities, DeckTheme.Tiny, DeckTheme.Dim);
        caps.AutoSize = true;
        caps.Dock = DockStyle.None;
        caps.Anchor = AnchorStyles.Right;
        caps.TextAlign = ContentAlignment.MiddleRight;
        caps.Margin = Padding.Empty;
        row.Controls.Add(caps, 4, 0);
        return row;
    }

    private void RefreshSettings()
    {
        if (logs.TextLength == 0) logs.Text = LogsPath;
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

    // A painted tab strip: the design's tabs are text on a hairline with a 2px
    // underline under the active one. Four flat buttons could not draw that
    // underline without leaving every tab looking selected.
    private sealed class SettingsTabsView : Control
    {
        private static readonly string[] Labels = ["接入", "引擎", "配对", "诊断"];
        private const int TabWidth = 76;
        private const int TabLeft = 16;
        private int selected;
        private int hover = -1;

        internal SettingsTabsView()
        {
            Height = 46;
            Dock = DockStyle.Fill;
            BackColor = DeckTheme.Surface;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        internal event Action<int>? Selected;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int SelectedIndex
        {
            get => selected;
            set
            {
                if (value < 0 || value >= Labels.Length || selected == value) return;
                selected = value;
                Invalidate();
            }
        }

        private Rectangle TabBounds(int index) => new(TabLeft + index * TabWidth, 0, TabWidth, Height - 1);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = Enumerable.Range(0, Labels.Length).FirstOrDefault(i => TabBounds(i).Contains(e.Location), -1);
            if (hit != hover) { hover = hit; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int hit = Enumerable.Range(0, Labels.Length).FirstOrDefault(i => TabBounds(i).Contains(e.Location), -1);
            if (hit >= 0) Selected?.Invoke(hit);
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var background = new SolidBrush(DeckTheme.Surface))
                g.FillRectangle(background, ClientRectangle);
            using (var line = new Pen(DeckTheme.Line))
                g.DrawLine(line, 0, Height - 1, Width, Height - 1);
            for (int index = 0; index < Labels.Length; index++)
            {
                var box = TabBounds(index);
                if (index == hover && index != selected)
                {
                    using var path = DeckTheme.Rounded(new RectangleF(box.X + 2, 5, box.Width - 4, box.Height - 10), 5);
                    using var fill = new SolidBrush(DeckTheme.Active);
                    g.FillPath(fill, path);
                }
                var color = index == selected ? DeckTheme.Ink : DeckTheme.Muted;
                TextRenderer.DrawText(g, Labels[index], index == selected ? DeckTheme.ButtonFaceBold : DeckTheme.ButtonFace,
                    box, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (index != selected) continue;
                using var underline = new SolidBrush(DeckTheme.Accent);
                g.FillRectangle(underline, box.X + 12, Height - 2, box.Width - 24, 2);
            }
        }
    }
}
