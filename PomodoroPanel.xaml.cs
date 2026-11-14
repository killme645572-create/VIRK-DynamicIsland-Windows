using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Island
{
    public partial class PomodoroPanel : UserControl
    {
        public event EventHandler CloseRequested;
        static Pomodoro P => Pomodoro.Instance;
        string _dotKey;

        public PomodoroPanel()
        {
            InitializeComponent();
            P.Changed += Refresh;
            Refresh();
        }

        static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        void Refresh()
        {
            var s = Settings.Current;
            bool active = P.Active;
            SetupRow.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            RunRow.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            PauseBtn.Content = P.Running ? "⏸ Пауза" : "▶ Дальше";

            switch (P.Phase)
            {
                case PomoPhase.Focus: PhaseText.Text = $"🍅 Фокус · {P.Round} из {P.Rounds}"; break;
                case PomoPhase.Break: PhaseText.Text = "☕ Перерыв"; break;
                case PomoPhase.LongBreak: PhaseText.Text = "🛋 Большой перерыв"; break;
                default: PhaseText.Text = "🍅 Помодоро"; break;
            }
            TimeText.Text = active ? IslandTimer.Format(P.Remaining) : IslandTimer.Format(TimeSpan.FromMinutes(s.PomoFocus));

            MarkPresets();
            BuildDots();
        }

        // подсвечиваем выбранный набор «фокус / перерыв»
        void MarkPresets()
        {
            var s = Settings.Current;
            Mark(P25, s.PomoFocus == 25 && s.PomoBreak == 5);
            Mark(P50, s.PomoFocus == 50 && s.PomoBreak == 10);
            Mark(P15, s.PomoFocus == 15 && s.PomoBreak == 3);
        }

        static void Mark(Button b, bool on) => b.Background = Hex(on ? "#59FFFFFF" : "#26FFFFFF");

        // кружки сессий: пройденные — белые, текущая — красная, будущие — бледные
        void BuildDots()
        {
            int rounds = P.Rounds;
            string key = rounds + ":" + P.Phase + ":" + P.Round;
            if (key == _dotKey) return;
            _dotKey = key;

            Dots.Children.Clear();
            for (int i = 1; i <= rounds; i++)
            {
                bool done, current;
                if (!P.Active) { done = false; current = false; }
                else if (i < P.Round) { done = true; current = false; }
                else if (i == P.Round) { done = P.Resting; current = !P.Resting; }
                else { done = false; current = false; }

                Dots.Children.Add(new Ellipse
                {
                    Width = 8, Height = 8, Margin = new Thickness(3, 0, 3, 0),
                    Fill = current ? Hex("#FF6347") : done ? Brushes.White : Hex("#33FFFFFF")
                });
            }
        }

        void Preset_Click(object s, RoutedEventArgs e)
        {
            if (!(s is Button b) || !(b.Tag is string tag)) return;
            var parts = tag.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int f) || !int.TryParse(parts[1], out int br)) return;
            var st = Settings.Current;
            st.PomoFocus = f; st.PomoBreak = br; st.PomoLong = br * 3;
            st.Save();
            Refresh();
        }

        void Start_Click(object s, RoutedEventArgs e) => P.Start();
        void Pause_Click(object s, RoutedEventArgs e) { if (P.Running) P.Pause(); else P.Resume(); }
        void Skip_Click(object s, RoutedEventArgs e) => P.Skip();
        void Stop_Click(object s, RoutedEventArgs e) => P.Stop();
        void Close_Click(object s, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
