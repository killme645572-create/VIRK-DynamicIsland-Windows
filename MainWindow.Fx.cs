using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Island
{
    // Эффекты: «жидкий» край острова и персонаж с глазками.
    public partial class MainWindow
    {
        readonly DispatcherTimer _fxTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        readonly Random _rng = new Random();
        double _phase;
        double _drop = 2.5;                       // на сколько пикселей «капает» нижний край
        DateTime _nextBlink = DateTime.UtcNow.AddSeconds(3);
        DateTime _blinkEnd = DateTime.MinValue;
        double _lookX, _lookY;
        DateTime _happyUntil = DateTime.MinValue;   // до какого момента персонаж радуется
        CharacterRig _rig;

        void InitFx()
        {
            _fxTimer.Tick += (s, e) => FxTick();
            Pill.SizeChanged += (s, e) => { if (Settings.Current.Liquid) BuildSkirt(); };
            ApplyFxSettings();
            _fxTimer.Start();
        }

        // вызывается при старте и при переключении настроек
        void ApplyFxSettings()
        {
            var s = Settings.Current;
            ApplyCharacter();
            Skirt.Visibility = s.Liquid ? Visibility.Visible : Visibility.Collapsed;
            UpdateIdleLayout();
            AlarmPanel.Margin = new Thickness(s.Character ? 62 : 18, 0, 18, 0);
            Fade(Eyes, s.Character && (_mode == Mode.Idle || _mode == Mode.Alarm) ? 1 : 0);
        }

        // с персонажем время/погода уезжают вправо, чтобы глаза были слева; с эквалайзером справа ещё и столбики
        void UpdateIdleLayout()
        {
            var s = Settings.Current;
            double eq = _eqShown ? EqExtra : 0;
            IdleText.HorizontalAlignment = s.Character ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            IdleText.Margin = new Thickness(0, 0, (s.Character ? 16 : 0) + eq, 0);
        }

        // собираем выбранного персонажа (если он поменялся)
        void ApplyCharacter()
        {
            var spec = Characters.Get(Settings.Current.CharacterId);
            if (_rig != null && _rig.Spec == spec) return;
            _rig = new CharacterRig(spec);
            Eyes.Children.Clear();
            Eyes.Children.Add(_rig.Root);
            Eyes.Children.Add(_restZ);   // «z» над головой во время перерыва (помодоро)
            _rig.Update(Mood.Normal, 0, 0, false, 0, true);
        }

        void FxTick()
        {
            var s = Settings.Current;
            _phase += 0.22;

            if (s.Liquid)
            {
                double target;
                switch (_mode)
                {
                    case Mode.Open:  target = 7; break;
                    case Mode.Alarm: target = 8 + Math.Sin(_phase * 2) * 2; break;
                    case Mode.Chat:
                    case Mode.Timer:
                    case Mode.Pomodoro:
                    case Mode.Answer: target = 5; break;
                    case Mode.Toast:
                    case Mode.ClipAct: target = 4; break;
                    default:         target = 2.5; break;
                }
                _drop += (target - _drop) * 0.15;
                BuildSkirt();
            }

            UpdateEq();
            if (s.Character && Eyes.Opacity > 0.01) AnimateEyes();
        }

        // Волнистая «юбка» под островом: заливка того же цвета, высота волн сходит на нет к краям,
        // поэтому скруглённые углы пилюли остаются на месте.
        void BuildSkirt()
        {
            double r = Math.Min(Pill.ActualHeight / 2, 28);
            double w = Pill.ActualWidth - 2 * r;
            if (w < 20) { Skirt.Data = null; return; }

            const double baseY = 2;   // линия нижнего края острова в координатах юбки
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(0, 0), true, true);
                c.LineTo(new Point(w, 0), true, false);
                int n = Math.Max(12, (int)(w / 8));
                for (int i = n; i >= 0; i--)
                {
                    double t = (double)i / n;
                    double env = Math.Sin(Math.PI * t);                      // 0 на краях, 1 в середине
                    double wave = 0.5 + 0.5 * Math.Sin(t * w / 46 * 2 * Math.PI + _phase);
                    c.LineTo(new Point(t * w, baseY + _drop * env * wave), true, false);
                }
            }
            g.Freeze();
            Skirt.Data = g;
            Skirt.Margin = new Thickness(0, Pill.ActualHeight - baseY, 0, 0);
        }

        void AnimateEyes()
        {
            var now = DateTime.UtcNow;
            if (now >= _nextBlink)
            {
                _blinkEnd = now.AddMilliseconds(130);
                _nextBlink = now.AddSeconds(2.5 + _rng.NextDouble() * 4);
            }

            // смотрит в сторону курсора
            try
            {
                var cur = System.Windows.Forms.Cursor.Position;
                var center = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
                double tx = Math.Max(-1, Math.Min(1, (cur.X - center.X) / 300.0)) * 3;
                double ty = Math.Max(-1, Math.Min(1, (cur.Y - center.Y) / 300.0)) * 2;
                _lookX += (tx - _lookX) * 0.25;
                _lookY += (ty - _lookY) * 0.25;
            }
            catch { }

            // настроение: сигнал таймера > радость > «думает» (ИИ отвечает) > помодоро (работает / отдыхает)
            // > щурится (таймер или Claude работает)
            Mood mood;
            var pomo = Pomodoro.Instance;
            if (_mode == Mode.Alarm) mood = Mood.Alarm;
            else if (now < _happyUntil) mood = Mood.Happy;
            else if (Chat.Busy || _smartBusy) mood = Mood.Think;
            else if (pomo.Active && pomo.Running) mood = pomo.Resting ? Mood.Rest : Mood.Work;
            else if (IslandTimer.Instance.Running || ClaudeWorking) mood = Mood.Squint;
            else mood = Mood.Normal;

            // «z» над головой, пока персонаж отдыхает
            _restZ.Opacity = mood == Mood.Rest ? 0.4 + 0.6 * Math.Abs(Math.Sin(_phase * 0.5)) : 0;
            System.Windows.Controls.Canvas.SetTop(_restZ, -2 - 2 * Math.Abs(Math.Sin(_phase * 0.5)));

            _rig?.Update(mood, _lookX, _lookY, now < _blinkEnd, _phase);
        }
    }
}
