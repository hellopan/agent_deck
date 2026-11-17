using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AgentDeck.Tray;

// 状态 page: the channel, this machine's identity and the phones that may talk
// to it. Everything here is read from disk or from the running process — the
// design's latency graph and "last reconnect" are deliberately absent because
// nothing measures them yet, and an invented number is worse than a gap.
internal sealed partial class ReviewWindow
{
    private readonly DeckTheme.StatusDot channelDot = DeckTheme.Dot(DeckTheme.Dim, 7);
    private readonly Label channelTitle = DeckTheme.Label("未注册", DeckTheme.Hero);
    private readonly Label channelAddr = DeckTheme.Label("", DeckTheme.MonoSmall, DeckTheme.Dim);
    private readonly Label channelSince = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label metricUptime = DeckTheme.Label("—", DeckTheme.MonoHero, DeckTheme.Muted);
    private readonly Label metricDevice = DeckTheme.Label("—", DeckTheme.MonoHero, DeckTheme.Muted);
    private readonly Label metricRegistered = DeckTheme.Label("—", DeckTheme.MonoHero, DeckTheme.Muted);
    private readonly Label identityMachine = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label identityDevice = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label identityRegistered = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label identityRelay = DeckTheme.Label("—", DeckTheme.MonoSmall, DeckTheme.Muted);
    private readonly Label asideVersion = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label asideUptime = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly FlowLayoutPanel pairedList = new()
    {
        Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
        FlowDirection = FlowDirection.TopDown, WrapContents = false
    };
    private readonly FlowLayoutPanel eventList = new()
    {
        Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
        FlowDirection = FlowDirection.TopDown, WrapContents = false
    };
    private Control pairedEmpty = null!;
    private readonly Label pairHint = DeckTheme.Label("同一时间只允许一个配对请求", DeckTheme.Tiny, DeckTheme.Dim);
    private string pairedSignature = "\0";
    private string eventSignature = "\0";

    private Control StatePage()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(StateMain(), 0, 0);
        body.Controls.Add(new Panel { BackColor = DeckTheme.Canvas }, 1, 0);
        body.Controls.Add(StateSide(), 2, 0);
        return Page("状态", "这台电脑的远程通道、身份与已配对设备。", body, StateAside());
    }

    private Control StateAside()
    {
        var aside = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            BackColor = DeckTheme.Canvas, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1
        };
        aside.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        aside.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        aside.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        aside.Controls.Add(AsideItem("CODEX", asideVersion), 0, 0);
        aside.Controls.Add(AsideItem("已运行", asideUptime), 1, 0);
        return aside;
    }

    private Control ChannelCard()
    {
        var card = Card("通道", out var body);

        var configure = DeckTheme.Button("配置云服务器", primary: true);
        configure.Margin = Padding.Empty;
        configure.Anchor = AnchorStyles.None;
        configure.MinimumSize = new Size(0, 30);
        configure.Click += async (_, _) => await RunActionAsync(ConfigureRelayAsync);

        // Hero: state word, endpoint and the one action that changes the state,
        // on the same line. The action used to sit at the bottom of a tall card,
        // hundreds of pixels away from the status it acts on.
        var hero = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hero.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        channelDot.Dock = DockStyle.None;
        channelDot.Anchor = AnchorStyles.Left;
        channelDot.Margin = new Padding(0, 2, 0, 0);
        hero.Controls.Add(channelDot, 0, 0);
        var titleLine = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        titleLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        channelTitle.AutoSize = true;
        channelTitle.Dock = DockStyle.None;
        channelTitle.Margin = new Padding(0, 0, 12, 0);
        titleLine.Controls.Add(channelTitle, 0, 0);
        channelAddr.AutoEllipsis = true;
        channelAddr.TextAlign = ContentAlignment.MiddleLeft;
        titleLine.Controls.Add(channelAddr, 1, 0);
        hero.Controls.Add(titleLine, 1, 0);
        hero.Controls.Add(configure, 2, 0);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 10);
        DeckTheme.Row(body, hero, 38);
        DeckTheme.Row(body, channelSince, 24);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 12);

        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 5, RowCount = 1
        };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        metrics.Controls.Add(Metric("已运行", metricUptime), 0, 0);
        var div1 = DeckTheme.Divider();
        div1.Margin = new Padding(0, 12, 0, 10);
        metrics.Controls.Add(div1, 1, 0);
        var middle = Metric("设备 ID", metricDevice);
        middle.Margin = new Padding(16, 0, 0, 0);
        metrics.Controls.Add(middle, 2, 0);
        var div2 = DeckTheme.Divider();
        div2.Margin = new Padding(0, 12, 0, 10);
        metrics.Controls.Add(div2, 3, 0);
        var last = Metric("注册于", metricRegistered);
        last.Margin = new Padding(16, 0, 0, 0);
        metrics.Controls.Add(last, 4, 0);
        DeckTheme.Row(body, metrics, 72);

        DeckTheme.Row(body, DeckTheme.Divider(), 1);
        DeckTheme.Row(body, DeckTheme.Caption("三种状态的含义"), 34);
        DeckTheme.Row(body, Legend("已就绪", DeckTheme.Good, "Agent 正在运行且已注册，手机此刻可以提交任务"), 28);
        DeckTheme.Row(body, Legend("已停止", DeckTheme.Warning, "进程已退出，先重启托盘，再检查 Codex 是否可用"), 28);
        DeckTheme.Row(body, Legend("未注册", DeckTheme.Dim, "还没有 Relay 身份，配置云服务器后手机才能连接"), 28);

        return card;
    }

    private Control StateMain()
    {
        var main = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(main, ChannelCard(), 332);
        DeckTheme.Row(main, new Panel { BackColor = DeckTheme.Canvas }, 16);
        DeckTheme.Row(main, EventsCard(), 100, fill: true);
        return main;
    }

    private Control EventsCard()
    {
        var card = Card("最近连接事件", out var body, note: "只保留最近 4 条");
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 2);
        DeckTheme.Row(body, eventList, 100, fill: true);
        return card;
    }

    private Control StateSide()
    {
        var side = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(side, IdentityCard(), 196);
        DeckTheme.Row(side, new Panel { BackColor = DeckTheme.Canvas }, 16);
        DeckTheme.Row(side, PairedCard(), 100, fill: true);
        return side;
    }

    private Control IdentityCard()
    {
        var card = Card("本机身份", out var body, note: "用于手机识别这台电脑");
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 4);
        var rows = new (string Caption, Label Value)[]
        {
            ("计算机名", identityMachine), ("设备 ID", identityDevice),
            ("注册于", identityRegistered), ("Relay", identityRelay)
        };
        for (int index = 0; index < rows.Length; index++)
        {
            var (caption, value) = rows[index];
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
                ColumnCount = 2, RowCount = 1
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.Controls.Add(DeckTheme.Label(caption, DeckTheme.Small, DeckTheme.Dim), 0, 0);
            value.AutoEllipsis = true;
            value.TextAlign = ContentAlignment.MiddleRight;
            row.Controls.Add(value, 1, 0);
            DeckTheme.Row(body, row, 33);
            if (index < rows.Length - 1) DeckTheme.Row(body, DeckTheme.Divider(), 1);
        }
        return card;
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
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 322));
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

        topBar.Host = Environment.MachineName;
        topBar.Device = registered ? shortId : "";
        topBar.State = running ? "AGENT 运行中" : registered ? "AGENT 未运行" : "未注册";
        topBar.StateColor = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;

        statusDevice.Text = registered ? shortId : "";
        statusService.Text = running ? "服务 · 运行中" : registered ? "服务 · 已停止" : "服务 · 未注册";
        statusService.ForeColor = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;
        rail.StatusColor = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;

        channelDot.Color = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;
        channelTitle.Text = running ? "已就绪" : registered ? "已停止" : "未注册";
        channelAddr.Text = RelayEndpoint() is { Length: > 0 } url ? url : "尚未配置 Relay";
        channelSince.Text = child
            ? $"Agent 进程已运行 {FormatSpan(uptime)} · 退出后手机立即断开"
            : service.Running ? "由 Windows 服务托管 · 登录前也能连接"
            : registered ? "Agent 未运行。重启托盘后会重新连接。" : "先配置 Relay 地址与注册令牌，手机才能连接。";

        SetMetric(metricUptime, child ? FormatSpan(uptime) : running ? "服务托管" : "未运行", !running);
        SetMetric(metricDevice, registered ? shortId : "未注册", !registered);
        SetMetric(metricRegistered,
            RegisteredAt() is { } at ? at.ToString("MM-dd HH:mm") : "未注册", RegisteredAt() is null);
        asideVersion.Text = codexVersion is { Length: > 0 } version ? version : "—";
        asideUptime.Text = child ? FormatSpan(uptime) : running ? "服务托管" : "未运行";

        SetValue(identityMachine, Environment.MachineName);
        SetValue(identityDevice, registered ? shortId : "未注册");
        SetValue(identityRegistered, RegisteredAt() is { } when ? when.ToString("yyyy-MM-dd HH:mm") : "—");
        SetValue(identityRelay, RelayEndpoint() is { Length: > 0 } endpoint ? endpoint : "—");

        pair.Enabled = registered;
        pairHint.Text = registered ? "同一时间只允许一个配对请求" : "配置云服务器后才能配对";
        RefreshPaired();
        RefreshEvents();
    }

    // A missing value reads as a label ("未注册"), not an em dash: three long
    // dashes in a row looked like rules drawn under the captions. Real values
    // take Ink so the numbers carry the eye; placeholders drop to Dim.
    private static void SetMetric(Label label, string text, bool placeholder)
    {
        label.Text = text;
        label.ForeColor = placeholder ? DeckTheme.Dim : DeckTheme.Ink;
        label.Font = DeckTheme.MonoHero;
    }

    private static void SetValue(Label label, string text)
    {
        label.Text = text;
        label.ForeColor = text is "—" or "未注册" or "" ? DeckTheme.Dim : DeckTheme.Ink;
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
            var live = DeckTheme.Chip("已信任", DeckTheme.Good);
            live.Anchor = AnchorStyles.Right;
            live.Margin = new Padding(12, 1, 0, 0);
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

    private void RefreshEvents()
    {
        var signature = string.Join("|", connectionEvents.Take(4));
        if (signature == eventSignature) return;
        eventSignature = signature;
        eventList.SuspendLayout();
        eventList.Controls.Clear();
        if (connectionEvents.Count == 0)
        {
            var empty = DeckTheme.Label("暂无事件。注册、启动、配对都会记在这里。", DeckTheme.Small, DeckTheme.Dim);
            empty.AutoSize = true;
            empty.Margin = new Padding(0, 6, 0, 0);
            eventList.Controls.Add(empty);
        }
        foreach (var line in connectionEvents.Take(4))
        {
            var entry = DeckTheme.Label(line, DeckTheme.MonoSmall, DeckTheme.Dim);
            entry.AutoSize = true;
            entry.Margin = new Padding(0, 0, 0, 6);
            eventList.Controls.Add(entry);
        }
        eventList.ResumeLayout();
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
