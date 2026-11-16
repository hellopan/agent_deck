using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AgentDeck.Tray;

// 状态 page: the channel, this machine's identity and the phones that may talk
// to it. Everything here is read from disk or from the running process — the
// design's latency graph and "last reconnect" are deliberately absent because
// nothing measures them yet, and an invented number is worse than a gap.
internal sealed partial class ReviewWindow
{
    private readonly DeckTheme.StatusDot channelDot = DeckTheme.Dot(DeckTheme.Dim);
    private readonly DeckTheme.StatusDot railStatus = DeckTheme.Dot(DeckTheme.Dim, 7);
    private readonly Label channelTitle = DeckTheme.Label("未注册", DeckTheme.Hero);
    private readonly Label channelAddr = DeckTheme.Label("", DeckTheme.Monospace, DeckTheme.Dim);
    private readonly Label channelSince = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Dim);
    private readonly Label metricUptime = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Ink);
    private readonly Label metricDevice = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Ink);
    private readonly Label metricRegistered = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Ink);
    private readonly Label identityMachine = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label identityDevice = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label identityRegistered = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label identityRelay = DeckTheme.Label("—", DeckTheme.Monospace, DeckTheme.Muted);
    private readonly Label asidePending = DeckTheme.Label("0", DeckTheme.Monospace, DeckTheme.Muted);
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
    private string pairedSignature = "\0";
    private string eventSignature = "\0";

    private Control StatePage()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Canvas, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(ChannelCard(), 0, 0);
        body.Controls.Add(new Panel { BackColor = DeckTheme.Canvas }, 1, 0);
        body.Controls.Add(StateSide(), 2, 0);
        return Page("状态", "这台电脑的远程通道、身份与已配对设备。", body, StateAside());
    }

    private Control StateAside()
    {
        var aside = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, BackColor = DeckTheme.Canvas,
            Margin = Padding.Empty, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 4, 0, 0)
        };
        aside.Controls.Add(AsideItem("待处理", "0", asidePending));
        return aside;
    }

    private static Control AsideItem(string caption, string fallback, Label value)
    {
        var table = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(table, DeckTheme.Caption(caption), 16);
        value.Text = fallback;
        value.TextAlign = ContentAlignment.MiddleRight;
        DeckTheme.Row(table, value, 18);
        table.Width = 74;
        table.Margin = new Padding(0, 0, 0, 0);
        table.Dock = DockStyle.None;
        table.AutoSize = true;
        return table;
    }

    private Control ChannelCard()
    {
        var card = Card("通道", out var body);

        var hero = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hero.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        channelDot.Dock = DockStyle.None;
        channelDot.Anchor = AnchorStyles.Left;
        channelDot.Margin = new Padding(0, 2, 10, 0);
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
        channelTitle.Margin = new Padding(0, 0, 10, 0);
        titleLine.Controls.Add(channelTitle, 0, 0);
        channelAddr.AutoEllipsis = true;
        titleLine.Controls.Add(channelAddr, 1, 0);
        hero.Controls.Add(titleLine, 1, 0);
        DeckTheme.Row(body, hero, 36);
        DeckTheme.Row(body, channelSince, 24);

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        for (int i = 0; i < 3; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        metrics.Controls.Add(Metric("已运行", metricUptime), 0, 0);
        metrics.Controls.Add(Metric("设备 ID", metricDevice), 1, 0);
        metrics.Controls.Add(Metric("注册于", metricRegistered), 2, 0);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Line2, Height = 1 }, 1);
        DeckTheme.Row(body, metrics, 52);

        DeckTheme.Row(body, DeckTheme.Caption("三种状态的含义"), 26);
        DeckTheme.Row(body, Legend("已就绪", DeckTheme.Good, "Agent 正在运行且已注册，手机此刻可以提交任务"), 24);
        DeckTheme.Row(body, Legend("已停止", DeckTheme.Warning, "进程已退出。先重启托盘，再检查 Codex 是否可用"), 24);
        DeckTheme.Row(body, Legend("未注册", DeckTheme.Dim, "还没有 Relay 身份。先配置云服务器，手机才能连接"), 24);

        DeckTheme.Row(body, DeckTheme.Caption("最近连接事件"), 26);
        DeckTheme.Row(body, eventList, 70);
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);

        var actions = DeckTheme.Actions(DeckTheme.Surface);
        AddAction(actions, "配置云服务器", ConfigureRelayAsync, primary: true);
        DeckTheme.Row(body, actions, 38);
        return card;
    }

    private Control StateSide()
    {
        var side = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(side, IdentityCard(), 176);
        DeckTheme.Row(side, new Panel { BackColor = DeckTheme.Canvas }, 14);
        DeckTheme.Row(side, PairedCard(), 100, fill: true);
        return side;
    }

    private Control IdentityCard()
    {
        var card = Card("本机身份", out var body, note: "用于手机识别这台电脑");
        foreach (var (caption, value) in new[]
                 {
                     ("计算机名", identityMachine), ("设备 ID", identityDevice),
                     ("注册于", identityRegistered), ("Relay", identityRelay)
                 })
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
                ColumnCount = 2, RowCount = 1
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var key = DeckTheme.Label(caption, DeckTheme.Small, DeckTheme.Dim);
            row.Controls.Add(key, 0, 0);
            value.AutoEllipsis = true;
            row.Controls.Add(value, 1, 0);
            DeckTheme.Row(body, row, 27);
        }
        DeckTheme.Row(body, new Panel { BackColor = DeckTheme.Surface }, 100, fill: true);
        return card;
    }

    private Control PairedCard()
    {
        var card = Card("已配对手机", out var body, note: "撤销后立即断连");
        DeckTheme.Row(body, pairedList, 100, fill: true);
        var actions = DeckTheme.Actions(DeckTheme.Surface);
        pair = AddAction(actions, "显示配对二维码", PairPhoneAsync);
        DeckTheme.Row(body, actions, 46);
        return card;
    }

    private void RefreshStatePage()
    {
        bool registered = DeviceRegistered;
        bool running = agent is { HasExited: false };
        var id = DeviceIdText();
        var shortId = DeviceShort(id);
        var uptime = running ? DateTime.Now - agent!.StartTime : TimeSpan.Zero;

        hostName.Text = Environment.MachineName;
        hostId.Text = registered ? shortId : "未注册";
        statusDevice.Text = registered ? shortId : "—";
        statusService.Text = running ? "服务 · 运行中" : registered ? "服务 · 已停止" : "服务 · 未注册";
        statusService.ForeColor = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;
        railStatus.Color = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;
        topDot.Color = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;
        topState.Text = running ? "AGENT 运行中" : registered ? "AGENT 未运行" : "未注册";
        topState.ForeColor = running ? DeckTheme.Muted : registered ? DeckTheme.Warning : DeckTheme.Dim;

        channelDot.Color = running ? DeckTheme.Good : registered ? DeckTheme.Warning : DeckTheme.Dim;
        channelTitle.Text = running ? "已就绪" : registered ? "已停止" : "未注册";
        channelAddr.Text = RelayEndpoint() is { Length: > 0 } url ? url : "尚未配置 Relay";
        channelSince.Text = running
            ? $"Agent 进程已运行 {FormatSpan(uptime)} · 退出后手机立即断开"
            : registered ? "Agent 未运行。重启托盘后会重新连接。" : "先配置 Relay 地址与注册令牌。";

        metricUptime.Text = running ? FormatSpan(uptime) : "—";
        metricDevice.Text = registered ? shortId : "—";
        metricRegistered.Text = RegisteredAt() is { } at ? at.ToString("MM-dd HH:mm") : "—";

        identityMachine.Text = Environment.MachineName;
        identityDevice.Text = registered ? shortId : "未注册";
        identityRegistered.Text = RegisteredAt() is { } when ? when.ToString("yyyy-MM-dd HH:mm") : "—";
        identityRelay.Text = RelayEndpoint() is { Length: > 0 } endpoint ? endpoint : "—";

        pair.Enabled = registered;
        RefreshPaired();
        RefreshEvents();
    }

    private void RefreshPaired()
    {
        var phones = TrustedPhones().ToList();
        var signature = string.Join("|", phones.Select(p => p.Fingerprint));
        if (signature == pairedSignature) return;
        pairedSignature = signature;
        pairedList.SuspendLayout();
        pairedList.Controls.Clear();
        if (phones.Count == 0)
        {
            var empty = DeckTheme.Label("还没有配对的手机。点下方按钮生成配对码。",
                DeckTheme.Small, DeckTheme.Dim);
            empty.AutoSize = true;
            empty.Margin = new Padding(0, 6, 0, 0);
            pairedList.Controls.Add(empty);
        }
        else
        {
            foreach (var phone in phones)
            {
                var row = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
                    ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 6, 0, 4), Height = 46
                };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
                row.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
                var who = DeckTheme.Label(ShortFingerprint(phone.Fingerprint), DeckTheme.Monospace, DeckTheme.Ink);
                row.Controls.Add(who, 0, 0);
                var when = DeckTheme.Label($"配对于 {phone.At:MM-dd HH:mm}", DeckTheme.Small, DeckTheme.Dim);
                row.Controls.Add(when, 1, 0);
                var dot = DeckTheme.Dot(DeckTheme.Good);
                dot.Dock = DockStyle.None;
                dot.Anchor = AnchorStyles.Left;
                dot.Margin = new Padding(0, 0, 6, 0);
                var state = DeckTheme.Label("已信任", DeckTheme.Small, DeckTheme.Good);
                var footLine = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill, BackColor = DeckTheme.Surface, Margin = Padding.Empty,
                    ColumnCount = 3, RowCount = 1
                };
                footLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
                footLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                footLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                footLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                footLine.Controls.Add(dot, 0, 0);
                footLine.Controls.Add(state, 1, 0);
                var expiry = DeckTheme.Label(
                    phone.Expires is { } expires ? $"证书到期 {expires:yyyy-MM-dd}" : "", DeckTheme.Small, DeckTheme.Dim);
                expiry.TextAlign = ContentAlignment.MiddleRight;
                footLine.Controls.Add(expiry, 2, 0);
                row.SetColumnSpan(footLine, 2);
                row.Controls.Add(footLine, 0, 1);
                pairedList.Controls.Add(row);
            }
        }
        pairedList.ResumeLayout();
    }

    private void RefreshEvents()
    {
        var signature = string.Join("|", connectionEvents.Take(3));
        if (signature == eventSignature) return;
        eventSignature = signature;
        eventList.SuspendLayout();
        eventList.Controls.Clear();
        if (connectionEvents.Count == 0)
        {
            var empty = DeckTheme.Label("暂无事件。注册、启动、配对都会记在这里。",
                DeckTheme.Small, DeckTheme.Dim);
            empty.AutoSize = true;
            eventList.Controls.Add(empty);
        }
        foreach (var line in connectionEvents.Take(3))
        {
            var entry = DeckTheme.Label(line, DeckTheme.Monospace, DeckTheme.Dim);
            entry.AutoSize = true;
            entry.Margin = new Padding(0, 0, 0, 4);
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
