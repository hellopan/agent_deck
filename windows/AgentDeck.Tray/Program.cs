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
        menu.Items.Add("叫停运行中的任务", null, async (_, _) =>
        {
            ShowWindow();
            await window.OpenTasksAndCancelTaskAsync();
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
    private readonly CancellationTokenSource lifetime = new();
    private string inboxPath = "";
    private string projectsPath = "";
    private string codexPath = "";
    private Process? agent;

    internal ReviewWindow()
    {
        BuildWorkspace();
        LoadPaths();
        EnsureDefaults();
        RefreshStatePage();
        UpdateConnection();
        FormClosed += (_, _) =>
        {
            lifetime.Cancel();
            try { if (agent is { HasExited: false }) agent.Kill(); }
            catch (InvalidOperationException) { }
            agent?.Dispose();
        };
        Shown += async (_, _) =>
        {
            if (codexPath.Length == 0) await DetectToolsAsync(fill: true);
            try
            {
                if (!File.Exists(Path.Combine(DeviceDirectory, "registered-device")) || !File.Exists(AgentConfig)) return;
                using var config = JsonDocument.Parse(File.ReadAllText(AgentConfig));
                StartAgent(config.RootElement.GetProperty("devices").EnumerateObject().Single().Name);
                RecordEvent("Agent 已启动");
                status.Text = "本机 Agent 已启动。";
                await RefreshInboxAsync();
            }
            catch (Exception error) { SetStatus(error.Message, error: true); }
            finally { UpdateConnection(); }
        };
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDeck", "tray-settings.json");
    private static string DeviceDirectory => Path.GetDirectoryName(SettingsPath)!;
    private static string AgentConfig => Path.Combine(DeviceDirectory, "agent.local.json");
    private static string PairingDirectory => Path.Combine(DeviceDirectory, "pairing");

    private string InboxPath => inboxPath.Length > 0 ? inboxPath : Path.Combine(DeviceDirectory, "inbox");
    private string ProjectsPath => projectsPath.Length > 0 ? projectsPath : Path.Combine(DeviceDirectory, "projects.local.json");
    private string LogsPath => logs.Text.Trim() is { Length: > 0 } text ? text : Path.Combine(DeviceDirectory, "logs");

    private void EnsureDefaults()
    {
        if (inboxPath.Length == 0) inboxPath = Path.Combine(DeviceDirectory, "inbox");
        if (projectsPath.Length == 0) projectsPath = Path.Combine(DeviceDirectory, "projects.local.json");
        if (logs.TextLength == 0) logs.Text = Path.Combine(DeviceDirectory, "logs");
    }

    private void SetStatus(string message, bool error = false)
    {
        status.Text = message;
        status.ForeColor = error ? DeckTheme.Danger : DeckTheme.Dim;
        flashTimer.Stop();
        flashTimer.Start();
    }

    private async Task ConfigureRelayAsync()
    {
        using var dialog = new RelayWindow();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            status.Text = "已取消配置。";
            return;
        }
        SetStatus("正在向 Relay 注册电脑…");
        Directory.CreateDirectory(DeviceDirectory);
        var id = (await RunWithInputAsync(dialog.Token, "enroll", "-config", AgentConfig,
            "-relay-url", dialog.Endpoint, "-fingerprint", dialog.Fingerprint)).Trim();
        File.WriteAllText(Path.Combine(DeviceDirectory, "registered-device"), id);
        StartAgent(id);
        RecordEvent("电脑已注册到 Relay");
        SetStatus("电脑已注册，正在连接 Relay。");
        UpdateConnection();
    }

    private void StartAgent(string id)
    {
        if (agent is { HasExited: false }) return;
        Directory.CreateDirectory(PairingDirectory);
        var start = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "agent", "-config", AgentConfig, "-device", id, "-pairing-dir", PairingDirectory };
        Directory.CreateDirectory(RequirePath(InboxPath, "任务收件箱"));
        Directory.CreateDirectory(RequirePath(LogsPath, "任务日志目录"));
        SavePaths();
        args.AddRange(["-projects", RequirePath(ProjectsPath, "项目授权清单"),
            "-task-inbox", RequirePath(InboxPath, "任务收件箱"), "-task-logs", RequirePath(LogsPath, "任务日志目录")]);
        // Detection already found the full Codex build, so pass the path it
        // chose. Leaving the flag out would make the Agent search again and could
        // land on the stripped copy that never raises approvals.
        if (codexPath.Trim().Length > 0) args.AddRange(["-codex", RequirePath(codexPath, "Codex 可执行文件")]);
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
        {
            RecordEvent("手机配对成功");
            pairedSignature = "";
            RefreshStatePage();
            SetStatus("手机已配对。请在手机刷新项目。");
        }
    }

    private void LoadPaths()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            var saved = JsonSerializer.Deserialize<TrayPaths>(File.ReadAllText(SettingsPath), Json)
                ?? throw new InvalidDataException("路径配置为空。");
            inboxPath = saved.Inbox;
            projectsPath = saved.Projects;
            logs.Text = saved.Logs;
            codexPath = saved.Codex;
            status.Text = "已加载本机配置。";
        }
        catch (Exception error)
        {
            SetStatus($"读取配置失败。{error.Message}", error: true);
        }
    }

    private void SavePaths()
    {
        if (logs.Text.Trim().Length > 0) RequirePath(LogsPath, "任务日志目录");
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"tray-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new TrayPaths(inboxPath, projectsPath, logs.Text.Trim(), codexPath.Trim()), Json));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        status.Text = "设置已保存。";
    }

    private void UpdateConnection()
    {
        RefreshStatePage();
        LoadTrace(force: false);
    }

    internal Task OpenTasksAndCancelTaskAsync()
    {
        SelectPage(1);
        var running = queueList.Controls.OfType<QueueRow>()
            .FirstOrDefault(row => row.Item.Status is "running" or "queued");
        if (running is null)
        {
            SetStatus("目前没有运行中的任务。");
            return Task.CompletedTask;
        }
        SelectTask(running);
        return RunActionAsync(CancelAsync);
    }

    private Button AddAction(FlowLayoutPanel row, string label, Func<Task> action,
        bool primary = false, bool danger = false)
    {
        var button = DeckTheme.Button(label, primary, danger);
        button.Click += async (_, _) => await RunActionAsync(action);
        row.Controls.Add(button);
        return button;
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        content.Enabled = false;
        SetStatus("正在处理…");
        try
        {
            await action();
            if (!IsDisposed && status.Text == "正在处理…") SetStatus("操作完成。");
        }
        catch (Exception error)
        {
            if (!IsDisposed) SetStatus(error.Message, error: true);
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

    private static string RequirePath(string path, string name)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"「{name}」需要本机绝对路径。");
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
    private sealed record TrayPaths(string Inbox, string Projects, string Logs, string Codex);
}
