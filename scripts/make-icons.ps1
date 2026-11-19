param(
    [Parameter(Mandatory = $true)][string]$Source
)

# Build every binary icon asset from one source image.
#
#   .\scripts\make-icons.ps1 -Source C:\path\to\logo.jpg
#
# The source is the black/white split "AD" mark. The white margin around the
# rounded square is knocked out (flood fill from the edges only, so the white
# half *inside* the mark stays opaque); everything else is kept at 1:1 until
# the final high-quality downscale. Outputs:
#
#   assets/agentdeck-logo-source.jpg   original, kept for regeneration
#   assets/agentdeck-logo.png          1024px master, transparent outer margin
#   assets/agentdeck.ico               Windows icon, 9 sizes, 32bpp DIB frames
#   assets/agentdeck-logo-32.png        title-bar mark for the WebView2 page
#   assets/icon-preview.png             light/dark sanity check
#   android/app/src/main/res/mipmap-*/  launcher / round / adaptive foreground
#
# DIB frames (not PNG-compressed ones) are used for the .ico on purpose:
# System.Drawing.Icon, the C# compiler's Win32 resource writer and Explorer
# all agree on them, including on the alpha channel.

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -Language CSharp -ReferencedAssemblies @('System.Drawing', 'System.Core') -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class AgentDeckIconTool
{
    public static Bitmap Load(string path)
    {
        using (Image src = Image.FromFile(path))
        {
            Bitmap bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.DrawImageUnscaled(src, 0, 0);
            }
            return bmp;
        }
    }

    static int White(int argb)
    {
        int r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
        return Math.Min(r, Math.Min(g, b));
    }

    // Only near-white pixels reachable from the image border are faded out.
    // The black outline around the mark encloses its white half, so that half
    // cannot be reached and stays solid white.
    public static int KnockOutOuterWhite(Bitmap bmp, int threshold, int fadeSpan)
    {
        int w = bmp.Width, h = bmp.Height;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int[] px = new int[w * h];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bool[] outer = new bool[w * h];
        Queue<int> q = new Queue<int>();
        for (int x = 0; x < w; x++) { Seed(px, outer, q, w, x, 0, threshold); Seed(px, outer, q, w, x, h - 1, threshold); }
        for (int y = 0; y < h; y++) { Seed(px, outer, q, w, 0, y, threshold); Seed(px, outer, q, w, w - 1, y, threshold); }
        int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % w, y = i / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dx[k], ny = y + dy[k];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                Seed(px, outer, q, w, nx, ny, threshold);
            }
        }
        int changed = 0;
        for (int i = 0; i < px.Length; i++)
        {
            if (!outer[i]) continue;
            int white = White(px[i]);
            // JPEG noise leaves a lot of 230-250 "almost white" pixels. Anything
            // above threshold + fadeSpan must be fully gone; only the last few
            // grey steps near the black outline keep a soft edge.
            int a = 255 - (white - threshold) * 255 / fadeSpan;
            if (a < 0) a = 0;
            if (a > 255) a = 255;
            px[i] = (px[i] & 0x00FFFFFF) | (a << 24);
            changed++;
        }
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
        return changed;
    }

    static void Seed(int[] px, bool[] outer, Queue<int> q, int w, int x, int y, int threshold)
    {
        int i = y * w + x;
        if (outer[i] || White(px[i]) < threshold) return;
        outer[i] = true;
        q.Enqueue(i);
    }

    public static Bitmap Square(Bitmap src, int padPercent)
    {
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        BitmapData data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] px = new int[src.Width * src.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        src.UnlockBits(data);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                if (((px[y * src.Width + x] >> 24) & 255) > 8)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        int side = Math.Max(bw, bh);
        int pad = side * padPercent / 100;
        side += pad * 2;
        Bitmap canvas = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle((side - bw) / 2, (side - bh) / 2, bw, bh),
                        new Rectangle(minX, minY, bw, bh), GraphicsUnit.Pixel);
        }
        return canvas;
    }

    public static Bitmap Resize(Bitmap src, int size)
    {
        Bitmap dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(dst))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, size, size),
                        new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
        }
        return dst;
    }

    // Adaptive foreground / round launcher: the mark sits on a transparent
    // 108dp canvas (or a white disc) at a fraction of the canvas width.
    public static Bitmap OnCanvas(Bitmap logo, int size, float fraction, bool whiteDisc)
    {
        Bitmap dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(dst))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (whiteDisc)
            {
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, 0, 0, size - 1, size - 1);
            }
            float inner = size * fraction;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(logo, new RectangleF((size - inner) / 2f, (size - inner) / 2f, inner, inner),
                        new RectangleF(0, 0, logo.Width, logo.Height), GraphicsUnit.Pixel);
        }
        return dst;
    }

    public static void SavePng(Bitmap bmp, string path)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, ImageFormat.Png);
    }

    public static string Stats(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, transparent = 0, coreTransparent = 0, core = 0;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] px = new int[w * h];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool clear = ((px[y * w + x] >> 24) & 255) < 8;
                if (clear) transparent++;
                if (x > w * 0.3 && x < w * 0.7 && y > h * 0.3 && y < h * 0.7)
                {
                    core++;
                    if (clear) coreTransparent++;
                }
            }
        return string.Format("transparent {0:F1}% / centre 40% transparent {1:F2}%",
                             100.0 * transparent / (w * h), 100.0 * coreTransparent / core);
    }

    public static void Preview(Bitmap logo, string path)
    {
        int w = 560, h = 280, s = 220;
        Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(dst))
        {
            using (SolidBrush light = new SolidBrush(Color.FromArgb(245, 246, 247)))
                g.FillRectangle(light, 0, 0, w / 2, h);
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(28, 31, 36)))
                g.FillRectangle(dark, w / 2, 0, w / 2, h);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(logo, new Rectangle((w / 2 - s) / 2, (h - s) / 2, s, s),
                        new Rectangle(0, 0, logo.Width, logo.Height), GraphicsUnit.Pixel);
            g.DrawImage(logo, new Rectangle(w / 2 + (w / 2 - s) / 2, (h - s) / 2, s, s),
                        new Rectangle(0, 0, logo.Width, logo.Height), GraphicsUnit.Pixel);
        }
        SavePng(dst, path);
    }

    public static void WriteIco(string path, int[] sizes, Bitmap[] framesSrc)
    {
        byte[][] frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) frames[i] = Dib(framesSrc[i]);
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)frames[i].Length);
                bw.Write((uint)offset);
                offset += frames[i].Length;
            }
            for (int i = 0; i < frames.Length; i++) bw.Write(frames[i]);
            bw.Flush();
            File.WriteAllBytes(path, ms.ToArray());
        }
    }

    static byte[] Dib(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        BitmapData data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] px = new int[w * h];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        src.UnlockBits(data);
        int andRow = ((w + 31) / 32) * 4;
        int xorLen = w * h * 4, andLen = andRow * h;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write((uint)40);
            bw.Write((int)w);
            bw.Write((int)(2 * h));
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)0);
            bw.Write((uint)(xorLen + andLen));
            bw.Write((int)0);
            bw.Write((int)0);
            bw.Write((uint)0);
            bw.Write((uint)0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    int c = px[y * w + x];
                    bw.Write((byte)(c & 255));
                    bw.Write((byte)((c >> 8) & 255));
                    bw.Write((byte)((c >> 16) & 255));
                    bw.Write((byte)((c >> 24) & 255));
                }
            bw.Write(new byte[andLen]);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
'@

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Copy-Item -LiteralPath $Source -Destination (Join-Path $assets 'agentdeck-logo-source.jpg') -Force

$raw = [AgentDeckIconTool]::Load($Source)
$faded = [AgentDeckIconTool]::KnockOutOuterWhite($raw, 190, 45)
Write-Host "outer white pixels faded: $faded"
$master = [AgentDeckIconTool]::Square($raw, 3)
Write-Host "master: $([AgentDeckIconTool]::Stats($master))"

$logo1024 = [AgentDeckIconTool]::Resize($master, 1024)
[AgentDeckIconTool]::SavePng($logo1024, (Join-Path $assets 'agentdeck-logo.png'))
[AgentDeckIconTool]::SavePng([AgentDeckIconTool]::Resize($master, 32), (Join-Path $assets 'agentdeck-logo-32.png'))
[AgentDeckIconTool]::Preview($logo1024, (Join-Path $assets 'icon-preview.png'))

$sizes = [int[]]@(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [System.Drawing.Bitmap[]]::new($sizes.Length)
for ($i = 0; $i -lt $sizes.Length; $i++) { $frames[$i] = [AgentDeckIconTool]::Resize($master, $sizes[$i]) }
[AgentDeckIconTool]::WriteIco((Join-Path $assets 'agentdeck.ico'), $sizes, $frames)
foreach ($frame in $frames) { $frame.Dispose() }

$density = @{ mdpi = 48; hdpi = 72; xhdpi = 96; xxhdpi = 144; xxxhdpi = 192 }
$foreground = @{ mdpi = 108; hdpi = 162; xhdpi = 216; xxhdpi = 324; xxxhdpi = 432 }
foreach ($d in @('mdpi', 'hdpi', 'xhdpi', 'xxhdpi', 'xxxhdpi')) {
    $dir = Join-Path $root "android/app/src/main/res/mipmap-$d"
    $launcher = [AgentDeckIconTool]::Resize($master, $density[$d])
    $round = [AgentDeckIconTool]::OnCanvas($master, $density[$d], 0.80, $true)
    $fg = [AgentDeckIconTool]::OnCanvas($master, $foreground[$d], 0.60, $false)
    [AgentDeckIconTool]::SavePng($launcher, (Join-Path $dir 'ic_launcher.png'))
    [AgentDeckIconTool]::SavePng($round, (Join-Path $dir 'ic_launcher_round.png'))
    [AgentDeckIconTool]::SavePng($fg, (Join-Path $dir 'ic_launcher_foreground.png'))
    $launcher.Dispose(); $round.Dispose(); $fg.Dispose()
    Write-Host "$d : launcher $($density[$d]) / foreground $($foreground[$d])"
}

$logo1024.Dispose(); $master.Dispose(); $raw.Dispose()
Write-Host 'done'
