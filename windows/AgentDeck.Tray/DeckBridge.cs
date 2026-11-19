using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QRCoder;

namespace AgentDeck.Tray;

// The data half of the WebView shell. The page owns pixels; this owns facts.
//
// Everything here reads the same files and runs the same backend commands the
// WinForms window used, so the two shells cannot drift: projects.local.json is
// still the authorisation list the Agent reads, the log directory is still the
// one passed to -task-logs, and a phone is trusted only if its pinned file is
// on disk.
//
// Commands are cheap on purpose. `state` touches disk only and answers in a few
// milliseconds, because it runs on every page switch; anything that has to
// spawn a child process (`tasks`, `engines`) is a separate request the page
// sends once it has painted.
internal sealed class DeckBridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDeck");

    private string SettingsPath => Path.Combine(root, "tray-settings.json");
    private string DeviceDirectory => root;
    private string AgentConfig => Path.Combine(root, "agent.local.json");
    private string PairingDirectory => Path.Combine(root, "pairing");
    private string RegisteredPath => Path.Combine(root, "registered-device");

    private string inbox = "";
    private string projects = "";
    private string logs = "";
    private string codex = "";
    private bool pathsLoaded;

    /// <summary>
    /// The shell window. Every dialog opened from here has to be owned by it:
    /// an ownerless dialog is a separate top-level window, so the shell paints
    /// over it the moment it is activated — the QR code appeared for a frame
    /// and then vanished behind the window that had just opened it. Ownership
    /// also keeps the dialog centred on the shell instead of the screen.
    /// </summary>
    internal Form? Owner { get; set; }

    private DialogResult ShowOwned(Form dialog)
    {
        var owner = Owner;
        if (owner is null || owner.IsDisposed || !owner.Visible) return dialog.ShowDialog();
        // An owned window still loses to a topmost owner, because TopMost is a
        // separate z-order band, not a hint. The shell raises itself while it
        // is being screenshotted, so the dialog has to rise with it.
        if (owner.TopMost) dialog.TopMost = true;
        return dialog.ShowDialog(owner);
    }

    private DialogResult Ask(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        var owner = Owner;
        return owner is null || owner.IsDisposed || !owner.Visible
            ? MessageBox.Show(text, caption, buttons, icon, defaultButton)
            : MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
    }

    internal DeckBridge()
    {
        LoadPaths();
    }

    // ---------------------------------------------------------------- paths

    private void LoadPaths()
    {
        if (pathsLoaded) return;
        pathsLoaded = true;
        inbox = Path.Combine(root, "inbox");
        projects = Path.Combine(root, "projects.local.json");
        logs = Path.Combine(root, "logs");
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var saved = JsonSerializer.Deserialize<TrayPaths>(File.ReadAllText(SettingsPath), Json);
            if (saved is null) return;
            if (!string.IsNullOrWhiteSpace(saved.Inbox)) inbox = saved.Inbox;
            if (!string.IsNullOrWhiteSpace(saved.Projects)) projects = saved.Projects;
            if (!string.IsNullOrWhiteSpace(saved.Logs)) logs = saved.Logs;
            if (!string.IsNullOrWhiteSpace(saved.Codex)) codex = saved.Codex;
        }
        catch (Exception error) when (error is IOException or JsonException) { }
    }

    private static string BackendPath => Path.Combine(
        typeof(DeckBridge).Assembly.GetManifestResourceInfo("AgentDeck.Backend.exe") is null
            ? AppContext.BaseDirectory : Program.InstallDirectory, "agentdeck-task-lab.exe");

    private Task<string> RunAsync(params string[] args) => RunWithInputAsync(null, args);

    private async Task<string> RunWithInputAsync(string? input, params string[] args)
    {
        var executable = BackendPath;
        if (!File.Exists(executable))
            throw new FileNotFoundException("请将 agentdeck-task-lab.exe 放在托盘程序同一目录。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = input is not null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 AgentDeck 命令。");
        if (input is not null)
        {
            await process.StandardInput.WriteLineAsync(input);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException((await errors).Trim() is { Length: > 0 } text ? text : "AgentDeck 命令失败。");
        return await output;
    }

    // ---------------------------------------------------------------- state

    /// <summary>Fast snapshot: disk only, no child processes.</summary>
    internal string State()
    {
        var service = ServiceControl.Query();
        var registered = File.Exists(RegisteredPath) && File.Exists(AgentConfig);
        var id = DeviceIdText();
        var phones = TrustedPhones();
        var (total, enabled) = PolicyCounts();
        var relay = RelayEndpoint();
        var running = service.Running || AgentRunning;

        var channelOk = running;
        var projectOk = enabled > 0;
        var pairOk = phones.Count > 0;

        JsonObject Ready(string key, bool ok, string value, string sub) => new()
        {
            ["key"] = key, ["ok"] = ok, ["value"] = value, ["sub"] = sub
        };

        var ready = new JsonArray
        {
            Ready("中枢通道", channelOk,
                running ? "已连通" : registered ? "已停止" : "未注册",
                running
                    ? $"{(relay.Length > 0 ? relay : "尚未配置 Relay")} · {(service.Running ? "由 Windows 服务托管" : "托盘进程托管")}"
                    : registered ? "Agent 未运行，手机此刻连不上。重启托盘或启动服务。"
                    : "先配置 Relay 地址与注册令牌，手机才能连接。"),
            Ready("引擎", EngineOk, EngineValue, EngineSub),
            Ready("项目", projectOk,
                $"{total} 个目录 · {enabled} 个启用 · {total - enabled} 个已停用",
                projectOk ? "手机只能看到启用中的项目；越界写入会被拒绝并记录。"
                    : "还没有启用任何项目，手机提交不了任务。去「项目」页添加一个目录。"),
            Ready("配对", pairOk,
                phones.Count == 0 ? "没有已授权的手机" : $"{phones.Count} 台手机已授权",
                phones.Count == 0 ? "点「显示配对二维码」，用手机扫码后在本机确认。"
                    : $"{phones[0].Name} · 指纹 {phones[0].Fingerprint} · {phones[0].AtText}配对 · 撤销后立即断连")
        };

        var pass = new[] { channelOk, EngineOk, projectOk, pairOk }.Count(ok => ok);

        var phonesJson = new JsonArray();
        foreach (var phone in phones)
            phonesJson.Add(new JsonObject
            {
                ["name"] = phone.Name, ["fingerprint"] = phone.Fingerprint,
                ["at"] = phone.AtText, ["expires"] = phone.Expires
            });

        var projectJson = new JsonArray();
        foreach (var entry in Projects())
            projectJson.Add(new JsonObject
            {
                ["id"] = entry.Id, ["directory"] = entry.Directory,
                ["mode"] = entry.Mode, ["enabled"] = entry.Enabled,
                ["engine"] = entry.Engine
            });

        var snapshot = new JsonObject
        {
            ["machine"] = Environment.MachineName,
            ["device"] = registered ? DeviceShort(id) : "",
            ["deviceFull"] = id,
            ["registered"] = registered,
            ["running"] = running,
            ["state"] = running ? "AGENT 运行中" : registered ? "AGENT 未运行" : "未注册",
            ["relay"] = relay,
            ["uptime"] = service.Running ? "服务托管" : running ? FormatSpan(Uptime()) : "",
            ["service"] = new JsonObject
            {
                ["installed"] = service.Installed,
                ["running"] = service.Running,
                ["mode"] = service.Installed
                    ? service.Running ? "Windows 服务 · 自动启动" : "Windows 服务 · 已安装，未运行"
                    : "托盘进程 · 登录后启动"
            },
            ["ready"] = new JsonObject
            {
                ["pass"] = pass, ["pending"] = 4 - pass, ["rows"] = ready
            },
            ["phones"] = phonesJson,
            ["projects"] = projectJson,
            ["engines"] = cachedEngines.DeepClone(),
            ["paths"] = new JsonObject
            {
                ["inbox"] = inbox, ["projects"] = projects, ["logs"] = logs, ["codex"] = codex
            },
            ["certDays"] = CertDays(),
            ["version"] = Version(),
            ["update"] = new JsonObject
            {
                ["available"] = UpdateManager.Cached is not null,
                ["version"] = UpdateManager.Cached?.Version ?? ""
            }
        };
        return snapshot.ToJsonString(Pretty);
    }

    private static bool AgentRunning
    {
        get
        {
            try
            {
                return Process.GetProcessesByName("agentdeck-task-lab").Length > 0;
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    private static TimeSpan Uptime()
    {
        try
        {
            var process = Process.GetProcessesByName("agentdeck-task-lab")
                .OrderBy(p => p.StartTime).FirstOrDefault();
            return process is null ? TimeSpan.Zero : DateTime.Now - process.StartTime;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private static string Version() => typeof(DeckBridge).Assembly
        .GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "—";

    // --------------------------------------------------------------- engines

    private static JsonArray cachedEngines = new();
    private static DateTime enginesAt;

    private static JsonArray Engines() => cachedEngines;

    internal async Task<string> RefreshEnginesAsync()
    {
        try
        {
            using var data = JsonDocument.Parse(await RunAsync("detect"));
            var array = new JsonArray();
            foreach (var tool in data.RootElement.EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString() ?? "";
                var path = tool.TryGetProperty("path", out var rawPath) ? rawPath.GetString() ?? "" : "";
                var version = tool.TryGetProperty("version", out var rawVersion) ? rawVersion.GetString() ?? "" : "";
                var usable = tool.GetProperty("usable").GetBoolean();
                var issue = tool.TryGetProperty("problem", out var rawProblem) ? rawProblem.GetString() ?? "" : "";
                array.Add(new JsonObject
                {
                    ["id"] = name,
                    ["name"] = name switch { "codex" => "Codex", "cursor" => "Cursor", _ => "WorkBuddy" },
                    ["version"] = version, ["path"] = path, ["ok"] = usable,
                    ["note"] = usable ? "" : path.Length > 0 ? issue : "未安装"
                });
                if (name == "codex" && usable && version.Length > 0)
                {
                    codexVersion = version;
                    if (codex.Length == 0) codex = path;
                }
            }
            cachedEngines = array;
            enginesAt = DateTime.UtcNow;
            return EnginesJson();
        }
        catch (Exception error)
        {
            return new JsonObject { ["error"] = error.Message }.ToJsonString();
        }
    }

    internal string EnginesJson() => new JsonObject { ["engines"] = cachedEngines.DeepClone() }.ToJsonString(Pretty);

    // EngineProblem returns why an engine cannot run tasks, or null when it is
    // ready. Detection is cached, so this is a cheap guard at save time. An
    // engine that was never detected is treated as not ready rather than
    // silently allowed through.
    private static string? EngineProblem(string id)
    {
        var detected = false;
        foreach (var node in cachedEngines)
        {
            if (node is not JsonObject engine) continue;
            if (engine["id"]?.GetValue<string>() != id) continue;
            detected = true;
            var ok = engine["ok"]?.GetValue<bool>() ?? false;
            if (ok) return null;
            var note = engine["note"]?.GetValue<string>() ?? "";
            return string.IsNullOrEmpty(note) ? "不可用" : note;
        }
        // Detection has not finished yet (warm-up races the first save). Do not
        // block on an unknown — the Agent re-checks the engine at task time.
        return detected ? "不可用" : null;
    }

    private static string ToEngineName(string id) => id switch
    {
        "codex" => "Codex", "cursor" => "Cursor", "workbuddy" => "WorkBuddy", _ => id
    };

    private string codexVersion = "";
    private bool EngineOk => codexVersion.Length > 0;
    private string EngineValue => codexVersion.Length > 0 ? $"Codex {codexVersion} 就绪" : "正在检测…";
    private string EngineSub => "只探测本机安装与登录状态；未就绪的引擎不会出现在手机端的工具列表里。";

    // ---------------------------------------------------------------- tasks

    internal async Task<string> TasksAsync()
    {
        try
        {
            var output = await RunAsync("inbox", "-task-inbox", inbox, "-projects", projects, "-task-logs", logs);
            var items = (JsonSerializer.Deserialize<QueueView>(output, Json)?.Items ?? [])
                .OrderByDescending(item => item.SubmittedAt).ToArray();

            var now = DateTimeOffset.Now;
            var running = new JsonArray();
            var done = new JsonArray();
            JsonObject? pending = null;

            var runningItems = items.Where(item => item.Status == "running").ToArray();
            var queued = items.Where(item => item.Status == "queued").ToArray();
            foreach (var item in runningItems)
            {
                var signal = ReadSignal(item.TaskId);
                running.Add(new JsonObject
                {
                    ["taskId"] = item.TaskId, ["projectId"] = item.ProjectId,
                    ["title"] = Title(item), ["elapsed"] = Elapsed(signal.Start, now),
                    ["last"] = LastEvent(signal), ["status"] = item.Status
                });
                if (signal.Pending is { } approval && pending is null)
                    pending = new JsonObject
                    {
                        ["projectId"] = item.ProjectId, ["text"] = approval.Text,
                        ["waited"] = Elapsed(approval.At, now),
                        ["scope"] = "逐次批准 · 仅此目录",
                        ["waiting"] = FirstPhoneName()
                    };
            }
            foreach (var item in items.Where(item => item.Status is not ("queued" or "running")).Take(8))
            {
                var signal = ReadSignal(item.TaskId);
                done.Add(new JsonObject
                {
                    ["taskId"] = item.TaskId, ["projectId"] = item.ProjectId, ["title"] = Title(item),
                    ["elapsed"] = Elapsed(signal.Start, signal.End),
                    ["end"] = signal.End is { } end ? end.ToLocalTime().ToString("HH:mm") : "—",
                    ["status"] = item.Status, ["word"] = StatusWord(item.Status)
                });
            }

            return new JsonObject
            {
                ["running"] = running, ["queued"] = queued.Length,
                ["pending"] = pending, ["done"] = done
            }.ToJsonString(Pretty);
        }
        catch (Exception error)
        {
            return new JsonObject { ["error"] = error.Message }.ToJsonString();
        }
    }

    internal async Task<string> CancelAsync(string projectId, string taskId)
    {
        try
        {
            await RunAsync("cancel-task", "-task-inbox", inbox, "-projects", projects,
                "-project-id", projectId, "-task-id", taskId, "-task-logs", logs);
            return new JsonObject { ["ok"] = true, ["message"] = "已请求叫停，等待 Agent 收尾。" }.ToJsonString();
        }
        catch (Exception error)
        {
            return new JsonObject { ["ok"] = false, ["message"] = error.Message }.ToJsonString();
        }
    }

    // ----------------------------------------------------------------- logs

    internal string Logs(int limit)
    {
        var events = new JsonArray();
        var files = 0;
        long lines = 0;
        var errors = 0;
        var since = DateTimeOffset.Now.AddHours(-24);
        try
        {
            if (Directory.Exists(logs))
            {
                var recent = new DirectoryInfo(logs).EnumerateFiles("*", SearchOption.AllDirectories)
                    .OrderByDescending(file => file.LastWriteTimeUtc).Take(120).ToArray();
                files = recent.Length;
                var collected = new List<(DateTimeOffset At, string Task, string Kind, string Text)>();
                foreach (var file in recent)
                {
                    var task = Path.GetFileNameWithoutExtension(file.Name);
                    try
                    {
                        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
                        const int tail = 96 * 1024;
                        long start = Math.Max(0, stream.Length - tail);
                        stream.Seek(start, SeekOrigin.Begin);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var body = reader.ReadToEnd();
                        if (start > 0)
                        {
                            var cut = body.IndexOf('\n');
                            if (cut >= 0) body = body[(cut + 1)..];
                        }
                        foreach (var raw in body.Split('\n'))
                        {
                            var line = raw.TrimEnd('\r');
                            if (line.Length == 0) continue;
                            lines++;
                            try
                            {
                                using var document = JsonDocument.Parse(line);
                                var node = document.RootElement;
                                var type = node.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                                var at = node.TryGetProperty("at", out var a) &&
                                         DateTimeOffset.TryParse(a.GetString(), out var when)
                                    ? when : (DateTimeOffset?)null;
                                var text = node.TryGetProperty("text", out var x) ? x.GetString() ?? ""
                                    : node.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                                if (at is null) continue;
                                var kind = KindOf(type);
                                if (kind == "错误" && at >= since) errors++;
                                collected.Add((at.Value, task, kind, text));
                            }
                            catch (JsonException) { }
                        }
                    }
                    catch (IOException) { }
                }
                foreach (var entry in collected.OrderByDescending(e => e.At).Take(limit))
                    events.Add(new JsonObject
                    {
                        ["at"] = entry.At.ToLocalTime().ToString("HH:mm:ss"),
                        ["task"] = entry.Task, ["kind"] = entry.Kind,
                        ["level"] = LevelOf(entry.Kind), ["text"] = entry.Text
                    });
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        return new JsonObject
        {
            ["events"] = events, ["files"] = files, ["lines"] = lines,
            ["errors24h"] = errors,
            ["last"] = events.Count > 0 ? events[0]!["at"]!.GetValue<string>() : ""
        }.ToJsonString(Pretty);
    }

    private static string KindOf(string type) => type switch
    {
        "item/commandExecution/outputDelta" => "命令",
        "item/agentMessage/delta" => "消息",
        "approval/requested" or "approval/approved" or "approval/declined" => "审批",
        "turn/started" or "task/finished" or "task/started" => "任务",
        "connection/state" => "连接",
        _ => type.Contains("error", StringComparison.OrdinalIgnoreCase) ? "错误" : "事件"
    };

    private static string LevelOf(string kind) => kind switch
    {
        "命令" => "c1", "审批" => "c4", "错误" => "c5", _ => "c2"
    };

    // -------------------------------------------------------------- projects

    private sealed record ProjectEntry(string Id, string Directory, string Mode, bool Enabled, string Engine);

    private List<ProjectEntry> Projects()
    {
        var list = new List<ProjectEntry>();
        try
        {
            if (!File.Exists(projects)) return list;
            using var document = JsonDocument.Parse(File.ReadAllText(projects));
            if (!document.RootElement.TryGetProperty("projects", out var array) ||
                array.ValueKind != JsonValueKind.Array) return list;
            foreach (var entry in array.EnumerateArray())
            {
                var engine = Field(entry, "engine");
                if (engine.Length == 0) engine = "codex";
                list.Add(new ProjectEntry(
                    Field(entry, "id"), Field(entry, "directory"), Field(entry, "mode"),
                    entry.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True,
                    engine));
            }
        }
        catch (Exception error) when (error is IOException or JsonException) { }
        return list;
    }

    private (int Total, int Enabled) PolicyCounts()
    {
        var list = Projects();
        return (list.Count, list.Count(entry => entry.Enabled));
    }

    internal string UpdateProject(string id, string? enabled, string? mode, bool remove)
    {
        try
        {
            if (!File.Exists(projects))
                return Fail("授权清单还不存在。");
            var node = JsonNode.Parse(File.ReadAllText(projects));
            if (node?["projects"] is not JsonArray array)
                return Fail("授权清单格式不认。");
            JsonNode? target = null;
            foreach (var entry in array)
                if (entry is not null && entry["id"]?.GetValue<string>() == id)
                    target = entry;
            if (target is null) return Fail("这个项目已经不在清单里了。");
            if (remove)
            {
                array.Remove(target);
            }
            else
            {
                if (enabled == "true")
                {
                    // Enabling is the moment a project starts receiving tasks.
                    // Refuse it when its engine cannot actually run them, so
                    // the phone never gets a task that fails at launch.
                    var engine = target["engine"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(engine)) engine = "codex";
                    var problem = EngineProblem(engine);
                    if (problem is not null)
                        return Fail($"无法启用：{ToEngineName(engine)} 引擎未就绪（{problem}）。请先在「设置 → 引擎」确认可用。");
                }
                if (enabled is not null) target["enabled"] = enabled == "true";
                if (mode is { Length: > 0 }) target["mode"] = mode;
            }
            File.WriteAllText(projects, node.ToJsonString(Pretty));
            return new JsonObject
            {
                ["ok"] = true,
                ["message"] = remove ? "已取消共享。" : mode is { Length: > 0 } ? "写入范围已更新。" : enabled == "true" ? "已启用。" : "已停用。"
            }.ToJsonString();
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }
    }

    internal string EditProjects()
    {
        try
        {
            using var editor = new ProjectEditor(projects);
            ShowOwned(editor);
            return new JsonObject { ["ok"] = true, ["message"] = "授权清单已保存。" }.ToJsonString();
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }
    }

    // The WebView page cannot pick a local folder itself (browser sandbox).
    // It asks here; the native dialog still runs owned by the shell window.
    internal string PickDirectory()
    {
        try
        {
            using var picker = new FolderBrowserDialog { Description = "选择允许远程控制的项目目录" };
            var owner = Owner;
            var result = owner is null || owner.IsDisposed || !owner.Visible
                ? picker.ShowDialog()
                : picker.ShowDialog(owner);
            if (result != DialogResult.OK) return Fail("没有选择目录。");
            return new JsonObject { ["ok"] = true, ["directory"] = picker.SelectedPath }.ToJsonString();
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }
    }

    // Mirrors ProjectEditor.Save: same id/path/mode validation, same lock and
    // atomic replace. The page sends the whole edited list; nothing is written
    // until every row passes, so a half-edited id can never reach the Agent.
    internal string SaveProjects(string payload)
    {
        const int maxBytes = 64 * 1024;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Fail("项目清单格式不认。");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<Dictionary<string, object?>>();
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var id = Field(row, "id");
                var directory = Field(row, "directory");
                var mode = Field(row, "mode");
                var engine = Field(row, "engine");
                if (engine.Length == 0) engine = "codex";
                var enabled = row.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9_-]{0,63}\z") || !seen.Add(id))
                    return Fail($"项目 ID「{id}」无效或重复：只用小写字母、数字、连字符，且不能重复。");
                if (!Path.IsPathFullyQualified(directory))
                    return Fail($"项目 {id} 的目录必须是本机绝对路径。");
                if (mode is not ("read-only" or "workspace-write"))
                    return Fail($"项目 {id} 的权限必须是只读或可写。");
                // The Agent only has a task-execution adapter for Codex. Refuse
                // any other engine here rather than letting a project pass
                // authorization and then fail at launch with no clear reason.
                if (engine != "codex")
                    return Fail($"项目 {id} 选的引擎暂不支持执行任务，请选择 Codex。");
                if (enabled)
                {
                    for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
                    {
                        dir.Refresh();
                        if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                            return Fail($"项目 {id} 的目录不存在或经过目录联接。");
                    }
                    // An enabled project whose engine is not ready is exactly
                    // what produces a failed task. Block it up front with the
                    // reason from the last engine detection.
                    var problem = EngineProblem("codex");
                    if (problem is not null)
                        return Fail($"项目 {id} 无法启用：Codex 引擎未就绪（{problem}）。请先在「设置 → 引擎」确认 Codex 可用。");
                }
                entries.Add(new Dictionary<string, object?>
                {
                    ["id"] = id, ["directory"] = directory, ["mode"] = mode,
                    ["enabled"] = enabled, ["engine"] = engine
                });
            }

            var data = JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object?> { ["projects"] = entries }, Pretty);
            if (data.Length > maxBytes) return Fail("项目授权清单超过 64 KiB。");

            var existed = File.Exists(projects);
            var original = existed ? File.ReadAllBytes(projects) : null;
            using (var saveLock = new FileStream(projects + ".agentdeck.lock",
                       FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var current = File.Exists(projects) ? File.ReadAllBytes(projects) : null;
                if ((current is null) != (original is null) ||
                    (current is not null && !current.AsSpan().SequenceEqual(original)))
                    return Fail("授权清单已在其他窗口修改。请关闭后重新打开。");
                var temporary = Path.Combine(Path.GetDirectoryName(projects)!, $"projects-{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                    {
                        file.Write(data);
                        file.Flush(flushToDisk: true);
                    }
                    if (original is null) File.Move(temporary, projects);
                    else File.Replace(temporary, projects, null);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            return new JsonObject { ["ok"] = true, ["message"] = "授权清单已保存。" }.ToJsonString();
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }
    }

    private static string Fail(string message) =>
        new JsonObject { ["ok"] = false, ["message"] = message }.ToJsonString();

    private static string Field(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    // ----------------------------------------------------------------- pair

    private sealed record Phone(string Name, string Fingerprint, string AtText, string Expires);

    private List<Phone> TrustedPhones()
    {
        var list = new List<Phone>();
        if (!Directory.Exists(PairingDirectory)) return list;
        foreach (var path in Directory.EnumerateFiles(PairingDirectory, "trusted-*.local.json"))
        {
            var name = Path.GetFileName(path);
            if (name.Length <= "trusted-.local.json".Length) continue;
            var middle = name["trusted-".Length..^".local.json".Length];
            var fingerprint = ShortFingerprint(middle.Length >= 64 ? middle[^64..] : middle);
            string expires = "";
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
                expires = $"剩余 {(int)(certificate.NotAfter - DateTime.Now).TotalDays} 天";
            }
            catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException) { }
            var at = File.GetCreationTime(path);
            list.Add(new Phone(PhoneName(path), fingerprint, Relative(at), expires));
        }
        return list;
    }

    // The pinned file is named after the fingerprint, so the friendly name lives
    // in the certificate subject when the phone sent one. Nothing else on disk
    // carries it; "手机" is the honest fallback.
    private static string PhoneName(string path)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
            var subject = certificate.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(subject) || subject.Length > 40 ? "手机" : subject;
        }
        catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException)
        {
            return "手机";
        }
    }

    private static string Relative(DateTime at)
    {
        var span = DateTime.Now - at;
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays} 天前"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时前"
            : $"{Math.Max(1, (int)span.TotalMinutes)} 分钟前";
    }

    // Pairing used to be a WinForms dialog stacked on the HTML shell: blue title
    // bar, GDI text, square corners — a different product sitting inside this
    // one. It is a pane in Settings now, so the invite is split into three
    // requests the page drives on its own clock: start returns the QR bitmap,
    // candidate polls for the phone that scanned it, confirm pins the
    // fingerprint. The page never sees a path or a command line.
    private string pairCode = "";
    private string pairDevice = "";
    private DateTime pairExpires;

    internal async Task<string> PairStartAsync()
    {
        try
        {
            if (!File.Exists(RegisteredPath) || !File.Exists(AgentConfig))
                return new JsonObject { ["ok"] = false, ["message"] = "请先在「接入」里配置云服务器。" }.ToJsonString();
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(AgentConfig));
            var device = config.RootElement.GetProperty("devices").EnumerateObject().Single().Name;
            Directory.CreateDirectory(PairingDirectory);
            var raw = (await RunAsync("pair-invite", "-config", AgentConfig,
                "-pairing-dir", PairingDirectory, "-device", device)).Trim();
            using var data = JsonDocument.Parse(raw);
            // "c" is the pairing code in the v2 invite; the invite itself is
            // encoded as-is, so the field names are part of the QR contract.
            pairCode = data.RootElement.GetProperty("c").GetString()!;
            pairDevice = device;
            pairExpires = DateTime.Now.AddSeconds(120);

            // The QR carries the invite document itself, not just the code, so
            // the phone can check the relay endpoint before it answers. The
            // shells renders a bitmap rather than shipping a QR encoder.
            // 302 bytes of invite (two public-key pins, no certificates) lands
            // on version 11 at ECC L — 61 modules across. Eight pixels per
            // module keeps every module a hard-edged 8x8 block at 1:1 in the
            // zoom sheet, which is what a camera needs to lock on.
            using var generator = new QRCodeGenerator();
            using var qr = generator.CreateQrCode(raw, QRCodeGenerator.ECCLevel.L);
            using var png = new PngByteQRCode(qr);
            return new JsonObject
            {
                ["ok"] = true,
                ["code"] = pairCode,
                ["png"] = "data:image/png;base64," + Convert.ToBase64String(png.GetGraphic(8)),
                ["modules"] = qr.ModuleMatrix.Count,
                ["expires"] = 120
            }.ToJsonString();
        }
        catch (Exception error)
        {
            return new JsonObject { ["ok"] = false, ["message"] = error.Message }.ToJsonString();
        }
    }

    /// <summary>
    /// Poll for the phone that scanned the current code. "Nobody yet" is the
    /// normal answer every few seconds, so it comes back as ok:false with a
    /// soft message rather than a failure the page would report as an error.
    /// </summary>
    internal async Task<string> PairCandidateAsync(string code)
    {
        if (pairDevice.Length == 0 || pairCode.Length == 0 || code != pairCode)
            return new JsonObject { ["ok"] = false, ["expired"] = true, ["message"] = "配对码已失效。" }.ToJsonString();
        if (DateTime.Now > pairExpires)
            return new JsonObject { ["ok"] = false, ["expired"] = true, ["message"] = "配对码已过期。" }.ToJsonString();
        try
        {
            var fingerprint = (await RunAsync("pair-candidate", "-pairing-dir", PairingDirectory,
                "-device", pairDevice, "-code", pairCode)).Trim();
            return fingerprint.Length == 0
                ? new JsonObject { ["ok"] = false, ["message"] = "尚未扫码。" }.ToJsonString()
                : new JsonObject { ["ok"] = true, ["fingerprint"] = fingerprint }.ToJsonString();
        }
        catch (Exception)
        {
            // The backend exits non-zero while no phone has answered yet.
            return new JsonObject { ["ok"] = false, ["message"] = "尚未扫码。" }.ToJsonString();
        }
    }

    internal async Task<string> PairConfirmAsync(string code, string fingerprint)
    {
        if (pairDevice.Length == 0 || pairCode.Length == 0 || code != pairCode)
            return new JsonObject { ["ok"] = false, ["message"] = "配对码已失效，请重新生成。" }.ToJsonString();
        try
        {
            await RunAsync("pair-confirm", "-pairing-dir", PairingDirectory, "-device", pairDevice,
                "-code", pairCode, "-fingerprint", fingerprint);
            pairCode = "";
            pairDevice = "";
            return new JsonObject { ["ok"] = true, ["message"] = "手机已配对。请在手机刷新项目。" }.ToJsonString();
        }
        catch (Exception error)
        {
            return new JsonObject { ["ok"] = false, ["message"] = error.Message }.ToJsonString();
        }
    }

    internal string RevokePhone(string fingerprint)
    {
        try
        {
            var clean = new string(fingerprint.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            var removed = 0;
            if (Directory.Exists(PairingDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(PairingDirectory, "trusted-*.local.json").ToArray())
                {
                    var name = Path.GetFileName(path);
                    var middle = name["trusted-".Length..^".local.json".Length].ToUpperInvariant();
                    if (!middle.Contains(clean, StringComparison.Ordinal)) continue;
                    File.Delete(path);
                    removed++;
                }
            }
            return new JsonObject
            {
                ["ok"] = removed > 0, ["message"] = removed > 0 ? $"已撤销 {removed} 台手机的授权。" : "没有找到这台手机。"
            }.ToJsonString();
        }
        catch (Exception error)
        {
            return new JsonObject { ["ok"] = false, ["message"] = error.Message }.ToJsonString();
        }
    }

    // -------------------------------------------------------------- identity

    private string DeviceIdText()
    {
        try { return File.Exists(RegisteredPath) ? File.ReadAllText(RegisteredPath).Trim() : ""; }
        catch (IOException) { return ""; }
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

    internal string Fingerprint()
    {
        try
        {
            if (!File.Exists(AgentConfig)) return "";
            using var config = JsonDocument.Parse(File.ReadAllText(AgentConfig));
            if (!config.RootElement.TryGetProperty("relayCertificate", out var pem)) return "";
            using var certificate = X509Certificate2.CreateFromPem(pem.GetString() ?? "");
            return "SHA256 " + certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        }
        catch (Exception error) when (error is IOException or JsonException or
            System.Security.Cryptography.CryptographicException) { return ""; }
    }

    private int CertDays()
    {
        try
        {
            if (!File.Exists(AgentConfig)) return 0;
            using var config = JsonDocument.Parse(File.ReadAllText(AgentConfig));
            if (!config.RootElement.TryGetProperty("relayCertificate", out var pem)) return 0;
            using var certificate = X509Certificate2.CreateFromPem(pem.GetString() ?? "");
            return Math.Max(0, (int)(certificate.NotAfter - DateTime.Now).TotalDays);
        }
        catch (Exception error) when (error is IOException or JsonException or
            System.Security.Cryptography.CryptographicException) { return 0; }
    }

    // -------------------------------------------------------------- services

    private Process? agent;

    internal async Task<string> ToggleServiceAsync()
    {
        try
        {
            var service = ServiceControl.Query();
            if (service.Installed)
            {
                if (Ask(
                        "卸载后开机不再自动运行 Agent，需要登录 Windows 后由托盘启动。\n\n确认卸载？",
                        "卸载开机自启服务", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.OK)
                    return Fail("已取消。");
                var code = await RunElevatedAsync("service-uninstall", "-name", ServiceControl.Name);
                if (ServiceControl.Query().Installed)
                    return Fail($"服务没有卸载成功（退出码 {code}）。");
                StopAgent();
                if (DeviceIdText() is { Length: > 0 } id) StartAgent(id);
                return new JsonObject { ["ok"] = true, ["message"] = "服务已卸载，Agent 改由托盘启动。" }.ToJsonString();
            }

            var device = DeviceIdText();
            if (device.Length == 0) return Fail("请先配置云服务器，再安装开机自启服务。");
            if (Ask(
                    "安装需要管理员权限，Windows 会弹出确认框。\n\n安装后 Agent 由服务启动，未登录也能连接手机。",
                    "安装开机自启服务", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return Fail("已取消。");

            var arguments = new List<string>
            {
                "service-install", "-name", ServiceControl.Name, "-exe", BackendPath,
                "-config", AgentConfig, "-pairing-dir", PairingDirectory, "-device", device,
                "-log", Path.Combine(DeviceDirectory, "service.log"),
                "-task-logs", logs, "-task-inbox", inbox, "-projects", projects
            };
            if (codex.Length > 0) arguments.AddRange(["-codex", codex]);
            var exit = await RunElevatedAsync([.. arguments]);
            if (!ServiceControl.Query().Installed)
                return Fail($"服务没有安装成功（退出码 {exit}）。如果 Windows 提示被取消，请重试。");
            StopAgent();
            return new JsonObject { ["ok"] = true, ["message"] = "服务已安装并启动，登录前也能连接。" }.ToJsonString();
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }
    }

    private async Task<int> RunElevatedAsync(params string[] args)
    {
        var start = new ProcessStartInfo(BackendPath)
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
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }

    internal string ToggleAgent()
    {
        try
        {
            if (ServiceControl.Query().Running)
                return Fail("当前由 Windows 服务托管，请在「设置 → 接入」里停止服务。");
            var id = DeviceIdText();
            if (id.Length == 0) return Fail("还没有注册这台电脑，先在「接入」里配置云服务器。");
            if (AgentRunning)
            {
                StopAgent();
                return new JsonObject { ["ok"] = true, ["message"] = "已暂停接收，手机此刻连不上。" }.ToJsonString();
            }
            StartAgent(id);
            return new JsonObject { ["ok"] = true, ["message"] = "已恢复接收。" }.ToJsonString();
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }
    }

    internal void StopAgent()
    {
        if (agent is { HasExited: false })
        {
            try { agent.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        agent?.Dispose();
        agent = null;
    }

    internal void StartAgent(string id)
    {
        if (agent is { HasExited: false }) return;
        if (ServiceControl.Query().Installed) return;
        Directory.CreateDirectory(PairingDirectory);
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(logs);
        var start = new ProcessStartInfo(BackendPath) { UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string>
        {
            "agent", "-config", AgentConfig, "-device", id, "-pairing-dir", PairingDirectory,
            "-projects", projects, "-task-inbox", inbox, "-task-logs", logs
        };
        if (codex.Length > 0) args.AddRange(["-codex", codex]);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        agent?.Dispose();
        agent = Process.Start(start);
    }

    // ------------------------------------------------------------ relay setup

    internal async Task<string> ConfigureRelayAsync()
    {
        try
        {
            using var dialog = new RelayWindow();
            if (ShowOwned(dialog) != DialogResult.OK) return Fail("已取消配置。");
            string id;
            var repinned = false;
            try
            {
                id = await EnrollAsync(dialog, replace: false);
            }
            catch (Exception error) when (PinnedToOldRelay(error.Message))
            {
                if (Ask(
                        "本机钉着的是旧证书，和这次填的对不上。\n\n继续会把本机改钉到新证书，并用同一个设备 ID 重新注册：\n" +
                        "· 项目白名单、路径与日志都保留\n· 已配对的手机会连不上，需要在手机上重新扫码\n" +
                        "· 旧的配对信任会改名留底，不删除\n\n继续？",
                        "服务器证书已更换", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return Fail("已取消，本机身份没有改动。");
                id = await RepinAsync(dialog);
                repinned = true;
            }
            File.WriteAllText(RegisteredPath, id);
            StartAgent(id);
            return new JsonObject
            {
                ["ok"] = true,
                ["message"] = repinned
                    ? ServiceControl.Query().Installed ? "已改钉新证书。重启电脑后服务才会用新证书。" : "已改钉新证书。请在手机上重新扫码配对。"
                    : "电脑已注册，正在连接 Relay。"
            }.ToJsonString();
        }
        catch (Exception error)
        {
            return Fail(Explain(error.Message));
        }
    }

    // WebView variant of ConfigureRelayAsync: the three fields and their
    // validation live in the page, so they arrive here as arguments. A cert
    // repin is answered with repinConfirm so the page can ask in its own
    // visual language instead of a native MessageBox.
    internal async Task<string> RegisterRelayAsync(string endpoint, string fingerprint, string token, bool replace)
    {
        try
        {
            endpoint = endpoint.Trim();
            fingerprint = fingerprint.Replace(":", "").Replace(" ", "").Trim().ToLowerInvariant();
            token = token.Trim();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.Host.Length == 0)
                return Fail("请输入以 wss:// 开头的 Relay 地址。");
            if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
                return Fail("请输入完整的 64 位十六进制证书指纹。");
            if (token.Length != 64 || !token.All(Uri.IsHexDigit))
                return Fail("请输入云端生成的 64 位十六进制注册令牌。");

            string id;
            if (replace)
            {
                // Confirmed repin: back up the pairing trust, re-enroll with
                // -replace, restore on failure. Never a plain enroll first.
                id = await RepinAsync(endpoint, fingerprint, token);
            }
            else
            {
                try
                {
                    id = await EnrollAsync(endpoint, fingerprint, token, replace: false);
                }
                catch (Exception error) when (PinnedToOldRelay(error.Message))
                {
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["repinConfirm"] = true,
                        ["message"] =
                            "本机钉着的是旧证书，和这次填的对不上。\n\n" +
                            "继续会把本机改钉到新证书，并用同一个设备 ID 重新注册：\n" +
                            "· 项目白名单、路径与日志都保留\n" +
                            "· 已配对的手机会连不上，需要在手机上重新扫码\n" +
                            "· 旧的配对信任会改名留底，不删除\n\n继续？"
                    }.ToJsonString();
                }
            }
            var repinned = replace;
            File.WriteAllText(RegisteredPath, id);
            StartAgent(id);
            return new JsonObject
            {
                ["ok"] = true,
                ["message"] = repinned
                    ? ServiceControl.Query().Installed ? "已改钉新证书。重启电脑后服务才会用新证书。" : "已改钉新证书。请在手机上重新扫码配对。"
                    : "电脑已注册，正在连接 Relay。"
            }.ToJsonString();
        }
        catch (Exception error)
        {
            return Fail(Explain(error.Message));
        }
    }

    private async Task<string> EnrollAsync(RelayWindow dialog, bool replace) =>
        await EnrollAsync(dialog.Endpoint, dialog.Fingerprint, dialog.Token, replace);

    private async Task<string> EnrollAsync(string endpoint, string fingerprint, string token, bool replace)
    {
        var arguments = new List<string>
        {
            "enroll", "-config", AgentConfig, "-relay-url", endpoint, "-fingerprint", fingerprint
        };
        if (replace) arguments.Add("-replace");
        return (await RunWithInputAsync(token, [.. arguments])).Trim();
    }

    private async Task<string> RepinAsync(RelayWindow dialog) =>
        await RepinAsync(dialog.Endpoint, dialog.Fingerprint, dialog.Token);

    private async Task<string> RepinAsync(string endpoint, string fingerprint, string token)
    {
        string? backup = null;
        if (Directory.Exists(PairingDirectory))
        {
            backup = UniquePath(PairingDirectory);
            Directory.Move(PairingDirectory, backup);
        }
        try
        {
            var id = await EnrollAsync(endpoint, fingerprint, token, replace: true);
            StopAgent();
            return id;
        }
        catch
        {
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(PairingDirectory))
                Directory.Move(backup, PairingDirectory);
            throw;
        }
    }

    private static bool PinnedToOldRelay(string message) =>
        message.Contains("pinned to another Relay certificate", StringComparison.Ordinal) ||
        message.Contains("belongs to another Relay", StringComparison.Ordinal);

    private static string UniquePath(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = $"{path}.{stamp}.bak";
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = $"{path}.{stamp}-{n}.bak";
        return candidate;
    }

    private static string Explain(string message)
    {
        if (message.Contains("fingerprint mismatch", StringComparison.Ordinal))
            return "注册失败：指纹与服务器证书不符，按 relay-fingerprint 的输出重新填";
        if (message.Contains("does not cover", StringComparison.Ordinal))
            return "注册失败：证书里没有你填的地址，改成证书上的那个名字";
        if (message.Contains("expired at", StringComparison.Ordinal))
            return "注册失败：服务器证书已过期，需要在云机重新 relay-init";
        if (message.Contains("not valid until", StringComparison.Ordinal))
            return "注册失败：服务器时钟超前，证书尚未生效";
        if (message.Contains("pinned to another Relay certificate", StringComparison.Ordinal))
            return "注册失败：本机钉的是旧证书，需要改钉新证书";
        if (message.Contains("belongs to another Relay", StringComparison.Ordinal))
            return "注册失败：本机配置指向另一个 Relay，需要改钉新证书";
        if (message.Contains("HTTP 401", StringComparison.Ordinal))
            return "注册失败：注册令牌不对";
        if (message.Contains("HTTP 409", StringComparison.Ordinal))
            return "注册失败：这台设备已用另一组密钥注册过";
        return "注册失败：" + message;
    }

    // ------------------------------------------------------------------ files

    internal string OpenPath(string what, string? projectId)
    {
        try
        {
            var target = what switch
            {
                "logs" => logs,
                "inbox" => inbox,
                "projects" => Path.GetDirectoryName(projects) ?? root,
                _ => Projects().FirstOrDefault(entry => entry.Id == projectId)?.Directory
            };
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
                return new JsonObject { ["ok"] = false, ["message"] = "目录还不存在。" }.ToJsonString();
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return new JsonObject { ["ok"] = true, ["message"] = "已打开目录。" }.ToJsonString();
        }
        catch (Exception error)
        {
            return new JsonObject { ["ok"] = false, ["message"] = error.Message }.ToJsonString();
        }
    }

    internal string OpenUrl(string url)
    {
        try
        {
            // The page is untrusted input from the WebView's point of view. Keep
            // this to the project's own host over TLS so it can never be turned
            // into an arbitrary file:// or executable launch.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return new JsonObject { ["ok"] = false, ["message"] = "只允许打开 github.com 的 HTTPS 链接。" }.ToJsonString();
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return new JsonObject { ["ok"] = true, ["message"] = "已在浏览器中打开。" }.ToJsonString();
        }
        catch (Exception error)
        {
            return new JsonObject { ["ok"] = false, ["message"] = error.Message }.ToJsonString();
        }
    }

    // ------------------------------------------------------------------ util

    private static string Title(QueueItem item) =>
        string.IsNullOrWhiteSpace(item.Summary) ? item.Prompt : item.Summary;

    private static string Elapsed(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is null || to is null) return "—";
        var span = to.Value - from.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }

    private static string LastEvent(TaskSignal signal) => signal.End is { } at
        ? $"{at.ToLocalTime():HH:mm:ss} {EventWord(signal.LastType)}" : "尚无事件";

    private static string EventWord(string type) => type switch
    {
        "item/agentMessage/delta" => "消息",
        "item/commandExecution/outputDelta" => "命令输出",
        "approval/requested" => "审批",
        "approval/approved" => "已批准",
        "approval/declined" => "已拒绝",
        "turn/started" => "开始",
        "task/finished" => "结束",
        _ => type.Length > 0 ? type : "事件"
    };

    private static string StatusWord(string status) => status switch
    {
        "queued" => "排队中", "running" => "执行中", "completed" => "完成",
        "failed" => "失败", "interrupted" => "已中断", "canceled" => "已取消", _ => status
    };

    private static string FormatSpan(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays} 天 {span.Hours} 小时"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
        : $"{Math.Max(0, (int)span.TotalMinutes)} 分钟";

    private static string ShortFingerprint(string fingerprint)
    {
        var clean = new string(fingerprint.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return clean.Length <= 8 ? clean : clean[..4] + "·" + clean[^4..];
    }

    private static string DeviceShort(string id)
    {
        var clean = new string(id.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length == 0) return "—";
        return clean.Length <= 8 ? clean : clean[..4] + "·" + clean[^4..];
    }

    private string FirstPhoneName() => TrustedPhones().FirstOrDefault()?.Name ?? "手机";

    private TaskSignal ReadSignal(string taskId)
    {
        var path = Path.Combine(logs, taskId + ".log");
        if (!File.Exists(path)) return new TaskSignal(null, null, "", "", null);
        try
        {
            const int limit = 192 * 1024;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var start = Math.Max(0, file.Length - limit);
            file.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[Math.Min(limit, file.Length)];
            int read = file.ReadAtLeast(bytes, bytes.Length, false);
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            if (start > 0)
            {
                int offset = text.IndexOf('\n');
                if (offset >= 0) text = text[(offset + 1)..];
            }

            DateTimeOffset? first = null, last = null;
            string lastType = "", lastText = "";
            DateTimeOffset? pendingAt = null;
            string pendingText = "";
            bool pending = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    var at = root.TryGetProperty("at", out var a) &&
                             DateTimeOffset.TryParse(a.GetString(), out var when) ? when : (DateTimeOffset?)null;
                    if (at is not null) { first ??= at; last = at; }
                    if (type.Length > 0)
                    {
                        lastType = type;
                        lastText = root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";
                    }
                    if (type is "approval/requested" or "approval/approved" or "approval/declined")
                    {
                        pending = type == "approval/requested";
                        pendingAt = at;
                        pendingText = lastText;
                    }
                }
                catch (JsonException) { }
            }
            if (pending && pendingAt is { } since && DateTimeOffset.Now - since < TimeSpan.FromMinutes(5))
                return new TaskSignal(first, last, lastType, lastText, new PendingApproval(pendingText, since));
            return new TaskSignal(first, last, lastType, lastText, null);
        }
        catch (IOException) { return new TaskSignal(null, null, "", "", null); }
    }

    private sealed record TaskSignal(DateTimeOffset? Start, DateTimeOffset? End,
        string LastType, string LastText, PendingApproval? Pending);

    private sealed record PendingApproval(string Text, DateTimeOffset At);

    private sealed record QueueItem(string TaskId, string ProjectId, string Directory, string Status,
        DateTimeOffset SubmittedAt, string Summary, string Prompt, string? Sandbox = null);

    private sealed record QueueView(QueueItem[] Items);

    private sealed record TrayPaths(string Inbox, string Projects, string Logs, string Codex);
}
