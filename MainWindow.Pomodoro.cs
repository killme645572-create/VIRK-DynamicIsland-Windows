using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Island
{
    // Помодоро: фокус-сессии с перерывами. Персонаж «работает» (смотрит вниз, бегает глазами по строке)
    // и «отдыхает» (глаза прикрыты, дышит, над головой «z») вместе с тобой.
    public partial class MainWindow
    {
        readonly TextBlock _restZ = new TextBlock
        {
            Text = "z", Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold,
            Opacity = 0, IsHitTestVisible = false
        };

        void InitPomodoro()
        {
            Canvas.SetLeft(_restZ, 34);
            Canvas.SetTop(_restZ, -2);

            Pomodoro.Instance.Changed += UpdateIdleText;
            Pomodoro.Instance.PhaseStarted += OnPomoPhase;
            Pomodoro.Instance.CycleFinished += OnPomoDone;
        }

        void Pomodoro_Click(object s, RoutedEventArgs e)
        {
            _leaveTimer.Stop();
            SetMode(Mode.Pomodoro);
            Activate();
        }

        void Pomo_Close(object s, EventArgs e) => SetMode(Mode.Idle);

        void OnPomoPhase(PomoPhase p)
        {
            var st = Settings.Current;
            var P = Pomodoro.Instance;
            bool firstStart = p == PomoPhase.Focus && P.Round == 1;   // только что нажали «Старт» — звук не нужен
            if (!firstStart)
            {
                try { (p == PomoPhase.Focus ? System.Media.SystemSounds.Exclamation : System.Media.SystemSounds.Asterisk).Play(); } catch { }
            }
            if (p != PomoPhase.Focus) _happyUntil = DateTime.UtcNow.AddSeconds(4);   // перерыв: сначала радуется

            string msg = p == PomoPhase.Focus ? $"🍅 Фокус · {st.PomoFocus} мин"
                       : p == PomoPhase.Break ? $"☕ Перерыв · {st.PomoBreak} мин"
                       : $"🛋 Большой перерыв · {st.PomoLong} мин";
            ShowToast(msg, 4);
        }

        void OnPomoDone()
        {
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            _happyUntil = DateTime.UtcNow.AddSeconds(8);
            ShowToast("🎉 Цикл помодоро завершён", 4);
        }

        // текст для пилюлы в покое; null — если помодоро не идёт
        static string PomoStatusText()
        {
            var P = Pomodoro.Instance;
            if (!P.Active) return null;
            return (P.Resting ? "☕ " : "🍅 ") + IslandTimer.Format(P.Remaining) + (P.Running ? "" : " ⏸");
        }
    }
}
