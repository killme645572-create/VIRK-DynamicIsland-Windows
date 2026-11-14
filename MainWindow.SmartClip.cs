using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Island
{
    // Умный буфер: скопировал текст — в тосте появляются кнопки «Объяснить / Перевести / Исправить / Коротко».
    // Ответ ИИ (тот же провайдер и модель, что в чате) приходит прямо в остров.
    public partial class MainWindow
    {
        string _smartText;                 // что скопировано
        string _smartQuestion, _smartAnswer;
        int _smartSeq;                     // номер запроса: ответы на устаревшие запросы выбрасываем
        bool _smartBusy;
        readonly DispatcherTimer _smartLeave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        void InitSmartClip()
        {
            _smartLeave.Tick += (s, e) =>
            {
                _smartLeave.Stop();
                if (_mode == Mode.ClipAct || (_mode == Mode.Answer && !_smartBusy)) SetMode(Mode.Idle);
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && (_mode == Mode.ClipAct || _mode == Mode.Answer))
                {
                    e.Handled = true;
                    CloseSmart();
                }
            };
        }

        // true — показали кнопки действий (обычный тост «Скопировано» тогда не нужен)
        bool TryShowClipActions(string text)
        {
            if (!Settings.Current.SmartClip) return false;
            if (_mode != Mode.Idle && _mode != Mode.Toast && _mode != Mode.ClipAct) return false;
            var t = (text ?? "").Trim();
            if (t.Length < 3) return false;

            _smartText = t;
            var one = Regex.Replace(t, @"\s+", " ");
            if (one.Length > 46) one = one.Substring(0, 46) + "…";
            ClipActText.Text = "Скопировано: " + one;

            _smartLeave.Stop();
            SetMode(Mode.ClipAct);
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(6);   // если не нажали — уберётся сам
            _toastTimer.Start();
            return true;
        }

        void CloseSmart()
        {
            StopSpeech();          // если помощник ещё читает ответ вслух — замолчать
            _smartSeq++;           // ответ на этот запрос уже никому не нужен
            _smartBusy = false;
            _smartLeave.Stop();
            _toastTimer.Stop();
            SetMode(Mode.Idle);
        }

        void SmartAct_Click(object s, RoutedEventArgs e)
        {
            if (s is Button b && b.Tag is string kind) _ = RunSmartAsync(kind);
        }

        static bool IsMostlyCyrillic(string t)
        {
            int letters = 0, cyr = 0;
            foreach (var ch in t)
            {
                if (!char.IsLetter(ch)) continue;
                letters++;
                if (ch >= '\u0400' && ch <= '\u04FF') cyr++;
            }
            return letters > 0 && cyr * 2 > letters;
        }

        async Task RunSmartAsync(string kind)
        {
            var text = _smartText;
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > 6000) text = text.Substring(0, 6000);

            string title, instr;
            switch (kind)
            {
                case "explain":
                    title = "💡 Объяснение";
                    instr = "Объясни простыми словами, что это значит и о чём речь. Если это код или текст ошибки — скажи, что происходит и как это исправить.";
                    break;
                case "translate":
                    string to = IsMostlyCyrillic(text) ? "английский" : (string.IsNullOrWhiteSpace(Settings.Current.TranslateTo) ? "русский" : Settings.Current.TranslateTo);
                    title = "🌐 Перевод → " + to;
                    instr = $"Переведи текст на {to} язык. Выведи только перевод, без пояснений.";
                    break;
                case "fix":
                    title = "✍ Исправленный текст";
                    instr = "Исправь орфографию, пунктуацию и грамматику, сохрани смысл и стиль. Выведи только исправленный текст, без пояснений.";
                    break;
                default:
                    title = "✂ Коротко";
                    instr = "Перескажи главное в 1–3 коротких предложениях на языке текста.";
                    break;
            }

            int seq = ++_smartSeq;
            _smartBusy = true;
            _smartAnswer = null;
            _smartQuestion = instr + "\n\n" + text;

            AnswerTitle.Text = title;
            AnswerText.Text = "Думаю…";
            AnswerCopyBtn.IsEnabled = false;
            AnswerChatBtn.IsEnabled = false;
            _toastTimer.Stop();
            _smartLeave.Stop();
            SetMode(Mode.Answer);

            try
            {
                var prov = ChatClient.Current;
                var history = new List<ChatTurn> { new ChatTurn { Role = "user", Text = _smartQuestion } };
                string answer = await ChatClient.AskAsync(prov, ChatClient.ModelFor(prov), history);
                if (seq != _smartSeq) return;
                _smartAnswer = answer;
                AnswerText.Text = answer;
            }
            catch (Exception ex)
            {
                if (seq != _smartSeq) return;
                AnswerText.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                if (seq == _smartSeq)
                {
                    _smartBusy = false;
                    AnswerCopyBtn.IsEnabled = _smartAnswer != null;
                    AnswerChatBtn.IsEnabled = _smartAnswer != null;
                    // пока думали, курсор мог уйти с острова — тогда пора запускать обратный отсчёт
                    if (!Pill.IsMouseOver) { _smartLeave.Interval = TimeSpan.FromSeconds(8); _smartLeave.Start(); }
                }
            }
        }

        void AnswerClose_Click(object s, RoutedEventArgs e) => CloseSmart();

        async void AnswerCopy_Click(object s, RoutedEventArgs e)
        {
            if (_smartAnswer == null) return;
            SetClipboardText(_smartAnswer);   // свою запись в буфер остров не «слушает»
            AnswerCopyBtn.Content = "✓ Скопировано";
            await Task.Delay(1200);
            AnswerCopyBtn.Content = "📋 Копировать";
        }

        void AnswerToChat_Click(object s, RoutedEventArgs e)
        {
            if (_smartAnswer == null) return;
            Chat.AddExchange(_smartQuestion, _smartAnswer);
            _smartLeave.Stop();
            _leaveTimer.Stop();
            SetMode(Mode.Chat);
            Activate();
            Dispatcher.BeginInvoke(new Action(Chat.FocusInput), DispatcherPriority.Input);
        }

        void SmartToggle_Click(object s, RoutedEventArgs e)
        {
            Settings.Current.SmartClip = !Settings.Current.SmartClip;
            Settings.Current.Save();
            UpdateToggleLabels();
        }
    }
}
