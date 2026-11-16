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
        All(main).OfType<Label>().First(label => label.Text == Environment.MachineName).Text = "DESKTOP";
        if (!hasSettings) Render(main, Path.Combine(args[0], "state.png"));
        select.Invoke(main, [1]);
        main.PerformLayout();
        if (!hasSettings) FillQueue(main, ui);
        Render(main, Path.Combine(args[0], "tasks.png"));
        select.Invoke(main, [2]);
        main.PerformLayout();
        if (!hasSettings)
        {
            Render(main, Path.Combine(args[0], "settings.png"));
            // Show what a real detection looks like: a version, the full path
            // and a green dot. The layout bug this guards against is a long
            // path pushing the capability tags out of the row.
            var theme = ui.GetType("AgentDeck.Tray.DeckTheme")!;
            var good = (Color)theme.GetField("Good", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
            var lines = All(main).OfType<Label>().Where(label => label.Text == "尚未检测").ToArray();
            if (lines.Length != 3) throw new InvalidOperationException("Detection rows: " + lines.Length);
            lines[0].Text = "0.167.0-alpha.4.1";
            lines[1].Text = "0.42.3";
            lines[2].Text = "1.7.2";
            foreach (var (field, value) in new[]
                     {
                         ("codexPathLine", @"…\9e1da79431da526b\codex.exe"),
                         ("cursorPathLine", @"…\cursor\Cursor.exe"),
                         ("buddyPathLine", @"…\WorkBuddy\WorkBuddy.exe")
                     })
            {
                var label = (Label)main.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(main)!;
                label.Text = value;
            }
            foreach (var name in new[] { "codexDot", "cursorDot", "buddyDot" })
            {
                var dot = main.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                dot.GetType().GetProperty("Color", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(dot, good);
            }
            main.PerformLayout();
            Render(main, Path.Combine(args[0], "settings-detected.png"));
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
        using var project = Create(ui, "ProjectEditor", Path.Combine(args[0], "smoke-only.json"));
        using var pair = Create(ui, "PairingWindow", "{\"sample\":\"for layout only\"}",
            (Func<Task<string>>)(() => Task.FromResult("")), (Func<string, Task<string>>)(_ => Task.FromResult("")));
        if (All(pair).OfType<PictureBox>().Single().Image is null) throw new InvalidOperationException("QR missing.");
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
                "补全用户模块的单元测试", "为 src/user 下的 5 个模块补单元测试，覆盖正常路径与错误分支，跑通后报告覆盖率。"),
            ("7c1e04b2a9d34f8e8b1c2d3e4f5a6b7c", "api-gateway", @"C:/work/api-gateway", "completed",
                "排查限流中间件的并发问题", "检查 token bucket 在并发下的计数漂移。")
        };
        Control? first = null;
        int index = 0;
        foreach (var (id, project, directory, status, summary, prompt) in samples)
        {
            var itemCtor = itemType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .OrderByDescending(c => c.GetParameters().Length).First();
            var item = itemCtor.Invoke([id, project, directory, status, DateTimeOffset.Now, summary, prompt]);
            var rowCtor = rowType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .First(c => c.GetParameters().Length == 1);
            var row = (Control)rowCtor.Invoke([item]);
            row.Height = 78;
            queue.Controls.Add(row);
            index++;
            first ??= row;
        }
        main.GetType().GetMethod("ResizeQueueRows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, null);
        if (first is not null) select.Invoke(main, [first]);
        var count = main.GetType().GetField("queueCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main) as Label;
        if (count is not null) count.Text = "1 条未结束 · 共 2 条";
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
