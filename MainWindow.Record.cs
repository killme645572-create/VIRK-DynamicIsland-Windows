using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Island
{
    // Запись экрана: кнопки во вкладке «Инструменты», таймер в пилюле.
    public partial class MainWindow
    {
        readonly DispatcherTimer _recTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        void InitRecord()
        {
            _recTimer.Tick += (s, e) => UpdateIdleText();
            RefreshRecButtons();
        }

        static string RecAudioLabel()
        {
            switch (Settings.Current.RecAudio)
            {
                case "none": return "🔇 Без звука";
                case "both": return "🎙 ПК + микрофон";
                case "mic": return "🎙 Только микрофон";
                default: return "🔊 Звук ПК";
            }
        }

        void RefreshRecButtons()
        {
            RecBtn.Content = ScreenRecorder.IsRecording ? "⏹ Остановить запись" : "⏺ Запись экрана";
            RecAudioBtn.Content = RecAudioLabel();
            RecAudioBtn.IsEnabled = !ScreenRecorder.IsRecording;
            int off = Settings.Current.RecAudioOffsetMs;
            RecOffsetLabel.Text = "звук " + (off > 0 ? "+" : "") + off + " мс";
        }

        // «звук −/+»: сдвиг звука относительно видео. «+» — звук позже (если он шёл раньше картинки), «−» — раньше.
        void RecOffset_Click(object s, RoutedEventArgs e)
        {
            int step = (s as FrameworkElement)?.Tag as string == "+" ? 50 : -50;
            Settings.Current.RecAudioOffsetMs = Math.Max(-3000, Math.Min(3000, Settings.Current.RecAudioOffsetMs + step));
            Settings.Current.Save();
            RefreshRecButtons();
        }

        // время записи для пилюлы, null — если запись не идёт
        static string RecStatusText()
        {
            if (!ScreenRecorder.IsRecording) return null;
            var d = DateTime.UtcNow - ScreenRecorder.StartedAt;
            return "⏺ " + (d.TotalHours >= 1 ? ((int)d.TotalHours + ":") : "") + d.Minutes.ToString(d.TotalHours >= 1 ? "00" : "0") + ":" + d.Seconds.ToString("00");
        }

        void ToolRecAudio_Click(object s, RoutedEventArgs e)
        {
            string cur = Settings.Current.RecAudio ?? "system";
            Settings.Current.RecAudio = cur == "system" ? "both" : cur == "both" ? "mic" : cur == "mic" ? "none" : "system";
            Settings.Current.Save();
            RefreshRecButtons();
        }

        async void ToolRec_Click(object s, RoutedEventArgs e)
        {
            if (ScreenRecorder.IsRecording) { await StopRecordingAsync(); return; }

            RecBtn.IsEnabled = false;
            RecBtn.Content = "Запуск…";
            string err = await ScreenRecorder.StartAsync(Settings.Current.RecAudio ?? "system");
            RecBtn.IsEnabled = true;
            RefreshRecButtons();
            if (err != null) { ShowToast(err, 4); return; }

            _recTimer.Start();
            SetMode(Mode.Idle);   // остров сворачивается и показывает таймер
            if (ScreenRecorder.Warning != null) ShowRecToast("⚠ " + ScreenRecorder.Warning, 6);
        }

        // как ShowToast, но работает и когда остров раскрыт — предупреждения про звук нельзя пропустить
        void ShowRecToast(string text, double secs)
        {
            ToastText.Text = text;
            SetMode(Mode.Toast);
            _toastTimer.Interval = TimeSpan.FromSeconds(secs);
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        async Task StopRecordingAsync()
        {
            RecBtn.IsEnabled = false;
            RecBtn.Content = "Сохраняю…";
            string file = await ScreenRecorder.StopAsync();
            _recTimer.Stop();
            RecBtn.IsEnabled = true;
            RefreshRecButtons();
            if (file == null) { ShowToast("Запись не сохранилась (ffmpeg.log)", 4); return; }
            if (ScreenRecorder.Warning != null) ShowRecToast("Запись сохранена, но: " + ScreenRecorder.Warning, 8);
            else ShowToast("Запись сохранена в Videos\\Island", 3);
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = false }); } catch { }
        }
    }
}
