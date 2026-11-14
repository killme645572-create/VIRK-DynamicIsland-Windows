using System;
using System.Threading.Tasks;
using System.Windows;

namespace Island
{
    // Статус Claude Code в пилюле: «работает», «ждёт тебя», «закончил».
    public partial class MainWindow
    {
        string _claudeState;   // null | "working" | "waiting"
        DateTime _claudeStarted = DateTime.MinValue;
        DateTime _claudeEventAt = DateTime.MinValue;

        bool ClaudeWorking => _claudeState == "working";

        void InitClaude()
        {
            ClaudeHookServer.Received += kind => Dispatcher.BeginInvoke(new Action(() => OnClaudeEvent(kind)));
            ClaudeHookServer.Start(Settings.Current.ClaudePort);
        }

        void OnClaudeEvent(string kind)
        {
            var now = DateTime.UtcNow;
            switch (kind)
            {
                case "working":
                    _claudeState = "working";
                    _claudeStarted = now;
                    _claudeEventAt = now;
                    break;

                case "attention":
                    _claudeState = "waiting";
                    _claudeEventAt = now;
                    try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                    ShowToast("✋ Claude Code ждёт тебя", 5);
                    break;

                case "done":
                    string took = "";
                    if (_claudeStarted != DateTime.MinValue)
                    {
                        var d = now - _claudeStarted;
                        took = d.TotalMinutes >= 1 ? $" · {(int)d.TotalMinutes}м {d.Seconds}с" : $" · {(int)d.TotalSeconds}с";
                    }
                    _claudeState = null;
                    _claudeStarted = DateTime.MinValue;
                    _happyUntil = now.AddSeconds(8);   // персонаж радуется
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    ShowToast("✓ Claude Code закончил" + took, 5);
                    break;
            }
            UpdateIdleText();
        }

        // текст для пилюлы в покое; null — если ничего не происходит
        string ClaudeStatusText()
        {
            if (_claudeState == null) return null;
            var now = DateTime.UtcNow;

            if (_claudeState == "working")
            {
                // страховка: если событие «done» потерялось, не висим вечно
                if (now - _claudeStarted > TimeSpan.FromMinutes(45)) { _claudeState = null; return null; }
                int min = (int)(now - _claudeStarted).TotalMinutes;
                return min < 1 ? "✦ Claude работает" : "✦ Claude · " + min + " мин";
            }

            if (now - _claudeEventAt > TimeSpan.FromMinutes(3)) { _claudeState = null; return null; }
            return "✋ Claude ждёт";
        }

        // ---------- подключение: хуки Claude Code ----------

        static string HookEntry(string ev, string path, int port) =>
            "    \"" + ev + "\": [{ \"hooks\": [{ \"type\": \"command\", \"command\": \"curl -s -m 2 -X POST http://127.0.0.1:" + port + "/claude/" + path + "\" }] }]";

        static string HooksJson(int port) =>
            "{\n  \"hooks\": {\n" +
            HookEntry("UserPromptSubmit", "working", port) + ",\n" +
            HookEntry("Notification", "attention", port) + ",\n" +
            HookEntry("Stop", "done", port) + "\n  }\n}";

        async void ClaudeHooks_Click(object s, RoutedEventArgs e)
        {
            SetClipboardText(HooksJson(Settings.Current.ClaudePort));
            ClaudeHooksBtn.Content = "✓ Скопировано";
            await Task.Delay(1500);
            ClaudeHooksBtn.Content = "🧩 Хуки Claude Code → в буфер";
        }
    }
}
