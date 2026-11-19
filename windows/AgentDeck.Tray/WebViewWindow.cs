using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentDeck.Tray;

// Probe shell: the v3 design board rendered by Chromium instead of GDI.
//
// Everything visual lives in ui/index.html — this class owns nothing but the
// window: no layout, no fonts, no colours. That is the whole point of the
// probe: if it looks right here, it will look right everywhere, because it is
// the same HTML the design board is written in.
//
// The board is frameless, so the window is too. Dragging is handled by
// Chromium itself through `app-region: drag` (Settings.IsNonClientRegionSupportEnabled
// — must be set before the first navigation), and the three window controls in
// the title bar post messages back here.
internal sealed class WebViewWindow : Form
{
    private readonly WebView2 view = new();
    private readonly DeckBridge bridge = new();
    private bool closing;

    internal WebViewWindow()
    {
        Text = "AgentDeck";
        Icon = DeckTheme.AppIcon;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 868);
        MinimumSize = new Size(1024, 700);
        BackColor = Color.FromArgb(0xF5, 0xF6, 0xF7);
        KeyPreview = true;

        view.Dock = DockStyle.Fill;
        view.DefaultBackgroundColor = Color.FromArgb(0xF5, 0xF6, 0xF7);
        Controls.Add(view);
        bridge.Owner = this;

        Load += async (_, _) => await BootAsync();
        FormClosing += (_, _) => { closing = true; bridge.StopAgent(); };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F12) view.CoreWebView2?.OpenDevToolsWindow();
            if (e.KeyCode == Keys.Escape && e.Control) Close();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowFrame();
    }

    // A WS_POPUP window has no frame, so DWM has nothing to hang a shadow on.
    // The classic stand-in is CS_DROPSHADOW, but that one lights the window
    // from the top left: a hard band down the right and bottom, nothing on the
    // other two sides. Extending a hairline frame into the client area instead
    // makes DWM treat the window as a normal framed one, and the shadow it
    // paints is the soft halo real windows get — all four sides, same falloff.
    //
    // The border that comes with that frame is pinned to the board's app
    // border colour, and the corners are handed to the system: Win11 draws the
    // same 8px radius a normal window has, which is what the frameless shell
    // was missing. Both attributes are Win11-only; on Win10 the calls return an
    // error code and the window simply stays square.
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMNCRP_ENABLED = 2;
    private const int DWMWCP_ROUND = 2;
    private const int BorderColor = 0x00DE_D9D5; // COLORREF 0x00BBGGRR = #d5d9de

    private void ApplyWindowFrame()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        if (DwmExtendFrameIntoClientArea(Handle, ref margins) != 0) return;
        int enabled = DWMNCRP_ENABLED;
        DwmSetWindowAttribute(Handle, DWMWA_NCRENDERING_POLICY, ref enabled, sizeof(int));
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int border = BorderColor;
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    // A WS_POPUP window is maximized to the whole monitor, not to the work
    // area, so the taskbar disappears under it — the one visible difference
    // between this shell and a normal window. Answering WM_GETMINMAXINFO with
    // the work area keeps the maximized window out of the taskbar's way.
    private const int WM_GETMINMAXINFO = 0x0024;

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != WM_GETMINMAXINFO || message.LParam == 0) return;
        var screen = Screen.FromHandle(Handle);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var info = Marshal.PtrToStructure<MinMaxInfo>(message.LParam);
        info.MaxPosition.X = work.Left - bounds.Left;
        info.MaxPosition.Y = work.Top - bounds.Top;
        info.MaxSize.X = work.Width;
        info.MaxSize.Y = work.Height;
        Marshal.StructureToPtr(info, message.LParam, false);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    // A debug build reads the page from its own folder, so CSS and JS edits are
    // visible without a rebuild. The shipped exe has no such folder — Install()
    // unpacks the embedded copy next to the backend, and that is what loads.
    private static string UiDirectory
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ui");
            return File.Exists(Path.Combine(local, "index.html"))
                ? local
                : Path.Combine(Program.InstallDirectory, "ui");
        }
    }

    internal void ShowPage(string page)
    {
        if (closing || view.CoreWebView2 is null) return;
        _ = view.CoreWebView2.ExecuteScriptAsync(
            $"document.querySelector('.nav-item[data-go=\"{page}\"]')?.click()");
    }

    private async Task BootAsync()
    {
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDeck", "webview");
        Directory.CreateDirectory(profile);

        var environment = await CoreWebView2Environment.CreateAsync(null, profile);
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;

        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        // Lets the page declare its own drag strip. Takes effect on the next
        // navigation, so it has to be set before Navigate below.
        core.Settings.IsNonClientRegionSupportEnabled = true;

        core.WebMessageReceived += OnMessage;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ShellScript);

        // QA hook: AGENTDECK_SHOT=<png> captures the page straight from the
        // compositor. A GDI BitBlt of the window returns black for WebView2
        // (it draws through DirectComposition), so screenshots have to come
        // from inside the control. CapturePreviewAsync silently produced an
        // empty file here; Page.captureScreenshot reports its own errors, so
        // the probe uses that and writes a sidecar log either way.
        core.NavigationCompleted += async (_, e) =>
        {
            var shot = Environment.GetEnvironmentVariable("AGENTDECK_SHOT");
            if (string.IsNullOrWhiteSpace(shot)) return;
            var log = shot + ".log";
            try
            {
                File.WriteAllText(log, $"success={e.IsSuccess} status={e.WebErrorStatus} url={core.Source}\n");
                if (!e.IsSuccess) return;
                await Task.Delay(1200);
                // QA hook: open a page or a settings tab before the capture, so
                // every screen can be reviewed without a human clicking.
                var page = Environment.GetEnvironmentVariable("AGENTDECK_SHELL_PAGE");
                if (!string.IsNullOrWhiteSpace(page))
                {
                    await core.ExecuteScriptAsync(
                        $"document.querySelector('.nav-item[data-go=\"{page}\"]')?.click()");
                    var tab = Environment.GetEnvironmentVariable("AGENTDECK_SHELL_TAB");
                    if (!string.IsNullOrWhiteSpace(tab))
                        await core.ExecuteScriptAsync(
                            $"document.querySelector('.page.active .tab[data-tab=\"{tab}\"]')?.click()");
                    await Task.Delay(900);
                }
                // QA hook: exercise one control before the capture, so actions
                // can be verified without a human pressing the button.
                var click = Environment.GetEnvironmentVariable("AGENTDECK_SHELL_CLICK");
                if (!string.IsNullOrWhiteSpace(click))
                {
                    await core.ExecuteScriptAsync(
                        $"document.querySelector('{click}')?.click()");
                    // Pairing shells out to the backend before it can paint a
                    // code, so a one-second pause captured an empty square.
                    await Task.Delay(2600);
                }
                // QA hook: run page script before the capture. Sheets and
                // other states that need a phone or a second window are opened
                // this way instead of being clicked into existence.
                var script = Environment.GetEnvironmentVariable("AGENTDECK_SHELL_JS");
                if (!string.IsNullOrWhiteSpace(script))
                {
                    await core.ExecuteScriptAsync(script);
                    await Task.Delay(700);
                }
                // QA hook: AGENTDECK_SHOT_DELAY waits for a state that only
                // arrives later — a phone answering the pairing code, for
                // instance — after every click has already been made.
                var wait = Environment.GetEnvironmentVariable("AGENTDECK_SHOT_DELAY");
                if (int.TryParse(wait, out var delayMs) && delayMs > 0) await Task.Delay(delayMs);
                var json = await core.CallDevToolsProtocolMethodAsync(
                    "Page.captureScreenshot", """{"format":"png"}""");
                using var document = JsonDocument.Parse(json);
                var data = document.RootElement.GetProperty("data").GetString() ?? "";
                File.WriteAllBytes(shot, Convert.FromBase64String(data));
                File.AppendAllText(log, $"bytes={new FileInfo(shot).Length}\n");
            }
            catch (Exception error)
            {
                File.AppendAllText(log, error.ToString());
            }
        };

        core.SetVirtualHostNameToFolderMapping(
            "deck.local", UiDirectory, CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate("https://deck.local/index.html");

        // Detection shells out and the page cannot paint before it answers, so
        // it runs once in the background and the page is told to re-read state
        // when it lands. Everything else in `state` is disk-only.
        StartAgentIfRegistered();
        _ = WarmUpAsync();
    }

    /// <summary>
    /// Hands the relay connection to the same process the WinForms shell used.
    /// Without this the window would paint "运行中" only if a stray agent
    /// happened to be alive, and the phone would never reach this machine.
    /// </summary>
    private void StartAgentIfRegistered()
    {
        try
        {
            var registered = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentDeck", "registered-device");
            if (!File.Exists(registered)) return;
            var id = File.ReadAllText(registered).Trim();
            if (id.Length > 0) bridge.StartAgent(id);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException) { }
    }

    private async Task WarmUpAsync()
    {
        try
        {
            await bridge.RefreshEnginesAsync();
            Post("engines", bridge.EnginesJson());
            Post("state", bridge.State());
            // A silent release probe runs after the first paint. It never
            // surfaces a network failure; a found version is folded into state
            // so the settings page can show it without another round trip.
            await UpdateManager.CheckSilentAsync();
            Post("state", bridge.State());
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException)
        {
            Post("notice", new JsonObject { ["level"] = "error", ["text"] = error.Message }.ToJsonString());
        }
    }

    private void Post(string channel, string json)
    {
        if (closing || view.CoreWebView2 is null) return;
        try
        {
            view.CoreWebView2.PostWebMessageAsJson(
                $$"""{"channel":"{{channel}}","data":{{json}}}""");
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
    }

    // Injected before any page script runs: wires the three painted window
    // controls to the real window. Kept here rather than in index.html so the
    // design board stays byte-comparable with design/v3-windows.html.
    private const string ShellScript = """
        (function () {
          function send(command) {
            try { chrome.webview.postMessage({ deck: command }); } catch (e) {}
          }
          function wire() {
            var controls = document.querySelectorAll('.titlebar .wctl');
            if (controls.length < 3) return;
            controls[0].addEventListener('click', function () { send('minimize'); });
            controls[1].addEventListener('click', function () { send('maximize'); });
            controls[2].addEventListener('click', function () { send('close'); });
            document.querySelectorAll('.titlebar .wctl').forEach(function (node) {
              node.setAttribute('role', 'button');
            });
          }
          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', wire);
          else wire();
        })();
        """;

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException) { return; }
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("deck", out var deckNode)) return;

        var command = deckNode.GetString();
        // Correlation id is a reserved numeric key (__rid). Never read the
        // business-level `id` here: it can be a project id string, which used
        // to be spliced into the reply envelope as a bare JSON token and make
        // PostWebMessageAsJson throw ArgumentException on the UI thread.
        string? id = root.TryGetProperty("__rid", out var idNode) &&
                     idNode.ValueKind == JsonValueKind.Number
            ? idNode.GetRawText() : null;
        string Text(string key) => root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? "" : "";

        switch (command)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                return;
            case "maximize":
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal : FormWindowState.Maximized;
                return;
            case "close" when !closing:
                Close();
                return;
            case "state":
                Reply(id, bridge.State());
                return;
            case "tasks":
                Reply(id, await bridge.TasksAsync());
                return;
            case "engines":
                await bridge.RefreshEnginesAsync();
                Reply(id, bridge.EnginesJson());
                Post("state", bridge.State());
                return;
            case "logs":
            {
                int limit = root.TryGetProperty("limit", out var limitNode) &&
                            limitNode.TryGetInt32(out var parsed) ? parsed : 300;
                Reply(id, bridge.Logs(limit));
                return;
            }
            case "fingerprint":
                Reply(id, new JsonObject { ["value"] = bridge.Fingerprint() }.ToJsonString());
                return;
            case "pair-start":
                Reply(id, await bridge.PairStartAsync());
                return;
            case "pair-candidate":
                Reply(id, await bridge.PairCandidateAsync(Text("code")));
                return;
            case "pair-confirm":
                Reply(id, await bridge.PairConfirmAsync(Text("code"), Text("fingerprint")));
                Post("state", bridge.State());
                return;
            case "update-check":
                Reply(id, await UpdateManager.CheckAsync());
                return;
            case "update-download":
                Reply(id, await UpdateManager.DownloadAsync(Text("url"), Text("sha256"), Text("version")));
                return;
            case "update-apply":
            {
                var (json, restart) = UpdateManager.Apply(Text("path"), bridge);
                Reply(id, json);
                // Reply first: the page needs the result before this process
                // leaves, and the replacement waits on the single-instance mutex.
                if (restart) Application.Exit();
                return;
            }
            case "open-url":
                // Navigation inside the WebView would replace the app itself;
                // external links belong in the user's default browser.
                Reply(id, bridge.OpenUrl(Text("url")));
                return;
            case "action":
                await ActionAsync(id, root);
                return;
        }
    }

    private async Task ActionAsync(string? id, JsonElement root)
    {
        var name = root.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
        string Arg(string key) => root.TryGetProperty(key, out var node)
            ? node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : node.ToString()
            : "";

        string result;
        switch (name)
        {
            case "revoke":
                result = bridge.RevokePhone(Arg("fingerprint"));
                break;
            case "cancel":
                result = await bridge.CancelAsync(Arg("projectId"), Arg("taskId"));
                break;
            case "open":
                result = bridge.OpenPath(Arg("what"), Arg("projectId"));
                break;
            case "toggle-project":
                result = bridge.UpdateProject(Arg("id"), Arg("enabled"), Arg("mode"), Arg("remove") == "true");
                break;
            case "service":
                result = await bridge.ToggleServiceAsync();
                break;
            case "relay":
                result = await bridge.ConfigureRelayAsync();
                break;
            case "edit-projects":
                result = bridge.EditProjects();
                break;
            case "pick-directory":
                result = bridge.PickDirectory();
                break;
            case "save-projects":
                result = bridge.SaveProjects(Arg("projects"));
                break;
            case "relay-register":
                result = await bridge.RegisterRelayAsync(
                    Arg("endpoint"), Arg("fingerprint"), Arg("token"), Arg("replace") == "true");
                break;
            case "pause":
                result = bridge.ToggleAgent();
                break;
            default:
                result = new JsonObject { ["ok"] = false, ["message"] = "未知操作。" }.ToJsonString();
                break;
        }
        Reply(id, result);
    }

    private void Reply(string? id, string json, string channel = "reply")
    {
        if (closing || view.CoreWebView2 is null) return;
        try
        {
            // `id` is a raw JSON number from OnMessage's __rid field, so the
            // envelope is always well-formed. Do NOT quote it: the page keys
            // `pending` by the same number, so a string would never match.
            var envelope = id is null
                ? $$"""{"channel":"{{channel}}","data":{{json}}}"""
                : $$"""{"channel":"{{channel}}","id":{{id}},"data":{{json}}}""";
            view.CoreWebView2.PostWebMessageAsJson(envelope);
        }
        // PostWebMessageAsJson throws ArgumentException for a malformed JSON
        // string. Swallow it here so a bad reply can never escape onto the
        // WinForms UI thread and pop the JIT debug dialog.
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or ArgumentException) { }
    }
}
