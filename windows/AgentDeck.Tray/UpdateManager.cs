using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentDeck.Tray;

/// <summary>
/// Checks one release channel and swaps the installed single-file exe.
///
/// Windows lets a running .exe be renamed but not overwritten, and the tray
/// owns a single-instance mutex, so the swap is deliberately two-staged:
/// rename the installed copy to .old, write the downloaded one in its place,
/// start it with --after-update (it waits for the mutex), then let this process
/// exit. The replacement runs Install(), which unpacks the matching backend and
/// ui; Cleanup() on the next start removes the leftovers.
///
/// No patch packages on purpose: Install() rewrites the backend and ui from
/// resources inside the exe on every start, so a standalone backend patch
/// would be overwritten again by the next launch.
/// </summary>
internal sealed class UpdateManager
{
    private const string ReleaseManifest =
        "https://github.com/hellopan/agent_deck/releases/latest/download/latest.json";

    // One client, no global timeout: the manifest probe wants 8 seconds, a
    // 130 MB download wants minutes. Callers pass their own cancellation.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static UpdateInfo? cached;

    internal sealed record UpdateInfo(string Version, string Notes, string Url, string Sha256, long Size);

    internal static UpdateInfo? Cached => cached;

    internal static string CurrentVersion => typeof(UpdateManager).Assembly
        .GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    internal static string UpdateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentDeck", "update");

    private static string ManifestUrl =>
        Environment.GetEnvironmentVariable("AGENTDECK_UPDATE_URL") is { Length: > 0 } url
            ? url : ReleaseManifest;

    private static string Fail(string message) => new JsonObject
    {
        ["ok"] = false, ["message"] = message
    }.ToJsonString();

    /// <summary>Compare only major.minor.build: the assembly version has a
    /// fourth revision component that must not make an equal version look old.</summary>
    private static bool Newer(string latest)
    {
        if (!Version.TryParse(latest, out var remote)) return false;
        var local = typeof(UpdateManager).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var a = Math.Max(remote.Build, 0);
        var b = Math.Max(local.Build, 0);
        return remote.Major > local.Major
            || remote.Major == local.Major && (remote.Minor > local.Minor
                || remote.Minor == local.Minor && a > b);
    }

    internal static async Task<string> CheckAsync()
    {
        try
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var json = await Http.GetStringAsync(ManifestUrl, probe.Token);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("windows", out var win))
                return Fail("更新清单里没有 windows 段。");

            var version = win.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            if (!Newer(version))
            {
                cached = null;
                return new JsonObject
                {
                    ["ok"] = true, ["available"] = false,
                    ["current"] = CurrentVersion, ["latest"] = version
                }.ToJsonString();
            }

            var url = win.TryGetProperty("full", out var f) ? f.GetString() ?? "" : "";
            var sha = win.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
            if (url.Length == 0 || sha.Length != 64)
                return Fail("更新清单不完整：缺少下载地址或 SHA-256。");

            var info = new UpdateInfo(
                version,
                win.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "",
                url, sha,
                win.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0);
            cached = info;
            return new JsonObject
            {
                ["ok"] = true, ["available"] = true,
                ["current"] = CurrentVersion, ["latest"] = version,
                ["notes"] = info.Notes, ["size"] = info.Size,
                ["url"] = info.Url, ["sha256"] = info.Sha256
            }.ToJsonString();
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return Fail("还没有发布任何版本。");
        }
        catch (OperationCanceledException) { return Fail("检查更新超时，稍后再试。"); }
        catch (HttpRequestException error) { return Fail("连不上更新源：" + error.Message); }
        catch (JsonException) { return Fail("更新清单格式不对，稍后再试。"); }
        catch (Exception error) { return Fail("检查更新失败：" + error.Message); }
    }

    /// <summary>Startup probe: never report a failure, only remember a find.</summary>
    internal static async Task CheckSilentAsync()
    {
        try { await CheckAsync(); }
        catch (Exception) { cached = null; }
    }

    internal static async Task<string> DownloadAsync(string url, string sha256, string version)
    {
        try
        {
            if (url.Length == 0 || sha256.Length != 64)
                return Fail("更新地址或校验值缺失，请重新检查更新。");

            var folder = UpdateDirectory;
            Directory.CreateDirectory(folder);
            var safe = string.Concat(version.Select(ch =>
                char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_'));
            var target = Path.Combine(folder, "AgentDeck-" + safe + ".exe");
            var part = target + ".part";

            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, deadline.Token);
            }

            using var sha = SHA256.Create();
            await using (var stream = File.OpenRead(part))
            {
                var actual = Convert.ToHexString(await sha.ComputeHashAsync(stream, deadline.Token));
                if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    return Fail("下载包的 SHA-256 对不上，已丢弃。请重新检查更新。");
                }
            }
            File.Move(part, target, overwrite: true);
            return new JsonObject
            {
                ["ok"] = true, ["path"] = target, ["message"] = "下载完成，校验通过。"
            }.ToJsonString();
        }
        catch (OperationCanceledException) { return Fail("下载超时，请检查网络后重试。"); }
        catch (HttpRequestException error) { return Fail("下载失败：" + error.Message); }
        catch (Exception error) { return Fail("下载失败：" + error.Message); }
    }

    internal static (string Json, bool Restart) Apply(string path, DeckBridge bridge)
    {
        try
        {
            if (!File.Exists(path)) return (Fail("更新包不在了，请重新下载。"), false);
            // A service holds the backend exe open, so Install() in the new
            // process could not replace it. Refuse rather than leave a half
            // updated machine; the settings page explains the manual route.
            if (ServiceControl.Query().Installed)
                return (Fail("这台电脑由 Windows 服务托管，自动更新会打断服务。请先在「设置 → 接入」卸载服务，更新后重新安装。"), false);

            bridge.StopAgent();
            var installed = Path.Combine(Program.InstallDirectory, "AgentDeck.exe");
            var running = Environment.ProcessPath ?? "";
            var old = installed + ".old";
            if (File.Exists(old)) File.Delete(old);
            if (string.Equals(running, installed, StringComparison.OrdinalIgnoreCase))
                File.Move(installed, old);   // A running exe may be renamed on Windows.
            File.Copy(path, installed, overwrite: true);

            var start = new ProcessStartInfo(installed) { UseShellExecute = false };
            start.ArgumentList.Add("--after-update");
            Process.Start(start);
            return (new JsonObject
            {
                ["ok"] = true, ["restart"] = true,
                ["message"] = "新版已就位，正在重启 AgentDeck…"
            }.ToJsonString(), true);
        }
        catch (Exception error) { return (Fail("安装更新失败：" + error.Message), false); }
    }

    internal static void Cleanup()
    {
        try
        {
            var folder = UpdateDirectory;
            if (Directory.Exists(folder))
                foreach (var file in Directory.GetFiles(folder))
                {
                    try { File.Delete(file); } catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            var old = Path.Combine(Program.InstallDirectory, "AgentDeck.exe.old");
            if (File.Exists(old))
            {
                try { File.Delete(old); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception) { }
    }
}
