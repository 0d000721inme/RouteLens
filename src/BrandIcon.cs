using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace RouteLens
{
    public static class BrandIcon
    {
#if GENERATE_ICON
        private static int Main(string[] args)
        {
            if (args.Length != 1)
                return 2;
            SaveIco(args[0]);
            return 0;
        }
#endif

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create()
        {
            using (Bitmap bitmap = CreateBitmap(64))
            {
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon icon = Icon.FromHandle(handle))
                        return (Icon)icon.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        public static void SaveIco(string path)
        {
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            byte[][] images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                images[i] = CreateIconImage(sizes[i]);

            using (FileStream stream = File.Create(path))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)sizes.Length);

                int offset = 6 + sizes.Length * 16;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int size = sizes[i];
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }

                for (int i = 0; i < images.Length; i++)
                    writer.Write(images[i]);
            }
        }

        private static byte[] CreateIconImage(int size)
        {
            using (Bitmap bitmap = CreateBitmap(size))
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int rowBytes = size * 4;
                int maskStride = ((size + 31) / 32) * 4;
                int maskBytes = maskStride * size;
                writer.Write(40);
                writer.Write(size);
                writer.Write(size * 2);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(0);
                writer.Write(rowBytes * size + maskBytes);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);

                for (int y = size - 1; y >= 0; y--)
                {
                    for (int x = 0; x < size; x++)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        writer.Write(pixel.B);
                        writer.Write(pixel.G);
                        writer.Write(pixel.R);
                        writer.Write(pixel.A);
                    }
                }

                writer.Write(new byte[maskBytes]);
                return stream.ToArray();
            }
        }

        private static Bitmap CreateBitmap(int size)
        {
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
                Draw(graphics, size, 1f);
            return bitmap;
        }

        internal static void Draw(Graphics graphics, int size, float progress)
        {
            using (Pen route = new Pen(Color.FromArgb(255, 100, 224, 194), Math.Max(1.5f, size * 0.065f)))
            using (SolidBrush background = new SolidBrush(Color.FromArgb(255, 18, 34, 45)))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                FillRoundedRectangle(graphics, background, size * 0.08f, size * 0.08f,
                    size * 0.84f, size * 0.84f, size * 0.2f);

                using (Pen rim = new Pen(Color.FromArgb(255, 203, 237, 233), Math.Max(1.4f, size * 0.045f)))
                    graphics.DrawEllipse(rim, size * 0.22f, size * 0.19f, size * 0.52f, size * 0.52f);

                PointF[] path = new PointF[] {
                    new PointF(size * 0.32f, size * 0.61f),
                    new PointF(size * 0.45f, size * 0.49f),
                    new PointF(size * 0.56f, size * 0.56f),
                    new PointF(size * 0.7f, size * 0.37f)
                };
                route.StartCap = LineCap.Round;
                route.EndCap = LineCap.Round;
                route.LineJoin = LineJoin.Round;
                if (progress >= 1f)
                    graphics.DrawLines(route, path);
                else if (progress > 0f)
                {
                    float position = progress * (path.Length - 1);
                    int segment = (int)position;
                    float fraction = position - segment;
                    PointF[] visible = new PointF[segment + 2];
                    Array.Copy(path, visible, segment + 1);
                    PointF from = path[segment], to = path[segment + 1];
                    visible[segment + 1] = new PointF(from.X + (to.X - from.X) * fraction, from.Y + (to.Y - from.Y) * fraction);
                    graphics.DrawLines(route, visible);
                }
                if (progress > .7f)
                {
                    float dot = Math.Max(2.5f, size * 0.105f);
                    int alpha = (int)(255 * Math.Min(1, (progress - .7f) / .3f));
                    using (SolidBrush point = new SolidBrush(Color.FromArgb(alpha, 255, 183, 91)))
                        graphics.FillEllipse(point, size * 0.64f - dot / 2, size * 0.31f - dot / 2, dot, dot);
                }
            }
        }

        private static void FillRoundedRectangle(Graphics graphics, Brush brush,
            float x, float y, float width, float height, float radius)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
                path.AddArc(x + width - radius * 2, y, radius * 2, radius * 2, 270, 90);
                path.AddArc(x + width - radius * 2, y + height - radius * 2, radius * 2, radius * 2, 0, 90);
                path.AddArc(x, y + height - radius * 2, radius * 2, radius * 2, 90, 90);
                path.CloseFigure();
                graphics.FillPath(brush, path);
            }
        }
    }
}
