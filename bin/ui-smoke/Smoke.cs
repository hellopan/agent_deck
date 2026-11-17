using System.Reflection;
using System.Drawing;
using System.Windows.Forms;

internal static class Smoke
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var ui = Assembly.Load("AgentDeck.Tray");
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDeck", "tray-settings.json");
        var hasSettings = File.Exists(settings);
        using var main = Create(ui, "ReviewWindow");
        _ = main.Handle;
        // Detection runs in the background at startup; the screenshots need its
        // result, so the smoke waits for it instead of racing it.
        var detect = main.GetType().GetMethod("DetectToolsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var detection = (Task)detect.Invoke(main, [true, true])!;
        while (!detection.IsCompleted) Application.DoEvents();
        // The real app refreshes every two seconds; the smoke has no message
        // loop, so it asks once after detection has filled the version.
        main.GetType().GetMethod("RefreshStatePage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, null);
        var select = main.GetType().GetMethod("SelectPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var names = new[] { "状态", "任务", "设置" };
        for (int index = 0; index < names.Length; index++)
        {
            select.Invoke(main, [index]);
            main.PerformLayout();
            var title = All(main).OfType<Label>()
                .First(label => label.Text == names[index] && label.Font.Size >= 13);
            if (title.Width < 450) throw new InvalidOperationException("Page width: " + names[index]);
        }
        // The minimum window scrolls rather than clipping, so the visibility
        // check belongs at the designed size; at 900x660 the settings page is
        // taller than the viewport by design.
        main.Size = main.MinimumSize;
        main.PerformLayout();
        select.Invoke(main, [2]);
        main.PerformLayout();
        Render(main, Path.Combine(args[0], "settings-empty.png"));
        var settingsPage = All(main).OfType<TableLayoutPanel>()
            .First(panel => panel.AutoScroll && panel.Controls.Count > 0);
        if (!settingsPage.AutoScroll) throw new InvalidOperationException("Settings page does not scroll.");
        main.ClientSize = new Size(1120, 720);
        main.PerformLayout();
        var logsField = All(main).OfType<TextBox>().Single(box => box.AccessibleName == "任务日志目录");
        if (!OnScreen(logsField, main)) throw new InvalidOperationException("Log directory field hidden at the designed size.");
        var manage = All(main).OfType<Button>().Single(button => button.Text == "管理项目…");
        if (!OnScreen(manage, main)) throw new InvalidOperationException("Manage projects button clipped.");
        select.Invoke(main, [0]);
        // The header is painted, so the machine name is a property now. The
        // screenshot uses a neutral name instead of the real host name.
        var bar = main.GetType().GetField("topBar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        bar.GetType().GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(bar, "DESKTOP");
        if (!hasSettings) Render(main, Path.Combine(args[0], "state.png"));
        select.Invoke(main, [1]);
        main.PerformLayout();
        if (!hasSettings) FillQueue(main, ui);
        Render(main, Path.Combine(args[0], "tasks.png"));
        // Two rows so the permission column is actually rendered, including the
        // writable label. The file lives in the temp directory: the smoke must
        // not leave a policy behind in the repository.
        var policyPath = Path.Combine(Path.GetTempPath(), $"agentdeck-smoke-{Guid.NewGuid():N}.json");
        File.WriteAllText(policyPath,
            """{"projects":[{"id":"web-app","directory":"C:/work/web-app","enabled":true,"mode":"workspace-write"},{"id":"api-gateway","directory":"C:/work/api-gateway","enabled":true}]}""");
        select.Invoke(main, [2]);
        main.PerformLayout();
        Render(main, Path.Combine(args[0], "settings-empty.png"));
        if (!hasSettings)
        {
            // Detection is real here: the smoke waits for it at startup, so this
            // render proves the rows carry a version and a resolved path instead
            // of the "尚未检测" placeholder.
            var version = (Label)main.GetType().GetField("codexLine", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(main)!;
            if (!version.Text.Contains('.')) throw new InvalidOperationException("Codex version not detected: " + version.Text);
            // Point the whitelist at the sample policy so the permission column
            // is visible in this render too, then refresh the card.
            main.GetType().GetField("projectsPath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(main, policyPath);
            main.GetType().GetMethod("RefreshProjects", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, null);
            main.PerformLayout();
            Render(main, Path.Combine(args[0], "settings.png"));
        }
        var pairButton = All(main).OfType<Button>().Single(b => b.Text == "显示配对二维码");
        if (!OnScreen(pairButton, main)) throw new InvalidOperationException("Pairing button clipped.");
        using var relay = Create(ui, "RelayWindow");
        _ = relay.Handle;
        var register = All(relay).OfType<Button>().Single(b => b.Text == "注册电脑");
        var click = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (register.Bottom + register.Parent!.Top > relay.ClientSize.Height)
            throw new InvalidOperationException("Register button clipped.");
        Render(relay, Path.Combine(args[0], "relay.png"));
        click.Invoke(register, [EventArgs.Empty]);
        if (relay.DialogResult != DialogResult.None) throw new InvalidOperationException("Blank relay form accepted.");
        var fields = All(relay).OfType<TextBox>().ToArray();
        fields.Single(t => t.AccessibleName == "Relay 地址").Text = "wss://relay.example.invalid:8443";
        fields.Single(t => t.AccessibleName == "证书 SHA-256 指纹").Text = new string('a', 64);
        var token = fields.Single(t => t.AccessibleName == "注册令牌");
        if (!token.UseSystemPasswordChar) throw new InvalidOperationException("Token unmasked.");
        token.Text = new string('b', 64);
        click.Invoke(register, [EventArgs.Empty]);
        if (relay.DialogResult != DialogResult.OK) throw new InvalidOperationException("Valid relay form rejected.");
        using var project = Create(ui, "ProjectEditor", policyPath);
        Render(project, Path.Combine(args[0], "project.png"));
        File.Delete(policyPath);
        using var pair = Create(ui, "PairingWindow", "{\"sample\":\"for layout only\"}",
            (Func<Task<string>>)(() => Task.FromResult("")), (Func<string, Task<string>>)(_ => Task.FromResult("")));
        var qr = All(pair).OfType<Control>().Single(control => control.AccessibleName == "手机配对二维码");
        var qrImage = qr.GetType().GetField("image", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(qr);
        if (qrImage is null) throw new InvalidOperationException("QR missing.");
        Render(pair, Path.Combine(args[0], "pairing.png"));
        Console.WriteLine("PASS: three-page shell, minimum-size settings fields, Relay validation and token masking, project editor, QR dialog.");
    }

    // Puts two realistic cards in the queue so the owner-painted rows are
    // actually exercised by the screenshot instead of showing an empty panel.
    static void FillQueue(Form main, Assembly ui)
    {
        var itemType = ui.GetType("AgentDeck.Tray.ReviewWindow+QueueItem")!;
        var rowType = ui.GetType("AgentDeck.Tray.ReviewWindow+QueueRow")!;
        var queue = (FlowLayoutPanel)main.GetType()
            .GetField("queueList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var select = main.GetType().GetMethod("SelectTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var samples = new[]
        {
            ("3f2a91d04b7e4c1d8a2f6e9b0c3d5a71", "web-app", @"C:/work/web-app", "running",
                "补全用户模块的单元测试", "为 src/user 下的 5 个模块补单元测试，覆盖正常路径与错误分支，跑通后报告覆盖率。",
                "workspace-write"),
            ("7c1e04b2a9d34f8e8b1c2d3e4f5a6b7c", "api-gateway", @"C:/work/api-gateway", "completed",
                "排查限流中间件的并发问题", "检查 token bucket 在并发下的计数漂移。", "read-only")
        };
        Control? first = null;
        int index = 0;
        foreach (var (id, project, directory, status, summary, prompt, sandbox) in samples)
        {
            var itemCtor = itemType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .OrderByDescending(c => c.GetParameters().Length).First();
            var item = itemCtor.Invoke([id, project, directory, status, DateTimeOffset.Now, summary, prompt, sandbox]);
            var rowCtor = rowType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .First(c => c.GetParameters().Length == 1);
            var row = (Control)rowCtor.Invoke([item]);
            row.Height = 84;
            queue.Controls.Add(row);
            index++;
            first ??= row;
        }
        main.GetType().GetMethod("ResizeQueueRows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, null);
        if (first is not null) select.Invoke(main, [first]);
        var count = main.GetType().GetField("queueCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main) as Label;
        if (count is not null) count.Text = "1 条未结束 · 共 2 条";
        var chip = main.GetType().GetField("queueChip", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main) as Label;
        if (chip is not null) chip.Text = "1";
        main.PerformLayout();
    }

    // An unshown form sits at (-32000, -32000), so absolute screen points are
    // useless. Comparing the two screen rectangles cancels that offset out.
    // Summing Left/Top up the parent chain is the only reliable geometry while
    // the form is still at (-32000, -32000); screen coordinates are meaningless.
    static bool OnScreen(Control control, Form form)
    {
        int x = 0, y = 0;
        for (var current = control; current is not null && current != form; current = current.Parent)
        {
            x += current.Left;
            y += current.Top;
        }
        return x >= 0 && y >= 0 && x + control.Width <= form.ClientSize.Width &&
               y + control.Height <= form.ClientSize.Height;
    }

    static void Render(Form form, string path)
    {
        _ = form.Handle;
        foreach (var control in All(form)) _ = control.Handle;
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        var origin = form.PointToScreen(Point.Empty);
        var client = new Rectangle(origin.X - form.Left, origin.Y - form.Top, form.ClientSize.Width, form.ClientSize.Height);
        using var cropped = image.Clone(client, image.PixelFormat);
        cropped.Save(path);
    }

    static Form Create(Assembly ui, string name, params object[] args) =>
        (Form)Activator.CreateInstance(ui.GetType("AgentDeck.Tray." + name)!,
            BindingFlags.Instance | BindingFlags.NonPublic, null, args, null)!;

    static IEnumerable<Control> All(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in All(child)) yield return descendant;
        }
    }
}
