using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Island
{
    // Полноэкранное окно поверх снимка экрана: выделение области мышью или выбор одной точки (пипетка).
    // Esc — отмена.
    class RegionSelector : Window
    {
        public System.Drawing.Rectangle? Result;
        public System.Drawing.Point? Picked;

        readonly System.Drawing.Bitmap _bmp;
        readonly bool _pick;
        readonly Canvas _canvas = new Canvas();
        readonly Rectangle _sel = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Visibility = Visibility.Collapsed
        };
        Point _start;
        bool _drag;

        public RegionSelector(System.Drawing.Bitmap bmp, bool pick)
        {
            _bmp = bmp; _pick = pick;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Cursor = Cursors.Cross;

            var grid = new Grid();
            grid.Children.Add(new Image { Source = Shot.ToSource(bmp), Stretch = Stretch.Fill });
            grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)) });
            _canvas.Children.Add(_sel);
            grid.Children.Add(_canvas);
            Content = grid;

            MouseLeftButtonDown += (s, e) =>
            {
                _start = e.GetPosition(this);
                if (_pick) return;
                _drag = true;
                CaptureMouse();
                _sel.Visibility = Visibility.Visible;
                UpdateSel(_start);
            };
            MouseMove += (s, e) => { if (_drag) UpdateSel(e.GetPosition(this)); };
            MouseLeftButtonUp += (s, e) => Finish(e.GetPosition(this));
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (s, e) => { Activate(); Focus(); };
        }

        void UpdateSel(Point p)
        {
            Canvas.SetLeft(_sel, Math.Min(_start.X, p.X));
            Canvas.SetTop(_sel, Math.Min(_start.Y, p.Y));
            _sel.Width = Math.Abs(p.X - _start.X);
            _sel.Height = Math.Abs(p.Y - _start.Y);
        }

        static int Clamp(double v, int max) => (int)Math.Max(0, Math.Min(max, v));

        void Finish(Point p)
        {
            // окно в DIP, снимок в пикселях — переводим координаты
            double kx = _bmp.Width / ActualWidth, ky = _bmp.Height / ActualHeight;

            if (_pick)
            {
                Picked = new System.Drawing.Point(Clamp(p.X * kx, _bmp.Width - 1), Clamp(p.Y * ky, _bmp.Height - 1));
                Close();
                return;
            }
            if (!_drag) return;
            _drag = false;
            ReleaseMouseCapture();

            int x1 = Clamp(Math.Min(_start.X, p.X) * kx, _bmp.Width);
            int y1 = Clamp(Math.Min(_start.Y, p.Y) * ky, _bmp.Height);
            int x2 = Clamp(Math.Max(_start.X, p.X) * kx, _bmp.Width);
            int y2 = Clamp(Math.Max(_start.Y, p.Y) * ky, _bmp.Height);
            if (x2 - x1 >= 4 && y2 - y1 >= 4)
                Result = new System.Drawing.Rectangle(x1, y1, x2 - x1, y2 - y1);
            Close();
        }
    }
}
