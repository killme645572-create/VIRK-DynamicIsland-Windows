using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Island
{
    // Маленькое окно с QR-кодом под островом. Закрывается по Esc, клику или потере фокуса.
    class QrWindow : Window
    {
        bool _closing;

        public QrWindow(string text, bool[,] m)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = (SystemParameters.PrimaryScreenWidth - 280) / 2;
            Top = 60;

            int n = m.GetLength(0);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        if (!m[y, x]) continue;
                        c.BeginFigure(new Point(x, y), true, true);
                        c.LineTo(new Point(x + 1, y), false, false);
                        c.LineTo(new Point(x + 1, y + 1), false, false);
                        c.LineTo(new Point(x, y + 1), false, false);
                    }
            }
            g.Freeze();

            var qr = new Path { Data = g, Fill = Brushes.Black, Stretch = Stretch.Uniform, Width = 220, Height = 220 };
            RenderOptions.SetEdgeMode(qr, EdgeMode.Aliased);

            // «тихая зона» вокруг кода — 4 модуля, иначе часть сканеров не распознаёт
            double quiet = Math.Max(18, 220.0 / n * 4);

            var caption = text.Replace("\r", " ").Replace("\n", " ");
            if (caption.Length > 36) caption = caption.Substring(0, 36) + "…";

            var stack = new StackPanel();
            stack.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(quiet), Child = qr, CornerRadius = new CornerRadius(8) });
            stack.Children.Add(new TextBlock
            {
                Text = caption, Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Esc или клик — закрыть", Foreground = new SolidColorBrush(Color.FromArgb(153, 255, 255, 255)),
                FontSize = 11, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
            });

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 22)),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(14),
                Child = stack
            };

            MouseLeftButtonDown += (s, e) => SafeClose();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) SafeClose(); };
            Deactivated += (s, e) => SafeClose();
        }

        void SafeClose()
        {
            if (_closing) return;
            _closing = true;
            try { Close(); } catch { }
        }
    }
}
