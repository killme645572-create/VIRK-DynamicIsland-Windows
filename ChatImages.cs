using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island
{
    // Один ход диалога: текст и (необязательно) приложенные картинки.
    public class ChatTurn
    {
        public string Role;
        public string Text;
        public List<ChatImage> Images;
    }

    // Приложенная к сообщению картинка: уменьшенный JPEG для отправки и миниатюра для показа в чате.
    public class ChatImage
    {
        const int MaxSide = 1280;     // длинная сторона после уменьшения — чтобы запрос был лёгким
        const int ThumbSide = 240;

        public BitmapSource Thumb { get; private set; }
        public string Base64 { get; private set; }
        public string DataUrl => "data:image/jpeg;base64," + Base64;

        public static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".gif" ||
                   ext == ".tif" || ext == ".tiff" || ext == ".webp";
        }

        public static ChatImage FromFile(string path)
        {
            var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource src = frame;
            try
            {
                // фото с телефона часто «лежат на боку» и поворачиваются меткой EXIF — применяем её
                if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation"))
                {
                    int o = Convert.ToInt32(md.GetQuery("System.Photo.Orientation"));
                    double angle = o == 3 ? 180 : o == 6 ? 90 : o == 8 ? 270 : 0;
                    if (angle != 0) src = new TransformedBitmap(frame, new RotateTransform(angle));
                }
            }
            catch { }
            return FromSource(src);
        }

        public static ChatImage FromSource(BitmapSource src)
        {
            double k = Math.Min(1.0, MaxSide / (double)Math.Max(src.PixelWidth, src.PixelHeight));
            int w = Math.Max(1, (int)Math.Round(src.PixelWidth * k));
            int h = Math.Max(1, (int)Math.Round(src.PixelHeight * k));

            // белая подложка вместо прозрачности, иначе у PNG фон станет чёрным
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawImage(src, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();

            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string b64;
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                b64 = Convert.ToBase64String(ms.ToArray());
            }

            double tk = Math.Min(1.0, ThumbSide / (double)Math.Max(w, h));
            BitmapSource thumb = tk < 1 ? (BitmapSource)new TransformedBitmap(rtb, new ScaleTransform(tk, tk)) : rtb;
            thumb.Freeze();

            return new ChatImage { Base64 = b64, Thumb = thumb };
        }
    }
}
