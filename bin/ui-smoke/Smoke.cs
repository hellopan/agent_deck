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
        var names = new[] { "连接", "任务", "项目", "日志", "设置" };
        for (int index = 0; index < names.Length; index++)
        {
            select.Invoke(main, [index]);
            main.PerformLayout();
            var page = All(main).OfType<Label>().First(label => label.Text == (index == 1 ? "任务审核" : names[index]));
            if (page.Width < 450) throw new InvalidOperationException("Page width: " + names[index]);
        }
        main.Size = main.MinimumSize;
        main.PerformLayout();
        var save = All(main).OfType<Button>().Single(b => b.Text == "保存设置");
        var card = save.Parent!.Parent!;
        if (save.Bottom + save.Parent.Top > card.ClientSize.Height)
            throw new InvalidOperationException("Save Settings button clipped.");
        main.ClientSize = new Size(1120, 720);
        select.Invoke(main, [0]);
        All(main).OfType<Label>().Single(label => label.Text == Environment.MachineName).Text = "DESKTOP";
        if (!hasSettings) Render(main, Path.Combine(args[0], "connection.png"));
        select.Invoke(main, [4]);
        if (!hasSettings) Render(main, Path.Combine(args[0], "settings.png"));
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
        Console.WriteLine("PASS: navigation layout, minimum-size Settings actions, Relay validation and token masking, project editor, QR dialog.");
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

