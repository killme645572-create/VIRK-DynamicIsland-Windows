using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Island
{
    static class Shot
    {
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        // снимок основного экрана (в физических пикселях)
        public static System.Drawing.Bitmap Capture()
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            var bmp = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size);
            return bmp;
        }

        public static BitmapSource ToSource(System.Drawing.Bitmap bmp)
        {
            var h = bmp.GetHbitmap();
            try
            {
                var s = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                s.Freeze();
                return s;
            }
            finally { DeleteObject(h); }
        }
    }
}
