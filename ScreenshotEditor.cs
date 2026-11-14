using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Island
{
    // Редактор поверх выделенной области: карандаш, стрелка, рамка, цвета, отмена.
    // Result == null — пользователь отменил (Esc).
    class ScreenshotEditor : Window
    {
        public System.Drawing.Bitmap Result;

        const double BarH = 44, Gap = 6, BarMinW = 440;

        readonly Grid _stage = new Grid();
        readonly Canvas _canvas = new Canvas { Background = Brushes.Transparent, Cursor = Cursors.Cross };
        readonly List<UIElement> _items = new List<UIElement>();
        readonly Dictionary<string, Border> _toolBtns = new Dictionary<string, Border>();
        readonly List<Ellipse> _dots = new List<Ellipse>();
        readonly double _sx, _sy;
        readonly int _pw, _ph;

        string _tool = "arrow";
        Brush _color = Br("#FFFF3B30");
        Point _start;
        bool _drag;
        UIElement _cur;
        Polyline _pen;

        static readonly string[] Palette = { "#FFFF3B30", "#FFFFCC00", "#FF34C759", "#FF2F6FEB", "#FFFFFFFF", "#FF000000" };

        static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        public ScreenshotEditor(System.Drawing.Bitmap crop, System.Drawing.Rectangle rect, int fullW, int fullH)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;

            double screenW = SystemParameters.PrimaryScreenWidth, screenH = SystemParameters.PrimaryScreenHeight;
            _sx = screenW / fullW;
            _sy = screenH / fullH;
            _pw = crop.Width;
            _ph = crop.Height;
            double w = _pw * _sx, h = _ph * _sy;
            double left = rect.X * _sx, top = rect.Y * _sy;

            bool barBelow = top + h + Gap + BarH <= screenH;
            double winW = Math.Max(w, BarMinW);
            double winLeft = Math.Min(left, screenW - winW);
            if (winLeft < 0) winLeft = 0;

            Width = winW;
            Height = h + Gap + BarH;
            Left = winLeft;
            Top = barBelow ? top : top - Gap - BarH;

            // снимок + слой рисования
            _stage.Width = w;
            _stage.Height = h;
            _stage.HorizontalAlignment = HorizontalAlignment.Left;
            _stage.VerticalAlignment = VerticalAlignment.Top;
            _stage.Margin = new Thickness(left - winLeft, 0, 0, 0);
            _stage.Children.Add(new Image { Source = Shot.ToSource(crop), Stretch = Stretch.Fill });
            _stage.Children.Add(_canvas);

            var frame = new Border
            {
                BorderBrush = Br("#FF2F6FEB"), BorderThickness = new Thickness(1),
                Margin = new Thickness(left - winLeft - 1, -1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Width = w + 2, Height = h + 2, IsHitTestVisible = false
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = barBelow ? new GridLength(h) : new GridLength(BarH) });
            root.RowDefinitions.Add(new RowDefinition { Height = barBelow ? new GridLength(Gap + BarH) : new GridLength(h + Gap) });

            var stageHost = new Grid();
            stageHost.Children.Add(_stage);
            stageHost.Children.Add(frame);
            Grid.SetRow(stageHost, barBelow ? 0 : 1);

            var bar = BuildBar();
            bar.VerticalAlignment = barBelow ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            bar.Margin = barBelow ? new Thickness(0, Gap, 0, 0) : new Thickness(0, 0, 0, Gap);
            bar.Margin = new Thickness(left - winLeft, bar.Margin.Top, 0, bar.Margin.Bottom);
            Grid.SetRow(bar, barBelow ? 1 : 0);

            root.Children.Add(stageHost);
            root.Children.Add(bar);
            Content = root;

            _canvas.MouseLeftButtonDown += Canvas_Down;
            _canvas.MouseMove += Canvas_Move;
            _canvas.MouseLeftButtonUp += Canvas_Up;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Cancel();
                else if (e.Key == Key.Enter) Done();
                else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) Undo();
            };
            Loaded += (s, e) => { Activate(); Focus(); };
            SelectTool("arrow");
            SelectColor(0);
        }

        // ---------- панель инструментов ----------

        Border BuildBar()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            sp.Children.Add(ToolBtn("pen", "✏", "Карандаш"));
            sp.Children.Add(ToolBtn("arrow", "↗", "Стрелка"));
            sp.Children.Add(ToolBtn("rect", "▭", "Рамка"));
            sp.Children.Add(Sep());
            for (int i = 0; i < Palette.Length; i++)
            {
                int idx = i;
                var dot = new Ellipse
                {
                    Width = 18, Height = 18, Fill = Br(Palette[i]), Margin = new Thickness(4, 0, 4, 0),
                    Stroke = Brushes.Transparent, StrokeThickness = 2, Cursor = Cursors.Hand
                };
                dot.MouseLeftButtonUp += (s, e) => SelectColor(idx);
                _dots.Add(dot);
                sp.Children.Add(dot);
            }
            sp.Children.Add(Sep());
            sp.Children.Add(Btn("↶", Undo, "Отменить (Ctrl+Z)"));
            sp.Children.Add(Btn("✕", Cancel, "Отмена (Esc)"));
            var ok = Btn("✓", Done, "Готово (Enter)");
            ok.Background = Br("#FF2F6FEB");
            sp.Children.Add(ok);

            return new Border
            {
                Background = Br("#F01C1C1E"), CornerRadius = new CornerRadius(14), Height = BarH,
                HorizontalAlignment = HorizontalAlignment.Left, Child = sp
            };
        }

        static FrameworkElement Sep() => new Border { Width = 1, Height = 20, Background = Br("#33FFFFFF"), Margin = new Thickness(6, 0, 6, 0) };

        Border Btn(string text, Action click, string tip)
        {
            var b = new Border
            {
                Width = 32, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 0, 2, 0),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip,
                Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        Border ToolBtn(string tool, string text, string tip)
        {
            var b = Btn(text, () => SelectTool(tool), tip);
            _toolBtns[tool] = b;
            return b;
        }

        void SelectTool(string tool)
        {
            _tool = tool;
            foreach (var kv in _toolBtns)
                kv.Value.Background = kv.Key == tool ? Br("#59FFFFFF") : Brushes.Transparent;
        }

        void SelectColor(int i)
        {
            _color = Br(Palette[i]);
            for (int k = 0; k < _dots.Count; k++)
                _dots[k].Stroke = k == i ? Brushes.White : Brushes.Transparent;
        }

        // ---------- рисование ----------

        void Canvas_Down(object s, MouseButtonEventArgs e)
        {
            _start = e.GetPosition(_canvas);
            _drag = true;
            _canvas.CaptureMouse();

            if (_tool == "pen")
            {
                _pen = new Polyline
                {
                    Stroke = _color, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                _pen.Points.Add(_start);
                _cur = _pen;
            }
            else if (_tool == "arrow")
            {
                _cur = new System.Windows.Shapes.Path
                {
                    Stroke = _color, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Data = ArrowGeo(_start, _start)
                };
            }
            else
            {
                var r = new Rectangle { Stroke = _color, StrokeThickness = 3, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(r, _start.X);
                Canvas.SetTop(r, _start.Y);
                _cur = r;
            }
            _canvas.Children.Add(_cur);
        }

        void Canvas_Move(object s, MouseEventArgs e)
        {
            if (!_drag || _cur == null) return;
            var p = e.GetPosition(_canvas);
            if (_tool == "pen") _pen.Points.Add(p);
            else if (_tool == "arrow") ((System.Windows.Shapes.Path)_cur).Data = ArrowGeo(_start, p);
            else
            {
                var r = (Rectangle)_cur;
                Canvas.SetLeft(r, Math.Min(_start.X, p.X));
                Canvas.SetTop(r, Math.Min(_start.Y, p.Y));
                r.Width = Math.Abs(p.X - _start.X);
                r.Height = Math.Abs(p.Y - _start.Y);
            }
        }

        void Canvas_Up(object s, MouseButtonEventArgs e)
        {
            if (!_drag) return;
            _drag = false;
            _canvas.ReleaseMouseCapture();
            var p = e.GetPosition(_canvas);
            bool tiny = Math.Abs(p.X - _start.X) < 3 && Math.Abs(p.Y - _start.Y) < 3;
            if (_cur != null)
            {
                if (tiny && _tool != "pen") _canvas.Children.Remove(_cur);   // случайный клик без протяжки
                else _items.Add(_cur);
            }
            _cur = null;
            _pen = null;
        }

        static Geometry ArrowGeo(Point a, Point b)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(a, false, false);
                c.LineTo(b, true, false);
                Vector v = a - b;
                double len = v.Length;
                if (len > 2)
                {
                    v /= len;
                    double head = Math.Min(22, len * 0.6) + 6;
                    c.BeginFigure(b + Rot(v, 28) * head, false, false);
                    c.LineTo(b, true, false);
                    c.LineTo(b + Rot(v, -28) * head, true, false);
                }
            }
            g.Freeze();
            return g;
        }

        static Vector Rot(Vector v, double deg)
        {
            double r = deg * Math.PI / 180, cs = Math.Cos(r), sn = Math.Sin(r);
            return new Vector(v.X * cs - v.Y * sn, v.X * sn + v.Y * cs);
        }

        // ---------- действия ----------

        void Undo()
        {
            if (_items.Count == 0) return;
            _canvas.Children.Remove(_items[_items.Count - 1]);
            _items.RemoveAt(_items.Count - 1);
        }

        void Cancel()
        {
            Result = null;
            Close();
        }

        void Done()
        {
            try
            {
                // рендерим снимок вместе с рисунками в исходном разрешении (1 пиксель = 1 пиксель)
                var rtb = new RenderTargetBitmap(_pw, _ph, 96 / _sx, 96 / _sy, PixelFormats.Pbgra32);
                rtb.Render(_stage);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    ms.Position = 0;
                    using (var tmp = new System.Drawing.Bitmap(ms))
                        Result = new System.Drawing.Bitmap(tmp);
                }
            }
            catch { Result = null; }
            Close();
        }
    }
}
