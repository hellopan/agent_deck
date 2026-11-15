namespace AgentDeck.Tray;

internal sealed class RelayWindow : Form
{
    private readonly TextBox endpoint = new() { PlaceholderText = "wss://relay.example.com:8443" };
    private readonly TextBox fingerprint = new();
    private readonly TextBox token = new() { UseSystemPasswordChar = true };

    internal string Endpoint => endpoint.Text.Trim();
    internal string Fingerprint => fingerprint.Text.Replace(":", "").Replace(" ", "").Trim().ToLowerInvariant();
    internal string Token => token.Text.Trim();

    internal RelayWindow()
    {
        DeckTheme.Form(this, "配置云服务器");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        ClientSize = new Size(576, 436);

        var layout = DeckTheme.Table(new Padding(28, 18, 28, 16));
        DeckTheme.Row(layout, DeckTheme.Label("配置云服务器", DeckTheme.Heading), 51);
        DeckTheme.Row(layout, DeckTheme.Label("使用你的 AgentDeck Relay 注册这台电脑。", DeckTheme.Body, DeckTheme.Muted), 33);
        DeckTheme.Row(layout, new Panel(), 13);
        AddField("Relay 地址", endpoint);
        AddField("证书 SHA-256 指纹", fingerprint);
        AddField("注册令牌", token);
        void AddField(string name, TextBox field)
        {
            DeckTheme.Row(layout, DeckTheme.Label(name), 27);
            DeckTheme.Row(layout, DeckTheme.Input(field, name), 37);
            DeckTheme.Row(layout, new Panel(), 9);
        }

        var feedback = DeckTheme.Label("", DeckTheme.Small, DeckTheme.Warning);
        DeckTheme.Row(layout, feedback, 37);
        var buttons = DeckTheme.Actions();
        var register = DeckTheme.Button("注册电脑", primary: true);
        var cancel = DeckTheme.Button("取消");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([register, cancel]);
        DeckTheme.Row(layout, buttons, 39);
        Controls.Add(layout);
        AcceptButton = register;
        CancelButton = cancel;

        register.Click += (_, _) =>
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.Host.Length == 0)
            {
                feedback.Text = "请输入以 wss:// 开头的 Relay 地址。";
                endpoint.Focus();
                return;
            }
            if (Fingerprint.Length != 64 || !Fingerprint.All(Uri.IsHexDigit))
            {
                feedback.Text = "请输入完整的 64 位十六进制证书指纹。";
                fingerprint.Focus();
                return;
            }
            if (Token.Length != 64 || !Token.All(Uri.IsHexDigit))
            {
                feedback.Text = "请输入云端生成的 64 位十六进制注册令牌。";
                token.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        };
        Shown += (_, _) => endpoint.Focus();
    }
}
