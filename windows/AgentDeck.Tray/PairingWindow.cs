using QRCoder;

namespace AgentDeck.Tray;

internal sealed class PairingWindow : Form
{
    internal PairingWindow(string payload, Func<Task<string>> load, Func<string, Task<string>> confirm)
    {
        DeckTheme.Form(this, "配对手机");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        ClientSize = new Size(620, 660);
        StartPosition = FormStartPosition.CenterParent;
        using var generator = new QRCodeGenerator();
        using var code = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.L);
        using var png = new PngByteQRCode(code);
        using var imageStream = new MemoryStream(png.GetGraphic(4));
        using var source = Image.FromStream(imageStream);
        var image = new Bitmap(source);
        var layout = DeckTheme.Table(new Padding(28, 20, 28, 20));
        DeckTheme.Row(layout, DeckTheme.Label("配对手机", DeckTheme.Heading), 52);
        DeckTheme.Row(layout, DeckTheme.Label("在 AgentDeck Android 应用中扫码。配对码两分钟内有效。",
            DeckTheme.Body, DeckTheme.Muted), 34);
        // A PictureBox centres the bitmap at its native pixel size, so a short
        // payload produced a 148px code floating in a 560px hole. QrPreview scales
        // to the available square with nearest-neighbour, keeping modules sharp.
        var preview = new QrPreview(image)
        {
            Dock = DockStyle.Fill, BackColor = DeckTheme.Well,
            AccessibleName = "手机配对二维码", Margin = Padding.Empty
        };
        DeckTheme.Row(layout, preview, 100, fill: true);
        var status = DeckTheme.Label("扫完后核对两台设备显示的手机指纹。", DeckTheme.Body, DeckTheme.Muted);
        DeckTheme.Row(layout, status, 52);
        var buttons = DeckTheme.Actions();
        var button = DeckTheme.Button("核对手机指纹", primary: true);
        var close = DeckTheme.Button("关闭");
        close.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([button, close]);
        DeckTheme.Row(layout, buttons, 40);
        Controls.Add(layout);
        CancelButton = close;
        FormClosed += (_, _) => image.Dispose();
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            status.Text = "正在检查手机扫码请求…";
            try
            {
                var fingerprint = (await load()).Trim();
                if (IsDisposed) return;
                var readable = string.Join(" ", fingerprint.Chunk(8).Select(chars => new string(chars)));
                if (MessageBox.Show(this, "请与手机显示的完整指纹核对。\n\n" + readable + "\n\n确认配对？",
                        "确认手机身份", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
                    != DialogResult.Yes)
                {
                    status.Text = "确认前可再次核对手机指纹。";
                    return;
                }
                await confirm(fingerprint);
                if (!IsDisposed) DialogResult = DialogResult.OK;
            }
            catch (Exception)
            {
                if (!IsDisposed) status.Text = "尚无有效扫码请求。确认 Agent 已启动并使用同一配对目录。";
            }
            finally
            {
                if (!IsDisposed) button.Enabled = true;
            }
        };
    }

    private sealed class QrPreview : Control
    {
        private readonly Image image;

        internal QrPreview(Image image)
        {
            this.image = image;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var background = new SolidBrush(BackColor)) g.FillRectangle(background, ClientRectangle);
            int available = Math.Max(40, Math.Min(Width, Height) - 48);
            float ratio = (float)available / image.Width;
            ratio = Math.Min(ratio, 4f);
            int size = Math.Max(40, (int)(image.Width * ratio));
            g.InterpolationMode = ratio >= 1
                ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var target = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
            // The quiet zone is four modules wide, so an 8px radius only trims
            // white — the modules themselves are never clipped.
            using var path = DeckTheme.Rounded(target, 8);
            var previous = g.Clip;
            g.SetClip(path);
            g.DrawImage(image, target);
            g.Clip = previous;
        }
    }
}
