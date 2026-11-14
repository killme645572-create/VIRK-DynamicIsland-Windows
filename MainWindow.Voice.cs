using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Island
{
    // Голосовой помощник. Запуск: Ctrl+Alt+V (держи и говори — или нажми и скажи, остановится по тишине),
    // фраза «Привет, остров» или кнопка «🎙 Голос» в «Инструментах».
    // Речь → текст (Whisper на Groq) → команда (помодоро, таймер, скриншот, запись, лента…) или вопрос к ИИ →
    // ответ текстом в острове и/или голосом (голоса Windows) — как выбрано в ⚙.
    public partial class MainWindow
    {
        const int VoiceHotkeyId = 0x4956;
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        WakeWord _wake;
        bool _voiceBusy, _voiceStop, _wakeErrShown, _cloudWakeNoted;
        string _voiceStage = "";                  // listening | thinking | speaking
        CancellationTokenSource _voiceCts;
        Action _voicePost;                        // действие, которое запускаем уже после ответа (скриншот, запись…)
        readonly List<ChatTurn> _voiceHist = new List<ChatTurn>();

        void InitVoice()
        {
            _wake = new WakeWord();
            _wake.Heard += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Settings.Current.Voice && Settings.Current.VoiceWake && !_voiceBusy) _ = RunVoiceAsync(false);
            }));
        }

        // ---------- горячая клавиша Ctrl+Alt+V ----------

        void RegisterVoiceHotkey()
        {
            if (!Settings.Current.Voice) return;
            bool ok = false;
            try { ok = RegisterHotKey(_hwnd, VoiceHotkeyId, 0x0002 | 0x0001 | 0x4000, 0x56); } catch { }
            if (!ok) Dispatcher.BeginInvoke(new Action(() => ShowToast("Ctrl+Alt+V занято другой программой", 3)), DispatcherPriority.ApplicationIdle);
        }

        void UnregisterVoiceHotkey()
        {
            try { UnregisterHotKey(_hwnd, VoiceHotkeyId); } catch { }
        }

        void OnVoiceHotkey()
        {
            if (!Settings.Current.Voice) return;
            if (_voiceBusy) { VoiceInterrupt(); return; }
            _ = RunVoiceAsync(true);
        }

        void ToolVoice_Click(object s, RoutedEventArgs e)
        {
            if (!Settings.Current.Voice) { ShowToast("Голосовой помощник выключен (⚙)", 3); return; }
            if (_voiceBusy) { VoiceInterrupt(); return; }
            _ = RunVoiceAsync(false);
        }

        // повторное нажатие: пока слушаем — заканчиваем запись, пока думаем или говорим — отменяем
        void VoiceInterrupt()
        {
            if (_voiceStage == "listening") _voiceStop = true;
            else { try { _voiceCts?.Cancel(); } catch { } Tts.Stop(); }
        }

        void StopSpeech()
        {
            Tts.Stop();
            if (_voiceStage == "speaking") { try { _voiceCts?.Cancel(); } catch { } }
        }

        // ---------- слово активации ----------

        async Task StartWakeAsync(bool report = false)
        {
            var st = Settings.Current;
            if (!st.Voice || !st.VoiceWake) return;
            string err = await _wake.StartAsync();
            if (err != null && (report || !_wakeErrShown))
            {
                _wakeErrShown = true;
                ShowToast("🗣 «Привет, " + Settings.Current.AssistantName + "» не работает: " + err, 8);
                if (report && _wake.NeedsRuSpeech)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); } catch { }
                }
            }
            else if (err == null && _wake.Mode == "groq" && (report || !_cloudWakeNoted))
            {
                _cloudWakeNoted = true;
                ShowToast(_wake.NeedsRuSpeech
                    ? "🗣 «Привет, " + Settings.Current.AssistantName + "» слушаю через Groq: в Windows нет русского речевого пакета. Короткие фразы уходят на распознавание в Groq."
                    : "🗣 «Привет, " + Settings.Current.AssistantName + "» слушаю через Groq. Короткие фразы уходят на распознавание в Groq.", 8);
            }
        }

        // ---------- сам диалог ----------

        async Task RunVoiceAsync(bool hotkey)
        {
            if (_voiceBusy) return;
            _voiceBusy = true;
            _voiceStop = false;
            _voicePost = null;
            _voiceCts = new CancellationTokenSource();
            var ct = _voiceCts.Token;
            try
            {
                Tts.Stop();
                await _wake.StopAsync();

                if (!hotkey)
                {
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    await Task.Delay(350, ct);
                }

                _voiceStage = "listening";
                ShowRecToast("🎙 Слушаю…", 45);
                Func<bool> keyDown = hotkey ? (Func<bool>)(() => (GetAsyncKeyState(0x56) & 0x8000) != 0) : null;
                byte[] wav = await VoiceCapture.RecordAsync(keyDown, () => _voiceStop, ct);
                if (wav == null) { ShowRecToast("🎙 Не услышал голос", 2.5); return; }

                _voiceStage = "thinking";
                ShowRecToast("⏳ Распознаю…", 45);
                string text = await Stt.TranscribeAsync(wav, ct);
                text = WakeWord.StripGreeting(text);
                VoiceLog.Write("текст: " + text);
                if (text.Length == 0) { ShowRecToast("🎙 Не расслышал — повтори", 2.5); return; }

                string reply = await TryVoiceCommandAsync(text);
                bool isCommand = reply != null;
                if (reply == null)
                {
                    ShowRecToast("💭 " + Short(text, 42), 60);
                    reply = await AskVoiceModelAsync(text, ct);
                }
                VoiceLog.Write((isCommand ? "команда → " : "ответ → ") + reply);
                await DeliverVoiceReplyAsync(text, reply, isCommand, ct);
            }
            catch (OperationCanceledException)
            {
                if (_mode == Mode.Toast) SetMode(Mode.Idle);
            }
            catch (Exception ex)
            {
                VoiceLog.Write("ошибка: " + ex);
                ShowRecToast("⚠ " + Short(ex.Message, 90), 6);
            }
            finally
            {
                _voiceBusy = false;
                _voiceStage = "";
                var post = _voicePost;
                _voicePost = null;
                if (post != null) { try { post(); } catch (Exception ex) { VoiceLog.Write("действие после ответа: " + ex.Message); } }
                if (Settings.Current.Voice && Settings.Current.VoiceWake) _ = StartWakeAsync();
            }
        }

        static string Short(string s, int n)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        static async Task<T> WithCancel<T>(Task<T> t, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            using (ct.Register(() => tcs.TrySetResult(true)))
            {
                var done = await Task.WhenAny(t, tcs.Task);
                if (done != t) throw new OperationCanceledException(ct);
                return await t;
            }
        }

        async Task<string> AskVoiceModelAsync(string text, CancellationToken ct)
        {
            var prov = ChatClient.Current;
            var hist = new List<ChatTurn>(_voiceHist);
            hist.Add(new ChatTurn
            {
                Role = "user",
                Text = "[Голосовой помощник по имени " + Settings.Current.AssistantName + ". Сейчас " + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) +
                       ". Ответь кратко, 1–3 коротких предложения, обычным текстом: без списков, markdown, смайликов и ссылок.] " + text
            });
            string answer = await WithCancel(ChatClient.AskAsync(prov, ChatClient.ModelFor(prov), hist), ct);

            _voiceHist.Add(new ChatTurn { Role = "user", Text = text });
            _voiceHist.Add(new ChatTurn { Role = "assistant", Text = answer });
            while (_voiceHist.Count > 8) _voiceHist.RemoveAt(0);
            return answer;
        }

        async Task DeliverVoiceReplyAsync(string question, string reply, bool isCommand, CancellationToken ct)
        {
            string mode = Settings.Current.VoiceReply ?? "both";
            bool showText = mode != "voice";
            bool speak = mode != "text";

            if (showText)
            {
                if (isCommand) ShowRecToast("🎙 " + reply, 5);
                else
                {
                    _smartSeq++;
                    _smartBusy = false;
                    _smartQuestion = question;
                    _smartAnswer = reply;
                    AnswerTitle.Text = "🎙 " + Short(question, 40);
                    AnswerText.Text = reply;
                    AnswerCopyBtn.IsEnabled = true;
                    AnswerChatBtn.IsEnabled = true;
                    _toastTimer.Stop();
                    _smartLeave.Stop();
                    SetMode(Mode.Answer);
                }
            }
            else ShowRecToast("🔊 " + Short(reply, 60), 60);

            if (speak)
            {
                _voiceStage = "speaking";
                try { await Tts.SpeakAsync(reply, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    VoiceLog.Write("озвучка: " + ex.Message);
                    if (!showText)
                    {
                        // голос недоступен — хотя бы покажем текст
                        _voiceStage = "";
                        ShowRecToast("⚠ " + Short(ex.Message, 80), 6);
                        return;
                    }
                    ShowToast("⚠ Озвучка: " + Short(ex.Message, 80), 5);
                }
            }

            if (!showText && _mode == Mode.Toast) SetMode(Mode.Idle);
            if (showText && !isCommand && _mode == Mode.Answer && !Pill.IsMouseOver)
            {
                _smartLeave.Interval = TimeSpan.FromSeconds(Math.Max(10, reply.Length / 10.0));
                _smartLeave.Start();
            }
        }

        // ---------- голосовые команды ----------

        static string Norm(string s)
        {
            s = (s ?? "").ToLowerInvariant().Replace('ё', 'е');
            s = Regex.Replace(s, @"[^\p{L}\p{Nd}\s]", " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        static readonly Dictionary<string, int> NumWords = new Dictionary<string, int>
        {
            ["один"] = 1, ["одну"] = 1, ["одна"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четыре"] = 4, ["пять"] = 5,
            ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10, ["одиннадцать"] = 11,
            ["двенадцать"] = 12, ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15, ["шестнадцать"] = 16,
            ["семнадцать"] = 17, ["восемнадцать"] = 18, ["девятнадцать"] = 19, ["двадцать"] = 20, ["тридцать"] = 30,
            ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60, ["семьдесят"] = 70, ["восемьдесят"] = 80,
            ["девяносто"] = 90, ["сто"] = 100
        };

        // первое число в тексте: цифрами или словами («двадцать пять»)
        static int? ParseNumber(string t)
        {
            var tok = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tok.Length; i++)
            {
                if (int.TryParse(tok[i], NumberStyles.None, CultureInfo.InvariantCulture, out int d)) return d;
                if (!NumWords.TryGetValue(tok[i], out int v)) continue;
                if (v >= 20 && v < 100 && i + 1 < tok.Length && NumWords.TryGetValue(tok[i + 1], out int ones) && ones < 10) v += ones;
                return v;
            }
            return null;
        }

        static TimeSpan? ParseDuration(string t)
        {
            if (Regex.IsMatch(t, @"\bполчаса\b")) return TimeSpan.FromMinutes(30);
            if (Regex.IsMatch(t, @"\bполтора часа\b")) return TimeSpan.FromMinutes(90);
            var tok = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tok.Length; i++)
            {
                string u = tok[i];
                double mult = u.StartsWith("сек") ? 1 : u.StartsWith("мин") && u != "минус" ? 60 : (u.StartsWith("час") && u != "часы") ? 3600 : 0;
                if (mult == 0) continue;
                int? n = null;
                if (i >= 2) n = ParseNumber(tok[i - 2] + " " + tok[i - 1]);
                if (n == null && i >= 1) n = ParseNumber(tok[i - 1]);
                if (n == null) n = 1;   // «на минуту», «на час»
                return TimeSpan.FromSeconds(n.Value * mult);
            }
            return null;
        }

        static string HumanDuration(TimeSpan d)
        {
            if (d.TotalSeconds < 60) return (int)d.TotalSeconds + " сек";
            if (d.TotalMinutes < 60) return (int)Math.Round(d.TotalMinutes) + " мин";
            int h = (int)d.TotalHours, m = d.Minutes;
            return m == 0 ? h + " ч" : h + " ч " + m + " мин";
        }

        static void MediaKey(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
        }

        // null — это не команда, пусть отвечает ИИ
        async Task<string> TryVoiceCommandAsync(string raw)
        {
            string t = Norm(raw);
            bool Has(string rx) => Regex.IsMatch(t, rx);

            if (Has(@"^(стоп|хватит|замолчи|отмена|ничего|не надо|забудь|ладно)$")) return "Хорошо.";

            // помодоро
            if (Has(@"пом[оиа]д[оа]р|помидор|\bфокус"))
            {
                if (Has(@"останови|выключи|отмени|заверши|стоп|хватит|прекрати"))
                {
                    if (Pomodoro.Instance.Active) { Pomodoro.Instance.Stop(); return "Помодоро остановлен."; }
                    return "Помодоро и так не запущен.";
                }
                if (Has(@"запусти|включи|начни|поставь|стартуй|старт|давай|заведи|сделай"))
                {
                    var d = ParseDuration(t);
                    if (d != null && d.Value.TotalMinutes >= 1 && d.Value.TotalMinutes <= 180)
                    {
                        Settings.Current.PomoFocus = (int)Math.Round(d.Value.TotalMinutes);
                        Settings.Current.Save();
                    }
                    Pomodoro.Instance.Start();
                    return "Фокус на " + Settings.Current.PomoFocus + " мин. Поехали!";
                }
            }

            // таймер
            if (Has(@"таймер|засеки|напомни через|разбуди через"))
            {
                if (Has(@"отмени|останови|выключи|сбрось|убери|стоп"))
                {
                    if (IslandTimer.Instance.Active) { IslandTimer.Instance.Cancel(); return "Таймер отменён."; }
                    return "Таймер не запущен.";
                }
                var d = ParseDuration(t);
                if (d == null) return "На сколько поставить таймер? Скажи, например: таймер на пять минут.";
                IslandTimer.Instance.Start(d.Value);
                return "Таймер на " + HumanDuration(d.Value) + ".";
            }

            // скриншот
            if (Has(@"скриншот|снимок экрана|сфотографируй экран|сними экран"))
            {
                if (Has(@"област|кусок|част|выдел|фрагмент")) { _voicePost = () => ToolArea_Click(null, null); return "Выдели область мышью."; }
                _voicePost = () => ToolScreen_Click(null, null);
                return "Делаю скриншот экрана.";
            }

            // запись экрана
            if (Has(@"запис|запиши") && Has(@"экран|видео"))
            {
                if (Has(@"останови|заверши|хватит|стоп|выключи|закончи|прекрати|заканчивай"))
                {
                    if (!ScreenRecorder.IsRecording) return "Запись и так не идёт.";
                    _voicePost = () => _ = StopRecordingAsync();
                    return "Останавливаю запись.";
                }
                if (ScreenRecorder.IsRecording) return "Запись уже идёт.";
                _voicePost = () => ToolRec_Click(null, null);
                return "Начинаю запись экрана.";
            }

            // вопрос про экран
            if (Has(@"что (у меня )?на экране|что происходит на экране|спроси про экран|посмотри на экран|прочитай экран|что тут написано"))
            {
                _voicePost = () => _ = AskAboutScreenAsync();
                return "Выдели область, и я посмотрю.";
            }

            // RGB-лента
            if (Has(@"\bлент|подсветк"))
            {
                bool off = Has(@"выключи|погаси|отключи|потуши");
                bool on = Has(@"включи|зажги|запусти");
                if (off || on)
                {
                    if (Settings.Current.LedAddress == 0) { _voicePost = () => OpenLedPanel(); return "Лента ещё не выбрана — выбери её в открывшемся окне."; }
                    await SetLedPower(on && !off);
                    return off ? "Ленту выключил." : "Ленту включил.";
                }
            }

            // тема Windows
            if (Has(@"(темн|светл)\w* (тем|режим)|переключи тему|смени тему"))
            {
                bool wantDark = Has(@"темн"), wantLight = Has(@"светл");
                bool dark = IsDarkTheme();
                if (wantDark && dark) return "Тёмная тема уже включена.";
                if (wantLight && !dark) return "Светлая тема уже включена.";
                ToggleTheme();
                return dark ? "Включаю светлую тему." : "Включаю тёмную тему.";
            }

            // громкость
            if (Has(@"громкост|громче|тише|без звука|выключи звук|отключи звук"))
            {
                float cur = VolumeControl.Get();
                if (cur >= 0)
                {
                    int? n = ParseNumber(t);
                    float target = cur;
                    if (Has(@"без звука|выключи звук|отключи звук")) target = 0;
                    else if (Has(@"громче|прибавь|увеличь|погромче")) target = cur + (n != null && n <= 100 ? n.Value / 100f : 0.1f);
                    else if (Has(@"тише|убавь|уменьши|потише|убери")) target = cur - (n != null && n <= 100 ? n.Value / 100f : 0.1f);
                    else if (n != null && n <= 100) target = n.Value / 100f;
                    else return null;
                    target = Math.Max(0f, Math.Min(1f, target));
                    VolumeControl.Set(target);
                    return "Громкость " + (int)Math.Round(target * 100) + " процентов.";
                }
            }

            // музыка (медиа-клавиши: работает с любым плеером)
            if (Has(@"следующ\w* (трек|песн|композиц)|переключи (трек|песн)|пропусти (трек|песн)")) { MediaKey(0xB0); return "Следующий трек."; }
            if (Has(@"предыдущ\w* (трек|песн|композиц)|прошл\w* (трек|песн)")) { MediaKey(0xB1); return "Предыдущий трек."; }
            if (Has(@"^(пауза|поставь на паузу|продолжи|продолжай|играй)$|останови музыку|включи музыку|поставь музыку|музык\w* на паузу")) { MediaKey(0xB3); return "Готово."; }

            // блокировка
            if (Has(@"заблокируй (компьютер|экран|пк|систему)")) { _voicePost = () => Lock_Click(null, null); return "Блокирую компьютер."; }

            // время, дата, погода — отвечаем сами, ИИ этого не знает
            if (Has(@"который час|сколько времени|сколько сейчас времени|скажи время|сколько натикало"))
                return "Сейчас " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + ".";
            if (Has(@"какое (сегодня )?число|какой (сегодня )?день|какая (сегодня )?дата|какое сегодня"))
                return "Сегодня " + DateTime.Now.ToString("d MMMM, dddd", new CultureInfo("ru-RU")) + ".";
            if (Has(@"погод|на улице|температур") && !Has(@"завтра|послезавтра|недел|выходн"))
            {
                var w = _weather;
                if (w != null)
                    return "Сейчас " + (int)Math.Round(w.Temp) + " градусов, ощущается как " + (int)Math.Round(w.Feels) + ", ветер " + (int)Math.Round(w.Wind) + " метров в секунду.";
            }

            // закрыть сайт (вкладку) или программу: «закрой ютуб», «закрой калькулятор», «убей телеграм»
            string closeReply = await TryCloseCommandAsync(t);
            if (closeReply != null) return closeReply;

            // открыть сайт, программу, папку; поиск в ютубе / гугле / яндексе
            string openReply = TryOpenCommand(raw, t);
            if (openReply != null) return openReply;

            return null;
        }

        // ---------- настройки (⚙) ----------

        static string VoiceReplyLabel()
        {
            switch (Settings.Current.VoiceReply)
            {
                case "text": return "💬 Ответ голоса: текстом";
                case "voice": return "🔊 Ответ голоса: голосом";
                default: return "💬🔊 Ответ голоса: текст и голос";
            }
        }

        void VoiceToggle_Click(object s, RoutedEventArgs e)
        {
            var st = Settings.Current;
            st.Voice = !st.Voice;
            st.Save();
            UpdateToggleLabels();
            if (st.Voice) { RegisterVoiceHotkey(); _ = StartWakeAsync(true); }
            else { UnregisterVoiceHotkey(); _ = _wake.StopAsync(); StopSpeech(); }
        }

        void VoiceWakeToggle_Click(object s, RoutedEventArgs e)
        {
            var st = Settings.Current;
            st.VoiceWake = !st.VoiceWake;
            st.Save();
            UpdateToggleLabels();
            if (st.VoiceWake && st.Voice) _ = StartWakeAsync(true);
            else _ = _wake.StopAsync();
        }

        void WakeSens_Click(object s, RoutedEventArgs e)
        {
            var st = Settings.Current;
            st.WakeSensitivity = (Math.Max(0, Math.Min(2, st.WakeSensitivity)) + 1) % 3;
            st.Save();
            UpdateToggleLabels();
            if (st.Voice && st.VoiceWake) _ = StartWakeAsync(true);   // список фраз для Windows собирается при запуске
        }

        void WakeEngine_Click(object s, RoutedEventArgs e)
        {
            var st = Settings.Current;
            st.WakeEngine = string.Equals(st.WakeEngine, "groq", StringComparison.OrdinalIgnoreCase) ? "auto" : "groq";
            st.Save();
            UpdateToggleLabels();
            if (st.Voice && st.VoiceWake) _ = StartWakeAsync(true);
        }

        // ---------- выбор микрофона ----------

        static string VoiceMicLabel()
        {
            string n = Settings.Current.VoiceMicDevice;
            if (string.IsNullOrWhiteSpace(n)) return "🎤 Микрофон: по умолчанию";
            n = n.Trim();
            return "🎤 Микрофон: " + (n.Length > 26 ? n.Substring(0, 26) + "…" : n);
        }

        void VoiceMic_Click(object s, RoutedEventArgs e)
        {
            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = VoiceMicBtn,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };
            string cur = Settings.Current.VoiceMicDevice;
            void Add(string header, string value)
            {
                bool on = string.IsNullOrWhiteSpace(value)
                    ? string.IsNullOrWhiteSpace(cur)
                    : !string.IsNullOrWhiteSpace(cur) && cur.Trim().Equals(value, StringComparison.OrdinalIgnoreCase);
                var mi = new System.Windows.Controls.MenuItem { Header = header, IsChecked = on };
                mi.Click += (a, b) => SetVoiceMic(value);
                menu.Items.Add(mi);
            }
            Add("По умолчанию (микрофон Windows)", null);
            try
            {
                using (var en = new NAudio.CoreAudioApi.MMDeviceEnumerator())
                {
                    foreach (NAudio.CoreAudioApi.MMDevice d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
                    {
                        string nm = null;
                        try { nm = d.FriendlyName; } catch { }
                        if (!string.IsNullOrWhiteSpace(nm)) Add(nm, nm);
                    }
                }
            }
            catch (Exception ex) { VoiceLog.Write("список микрофонов: " + ex.Message); }

            // пока открыто меню, остров не сворачиваем
            _holdOpen = true;
            _leaveTimer.Stop();
            menu.Closed += (a, b) =>
            {
                _holdOpen = false;
                if (!Pill.IsMouseOver && _mode == Mode.Open) { _leaveTimer.Stop(); _leaveTimer.Start(); }
            };
            menu.IsOpen = true;
        }

        void SetVoiceMic(string name)
        {
            var st = Settings.Current;
            st.VoiceMicDevice = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            st.Save();
            UpdateToggleLabels();
            VoiceLog.Write("микрофон помощника выбран: " + (st.VoiceMicDevice ?? "по умолчанию"));
            if (st.Voice && st.VoiceWake) _ = StartWakeAsync(true);   // слушалка перезапускается уже на новом микрофоне
        }

        void VoiceReplyBtn_Click(object s, RoutedEventArgs e)
        {
            var st = Settings.Current;
            st.VoiceReply = st.VoiceReply == "both" || string.IsNullOrEmpty(st.VoiceReply) ? "text" : st.VoiceReply == "text" ? "voice" : "both";
            st.Save();
            UpdateToggleLabels();
        }
    }
}
