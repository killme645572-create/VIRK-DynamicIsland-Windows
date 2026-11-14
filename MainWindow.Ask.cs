using System;
using System.Threading.Tasks;
using System.Windows;

namespace Island
{
    // «Спроси про экран»: горячая клавиша Ctrl+Alt+Space (или кнопка во вкладке «Инструменты») → выделяешь область →
    // снимок сразу улетает в чат с вопросом (текст вопроса — AskPrompt в settings.json).
    public partial class MainWindow
    {
        const int AskHotkeyId = 0x4954;

        void RegisterAskHotkey()
        {
            if (!Settings.Current.AskHotkey) return;
            bool ok = false;
            // Ctrl + Alt + Space, без автоповтора
            try { ok = RegisterHotKey(_hwnd, AskHotkeyId, 0x0002 | 0x0001 | 0x4000, 0x20); } catch { }
            if (!ok) Dispatcher.BeginInvoke(new Action(() => ShowToast("Ctrl+Alt+Space занято другой программой", 3)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        void UnregisterAskHotkey()
        {
            try { UnregisterHotKey(_hwnd, AskHotkeyId); } catch { }
        }

        void ToolAsk_Click(object s, RoutedEventArgs e) => _ = AskAboutScreenAsync();

        async Task AskAboutScreenAsync()
        {
            if (Chat.Busy) { ShowToast("ИИ ещё отвечает — подожди секунду"); return; }
            try
            {
                using var full = await GrabScreenAsync();
                var rect = SelectRegion(full);
                Opacity = 1;
                if (rect == null) return;   // Esc — отмена

                using var crop = full.Clone(rect.Value, full.PixelFormat);
                var img = ChatImage.FromSource(Shot.ToSource(crop));

                _leaveTimer.Stop();
                _toastTimer.Stop();
                SetMode(Mode.Chat);
                Activate();
                Dispatcher.BeginInvoke(new Action(() => { Activate(); Chat.FocusInput(); }), System.Windows.Threading.DispatcherPriority.Input);
                await Chat.AskAboutImageAsync(img, Settings.Current.AskPrompt);
            }
            catch (Exception ex) { Opacity = 1; ShowToast("Ошибка: " + ex.Message); }
        }
    }
}
