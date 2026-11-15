namespace AgentDeck.Tray;

internal sealed partial class ReviewWindow
{
    private readonly Panel content = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label status = DeckTheme.Label("就绪", DeckTheme.Small, DeckTheme.Muted);
    private readonly Label connectionState = DeckTheme.Label("", DeckTheme.Section);
    private readonly Label relayState = DeckTheme.Label("", DeckTheme.Body, DeckTheme.Muted);
    private readonly Label phoneHint = DeckTheme.Label("", DeckTheme.Body, DeckTheme.Muted);
    private readonly Label railState = DeckTheme.Label("", DeckTheme.Small);
    private readonly Label inboxCount = DeckTheme.Label("待审核任务", DeckTheme.Body, DeckTheme.Muted);
    private readonly Label logTitle = DeckTheme.Label("任务日志", DeckTheme.Section);
    private readonly TextBox logOutput = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, AccessibleName = "任务日志内容"
    };
    private readonly List<(Control View, Button Tab)> pages = [];
    private Button pair = null!;
    private int logPageIndex;
    private bool busy;

    private bool DeviceRegistered => File.Exists(Path.Combine(DeviceDirectory, "registered-device")) &&
        File.Exists(AgentConfig);

    private void BuildWorkspace()
    {
        DeckTheme.Form(this, "AgentDeck");
        MinimumSize = new Size(900, 660);
        ClientSize = new Size(1120, 720);
        StartPosition = FormStartPosition.CenterScreen;

        var shell = DeckTheme.Table();
        shell.ColumnCount = 2;
        shell.RowCount = 1;
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 194));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(content, 1, 0);
        var rail = DeckTheme.Table(new Padding(16, 23, 16, 16));
        rail.BackColor = DeckTheme.Rail;
        shell.Controls.Add(rail, 0, 0);
        Controls.Add(shell);

        var brand = DeckTheme.Label("AgentDeck", DeckTheme.Brand);
        brand.Padding = new Padding(9, 0, 0, 0);
        DeckTheme.Row(rail, brand, 37);
        var deviceLabel = DeckTheme.Label(Environment.MachineName, DeckTheme.Small, DeckTheme.Muted);
        deviceLabel.AutoEllipsis = true;
        deviceLabel.Padding = new Padding(9, 0, 5, 0);
        DeckTheme.Row(rail, deviceLabel, 25);
        DeckTheme.Row(rail, new Panel(), 28);

        AddPage(rail, "连接", ConnectionPage());
        AddPage(rail, "任务", TasksPage());
        AddPage(rail, "项目", ProjectsPage());
        logPageIndex = pages.Count;
        AddPage(rail, "日志", LogsPage());
        AddPage(rail, "设置", SettingsPage());
        DeckTheme.Row(rail, new Panel(), 100, fill: true);
        railState.Padding = new Padding(9, 0, 0, 0);
        DeckTheme.Row(rail, railState, 33);
        SelectPage(0);

        var monitor = new System.Windows.Forms.Timer { Interval = 2000 };
        monitor.Tick += (_, _) => UpdateConnection();
        Shown += (_, _) => monitor.Start();
        FormClosed += (_, _) => monitor.Dispose();
    }

    private void AddPage(TableLayoutPanel rail, string title, Control page)
    {
        int index = pages.Count;
        var tab = DeckTheme.Button(title);
        tab.AutoSize = false;
        tab.MinimumSize = Size.Empty;
        tab.Dock = DockStyle.Fill;
        tab.Margin = new Padding(0, 0, 0, 5);
        tab.TextAlign = ContentAlignment.MiddleLeft;
        tab.Click += (_, _) => SelectPage(index);
        DeckTheme.Row(rail, tab, 48);
        pages.Add((page, tab));
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
            tab.BackColor = selected ? DeckTheme.Active : DeckTheme.Rail;
            tab.ForeColor = selected ? DeckTheme.Accent : DeckTheme.Muted;
            tab.FlatAppearance.BorderColor = selected ? DeckTheme.Active : DeckTheme.Rail;
            tab.AccessibleDescription = selected ? "当前页面" : "";
        }
        content.ResumeLayout();
    }

    private TableLayoutPanel Page(string title, string description, Control body)
    {
        var page = DeckTheme.Table(new Padding(32, 22, 32, 10));
        DeckTheme.Row(page, DeckTheme.Label(title, DeckTheme.Heading), 52);
        DeckTheme.Row(page, DeckTheme.Label(description, DeckTheme.Body, DeckTheme.Muted), 31);
        DeckTheme.Row(page, new Panel(), 20);
        DeckTheme.Row(page, body, 100, fill: true);
        var feedback = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Muted);
        status.TextChanged += (_, _) => feedback.Text = status.Text;
        feedback.Text = status.Text;
        feedback.Padding = new Padding(0, 6, 0, 0);
        feedback.AccessibleName = "操作状态";
        feedback.LiveSetting = System.Windows.Forms.Automation.AutomationLiveSetting.Polite;
        feedback.VisibleChanged += (_, _) => feedback.Text = status.Text;
        DeckTheme.Row(page, feedback, 52);
        return page;
    }

    private static TableLayoutPanel Card()
    {
        var card = DeckTheme.Table(new Padding(22, 16, 22, 16));
        card.BackColor = DeckTheme.Surface;
        return card;
    }

    private Control ConnectionPage()
    {
        var body = DeckTheme.Table();
        body.ColumnCount = 3;
        body.RowCount = 2;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var connection = Card();
        connection.Margin = Padding.Empty;
        DeckTheme.Row(connection, connectionState, 36);
        DeckTheme.Row(connection, relayState, 62);
        var serverActions = DeckTheme.Actions();
        AddAction(serverActions, "配置云服务器", ConfigureRelayAsync, primary: true);
        DeckTheme.Row(connection, serverActions, 40);
        body.Controls.Add(connection, 0, 0);

        var phone = Card();
        DeckTheme.Row(phone, DeckTheme.Label("配对手机", DeckTheme.Section), 36);
        DeckTheme.Row(phone, phoneHint, 62);
        var phoneActions = DeckTheme.Actions();
        pair = AddAction(phoneActions, "显示配对二维码", PairPhoneAsync);
        DeckTheme.Row(phone, phoneActions, 40);
        body.Controls.Add(phone, 2, 0);
        return Page("连接", "注册这台电脑并配对 Android 设备。", body);
    }

    private Control TasksPage()
    {
        var body = Card();
        var actions = DeckTheme.Actions();
        AddAction(actions, "刷新待办", RefreshInboxAsync, primary: true);
        var preview = AddAction(actions, "查看任务", async () =>
        {
            var selected = Selected();
            detail.Text = FormatPreview(selected, await PreviewAsync(selected));
        });
        var approve = AddAction(actions, "批准启动一次", () => ReviewAsync(true));
        var decline = AddAction(actions, "拒绝任务", () => ReviewAsync(false));
        void UpdateSelection()
        {
            preview.Enabled = approve.Enabled = decline.Enabled = pending.SelectedIndex >= 0;
        }
        pending.SelectedIndexChanged += (_, _) => UpdateSelection();
        UpdateSelection();
        DeckTheme.Row(body, actions, 46);
        DeckTheme.Row(body, inboxCount, 28);

        pending.BorderStyle = BorderStyle.None;
        pending.BackColor = DeckTheme.Well;
        pending.ForeColor = DeckTheme.Ink;
        pending.Font = DeckTheme.Body;
        pending.HorizontalScrollbar = true;
        pending.IntegralHeight = false;
        pending.Margin = Padding.Empty;
        var list = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = DeckTheme.Well };
        list.Controls.Add(pending);
        DeckTheme.Row(body, list, 35, fill: true);
        DeckTheme.Row(body, DeckTheme.Label("任务详情", DeckTheme.Body, DeckTheme.Muted), 34);
        DeckTheme.Row(body, DeckTheme.Reader(detail, "刷新待办后选择任务，即可查看请求内容。"), 65, fill: true);
        return Page("任务审核", "查看发到这台电脑的任务请求，并在本机批准或拒绝。", body);
    }

    private Control ProjectsPage()
    {
        var body = Card();
        var file = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Muted);
        file.AutoEllipsis = true;
        void UpdatePath() => file.Text = string.IsNullOrWhiteSpace(projects.Text)
            ? "授权清单将在注册电脑后生成路径，也可在「设置」中指定。"
            : projects.Text;
        projects.TextChanged += (_, _) => UpdatePath();
        UpdatePath();
        DeckTheme.Row(body, DeckTheme.Label("本机项目授权", DeckTheme.Section), 38);
        DeckTheme.Row(body, file, 32);
        var buttons = DeckTheme.Actions();
        AddAction(buttons, "管理项目", () =>
        {
            using var editor = new ProjectEditor(RequirePath(projects));
            status.Text = editor.ShowDialog(this) == DialogResult.OK ? "项目授权已保存。" : "已取消。";
            return Task.CompletedTask;
        }, primary: true);
        DeckTheme.Row(body, buttons, 40);
        var wrapper = DeckTheme.Table();
        DeckTheme.Row(wrapper, body, 146);
        DeckTheme.Row(wrapper, new Panel(), 100, fill: true);
        return Page("项目", "手机只能访问你在这台电脑上启用的项目。", wrapper);
    }

    private Control LogsPage()
    {
        var body = Card();
        var actions = DeckTheme.Actions();
        AddAction(actions, "打开任务日志", ViewLogAsync, primary: true);
        AddAction(actions, "取消运行任务", CancelTaskAsync);
        DeckTheme.Row(body, actions, 47);
        DeckTheme.Row(body, logTitle, 37);
        DeckTheme.Row(body, DeckTheme.Reader(logOutput, "打开一个任务日志以查看运行输出。"), 100, fill: true);
        return Page("日志", "查看本机任务输出，或向正在运行的任务发送取消请求。", body);
    }

    private Control SettingsPage()
    {
        var body = Card();
        body.AutoScroll = true;
        DeckTheme.Row(body, PathField("Codex 可执行文件", codex, false), 69);
        DeckTheme.Row(body, PathField("Codex 配置目录", home, true), 69);
        DeckTheme.Row(body, PathField("项目授权清单", projects, false), 69);
        DeckTheme.Row(body, PathField("任务收件箱", inbox, true), 69);
        DeckTheme.Row(body, PathField("任务日志目录", logs, true), 69);
        var actions = DeckTheme.Actions();
        AddAction(actions, "保存设置", () =>
        {
            SavePaths();
            return Task.CompletedTask;
        }, primary: true);
        DeckTheme.Row(body, new Panel(), 8);
        DeckTheme.Row(body, actions, 40);
        DeckTheme.Row(body, new Panel(), 100, fill: true);
        return Page("设置", "配置本机 Codex 和任务数据的位置。运行中的 Agent 会在下次启动时读取路径设置。", body);
    }

    private void UpdateConnection()
    {
        bool registered = DeviceRegistered;
        bool running = agent is { HasExited: false };
        connectionState.Text = registered ? "这台电脑已注册" : "连接你的云服务器";
        relayState.Text = registered
            ? "设备身份已保存在本机。"
            : "准备好 Relay 地址、证书指纹和注册令牌后，即可注册这台电脑。";
        phoneHint.Text = registered
            ? "在 Android 应用中扫码，然后在这台电脑上核对手机指纹。"
            : "注册电脑后，即可用 Android 应用扫码配对。";
        railState.Text = running ? "Agent 运行中" : registered ? "Agent 未运行" : "电脑未注册";
        railState.ForeColor = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Muted;
        pair.Enabled = registered;
        pair.Visible = registered;
    }

    internal Task OpenLogsAndCancelTaskAsync()
    {
        SelectPage(logPageIndex);
        return RunActionAsync(CancelTaskAsync);
    }

    private Button AddAction(FlowLayoutPanel row, string label, Func<Task> action, bool primary = false)
    {
        var button = DeckTheme.Button(label, primary);
        button.Click += async (_, _) => await RunActionAsync(action);
        row.Controls.Add(button);
        return button;
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        content.Enabled = false;
        status.Text = "正在处理…";
        try
        {
            await action();
            if (!IsDisposed && status.Text == "正在处理…")
                status.Text = "操作完成。";
        }
        catch (Exception error)
        {
            if (!IsDisposed) status.Text = error.Message;
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                content.Enabled = true;
                UpdateConnection();
            }
        }
    }
}
