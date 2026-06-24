using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class MakeIcon
{
    static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2;
        GraphicsPath p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Render(int S)
    {
        Bitmap bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Rectangle full = new Rectangle(0, 0, S, S);
            int rad = (int)(S * 0.22f);
            using (GraphicsPath path = Round(full, rad))
            using (LinearGradientBrush br = new LinearGradientBrush(full,
                Color.FromArgb(124, 58, 237), Color.FromArgb(168, 85, 247), 55f))
                g.FillPath(br, path);

            float X = 0, Y = 0;
            Color socket = Color.FromArgb(74, 28, 120);
            using (SolidBrush wb = new SolidBrush(Color.White))
            {
                g.FillEllipse(wb, X + S * 0.24f, Y + S * 0.17f, S * 0.52f, S * 0.47f);
                Rectangle jaw = new Rectangle((int)(X + S * 0.34f), (int)(Y + S * 0.50f), (int)(S * 0.32f), (int)(S * 0.26f));
                using (GraphicsPath jp = Round(jaw, (int)(S * 0.07f))) g.FillPath(wb, jp);
            }
            using (SolidBrush sb = new SolidBrush(socket))
            {
                float er = S * 0.145f;
                g.FillEllipse(sb, X + S * 0.295f, Y + S * 0.32f, er, er);
                g.FillEllipse(sb, X + S * 0.56f, Y + S * 0.32f, er, er);
                PointF[] nose = {
                    new PointF(X + S * 0.5f, Y + S * 0.49f),
                    new PointF(X + S * 0.452f, Y + S * 0.59f),
                    new PointF(X + S * 0.548f, Y + S * 0.59f) };
                g.FillPolygon(sb, nose);
            }
            using (Pen pen = new Pen(socket, Math.Max(1f, S * 0.03f)))
            {
                float ty = Y + S * 0.52f, by = Y + S * 0.745f, cx = X + S * 0.5f;
                g.DrawLine(pen, cx, ty, cx, by);
                g.DrawLine(pen, X + S * 0.425f, ty, X + S * 0.425f, by);
                g.DrawLine(pen, X + S * 0.575f, ty, X + S * 0.575f, by);
            }
        }
        return bmp;
    }

    static byte[] DibFrame(Bitmap b)
    {
        int S = b.Width;
        MemoryStream ms = new MemoryStream();
        BinaryWriter bw = new BinaryWriter(ms);
        bw.Write(40);            // biSize
        bw.Write(S);             // biWidth
        bw.Write(S * 2);         // biHeight (XOR + AND)
        bw.Write((short)1);      // planes
        bw.Write((short)32);     // bitcount
        bw.Write(0);             // compression BI_RGB
        bw.Write(0);             // sizeImage
        bw.Write(0); bw.Write(0);
        bw.Write(0); bw.Write(0);
        for (int y = S - 1; y >= 0; y--)
            for (int x = 0; x < S; x++)
            {
                Color c = b.GetPixel(x, y);
                bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
            }
        int rowBytes = ((S + 31) / 32) * 4;
        for (int y = S - 1; y >= 0; y--)
        {
            byte[] row = new byte[rowBytes];
            for (int x = 0; x < S; x++)
            {
                Color c = b.GetPixel(x, y);
                if (c.A < 128) row[x >> 3] |= (byte)(0x80 >> (x & 7));
            }
            bw.Write(row);
        }
        return ms.ToArray();
    }

    static void Main()
    {
        int[] sizes = new int[] { 16, 32, 48, 64, 128, 256 };
        byte[][] frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (Bitmap b = Render(sizes[i]))
                frames[i] = DibFrame(b);

        string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skull.ico");
        using (FileStream fs = new FileStream(outPath, FileMode.Create))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);
            bw.Write((short)1);
            bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(frames[i].Length);
                bw.Write(offset);
                offset += frames[i].Length;
            }
            for (int i = 0; i < sizes.Length; i++) bw.Write(frames[i]);
        }
        Console.WriteLine("skull.ico criado: " + outPath);
    }
}
