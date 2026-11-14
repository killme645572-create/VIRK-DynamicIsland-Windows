using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Island
{
    // Уведомление об обновлении через GitHub: тост в острове + всплывающее сообщение у значка в трее,
    // а в ⚙ появляется кнопка «⬆ Обновить до X». Проверка — через 12 с после запуска и потом раз в 6 часов.
    public partial class MainWindow
    {
        readonly DispatcherTimer _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        UpdateInfo _update;        // найденная новая версия (null — обновления нет или ещё не проверяли)
        string _updateNote;        // временная надпись на кнопке: «Проверяю…», «Последняя версия», ошибка
        bool _updateBusy;

        void InitUpdates()
        {
            UpdateUpdateLabel();
            if (!Settings.Current.UpdateCheck) return;
            var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            first.Tick += (s, e) => { first.Stop(); _ = CheckUpdateAsync(false); };
            first.Start();
            _updateTimer.Tick += (s, e) => _ = CheckUpdateAsync(false);
            _updateTimer.Start();
        }

        void UpdateUpdateLabel()
        {
            if (UpdateBtn == null) return;
            if (_update != null)
            {
                UpdateBtn.Content = "⬆ Обновить до " + _update.Version.ToString(3);
                UpdateBtn.Background = Hex("#5534C759");
                UpdateBtn.ToolTip = "Сейчас " + Updater.CurrentText + ". Откроется загрузка установщика с GitHub: запусти его, остров сам попросит закрыть старую версию.";
            }
            else
            {
                UpdateBtn.Content = _updateNote ?? ("🔄 Проверить обновления (v" + Updater.CurrentText + ")");
                UpdateBtn.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                UpdateBtn.ToolTip = Updater.Configured ? "Репозиторий: " + Updater.Repo : "Впиши репозиторий GitHub в UpdateRepo (settings.json) или в Updater.DefaultRepo";
            }
        }

        async Task CheckUpdateAsync(bool manual)
        {
            if (_updateBusy) return;
            if (!manual && !Updater.Configured) return;   // репозиторий не задан — молчим
            _updateBusy = true;
            try
            {
                if (manual) { _updateNote = "🔄 Проверяю…"; UpdateUpdateLabel(); }
                var r = await Updater.CheckAsync();
                if (r.Error != null)
                {
                    _updateNote = manual ? "⚠ " + r.Error : null;
                    return;
                }
                if (r.Newer)
                {
                    _update = r.Info;
                    _updateNote = null;
                    var st = Settings.Current;
                    if (manual || st.UpdateNotified != r.Info.Tag)
                    {
                        st.UpdateNotified = r.Info.Tag;
                        st.Save();
                        NotifyUpdate(r.Info);
                    }
                }
                else
                {
                    _update = null;
                    _updateNote = manual ? "✓ Последняя версия (v" + Updater.CurrentText + ")" : null;
                }
            }
            finally
            {
                _updateBusy = false;
                UpdateUpdateLabel();
            }
        }

        void NotifyUpdate(UpdateInfo u)
        {
            string ver = u.Version.ToString(3);
            ShowToast("⬆ Доступно обновление " + ver + " (⚙ → Обновить)", 6);
            try
            {
                if (_tray != null)
                {
                    _tray.BalloonTipTitle = "Island: доступно обновление " + ver;
                    _tray.BalloonTipText = "У тебя версия " + Updater.CurrentText + ". Нажми, чтобы скачать новую.";
                    _tray.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
                    _tray.ShowBalloonTip(10000);
                }
            }
            catch { }
        }

        void OpenUpdatePage()
        {
            string url = _update?.DownloadUrl ?? _update?.PageUrl;
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        async void UpdateBtn_Click(object s, RoutedEventArgs e)
        {
            if (_update != null) { OpenUpdatePage(); return; }
            await CheckUpdateAsync(true);
        }
    }
}
