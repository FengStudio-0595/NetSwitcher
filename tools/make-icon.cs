// 用 DSH 鲸鱼余额插件的原图（DSniang1.png）生成 NetSwitcher 图标
//
//   make-icon.exe <源图.png> <输出目录>
//
// 源图是 610x610、32bpp 带 Alpha，四角完全透明，内容包围盒 565x600。
// 做法：垫一层圆角方形底色，再把角色按原画布等比铺满并裁进圆角里。
// 之所以要垫底色而不是直接用透明底图：图标在任务栏只有 16~24px，
// 透明底的角色会缩成一小团蓝色墨点，加底色才能保证一眼认出。
//
// 输出两套配色供挑选：
//   app-blue.ico  / preview-blue.png   （蓝色渐变底，跟程序主题色一致）
//   app-white.ico / preview-white.png  （浅色底，跟角色本身的白头饰协调）

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIcon
{
    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// 小尺寸专用：只框住脸的紧裁剪框。
    /// 原画细节太多，缩到 16~32px 会糊成一团蓝；把脸放大后至少还认得出是个人。
    /// 中心略偏下（脸在画面中下部），边长取原图的 68%。
    /// </summary>
    private static Rectangle CropRect(Image src)
    {
        double side = 0.68;
        double cx = 0.50, cy = 0.65;
        int w = (int)Math.Round(src.Width * side);
        int h = (int)Math.Round(src.Height * side);
        int x = (int)Math.Round(src.Width * cx - w / 2.0);
        int y = (int)Math.Round(src.Height * cy - h / 2.0);
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x + w > src.Width) x = src.Width - w;
        if (y + h > src.Height) y = src.Height - h;
        return new Rectangle(x, y, w, h);
    }

    /// <summary>variant: 0 = 蓝底，1 = 白底。zoom = true 时用紧裁剪。</summary>
    private static Bitmap Render(int size, Image src, int variant, bool zoom)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;

            var full = new Rectangle(0, 0, size, size);
            int radius = (int)Math.Round(size * 0.22);

            using (GraphicsPath p = RoundRect(full, radius))
            {
                if (variant == 0)
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(
                               full, Color.FromArgb(0x64, 0xB5, 0xF6),
                               Color.FromArgb(0x0D, 0x47, 0xA1), 62f))
                        g.FillPath(b, p);
                }
                else
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(
                               full, Color.White, Color.FromArgb(0xDC, 0xEB, 0xFF), 62f))
                        g.FillPath(b, p);
                }

                // 把角色裁进圆角里
                Rectangle srcRect = zoom
                    ? CropRect(src)
                    : new Rectangle(0, 0, src.Width, src.Height);

                GraphicsState st = g.Save();
                g.SetClip(p);
                g.DrawImage(src, full, srcRect, GraphicsUnit.Pixel);
                g.Restore(st);
            }
        }
        return bmp;
    }

    /// <summary>尺寸 <= 这个值的用紧裁剪。</summary>
    private const int ZoomMax = 32;

    private static void WriteIco(string path, int[] sizes, Image src, int variant)
    {
        var blobs = new System.Collections.Generic.List<byte[]>();
        foreach (int s in sizes)
        {
            using (Bitmap bmp = Render(s, src, variant, s <= ZoomMax))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                blobs.Add(ms.ToArray());
            }
        }

        using (var fs = new FileStream(path, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(blobs[i].Length);
                w.Write(offset);
                offset += blobs[i].Length;
            }
            foreach (byte[] b in blobs) w.Write(b);
        }
    }

    private static void WriteStrip(string path, Image src, int variant)
    {
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128 };
        int W = 8, H = 0;
        foreach (int s in sizes) { W += s * 2 + 8; if (s * 2 > H) H = s * 2; }

        using (var strip = new Bitmap(W, H + 16))
        using (Graphics g = Graphics.FromImage(strip))
        {
            g.Clear(Color.FromArgb(245, 245, 245));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int x = 8;
            foreach (int s in sizes)
            {
                using (Bitmap b = Render(s, src, variant, s <= ZoomMax))
                    g.DrawImage(b, x, 8 + (H - s * 2) / 2, s * 2, s * 2);
                x += s * 2 + 8;
            }
            strip.Save(path, ImageFormat.Png);
        }
    }

    private static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: make-icon.exe <源图.png> <输出目录> [variant]");
            Console.WriteLine("  variant: blue(默认) / white");
            return;
        }
        string srcPath = args[0];
        string dir = args[1];
        int variant = (args.Length > 2 && args[2].ToLowerInvariant() == "white") ? 1 : 0;
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };

        Console.WriteLine("源图: " + srcPath);
        using (Image src = Image.FromFile(srcPath))
        {
            Console.WriteLine("  " + src.Width + "x" + src.Height + "  " + src.PixelFormat);
            Console.WriteLine("  底色: " + (variant == 0 ? "蓝色渐变" : "浅色渐变")
                              + "，<= " + ZoomMax + "px 用紧裁剪 " + CropRect(src));

            string ico = Path.Combine(dir, "app.ico");
            WriteIco(ico, sizes, src, variant);
            Console.WriteLine("写出 " + ico);

            string prev = Path.Combine(dir, "preview.png");
            using (Bitmap b = Render(256, src, variant, false)) b.Save(prev, ImageFormat.Png);
            Console.WriteLine("写出 " + prev);

            string strip = Path.Combine(dir, "strip.png");
            WriteStrip(strip, src, variant);
            Console.WriteLine("写出 " + strip + "  (16/24/32 紧裁剪，48/64/128 完整图，各放大 2 倍)");
        }
    }
}
