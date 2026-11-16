using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentDeck.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, @"Local\AgentDeck.Tray", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("AgentDeck 已运行，请从系统托盘打开。");
            return;
        }
        try { Install(); }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "AgentDeck 安装失败");
            return;
        }
        using var window = new ReviewWindow();
        var exiting = false;
        using var menu = new ContextMenuStrip();
        using var tray = new NotifyIcon
        {
            Icon = window.Icon, Text = "AgentDeck", ContextMenuStrip = menu, Visible = true
        };
        void ShowWindow()
        {
            window.Show();
            window.WindowState = FormWindowState.Normal;
            window.Activate();
        }
        window.FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing || exiting)
                return;
            e.Cancel = true;
            window.Hide();
        };
        tray.DoubleClick += (_, _) => ShowWindow();
        menu.Items.Add("打开 AgentDeck", null, (_, _) => ShowWindow());
        menu.Items.Add("取消运行任务", null, async (_, _) =>
        {
            ShowWindow();
            await window.OpenLogsAndCancelTaskAsync();
        });
        menu.Items.Add("退出托盘", null, (_, _) =>
        {
            exiting = true;
            window.Close();
        });
        Application.Run(window);
    }

    internal static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDeck", "app");

    private static void Install()
    {
        using var resource = typeof(Program).Assembly.GetManifestResourceStream("AgentDeck.Backend.exe");
        if (resource is null) return; // Development build with a side-by-side backend.
        Directory.CreateDirectory(InstallDirectory);
        var backend = Path.Combine(InstallDirectory, "agentdeck-task-lab.exe");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var data = memory.ToArray();
        WriteIfChanged(backend, data);
        var current = Environment.ProcessPath!;
        var installed = Path.Combine(InstallDirectory, "AgentDeck.exe");
        if (!string.Equals(current, installed, StringComparison.OrdinalIgnoreCase))
            WriteIfChanged(installed, File.ReadAllBytes(current));
    }

    private static void WriteIfChanged(string path, byte[] data)
    {
        if (File.Exists(path) && data.AsSpan().SequenceEqual(File.ReadAllBytes(path))) return;
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(staging, data);
            File.Move(staging, path, overwrite: true);
        }
        finally { File.Delete(staging); }
    }
}

internal sealed partial class ReviewWindow : Form
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TextBox inbox = new();
    private readonly TextBox projects = new();
    private readonly TextBox logs = new();
    private readonly TextBox codex = new();
    private readonly TextBox home = new();
    private readonly Label codexState = DeckTheme.Label("未检测", DeckTheme.Small, DeckTheme.Muted);
    private readonly ListBox pending = new() { Dock = DockStyle.Fill, AccessibleName = "任务队列" };
    private readonly TextBox detail = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        AccessibleName = "任务详情和运行输出"
    };
    private readonly CancellationTokenSource lifetime = new();
    private Process? agent;

    internal ReviewWindow()
    {
        BuildWorkspace();
        FormClosed += (_, _) =>
        {
            lifetime.Cancel();
            try { if (agent is { HasExited: false }) agent.Kill(); }
            catch (InvalidOperationException) { }
            agent?.Dispose();
        };
        LoadPaths();
        UpdateConnection();
        Shown += async (_, _) =>
        {
            if (codex.Text.Trim().Length == 0) await DetectToolsAsync(true);
            try
            {
                if (!File.Exists(Path.Combine(DeviceDirectory, "registered-device")) || !File.Exists(AgentConfig)) return;
                using var config = JsonDocument.Parse(File.ReadAllText(AgentConfig));
                StartAgent(config.RootElement.GetProperty("devices").EnumerateObject().Single().Name);
                status.Text = "本机 Agent 已启动。";
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { UpdateConnection(); }
        };
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDeck", "tray-settings.json");
    private static string DeviceDirectory => Path.GetDirectoryName(SettingsPath)!;
    private static string AgentConfig => Path.Combine(DeviceDirectory, "agent.local.json");
    private static string PairingDirectory => Path.Combine(DeviceDirectory, "pairing");

    private async Task ConfigureRelayAsync()
    {
        using var dialog = new RelayWindow();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            status.Text = "已取消配置。";
            return;
        }
        status.Text = "正在向 Relay 注册电脑…";
        Directory.CreateDirectory(DeviceDirectory);
        var id = (await RunWithInputAsync(dialog.Token, "enroll", "-config", AgentConfig,
            "-relay-url", dialog.Endpoint, "-fingerprint", dialog.Fingerprint)).Trim();
        File.WriteAllText(Path.Combine(DeviceDirectory, "registered-device"), id);
        StartAgent(id);
        status.Text = "电脑已注册，正在连接 Relay。";
    }

    private void StartAgent(string id)
    {
        if (agent is { HasExited: false }) return;
        Directory.CreateDirectory(PairingDirectory);
        var start = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "agent", "-config", AgentConfig, "-device", id, "-pairing-dir", PairingDirectory };
        var workspace = Path.Combine(DeviceDirectory, "projects.local.json");
        var taskInbox = Path.Combine(DeviceDirectory, "inbox");
        var taskLogs = Path.Combine(DeviceDirectory, "logs");
        if (projects.Text.Length == 0) projects.Text = workspace;
        if (inbox.Text.Length == 0) inbox.Text = taskInbox;
        if (logs.Text.Length == 0) logs.Text = taskLogs;
        Directory.CreateDirectory(RequirePath(inbox));
        Directory.CreateDirectory(RequirePath(logs));
        SavePaths();
        args.AddRange(["-projects", RequirePath(projects), "-task-inbox", RequirePath(inbox), "-task-logs", RequirePath(logs)]);
        // These two were collected on the settings page but never passed on, so
        // the Agent silently ignored whatever the user picked. Passing them only
        // when filled leaves detection in charge of an empty field.
        if (codex.Text.Trim().Length > 0) args.AddRange(["-codex", RequirePath(codex)]);
        if (home.Text.Trim().Length > 0) args.AddRange(["-codex-home", RequirePath(home)]);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        agent?.Dispose();
        agent = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Agent。");
    }

    private async Task PairPhoneAsync()
    {
        if (!File.Exists(Path.Combine(DeviceDirectory, "registered-device")) || !File.Exists(AgentConfig))
            throw new InvalidOperationException("请先点击「配置云服务器」。");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(AgentConfig));
        var device = config.RootElement.GetProperty("devices").EnumerateObject().Single().Name;
        StartAgent(device);
        var directory = PairingDirectory;
        var raw = await RunAsync("pair-invite", "-config", AgentConfig,
            "-pairing-dir", directory, "-device", device);
        using var data = JsonDocument.Parse(raw);
        var code = data.RootElement.GetProperty("code").GetString()!;
        using var dialog = new PairingWindow(raw.Trim(),
            () => RunAsync("pair-candidate", "-pairing-dir", directory, "-device", device, "-code", code),
            fingerprint => RunAsync("pair-confirm", "-pairing-dir", directory, "-device", device, "-code", code,
                "-fingerprint", fingerprint));
        if (dialog.ShowDialog(this) == DialogResult.OK)
            status.Text = "手机已配对。请在手机刷新项目。";
    }

    /// <summary>
    /// The Codex path row with detection built in. The status sits on the title
    /// line and the detect button shares the input line, so detection costs no
    /// vertical space and the save button stays visible at the minimum size.
    /// </summary>
    private Control CodexPathField()
    {
        var row = DeckTheme.Table();
        var header = DeckTheme.Table();
        header.ColumnCount = 2;
        header.RowCount = 1;
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(DeckTheme.Label("Codex 可执行文件"), 0, 0);
        codexState.TextAlign = ContentAlignment.MiddleRight;
        codexState.AutoEllipsis = true;
        codexState.Margin = new Padding(12, 0, 0, 0);
        header.Controls.Add(codexState, 1, 0);
        DeckTheme.Row(row, header, 26);

        var line = DeckTheme.Table();
        line.ColumnCount = 3;
        line.RowCount = 1;
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        line.Controls.Add(DeckTheme.Input(codex, "Codex 可执行文件"), 0, 0);
        var browse = DeckTheme.Button("浏览…");
        browse.AccessibleName = "浏览 Codex 可执行文件";
        browse.Margin = new Padding(8, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "Codex 可执行文件", Filter = "可执行文件 (*.exe)|*.exe" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            codex.Text = dialog.FileName;
            codexState.Text = "已手填，点「检测」核对";
            codexState.ForeColor = DeckTheme.Muted;
        };
        line.Controls.Add(browse, 1, 0);
        var detect = DeckTheme.Button("检测");
        detect.AccessibleName = "自动检测本机 Codex";
        detect.Margin = new Padding(6, 0, 0, 0);
        detect.Click += async (_, _) => await DetectToolsAsync(true);
        line.Controls.Add(detect, 2, 0);
        DeckTheme.Row(row, line, 37);
        return row;
    }

    /// <summary>
    /// Fills the Codex path from what is really installed and reports the other
    /// editors. Detection never overwrites a path the user typed by hand, and a
    /// "found but unusable" result is a warning, not a success.
    /// </summary>
    private async Task DetectToolsAsync(bool fill)
    {
        try
        {
            using var data = JsonDocument.Parse(await RunAsync("detect"));
            var found = new List<string>();
            var usable = false;
            var problem = "没有找到可用的 Codex。";
            foreach (var tool in data.RootElement.EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString() ?? "";
                var path = tool.TryGetProperty("path", out var rawPath) ? rawPath.GetString() ?? "" : "";
                var ok = tool.GetProperty("usable").GetBoolean();
                var issue = tool.TryGetProperty("problem", out var rawProblem) ? rawProblem.GetString() ?? "" : "";
                if (name == "codex")
                {
                    usable = ok;
                    if (!ok) problem = issue.Length > 0 ? issue : problem;
                    if (ok && fill && codex.Text.Trim().Length == 0) codex.Text = path;
                    continue;
                }
                if (ok) found.Add(name == "cursor" ? "Cursor" : "WorkBuddy");
            }
            var others = found.Count > 0 ? $"，同时找到 {string.Join("、", found)}" : "";
            // The title row only has room for a verdict; the reason and the
            // other editors go to the status bar, which has the full width.
            codexState.Text = usable ? "✓ 可用" : "⚠ 不可用";
            codexState.ForeColor = usable ? DeckTheme.Good : DeckTheme.Warning;
            status.Text = usable
                ? $"已检测到可用的 Codex{others}。"
                : $"未找到可用的 Codex：{problem}";
        }
        catch (Exception error) { status.Text = $"检测失败。{error.Message}"; }
    }

    private void LoadPaths()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            var saved = JsonSerializer.Deserialize<TrayPaths>(File.ReadAllText(SettingsPath), Json)
                ?? throw new InvalidDataException("路径配置为空。");
            inbox.Text = saved.Inbox;
            projects.Text = saved.Projects;
            logs.Text = saved.Logs;
            codex.Text = saved.Codex;
            home.Text = saved.Home;
            status.Text = "已加载本机路径。";
        }
        catch (Exception error)
        {
            status.Text = $"读取路径配置失败。{error.Message}";
        }
    }

    private void SavePaths()
    {
        foreach (var field in new[] { inbox, projects, logs, codex, home })
            if (!string.IsNullOrWhiteSpace(field.Text))
                RequirePath(field);
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"tray-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new TrayPaths(inbox.Text.Trim(), projects.Text.Trim(), logs.Text.Trim(), codex.Text.Trim(), home.Text.Trim()), Json));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        status.Text = "路径已保存。";
    }

    private async Task ViewLogAsync()
    {
        var path = RequirePath(logs);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("任务日志目录不存在。");
        using var picker = new OpenFileDialog
        {
            Title = "选择任务日志", InitialDirectory = path, Filter = "任务日志 (*.log)|*.log",
            CheckFileExists = true, RestoreDirectory = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            status.Text = "已取消。";
            return;
        }
        var selected = Path.GetFullPath(picker.FileName);
        if (!string.Equals(Path.GetDirectoryName(selected), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(selected) is not { Length: 32 } id ||
            !id.All(Uri.IsHexDigit) ||
            (File.GetAttributes(selected) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("请选择日志目录中的任务日志。");

        const int limit = 256 * 1024;
        await using var file = new FileStream(selected, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        var start = Math.Max(0, file.Length - limit);
        file.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[limit];
        int length = await file.ReadAtLeastAsync(bytes, bytes.Length, false, lifetime.Token);
        if (IsDisposed)
            return;
        int end = bytes.AsSpan(0, length).LastIndexOf((byte)'\n') + 1;
        if (start > 0)
        {
            int offset = Array.IndexOf(bytes, (byte)'\n', 0, length) + 1;
            logOutput.Text = "仅显示日志末尾的完整行。\r\n" +
                (offset == 0 ? "" : Encoding.UTF8.GetString(bytes, offset, end - offset));
        }
        else
        {
            logOutput.Text = Encoding.UTF8.GetString(bytes, 0, end);
        }
        logTitle.Text = $"任务 {id}";
        status.Text = "已打开任务日志。";
    }

    private async Task CancelTaskAsync()
    {
        try
        {
            var directory = RequirePath(logs);
            using var picker = new OpenFileDialog
            {
                Title = "选择要取消的任务日志", InitialDirectory = directory,
                Filter = "任务日志 (*.log)|*.log", RestoreDirectory = true
            };
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            var id = Path.GetFileNameWithoutExtension(picker.FileName);
            if (!string.Equals(Path.GetDirectoryName(picker.FileName),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase) ||
                id.Length != 32 || !id.All(Uri.IsHexDigit))
                throw new InvalidDataException("请选择已配置日志目录中的任务日志。");
            if (MessageBox.Show(this, $"向任务 {id} 发送取消请求？", "取消任务",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            await RunAsync("cancel-task", "-task-logs", directory, "-task-id", id);
            if (!IsDisposed)
                status.Text = "取消请求已记录。最终状态请查看任务日志。";
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                status.Text = error.Message;
        }
    }

    private Control PathField(string label, TextBox field, bool folder)
    {
        var row = DeckTheme.Table();
        row.ColumnCount = 2;
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = DeckTheme.Label(label);
        DeckTheme.Row(row, title, 26);
        row.SetColumnSpan(title, 2);
        var input = DeckTheme.Input(field, label);
        DeckTheme.Row(row, input, 37);
        var browse = DeckTheme.Button("浏览…");
        browse.AccessibleName = $"浏览{label}";
        browse.Margin = new Padding(8, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            if (folder)
            {
                using var dialog = new FolderBrowserDialog { Description = label };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    field.Text = dialog.SelectedPath;
            }
            else
            {
                using var dialog = new OpenFileDialog
                {
                    Title = label,
                    CheckFileExists = field != projects,
                    Filter = field == codex ? "可执行文件 (*.exe)|*.exe" : "JSON 文件 (*.json)|*.json"
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    field.Text = dialog.FileName;
            }
        };
        row.Controls.Add(browse, 1, 1);
        DeckTheme.Row(row, new Panel(), 100, fill: true);
        return row;
    }

    private async Task RefreshInboxAsync()
    {
        var output = await RunAsync("inbox", "-task-inbox", RequirePath(inbox),
            "-projects", RequirePath(projects), "-task-logs", RequirePath(logs));
        var items = JsonSerializer.Deserialize<QueueView>(output, Json)?.Items ?? [];
        var selectedID = (pending.SelectedItem as QueueItem)?.TaskId;
        pending.Items.Clear();
        pending.Items.AddRange(items);
        var previousIndex = Array.FindIndex(items, item => item.TaskId == selectedID);
        if (items.Length > 0)
            pending.SelectedIndex = Math.Max(0, previousIndex);
        var active = items.Count(item => item.Status is "queued" or "running");
        inboxCount.Text = items.Length == 0 ? "队列为空" : $"队列 · {active} 条未结束";
        status.Text = items.Length == 0 ? "队列为空。" : $"已加载 {items.Length} 条任务。";
    }

    private QueueItem Selected() => pending.SelectedItem as QueueItem
        ?? throw new InvalidOperationException("请先选择一条任务。");

    private static string FormatPreview(QueueItem item)
    {
        var submitted = item.SubmittedAt.ToLocalTime().ToString("MM-dd HH:mm");
        return $"项目 {item.ProjectId}\r\n任务 {item.TaskId}\r\n状态 {item.Status}    提交 {submitted}\r\n\r\n{item.Prompt}";
    }

    // 审批只在手机上完成。这里能做的是叫停，以及查看任务将要做什么。
    private async Task CancelAsync()
    {
        var item = Selected();
        detail.Text = FormatPreview(item);
        if (MessageBox.Show(this, $"确认叫停这个任务？\r\n\r\n{detail.Text}",
                "确认叫停", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            status.Text = "已取消。";
            return;
        }
        await RunAsync("cancel-task", "-task-inbox", RequirePath(inbox),
            "-projects", RequirePath(projects), "-project-id", item.ProjectId,
            "-task-id", item.TaskId, "-task-logs", RequirePath(logs));
        status.Text = "已请求叫停，等待 Agent 收尾。";
        await RefreshInboxAsync();
    }

    private static string RequirePath(TextBox field)
    {
        var path = field.Text.Trim();
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"请为「{field.AccessibleName}」填写本机绝对路径。");
        return path;
    }

    private static string ExecutablePath => Path.Combine(
        typeof(Program).Assembly.GetManifestResourceInfo("AgentDeck.Backend.exe") is null
            ? AppContext.BaseDirectory : Program.InstallDirectory, "agentdeck-task-lab.exe");

    private Task<string> RunAsync(params string[] args) => RunWithInputAsync(null, args);

    private async Task<string> RunWithInputAsync(string? input, params string[] args)
    {
        var executable = ExecutablePath;
        if (!File.Exists(executable))
            throw new FileNotFoundException("请将 agentdeck-task-lab.exe 放在托盘程序同一目录。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 AgentDeck 命令。");
        if (input is not null)
        {
            await process.StandardInput.WriteLineAsync(input);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException((await errors).Trim() is { Length: > 0 } text ? text : "AgentDeck 命令失败。");
        return await output;
    }

    private sealed record QueueItem(string TaskId, string ProjectId, string Directory, string Status,
        DateTimeOffset SubmittedAt, string Summary, string Prompt)
    {
        public override string ToString() => $"{ProjectId}    {Status}    {Summary}";
    }

    private sealed record QueueView(QueueItem[] Items);
    private sealed record TrayPaths(string Inbox, string Projects, string Logs, string Codex, string Home);
}
