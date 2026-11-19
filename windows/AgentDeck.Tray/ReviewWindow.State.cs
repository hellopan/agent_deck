using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AgentDeck.Tray;

// 主机 page: four readiness checks on one card, then service and identity. The
// page answers one question — can a phone give this machine an instruction right
// now — so every row carries both the value and what to do about it. Numbers the
// process cannot measure (latency, last reconnect) are absent rather than faked.
internal sealed partial class ReviewWindow
{
    private readonly DeckTheme.PillLabel readyPass =
        DeckTheme.Pill("0 项通过", DeckTheme.PillTone.Good, dot: true);
    private readonly DeckTheme.PillLabel readyPending =
        DeckTheme.Pill("0 项待处理", DeckTheme.PillTone.Warning);
    private readonly Label readyChannelValue = DeckTheme.Label("—", DeckTheme.Body);
    private readonly Label readyChannelSub = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label readyEngineValue = DeckTheme.Label("—", DeckTheme.Body);
    private readonly Label readyEngineSub = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label readyProjectValue = DeckTheme.Label("—", DeckTheme.Body);
    private readonly Label readyProjectSub = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label readyPairValue = DeckTheme.Label("—", DeckTheme.Body);
    private readonly Label readyPairSub = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label serviceModeValue = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label serviceRelayValue = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label serviceDeviceValue = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label serviceSinceValue = DeckTheme.Label("—", DeckTheme.Small, DeckTheme.Muted);
    private readonly Label asideVersion = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label asideUptime = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly FlowLayoutPanel pairedList = new()
    {
        Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
        FlowDirection = FlowDirection.TopDown, WrapContents = false
    };
    private Control pairedEmpty = null!;
    private readonly Label pairHint = DeckTheme.Label("同一时间只允许一个配对请求", DeckTheme.Tiny, DeckTheme.Dim);
    private string pairedSignature = "\0";
    private bool readyChannelOk, readyEngineOk, readyProjectOk, readyPairOk;

    private Control StatePage()
    {
        var body = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(body, ReadinessCard(), 100, fill: true);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Canvas }, 16);
        DeckTheme.Row(body, ServiceIdentityCard(), 196);
        return Page("主机", "这台电脑正在替你的手机做什么。四项都通过，手机才能下发指令。", body, StateAside());
    }

    private Control StateAside()
    {
        // One line, not two stacked columns: the stacked version needed ~200px and
        // the page title took it, so the values wrapped mid-word at this width.
        var row = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            BackColor = DeckTheme.Canvas, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var caption = DeckTheme.Label("CODEX", DeckTheme.Tiny, DeckTheme.Dim);
        caption.AutoSize = true;
        caption.Dock = DockStyle.None;
        caption.Anchor = AnchorStyles.Right;
        caption.Margin = new Padding(0, 0, 7, 0);
        row.Controls.Add(caption, 0, 0);

        asideVersion.AutoSize = true;
        asideVersion.Dock = DockStyle.None;
        asideVersion.Anchor = AnchorStyles.Right;
        asideVersion.Font = DeckTheme.MonoSmall;
        asideVersion.ForeColor = DeckTheme.Muted;
        asideVersion.Margin = new Padding(0, 0, 10, 0);
        row.Controls.Add(asideVersion, 1, 0);

        asideUptime.AutoSize = true;
        asideUptime.Dock = DockStyle.None;
        asideUptime.Anchor = AnchorStyles.Right;
        asideUptime.Font = DeckTheme.MonoSmall;
        asideUptime.ForeColor = DeckTheme.Dim;
        row.Controls.Add(asideUptime, 2, 0);
        return row;
    }

    private Control ReadinessCard()
    {
        var check = DeckTheme.Link("重新检查", DeckTheme.LinkColor);
        check.Click += async (_, _) =>
        {
            await RunActionAsync(() => DetectToolsAsync(fill: true, quiet: true));
            RefreshStatePage();
        };
        var header = DeckTheme.CardHeaderWith("就绪度", null, [readyPass, readyPending], check);

        var body = DeckTheme.Table(new Padding(16, 0, 16, 6), DeckTheme.Surface);
        DeckTheme.Row(body, header, 42);
        DeckTheme.Row(body, ReadyRow("中枢通道", readyChannelValue, readyChannelSub,
            Link("日志", () => status.Text = connectionEvents.FirstOrDefault() ?? "还没有连接事件。"),
            Link("重新检查", () => RefreshStatePage())), 62);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        DeckTheme.Row(body, ReadyRow("引擎", readyEngineValue, readyEngineSub,
            Link("去处理", () => OpenSettingsTab(1)),
            Link("重新检测", () => _ = RunActionAsync(() => DetectToolsAsync(fill: true)))), 62);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        DeckTheme.Row(body, ReadyRow("项目", readyProjectValue, readyProjectSub,
            Link("管理项目", () => SelectPage(2))), 62);
        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        DeckTheme.Row(body, ReadyRow("配对", readyPairValue, readyPairSub,
            Link("管理", () => OpenSettingsTab(2))), 62);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);

        var card = new DeckTheme.CardPanel();
        card.Controls.Add(body);
        return card;
    }

    private static Button Link(string text, Action action)
    {
        var button = DeckTheme.Link(text, DeckTheme.LinkColor);
        button.Margin = new Padding(10, 0, 0, 0);
        button.Click += (_, _) => action();
        return button;
    }

    private static Control ReadyRow(string key, Label value, Label sub, params Control[] actions)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 2
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

        // The design aligns the key with the first line of the value, not with
        // the middle of the two-line block. Same cell, same alignment, so the
        // baselines line up whatever the font does at runtime.
        var caption = DeckTheme.Label(key, DeckTheme.Small, DeckTheme.Dim);
        row.Controls.Add(caption, 0, 0);

        value.AutoEllipsis = true;
        value.Margin = Padding.Empty;
        row.Controls.Add(value, 1, 0);
        sub.AutoEllipsis = true;
        row.Controls.Add(sub, 1, 1);

        if (actions.Length > 0)
        {
            var strip = DeckTheme.Inline(actions, gap: 12);
            strip.Anchor = AnchorStyles.Right;
            row.Controls.Add(strip, 2, 0);
        }
        return row;
    }

    private Control ServiceIdentityCard()
    {
        var qr = DeckTheme.Link("显示配对二维码", DeckTheme.LinkColor);
        qr.Click += async (_, _) => await RunActionAsync(PairPhoneAsync);
        var manage = DeckTheme.Link("管理配对", DeckTheme.Dim);
        manage.Click += (_, _) => OpenSettingsTab(2);
        var header = DeckTheme.CardHeader("服务与身份", null, null, manage, qr);

        var body = DeckTheme.Table(new Padding(16, 0, 16, 12), DeckTheme.Surface);
        DeckTheme.Row(body, header, 42);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 2
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.Controls.Add(Kv("服务模式", serviceModeValue), 0, 0);
        grid.Controls.Add(Kv("Relay 地址", serviceRelayValue), 1, 0);
        grid.Controls.Add(Kv("设备 ID", serviceDeviceValue), 0, 1);
        grid.Controls.Add(Kv("上次上线", serviceSinceValue), 1, 1);
        DeckTheme.Row(body, grid, 100, fill: true);

        var card = new DeckTheme.CardPanel();
        card.Controls.Add(body);
        return card;
    }

    private static Control Kv(string caption, Label value)
    {
        var table = DeckTheme.Table(new Padding(0, 8, 12, 6), DeckTheme.Surface);
        DeckTheme.Row(table, DeckTheme.Caption(caption), 18);
        value.AutoEllipsis = true;
        DeckTheme.Row(table, value, 22);
        return table;
    }

    private void RefreshStatePage()
    {
        bool registered = DeviceRegistered;
        // Two ways to be reachable: the process this window started, or the
        // service that starts before anyone logs in. Only the first has an
        // uptime this window can measure.
        bool child = agent is { HasExited: false };
        var service = ServiceControl.Query();
        bool running = child || service.Running;
        var id = DeviceIdText();
        var shortId = DeviceShort(id);
        var uptime = child ? DateTime.Now - agent!.StartTime : TimeSpan.Zero;
        var phones = TrustedPhones().ToList();

        topBar.Host = Environment.MachineName;
        topBar.Device = registered ? shortId : "";
        topBar.State = running ? "AGENT 运行中" : registered ? "AGENT 未运行" : "未注册";
        topBar.StateColor = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;

        statusDevice.Text = registered ? shortId : "";
        statusService.Text = running ? "服务 · 运行中" : registered ? "服务 · 已停止" : "服务 · 未注册";
        statusService.ForeColor = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;
        rail.StatusColor = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;

        // 通道
        readyChannelOk = running;
        readyChannelValue.Text = running ? "已连通" : registered ? "已停止" : "未注册";
        readyChannelValue.ForeColor = running ? DeckTheme.Ink : DeckTheme.Muted;
        var relay = RelayEndpoint();
        readyChannelSub.Text = running
            ? $"{(relay.Length > 0 ? relay : "尚未配置 Relay")} · " +
              $"{(child ? $"进程已运行 {FormatSpan(uptime)}" : "由 Windows 服务托管")}"
            : registered ? "Agent 未运行，手机此刻连不上。重启托盘或启动服务。" : "先配置 Relay 地址与注册令牌，手机才能连接。";

        // 引擎
        var codex = codexLine.Text;
        readyEngineOk = codexVersion.Length > 0;
        readyEngineValue.Text = readyEngineOk ? $"Codex {codexVersion} 就绪"
            : codex == "尚未检测" ? "正在检测…" : "Codex 不可用";
        readyEngineValue.ForeColor = readyEngineOk ? DeckTheme.Ink : DeckTheme.Warning;
        var cursor = cursorLine.Text;
        var buddy = buddyLine.Text;
        readyEngineSub.Text = $"Cursor {(cursor == "尚未检测" ? "未检测" : cursor)} · " +
                              $"WorkBuddy {(buddy == "尚未检测" ? "未检测" : buddy)} · 未就绪的引擎不会出现在手机端";

        // 项目
        var (total, enabled) = PolicyCounts();
        readyProjectOk = enabled > 0;
        readyProjectValue.Text = $"{total} 个目录 · {enabled} 个启用";
        readyProjectValue.ForeColor = readyProjectOk ? DeckTheme.Ink : DeckTheme.Warning;
        readyProjectSub.Text = readyProjectOk
            ? "手机只能看到启用中的项目；越界写入会被拒绝并记录。"
            : "还没有启用任何项目，手机提交不了任务。去「项目」页添加一个目录。";

        // 配对
        readyPairOk = phones.Count > 0;
        readyPairValue.Text = phones.Count == 0 ? "没有已授权的手机" : $"{phones.Count} 台手机已授权";
        readyPairValue.ForeColor = readyPairOk ? DeckTheme.Ink : DeckTheme.Warning;
        readyPairSub.Text = phones.Count == 0
            ? "点「显示配对二维码」，用手机扫码后在本机确认。"
            : $"{ShortFingerprint(phones[0].Fingerprint)} · {phones[0].At:MM-dd HH:mm} 配对 · 撤销后立即断连";

        var pass = new[] { readyChannelOk, readyEngineOk, readyProjectOk, readyPairOk }.Count(ok => ok);
        // The design keeps "N 项通过" green even when something else is missing:
        // green means "these four, this many are fine", amber carries the gap.
        readyPass.Text = $"{pass} 项通过";
        readyPass.Tone = pass > 0 ? DeckTheme.PillTone.Good : DeckTheme.PillTone.Neutral;
        readyPending.Text = $"{4 - pass} 项待处理";
        readyPending.Tone = pass == 4 ? DeckTheme.PillTone.Neutral : DeckTheme.PillTone.Warning;

        // 服务与身份
        serviceModeValue.Text = service.Installed
            ? service.Running ? "Windows 服务 · 自动启动" : "Windows 服务 · 已安装，未运行"
            : "托盘进程 · 登录后启动";
        serviceRelayValue.Text = relay.Length > 0 ? relay : "—";
        serviceDeviceValue.Text = registered ? shortId : "未注册";
        serviceSinceValue.Text = child
            ? $"本次运行 {FormatSpan(uptime)}"
            : service.Running ? "由服务托管，登录前即可上线" : "—";

        asideVersion.Text = codexVersion is { Length: > 0 } version ? version : "—";
        asideUptime.Text = child ? FormatSpan(uptime) : running ? "服务托管" : "未运行";

        pair.Enabled = registered;
        pairHint.Text = registered ? "同一时间只允许一个配对请求" : "配置云服务器后才能配对";
        RefreshPaired();
    }

    private (int Total, int Enabled) PolicyCounts()
    {
        int total = 0, enabled = 0;
        try
        {
            if (!File.Exists(ProjectsPath)) return (0, 0);
            using var document = JsonDocument.Parse(File.ReadAllText(ProjectsPath));
            if (!document.RootElement.TryGetProperty("projects", out var list) ||
                list.ValueKind != JsonValueKind.Array) return (0, 0);
            foreach (var entry in list.EnumerateArray())
            {
                total++;
                if (entry.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True)
                    enabled++;
            }
        }
        catch (Exception error) when (error is IOException or JsonException) { }
        return (total, enabled);
    }

    private Control PairedCard()
    {
        var card = Card("已配对手机", out var body, note: "撤销后立即断连");
        var empty = DeckTheme.Table(background: DeckTheme.Surface);
        pairedEmpty = empty;
        DeckTheme.Row(empty, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        var emptyTitle = DeckTheme.Label("还没有配对的手机", DeckTheme.Body, DeckTheme.Muted);
        emptyTitle.TextAlign = ContentAlignment.MiddleCenter;
        DeckTheme.Row(empty, emptyTitle, 24);
        var emptyHint = DeckTheme.Label("生成配对码，用手机扫码即可连接", DeckTheme.Small, DeckTheme.Dim);
        emptyHint.TextAlign = ContentAlignment.MiddleCenter;
        DeckTheme.Row(empty, emptyHint, 20);
        DeckTheme.Row(empty, new Panel { BackColor = DeckTheme.Surface }, 20);
        // Three steps, not a blank card: an empty list with nothing but a
        // sentence in the middle of 300px reads as a rendering failure.
        DeckTheme.Row(empty, PairingSteps(), 84);
        DeckTheme.Row(empty, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);

        var stack = new Panel { Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty };
        stack.Controls.Add(pairedList);
        stack.Controls.Add(empty);
        empty.BringToFront();
        DeckTheme.Row(body, stack, 100, fill: true);
        // A flow panel never stretches its children, so the rows have to be told
        // the card's width or the pill and the revoke link end up packed against
        // the fingerprint instead of sitting on the right edge. Layout, not just
        // SizeChanged: the page is laid out while hidden, and a hidden control
        // can reach its final width without ever raising a size change.
        pairedList.SizeChanged += (_, _) => ResizePairedRows();
        pairedList.Layout += (_, _) => ResizePairedRows();

        pair = DeckTheme.Button("显示配对二维码");
        pair.Margin = Padding.Empty;
        pair.Click += async (_, _) => await RunActionAsync(PairPhoneAsync);
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pairHint.AutoEllipsis = true;
        footer.Controls.Add(pairHint, 0, 0);
        footer.Controls.Add(pair, 1, 0);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 10);
        DeckTheme.Row(body, footer, 32);

        return card;
    }

    // Centred because the two flexible columns match either side of the block,
    // so the step list stays on the card's centre line at any window width.
    private static Control PairingSteps()
    {
        var steps = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 4, RowCount = 3
        };
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22));
        // 322px clipped the third step to "…再点确..": the line needs 300px of text
        // plus the number column, and an ellipsis in an instruction is a dead end.
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 372));
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        string[] lines =
        [
            "点「显示配对二维码」，屏幕上出现二维码",
            "手机打开 AgentDeck 扫描这个二维码",
            "核对两台设备显示的手机指纹一致，再点确认"
        ];
        for (int index = 0; index < lines.Length; index++)
        {
            steps.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            var number = DeckTheme.Label($"{index + 1}", DeckTheme.MonoMicro, DeckTheme.Accent);
            number.TextAlign = ContentAlignment.MiddleLeft;
            steps.Controls.Add(number, 1, index);
            var text = DeckTheme.Label(lines[index], DeckTheme.Small, DeckTheme.Dim);
            text.AutoEllipsis = true;
            steps.Controls.Add(text, 2, index);
        }
        return steps;
    }

    private void RefreshPaired()
    {
        var phones = TrustedPhones().ToList();
        var signature = string.Join("|", phones.Select(p => p.Fingerprint));
        if (signature == pairedSignature) return;
        pairedSignature = signature;
        pairedEmpty.Visible = phones.Count == 0;
        pairedList.Visible = phones.Count > 0;
        pairedList.SuspendLayout();
        pairedList.Controls.Clear();
        foreach (var phone in phones)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
                ColumnCount = 3, RowCount = 2, Height = 58, Padding = new Padding(2, 9, 2, 9)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            var who = DeckTheme.Label(ShortFingerprint(phone.Fingerprint), DeckTheme.Monospace, DeckTheme.Ink);
            row.Controls.Add(who, 0, 0);
            var live = DeckTheme.Pill("已信任", DeckTheme.PillTone.Good, dot: true);
            live.Anchor = AnchorStyles.Right;
            live.Margin = new Padding(12, 0, 0, 0);
            row.Controls.Add(live, 1, 0);
            // The trust file is the whole authorization, so removing it is the
            // revocation: the Agent refuses this phone on its next request.
            var fingerprint = phone.Fingerprint;
            var revoke = DeckTheme.Link("撤销", DeckTheme.Dim, DeckTheme.Danger);
            revoke.Anchor = AnchorStyles.Right;
            revoke.Margin = new Padding(4, 1, 0, 0);
            revoke.AccessibleName = $"撤销手机 {ShortFingerprint(fingerprint)}";
            revoke.Click += async (_, _) => await RunActionAsync(() => RevokePhoneAsync(fingerprint));
            row.Controls.Add(revoke, 2, 0);
            var meta = $"配对于 {phone.At:MM-dd HH:mm}" +
                       (phone.Expires is { } expires ? $" · 到期 {expires:yyyy-MM}" : "");
            var when = DeckTheme.Label(meta, DeckTheme.Tiny, DeckTheme.Dim);
            when.AutoEllipsis = true;
            row.SetColumnSpan(when, 3);
            row.Controls.Add(when, 0, 1);
            pairedList.Controls.Add(row);
            var line = new Panel { BackColor = DeckTheme.Hairline, Height = 1, Margin = Padding.Empty };
            pairedList.Controls.Add(line);
        }
        pairedList.ResumeLayout();
        ResizePairedRows();
    }

    // The rows carry their own table because two columns of text and a link do
    // not line up in a flow layout. Width has to be pushed in by hand, and only
    // when it differs: the guard is what keeps the Layout handler from looping.
    private void ResizePairedRows()
    {
        int width = pairedList.ClientSize.Width - 1;
        if (width <= 0) return;
        foreach (Control control in pairedList.Controls)
        {
            if (control.Width != width) control.Width = width;
        }
    }

    // Revoking is a delete in the trust folder, so it works even while the Agent
    // is stopped: the Agent re-reads that folder on every tunnel request, which
    // is also why the phone loses access on its next poll rather than after a
    // restart.
    private async Task RevokePhoneAsync(string fingerprint)
    {
        var device = DeviceIdText();
        if (device.Length == 0) throw new InvalidOperationException("本机还没有注册到 Relay。");
        var answer = MessageBox.Show(this,
            $"撤销后，这台手机需要重新配对才能连接。\n\n手机指纹 {ShortFingerprint(fingerprint)}",
            "撤销手机配对", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        await RunAsync("pair-revoke", "-pairing-dir", PairingDirectory, "-device", device,
            "-fingerprint", fingerprint);
        RecordEvent("已撤销手机配对");
        RefreshStatePage();
        SetStatus("已撤销手机配对，该手机需要重新扫码。");
    }

    // The event feed moved to the 诊断 tab; the tray still records events, so the
    // refresh hook stays and forwards to the one label that shows them.
    private void RefreshEvents()
    {
        if (settingsPanes.Count > 3) RefreshDiagnostics();
    }

    private void RecordEvent(string text)
    {
        connectionEvents.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        if (connectionEvents.Count > 12) connectionEvents.RemoveRange(12, connectionEvents.Count - 12);
    }

    private static string FormatSpan(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays} 天 {span.Hours} 小时"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分" : $"{Math.Max(0, (int)span.TotalMinutes)} 分钟";

    private static string ShortFingerprint(string fingerprint)
    {
        var clean = new string(fingerprint.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length <= 8) return clean;
        return clean[..4] + "·" + clean[^4..];
    }

    private static string DeviceShort(string id)
    {
        var clean = new string(id.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length == 0) return "—";
        if (clean.Length <= 8) return clean;
        return clean[..4] + "·" + clean[^4..];
    }

    private string DeviceIdText()
    {
        try
        {
            var path = Path.Combine(DeviceDirectory, "registered-device");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (IOException) { return ""; }
    }

    private DateTime? RegisteredAt()
    {
        try
        {
            var path = Path.Combine(DeviceDirectory, "registered-device");
            return File.Exists(path) ? File.GetCreationTime(path) : null;
        }
        catch (IOException) { return null; }
    }

    private string RelayEndpoint()
    {
        try
        {
            if (!File.Exists(AgentConfig)) return "";
            using var config = JsonDocument.Parse(File.ReadAllText(AgentConfig));
            return config.RootElement.TryGetProperty("relayUrl", out var url) ? url.GetString() ?? "" : "";
        }
        catch (Exception error) when (error is IOException or JsonException) { return ""; }
    }

    private IEnumerable<(string Fingerprint, DateTime At, DateTime? Expires)> TrustedPhones()
    {
        if (!Directory.Exists(PairingDirectory)) yield break;
        foreach (var path in Directory.EnumerateFiles(PairingDirectory, "trusted-*.local.json"))
        {
            var name = Path.GetFileName(path);
            if (name.Length <= "trusted-.local.json".Length) continue;
            var middle = name["trusted-".Length..^".local.json".Length];
            var fingerprint = middle.Length >= 64 ? middle[^64..] : middle;
            DateTime? expires = null;
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
                expires = certificate.NotAfter;
            }
            catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException) { }
            yield return (fingerprint, File.GetCreationTime(path), expires);
        }
    }
}
