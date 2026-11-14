using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Island
{
    public partial class TimerPanel : UserControl
    {
        public event EventHandler CloseRequested;
        static IslandTimer T => IslandTimer.Instance;

        public TimerPanel()
        {
            InitializeComponent();
            T.Changed += Refresh;
            Refresh();
        }

        void Refresh()
        {
            bool active = T.Active;
            SetupRow.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            RunRow.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            TimeText.Text = active ? IslandTimer.Format(T.Remaining) : "00:00";
            PauseBtn.Content = T.Running ? "⏸ Пауза" : "▶ Дальше";
        }

        void Preset_Click(object s, RoutedEventArgs e)
        {
            if (s is Button b && double.TryParse((string)b.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
                T.Start(TimeSpan.FromMinutes(m));
        }

        void Custom_Click(object s, RoutedEventArgs e) => StartCustom();

        void StartCustom()
        {
            var t = Input.Text.Trim().Replace(',', '.');
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0 && m <= 1440)
                T.Start(TimeSpan.FromMinutes(m));
            Input.Clear();
        }

        void Pause_Click(object s, RoutedEventArgs e) { if (T.Running) T.Pause(); else T.Resume(); }
        void Stop_Click(object s, RoutedEventArgs e) => T.Cancel();
        void Close_Click(object s, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        void Input_PreviewKeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; StartCustom(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CloseRequested?.Invoke(this, EventArgs.Empty); }
        }
    }
}
