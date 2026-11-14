using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Island
{
    // Настроение персонажа: его выбирает остров по тому, что происходит.
    internal enum Mood { Normal, Squint, Think, Alarm, Happy, Work, Rest }

    internal enum EyeKind { Oval, Rect, Pixel, Line, Arc, Star, Heart, Cross, Slit }

    // Описание персонажа. Рисуется в поле 44×26 пикселей.
    // Decor — слова через пробел: ears-cat ears-fox ears-round ears-bunny antenna horns
    //   brows-angry glasses-round bridge cheeks whiskers nose beak cup zzz
    //   mouth-smile mouth-wide mouth-o mouth-w mouth-flat mouth-fang mouth-stitch mouth-tooth mouth-pixel
    internal sealed class CharSpec
    {
        public string Id, Name;
        public EyeKind Eye = EyeKind.Oval;
        public string Color = "#FFFFFF";    // цвет глаз
        public string Accent = "#FFFFFF";   // цвет ушей, щёк, носа и прочих деталей
        public int Count = 2;               // сколько глаз
        public double W = 7, H = 11, Gap = 6;
        public double Tilt;                 // наклон глаз, градусы (плюс — верх внутрь: «/ \»)
        public bool Pupil;                  // зрачок, который следит за курсором
        public double EyeY = 10.5;          // высота центра глаз
        public double ShiftX;               // сдвиг лица по горизонтали
        public double Lid = 1;              // «сонливость»: 1 — глаза полностью открыты
        public string Decor = "";
    }

    internal static class Characters
    {
        public static readonly CharSpec[] All =
        {
            new CharSpec { Id = "classic",  Name = "Глазки" },
            new CharSpec { Id = "round",    Name = "Кругляш",    W = 9, H = 9, Pupil = true, Decor = "mouth-smile" },
            new CharSpec { Id = "sleepy",   Name = "Соня",       Eye = EyeKind.Line, W = 9, H = 4, Accent = "#9AA7FF", Decor = "zzz mouth-o" },
            new CharSpec { Id = "happy",    Name = "Радость",    Eye = EyeKind.Arc, W = 9, H = 7, EyeY = 9, Accent = "#FF7A9C", Decor = "cheeks mouth-smile" },
            new CharSpec { Id = "cat",      Name = "Кот",        Eye = EyeKind.Slit, Color = "#8CFF6B", Accent = "#8E8E93", W = 8, H = 11, Pupil = true, EyeY = 11, Decor = "ears-cat whiskers mouth-w" },
            new CharSpec { Id = "bear",     Name = "Мишка",      W = 5, H = 5, Gap = 10, EyeY = 11, Accent = "#C8915F", Decor = "ears-round nose mouth-smile" },
            new CharSpec { Id = "robot",    Name = "Робот",      Eye = EyeKind.Rect, Color = "#5EE6FF", Accent = "#5EE6FF", W = 7, H = 7, Gap = 7, EyeY = 13, Decor = "antenna mouth-flat" },
            new CharSpec { Id = "alien",    Name = "Пришелец",   Color = "#7CFF7C", Accent = "#7CFF7C", W = 7, H = 12, Tilt = 22, EyeY = 13, Decor = "antenna" },
            new CharSpec { Id = "cyclops",  Name = "Циклоп",     Count = 1, W = 14, H = 14, Pupil = true, EyeY = 11.5, Decor = "mouth-smile" },
            new CharSpec { Id = "triclops", Name = "Трёхглазик", Count = 3, Color = "#FFD166", W = 6, H = 8, Gap = 4, EyeY = 12 },
            new CharSpec { Id = "ghost",    Name = "Бука",       W = 8, H = 10, Accent = "#9AA7FF", Decor = "cheeks mouth-o" },
            new CharSpec { Id = "angry",    Name = "Злюка",      Color = "#FF5A5A", Accent = "#FF5A5A", W = 7, H = 9, Tilt = 15, EyeY = 11.5, Decor = "brows-angry mouth-flat" },
            new CharSpec { Id = "love",     Name = "Влюблённый", Eye = EyeKind.Heart, Color = "#FF4F8B", W = 10, H = 9, Gap = 4, Decor = "mouth-smile" },
            new CharSpec { Id = "star",     Name = "Звёздочка",  Eye = EyeKind.Star, Color = "#FFD60A", W = 11, H = 11, Gap = 3, Decor = "mouth-smile" },
            new CharSpec { Id = "cool",     Name = "Крутой",     Eye = EyeKind.Rect, Color = "#7CC4FF", W = 11, H = 7, Gap = 2, EyeY = 10, Decor = "bridge mouth-smile" },
            new CharSpec { Id = "nerd",     Name = "Умник",      W = 7, H = 7, Gap = 8, Pupil = true, Decor = "glasses-round mouth-smile" },
            new CharSpec { Id = "zombie",   Name = "Зомби",      Eye = EyeKind.Cross, Color = "#7CFF7C", Accent = "#7CFF7C", W = 8, H = 8, EyeY = 9.5, Decor = "mouth-stitch" },
            new CharSpec { Id = "coffee",   Name = "Кофеман",    W = 7, H = 9, Gap = 5, ShiftX = -5, EyeY = 11, Lid = 0.8, Decor = "cup" },
            new CharSpec { Id = "fox",      Name = "Лис",        Color = "#FFB347", Accent = "#FF8A2B", W = 6, H = 9, Tilt = 18, EyeY = 11, Decor = "ears-fox nose mouth-w" },
            new CharSpec { Id = "bunny",    Name = "Зайка",      W = 6, H = 8, Gap = 7, EyeY = 15, Accent = "#FFB3D1", Decor = "ears-bunny cheeks nose" },
            new CharSpec { Id = "owl",      Name = "Сова",       Color = "#FFC94D", Accent = "#B07A4A", W = 11, H = 11, Gap = 1, Pupil = true, EyeY = 12, Decor = "ears-fox beak" },
            new CharSpec { Id = "frog",     Name = "Лягушка",    Color = "#B8FF6B", Accent = "#4CD964", W = 9, H = 9, Gap = 4, Pupil = true, EyeY = 8, Decor = "mouth-wide" },
            new CharSpec { Id = "vampire",  Name = "Вампир",     Color = "#FF3B3B", Accent = "#7B6CF6", W = 6, H = 9, Tilt = 12, EyeY = 11, Decor = "ears-fox mouth-fang" },
            new CharSpec { Id = "monster",  Name = "Монстрик",   Color = "#E0B3FF", Accent = "#B26BFF", W = 8, H = 8, Gap = 5, Pupil = true, EyeY = 12, Decor = "horns mouth-tooth" },
            new CharSpec { Id = "pixel",    Name = "Пиксель",    Eye = EyeKind.Pixel, Color = "#00FFB2", Accent = "#FF4FD8", W = 6, H = 6, EyeY = 10, Decor = "mouth-pixel" },
        };

        public static CharSpec Get(string id)
        {
            foreach (var c in All)
                if (string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) return c;
            return All[0];
        }
    }

    // Нарисованный персонаж. Root — поле 44×26, его можно класть куда угодно.
    // Update вызывается каждый кадр: смотрит, моргает, щурится, задумывается, трясясь на сигнале таймера.
    internal sealed class CharacterRig
    {
        sealed class Pup { public Ellipse E; public TranslateTransform T; public double MX, MY; }

        public readonly CharSpec Spec;
        public readonly Canvas Root = new Canvas { Width = 44, Height = 26 };

        readonly Canvas _all = new Canvas { Width = 44, Height = 26 };    // всё вместе: трясётся и раздувается на тревоге
        readonly Canvas _face = new Canvas { Width = 44, Height = 26 };   // лицо: смещается за курсором
        readonly ScaleTransform _allScale = new ScaleTransform(1, 1);
        readonly TranslateTransform _allMove = new TranslateTransform();
        readonly TranslateTransform _faceMove = new TranslateTransform();
        readonly List<FrameworkElement> _eyes = new List<FrameworkElement>();
        readonly List<ScaleTransform> _eyeScale = new List<ScaleTransform>();
        readonly List<Pup> _pupils = new List<Pup>();
        readonly List<Ellipse> _dots = new List<Ellipse>();
        readonly Canvas _happyEyes = new Canvas { Width = 44, Height = 26, Visibility = Visibility.Collapsed };
        Path _happySmile;
        UIElement _zzz, _steam;
        bool _hasMouth, _hasPupils;
        double _cur = 1;

        // геометрия лица (нужна при сборке деталей)
        double _cx, _x0, _eyeTop, _eyeBottom, _my;

        public CharacterRig(CharSpec s)
        {
            Spec = s;
            var tokens = (s.Decor ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            _cx = 22 + s.ShiftX;
            double total = s.Count * s.W + (s.Count - 1) * s.Gap;
            _x0 = _cx - total / 2;
            _eyeTop = s.EyeY - s.H / 2;
            _eyeBottom = s.EyeY + s.H / 2;
            _my = Math.Min(_eyeBottom + 3.5, 21.5);   // линия рта
            Brush col = B(s.Color), acc = B(s.Accent);

            _all.RenderTransformOrigin = new Point(0.5, 0.5);
            var allGroup = new TransformGroup();
            allGroup.Children.Add(_allScale);
            allGroup.Children.Add(_allMove);
            _all.RenderTransform = allGroup;
            _face.RenderTransform = _faceMove;

            // уши и антенны — позади лица
            foreach (var t in tokens) Back(t, acc);
            _all.Children.Add(_face);

            // глаза
            for (int i = 0; i < s.Count; i++)
            {
                Ellipse pup;
                var eye = MakeEye(s, col, out pup);
                var sc = new ScaleTransform(1, 1);
                double mid = s.Count / 2.0 - 0.5;
                double tilt = i < mid ? s.Tilt : (i > mid ? -s.Tilt : 0);
                var tg = new TransformGroup();
                tg.Children.Add(sc);
                tg.Children.Add(new RotateTransform(tilt));
                eye.RenderTransformOrigin = new Point(0.5, 0.5);
                eye.RenderTransform = tg;
                Put(_face, eye, _x0 + i * (s.W + s.Gap), _eyeTop);
                _eyes.Add(eye);
                _eyeScale.Add(sc);

                if (pup != null)
                {
                    var tt = new TranslateTransform();
                    pup.RenderTransform = tt;
                    _pupils.Add(new Pup { E = pup, T = tt, MX = (s.W - pup.Width) / 2 * 0.9, MY = (s.H - pup.Height) / 2 * 0.9 });
                    _hasPupils = true;
                }

                // «счастливые» глаза-дужки: показываются вместо обычных в настроении Happy
                double aw = Math.Max(s.W, 8);
                var arc = ArcPath(aw, 6, col, 2.2);
                Put(_happyEyes, arc, _x0 + i * (s.W + s.Gap) + s.W / 2 - aw / 2, s.EyeY - 3);
            }

            // лицо: щёки, очки, нос, рот
            foreach (var t in tokens) Front(t, acc);
            _face.Children.Add(_happyEyes);

            _happySmile = Curve(_cx - 4, _my, _cx, _my + 5, _cx + 4, _my, B("#FFFFFF"), 1.7);
            _happySmile.Visibility = Visibility.Collapsed;
            _face.Children.Add(_happySmile);

            // детали, которые не двигаются за взглядом
            foreach (var t in tokens) Extra(t);

            // точки «думаю…»
            for (int i = 0; i < 3; i++)
            {
                var d = new Ellipse { Width = 2.6, Height = 2.6, Fill = B("#FFFFFF"), Opacity = 0 };
                Put(_all, d, 33.5 + i * 4, 1);
                _dots.Add(d);
            }

            Root.Children.Add(_all);
        }

        // ---------- кадр ----------

        public void Update(Mood mood, double lookX, double lookY, bool blink, double phase, bool instant = false)
        {
            if (mood == Mood.Think) { lookX = 2.5; lookY = -2; }   // смотрит вверх и вбок, как будто размышляет
            if (mood == Mood.Alarm) { lookX = 0; lookY = 0; }
            if (mood == Mood.Work) { lookX = Math.Sin(phase * 0.35) * 2.4; lookY = 1.8; }   // «работает»: взгляд вниз и бегает по строке
            if (mood == Mood.Rest) { lookX = 0; lookY = 0; }                                // «отдыхает»: смотрит прямо, глаза прикрыты

            double k = _hasPupils ? 0.35 : 1.0;   // у глаз со зрачками смещается в основном зрачок
            _faceMove.X = lookX * k;
            _faceMove.Y = lookY * k;
            foreach (var p in _pupils)
            {
                p.T.X = Clamp(lookX / 3.0) * p.MX;
                p.T.Y = Clamp(lookY / 2.0) * p.MY;
            }

            double f = mood == Mood.Squint ? 0.75 : mood == Mood.Alarm ? 1.2 : mood == Mood.Work ? 0.7 : mood == Mood.Rest ? 0.3 : 1.0;
            double target = blink ? 0.12 : Spec.Lid * f;
            _cur = instant ? target : _cur + (target - _cur) * 0.6;
            foreach (var sc in _eyeScale) sc.ScaleY = _cur;

            bool happy = mood == Mood.Happy;
            foreach (var e in _eyes) e.Visibility = happy ? Visibility.Collapsed : Visibility.Visible;
            _happyEyes.Visibility = happy ? Visibility.Visible : Visibility.Collapsed;
            _happySmile.Visibility = happy && !_hasMouth ? Visibility.Visible : Visibility.Collapsed;

            bool alarm = mood == Mood.Alarm;
            _allMove.X = alarm ? Math.Sin(phase * 3) * 2.5 : 0;
            _allMove.Y = mood == Mood.Work ? Math.Abs(Math.Sin(phase * 1.6)) * 0.9 : 0;                 // лёгкое «печатает»
            _allScale.ScaleX = _allScale.ScaleY =
                alarm ? 1.12 : mood == Mood.Rest ? 1 + 0.05 * Math.Sin(phase * 0.45) : 1.0;           // на отдыхе «дышит»

            for (int i = 0; i < _dots.Count; i++)
                _dots[i].Opacity = mood == Mood.Think ? 0.25 + 0.75 * Math.Max(0, Math.Sin(phase * 1.2 - i * 0.9)) : 0;

            if (_zzz != null) _zzz.Opacity = 0.55 + 0.45 * Math.Sin(phase * 0.8);
            if (_steam != null) _steam.Opacity = 0.5 + 0.4 * Math.Sin(phase * 0.9);
        }

        static double Clamp(double v) => Math.Max(-1, Math.Min(1, v));

        // ---------- сборка ----------

        void Back(string t, Brush acc)
        {
            double cx = _cx;
            switch (t)
            {
                case "ears-cat":
                    _all.Children.Add(Poly(acc, new Point(cx - 15, 9), new Point(cx - 14, 0), new Point(cx - 6, 5)));
                    _all.Children.Add(Poly(acc, new Point(cx + 15, 9), new Point(cx + 14, 0), new Point(cx + 6, 5)));
                    break;
                case "ears-fox":
                    _all.Children.Add(Poly(acc, new Point(cx - 15, 10), new Point(cx - 12, 0), new Point(cx - 5, 6)));
                    _all.Children.Add(Poly(acc, new Point(cx + 15, 10), new Point(cx + 12, 0), new Point(cx + 5, 6)));
                    break;
                case "ears-round":
                    Put(_all, new Ellipse { Width = 8, Height = 8, Fill = acc }, cx - 16, 1);
                    Put(_all, new Ellipse { Width = 8, Height = 8, Fill = acc }, cx + 8, 1);
                    break;
                case "ears-bunny":
                    var l = new Ellipse { Width = 5, Height = 12, Fill = acc, RenderTransform = new RotateTransform(-8, 2.5, 6) };
                    var r = new Ellipse { Width = 5, Height = 12, Fill = acc, RenderTransform = new RotateTransform(8, 2.5, 6) };
                    Put(_all, l, cx - 9, 0);
                    Put(_all, r, cx + 4, 0);
                    break;
                case "antenna":
                    _all.Children.Add(Ln(_cx, 4, _cx, 9, acc, 1.5));
                    Put(_all, new Ellipse { Width = 4, Height = 4, Fill = acc }, _cx - 2, 0);
                    break;
                case "horns":
                    _all.Children.Add(Poly(acc, new Point(cx - 8, 5), new Point(cx - 16, 0), new Point(cx - 12, 9)));
                    _all.Children.Add(Poly(acc, new Point(cx + 8, 5), new Point(cx + 16, 0), new Point(cx + 12, 9)));
                    break;
            }
        }

        void Front(string t, Brush acc)
        {
            var s = Spec;
            double cx = _cx;
            Brush white = B("#FFFFFF");
            double eyesRight = _x0 + s.Count * s.W + (s.Count - 1) * s.Gap;

            switch (t)
            {
                case "brows-angry":
                    _face.Children.Add(Ln(_x0 - 1, _eyeTop - 3.5, _x0 + s.W + 1, _eyeTop - 0.5, acc, 2));
                    _face.Children.Add(Ln(eyesRight + 1, _eyeTop - 3.5, eyesRight - s.W - 1, _eyeTop - 0.5, acc, 2));
                    break;

                case "glasses-round":
                    {
                        double d = s.W + 6;
                        for (int i = 0; i < s.Count; i++)
                        {
                            double ex = _x0 + i * (s.W + s.Gap) + s.W / 2;
                            Put(_face, new Ellipse { Width = d, Height = d, Stroke = white, StrokeThickness = 1.4 }, ex - d / 2, s.EyeY - d / 2);
                        }
                        if (s.Count == 2)
                            _face.Children.Add(Ln(_x0 + s.W / 2 + d / 2, s.EyeY, _x0 + s.W + s.Gap + s.W / 2 - d / 2, s.EyeY, white, 1.4));
                        break;
                    }

                case "bridge":
                    if (s.Count == 2)
                        _face.Children.Add(Ln(_x0 + s.W, s.EyeY - 1, _x0 + s.W + s.Gap, s.EyeY - 1, white, 1.6));
                    break;

                case "cheeks":
                    Put(_face, new Ellipse { Width = 5, Height = 3, Fill = acc, Opacity = 0.85 }, _x0 - 5.5, _eyeBottom - 0.5);
                    Put(_face, new Ellipse { Width = 5, Height = 3, Fill = acc, Opacity = 0.85 }, eyesRight + 0.5, _eyeBottom - 0.5);
                    break;

                case "whiskers":
                    foreach (int sign in new[] { -1, 1 })
                    {
                        _face.Children.Add(Ln(cx + sign * 14, 16.5, cx + sign * 21, 15, white, 1, 0.7));
                        _face.Children.Add(Ln(cx + sign * 14, 18.5, cx + sign * 21, 19.5, white, 1, 0.7));
                    }
                    break;

                case "nose":
                    Put(_face, new Ellipse { Width = 4, Height = 3, Fill = acc }, cx - 2, _eyeBottom + 0.5);
                    break;

                case "beak":
                    _face.Children.Add(Poly(B("#FF9F1C"), new Point(cx - 2.5, s.EyeY + 0.5), new Point(cx + 2.5, s.EyeY + 0.5), new Point(cx, s.EyeY + 5.5)));
                    break;

                // ----- рты -----
                case "mouth-smile":
                    _hasMouth = true;
                    _face.Children.Add(Curve(cx - 3.5, _my, cx, _my + 4.5, cx + 3.5, _my, white, 1.6));
                    break;
                case "mouth-wide":
                    _hasMouth = true;
                    _face.Children.Add(Curve(cx - 8, _my, cx, _my + 7, cx + 8, _my, acc, 1.7));
                    break;
                case "mouth-o":
                    _hasMouth = true;
                    Put(_face, new Ellipse { Width = 4.5, Height = 5.5, Stroke = white, StrokeThickness = 1.5 }, cx - 2.25, _my - 1);
                    break;
                case "mouth-w":
                    _hasMouth = true;
                    _face.Children.Add(Curve(cx - 4, _my, cx - 2, _my + 3.5, cx, _my, white, 1.4));
                    _face.Children.Add(Curve(cx, _my, cx + 2, _my + 3.5, cx + 4, _my, white, 1.4));
                    break;
                case "mouth-flat":
                    _hasMouth = true;
                    _face.Children.Add(Ln(cx - 3, _my + 2, cx + 3, _my + 2, white, 1.6));
                    break;
                case "mouth-fang":
                    _hasMouth = true;
                    _face.Children.Add(Ln(cx - 4, _my + 1.5, cx + 4, _my + 1.5, white, 1.5));
                    _face.Children.Add(Poly(white, new Point(cx - 3, _my + 1.8), new Point(cx - 1, _my + 1.8), new Point(cx - 2, _my + 5.2)));
                    _face.Children.Add(Poly(white, new Point(cx + 1, _my + 1.8), new Point(cx + 3, _my + 1.8), new Point(cx + 2, _my + 5.2)));
                    break;
                case "mouth-stitch":
                    _hasMouth = true;
                    _face.Children.Add(Ln(cx - 7, _my + 1.5, cx + 7, _my + 1.5, acc, 1.4));
                    foreach (double dx in new[] { -5.0, -1.7, 1.7, 5.0 })
                        _face.Children.Add(Ln(cx + dx, _my - 0.5, cx + dx, _my + 3.5, acc, 1.2));
                    break;
                case "mouth-tooth":
                    _hasMouth = true;
                    _face.Children.Add(Curve(cx - 6, _my, cx, _my + 5, cx + 6, _my, white, 1.6));
                    _face.Children.Add(Poly(white, new Point(cx - 1.8, _my + 1.6), new Point(cx + 1.8, _my + 1.6), new Point(cx + 1.8, _my + 4.6), new Point(cx - 1.8, _my + 4.6)));
                    break;
                case "mouth-pixel":
                    _hasMouth = true;
                    foreach (var p in new[] { new Point(-5, 1), new Point(-3, 3), new Point(-1, 3), new Point(1, 3), new Point(3, 1) })
                        Put(_face, new Rectangle { Width = 2, Height = 2, Fill = acc }, cx + p.X, _my + p.Y);
                    break;
            }
        }

        void Extra(string t)
        {
            switch (t)
            {
                case "cup":
                    {
                        Brush w = B("#FFFFFF");
                        _all.Children.Add(Poly(w, new Point(32, 13), new Point(40, 13), new Point(38.5, 21.5), new Point(33.5, 21.5)));
                        Put(_all, new Ellipse { Width = 4, Height = 4.5, Stroke = w, StrokeThickness = 1.4 }, 38.5, 14.5);
                        var steam = new Canvas { Width = 44, Height = 26 };
                        steam.Children.Add(Curve(35, 11, 36.5, 8.5, 35, 6, w, 0.9));
                        steam.Children.Add(Curve(38, 11, 39.5, 8.5, 38, 6, w, 0.9));
                        _all.Children.Add(steam);
                        _steam = steam;
                        break;
                    }
                case "zzz":
                    {
                        var z = new Canvas { Width = 44, Height = 26 };
                        z.Children.Add(new TextBlock { Text = "z", FontSize = 7, FontWeight = FontWeights.Bold, Foreground = B("#9AA7FF") });
                        Canvas.SetLeft(z.Children[0], 31); Canvas.SetTop(z.Children[0], 3);
                        z.Children.Add(new TextBlock { Text = "Z", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = B("#9AA7FF") });
                        Canvas.SetLeft(z.Children[1], 35.5); Canvas.SetTop(z.Children[1], -1);
                        _all.Children.Add(z);
                        _zzz = z;
                        break;
                    }
            }
        }

        // ---------- рисование ----------

        static FrameworkElement MakeEye(CharSpec s, Brush col, out Ellipse pupil)
        {
            pupil = null;
            var c = new Canvas { Width = s.W, Height = s.H };
            switch (s.Eye)
            {
                case EyeKind.Oval:
                case EyeKind.Slit:
                    c.Children.Add(new Ellipse { Width = s.W, Height = s.H, Fill = col });
                    if (s.Pupil)
                    {
                        bool slit = s.Eye == EyeKind.Slit;
                        double pw = slit ? s.W * 0.28 : s.W * 0.5;
                        double ph = slit ? s.H * 0.9 : s.H * 0.5;
                        pupil = new Ellipse { Width = pw, Height = ph, Fill = B("#000000") };
                        Put(c, pupil, (s.W - pw) / 2, (s.H - ph) / 2);
                    }
                    break;
                case EyeKind.Rect:
                    c.Children.Add(new Rectangle { Width = s.W, Height = s.H, RadiusX = 2, RadiusY = 2, Fill = col });
                    break;
                case EyeKind.Pixel:
                    c.Children.Add(new Rectangle { Width = s.W, Height = s.H, Fill = col });
                    break;
                case EyeKind.Line:
                    Put(c, new Rectangle { Width = s.W, Height = 2.4, RadiusX = 1.2, RadiusY = 1.2, Fill = col }, 0, (s.H - 2.4) / 2);
                    break;
                case EyeKind.Arc:
                    c.Children.Add(ArcPath(s.W, s.H, col, 2.3));
                    break;
                case EyeKind.Star:
                    {
                        var pts = new PointCollection();
                        double R = Math.Min(s.W, s.H) / 2, r = R * 0.46;
                        for (int i = 0; i < 10; i++)
                        {
                            double a = -Math.PI / 2 + i * Math.PI / 5;
                            double rad = i % 2 == 0 ? R : r;
                            pts.Add(new Point(s.W / 2 + rad * Math.Cos(a), s.H / 2 + rad * Math.Sin(a)));
                        }
                        c.Children.Add(new Polygon { Points = pts, Fill = col });
                        break;
                    }
                case EyeKind.Heart:
                    {
                        double sx = s.W / 100.0, sy = s.H / 90.0;
                        Point P(double x, double y) => new Point(x * sx, y * sy);
                        var g = new StreamGeometry();
                        using (var ctx = g.Open())
                        {
                            ctx.BeginFigure(P(50, 90), true, true);
                            ctx.BezierTo(P(10, 60), P(0, 35), P(15, 15), true, false);
                            ctx.BezierTo(P(30, -2), P(50, 10), P(50, 25), true, false);
                            ctx.BezierTo(P(50, 10), P(70, -2), P(85, 15), true, false);
                            ctx.BezierTo(P(100, 35), P(90, 60), P(50, 90), true, false);
                        }
                        g.Freeze();
                        c.Children.Add(new Path { Data = g, Fill = col });
                        break;
                    }
                case EyeKind.Cross:
                    {
                        var g = new StreamGeometry();
                        using (var ctx = g.Open())
                        {
                            ctx.BeginFigure(new Point(1, 1), false, false);
                            ctx.LineTo(new Point(s.W - 1, s.H - 1), true, false);
                            ctx.BeginFigure(new Point(s.W - 1, 1), false, false);
                            ctx.LineTo(new Point(1, s.H - 1), true, false);
                        }
                        g.Freeze();
                        c.Children.Add(new Path { Data = g, Stroke = col, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                        break;
                    }
            }
            return c;
        }

        static Path ArcPath(double w, double h, Brush col, double t)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(t / 2, h - t / 2), false, false);
                ctx.QuadraticBezierTo(new Point(w / 2, -h * 0.35), new Point(w - t / 2, h - t / 2), true, false);
            }
            g.Freeze();
            return new Path { Data = g, Stroke = col, StrokeThickness = t, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        }

        static Path Ln(double x1, double y1, double x2, double y2, Brush stroke, double t, double opacity = 1)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x1, y1), false, false);
                ctx.LineTo(new Point(x2, y2), true, false);
            }
            g.Freeze();
            return new Path { Data = g, Stroke = stroke, StrokeThickness = t, Opacity = opacity, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        }

        static Path Curve(double x1, double y1, double cx, double cy, double x2, double y2, Brush stroke, double t)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x1, y1), false, false);
                ctx.QuadraticBezierTo(new Point(cx, cy), new Point(x2, y2), true, false);
            }
            g.Freeze();
            return new Path { Data = g, Stroke = stroke, StrokeThickness = t, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        }

        static Polygon Poly(Brush fill, params Point[] pts) => new Polygon { Fill = fill, Points = new PointCollection(pts) };

        static void Put(Canvas c, UIElement e, double x, double y)
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            c.Children.Add(e);
        }

        static Brush B(string hex)
        {
            var b = (Brush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
