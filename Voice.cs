using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;

namespace Island
{
    // Лог голосового помощника: %AppData%\Island\voice.log (что услышал, что распознал, почему не сработало)
    static class VoiceLog
    {
        static readonly object L = new object();

        public static void Write(string text)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");
                Directory.CreateDirectory(dir);
                var f = Path.Combine(dir, "voice.log");
                lock (L)
                {
                    if (File.Exists(f) && new FileInfo(f).Length > 200000) File.Delete(f);
                    File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + "\r\n");
                }
            }
            catch { }
        }
    }

    // Запись голоса с микрофона. Конец фразы определяем по тишине (или по отпусканию клавиши).
    static class VoiceCapture
    {
        internal static float RmsOf(byte[] buf, int n, WaveFormat wf)
        {
            try
            {
                var std = (wf as WaveFormatExtensible)?.ToStandardWaveFormat() ?? wf;
                double sum = 0; int cnt = 0;
                if (std.Encoding == WaveFormatEncoding.IeeeFloat && std.BitsPerSample == 32)
                {
                    for (int i = 0; i + 3 < n; i += 4) { float v = BitConverter.ToSingle(buf, i); sum += v * v; cnt++; }
                }
                else if (std.BitsPerSample == 16)
                {
                    for (int i = 0; i + 1 < n; i += 2) { float v = BitConverter.ToInt16(buf, i) / 32768f; sum += v * v; cnt++; }
                }
                else if (std.BitsPerSample == 32)
                {
                    for (int i = 0; i + 3 < n; i += 4) { float v = BitConverter.ToInt32(buf, i) / 2147483648f; sum += v * v; cnt++; }
                }
                else return 0.05f;
                return cnt == 0 ? 0f : (float)Math.Sqrt(sum / cnt);
            }
            catch { return 0.05f; }
        }

        // keyDown != null — запуск горячей клавишей: если клавишу держат дольше 0,35 с, пишем, пока держат;
        // иначе (быстрое нажатие или фраза «привет остров») — пока не наступит тишина.
        // Возвращает WAV 16 кГц моно или null, если голоса не было.
        public static async Task<byte[]> RecordAsync(Func<bool> keyDown, Func<bool> stopRequested, CancellationToken ct)
        {
            var lk = new object();
            var ms = new MemoryStream();
            var sw = Stopwatch.StartNew();
            float minRms = float.MaxValue;
            bool heard = false;
            long lastVoiceMs = 0;
            WaveFormat fmt = null;
            WasapiCapture cap = null;
            var stopped = new TaskCompletionSource<bool>();

            using (var en = new MMDeviceEnumerator())
            {
                foreach (var dev in ScreenRecorder.MicCandidates(en, Settings.Current.VoiceMicDevice))
                {
                    string nm = "?";
                    try { nm = dev.FriendlyName; } catch { }
                    try
                    {
                        var c = new WasapiCapture(dev);
                        var f = c.WaveFormat;
                        c.DataAvailable += (s, e) =>
                        {
                            float rms = RmsOf(e.Buffer, e.BytesRecorded, f);
                            lock (lk)
                            {
                                ms.Write(e.Buffer, 0, e.BytesRecorded);
                                long t = sw.ElapsedMilliseconds;
                                if (t < 500)
                                {
                                    if (rms < minRms) minRms = rms;   // самый тихий кусок начала — это фон
                                }
                                else
                                {
                                    float thr = Math.Max(0.008f, (minRms == float.MaxValue ? 0f : minRms) * 3f);
                                    if (rms > thr) { heard = true; lastVoiceMs = t; }
                                }
                            }
                        };
                        c.RecordingStopped += (s, e) => stopped.TrySetResult(true);
                        c.StartRecording();
                        cap = c; fmt = f;
                        VoiceLog.Write("микрофон: " + nm + " (" + f + ")");
                        break;
                    }
                    catch (Exception ex) { VoiceLog.Write("микрофон «" + nm + "» не стартовал: " + ex.Message); }
                }
                if (cap == null) throw new InvalidOperationException("микрофон не запустился (см. audio.log)");

                bool holdMode = false, decided = false;
                try
                {
                    while (true)
                    {
                        await Task.Delay(40, ct).ConfigureAwait(true);
                        long el = sw.ElapsedMilliseconds;
                        bool down = keyDown != null && keyDown();

                        if (stopRequested != null && stopRequested()) break;
                        if (keyDown != null && !decided && el >= 350) { decided = true; holdMode = down; }

                        if (holdMode)
                        {
                            if (!down) { await Task.Delay(250, ct).ConfigureAwait(true); break; }   // хвост после отпускания
                            if (el > 40000) break;
                        }
                        else
                        {
                            bool h; long lv;
                            lock (lk) { h = heard; lv = lastVoiceMs; }
                            if (h && el - lv > 1300) break;     // договорил
                            if (!h && el > 7000) break;         // так и не заговорил
                            if (el > 25000) break;
                        }
                    }
                }
                finally
                {
                    try { cap.StopRecording(); } catch { stopped.TrySetResult(true); }
                    await Task.WhenAny(stopped.Task, Task.Delay(2000)).ConfigureAwait(true);
                    try { cap.Dispose(); } catch { }
                }
            }

            byte[] raw; bool wasHeard;
            lock (lk) { raw = ms.ToArray(); wasHeard = heard; }
            VoiceLog.Write("записано " + raw.Length + " байт, голос " + (wasHeard ? "был" : "не был") + ", " + sw.ElapsedMilliseconds + " мс");
            if (!wasHeard || raw.Length < 4000) return null;
            return await Task.Run(() => ToWhisperWav(raw, fmt)).ConfigureAwait(true);
        }

        // WAV микрофона → 16 кГц моно (меньше размер, Whisper так и работает). Конвертирует ffmpeg, который уже есть для записи экрана.
        internal static byte[] ToWhisperWav(byte[] raw, WaveFormat fmt)
        {
            string stamp = Guid.NewGuid().ToString("N");
            string tin = Path.Combine(Path.GetTempPath(), "island_vin_" + stamp + ".wav");
            string tout = Path.Combine(Path.GetTempPath(), "island_vout_" + stamp + ".wav");
            try
            {
                using (var w = new WaveFileWriter(tin, fmt)) w.Write(raw, 0, raw.Length);
                var ff = ScreenRecorder.FindFfmpeg();
                if (ff != null)
                {
                    var psi = new ProcessStartInfo(ff, "-y -i \"" + tin + "\" -ac 1 -ar 16000 -c:a pcm_s16le \"" + tout + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            p.ErrorDataReceived += (s, e) => { };
                            p.BeginErrorReadLine();
                            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } }
                        }
                    }
                    if (File.Exists(tout) && new FileInfo(tout).Length > 1000) return File.ReadAllBytes(tout);
                    VoiceLog.Write("ffmpeg не смог сконвертировать — отправляю как есть");
                }
                return File.ReadAllBytes(tin);
            }
            finally
            {
                try { File.Delete(tin); } catch { }
                try { File.Delete(tout); } catch { }
            }
        }
    }

    // Распознавание речи: Whisper на Groq (тот же ключ Groq, что и для чата).
    static class Stt
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        static readonly Regex Junk = new Regex(
            @"^\s*(продолжение следует|субтитры.*|редактор субтитров.*|корректор.*|спасибо за просмотр.*|подписывайтесь.*|музыка)\W*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct, string prompt = null)
        {
            var groq = ChatClient.Find("groq");
            var key = groq == null ? null : KeyStore.Get(groq);
            if (key == null)
                throw new InvalidOperationException("для распознавания речи нужен ключ Groq — команда в чате: /key groq gsk_...  (взять: console.groq.com/keys)");

            using (var form = new MultipartFormDataContent())
            {
                var file = new ByteArrayContent(wav);
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "voice.wav");
                form.Add(new StringContent("whisper-large-v3-turbo"), "model");
                form.Add(new StringContent("json"), "response_format");
                form.Add(new StringContent("0"), "temperature");
                var lang = Settings.Current.VoiceLang;
                if (!string.IsNullOrWhiteSpace(lang)) form.Add(new StringContent(lang.Trim()), "language");
                if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");

                using (var req = new HttpRequestMessage(HttpMethod.Post, groq.BaseUrl + "/audio/transcriptions") { Content = form })
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    HttpResponseMessage resp;
                    try { resp = await Http.SendAsync(req, ct).ConfigureAwait(true); }
                    catch (HttpRequestException ex) { throw new Exception("нет связи с Groq: " + ex.Message); }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new Exception("Groq слишком долго не отвечает"); }

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
                    if (!resp.IsSuccessStatusCode)
                    {
                        string msg = body;
                        try { msg = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString(); } catch { }
                        VoiceLog.Write("Whisper " + (int)resp.StatusCode + ": " + msg);
                        throw new Exception("распознавание: " + (msg.Length > 140 ? msg.Substring(0, 140) + "…" : msg));
                    }
                    string text = "";
                    try { text = JsonDocument.Parse(body).RootElement.GetProperty("text").GetString() ?? ""; } catch { }
                    text = text.Trim();
                    if (Junk.IsMatch(text)) { VoiceLog.Write("отброшено как галлюцинация: " + text); return ""; }
                    return text;
                }
            }
        }
    }

    // Озвучка ответа голосами Windows (те же, что у Диктора). Нужен русский голос: Параметры → Время и язык → Речь.
    static class Tts
    {
        static WaveOutEvent _out;
        static readonly object L = new object();

        public static void Stop()
        {
            lock (L) { try { _out?.Stop(); } catch { } }
        }

        // Чтобы не зачитывать разметку и огромные ответы
        public static string Clean(string text)
        {
            var t = Regex.Replace(text ?? "", @"```.*?```", " ", RegexOptions.Singleline);
            t = Regex.Replace(t, @"[*_`#>]+", "");
            t = Regex.Replace(t, @"https?://\S+", "ссылка");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            if (t.Length > 700)
            {
                int cut = t.LastIndexOfAny(new[] { '.', '!', '?' }, 700);
                t = cut > 200 ? t.Substring(0, cut + 1) : t.Substring(0, 700);
            }
            return t;
        }

        static VoiceInformation PickVoice()
        {
            var all = SpeechSynthesizer.AllVoices.ToList();
            string want = Settings.Current.VoiceName;
            if (!string.IsNullOrWhiteSpace(want))
            {
                var named = all.FirstOrDefault(v => v.DisplayName.IndexOf(want.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                if (named != null) return named;
            }
            var ru = all.Where(v => v.Language != null && v.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase)).ToList();
            return ru.FirstOrDefault();
        }

        public static async Task SpeakAsync(string text, CancellationToken ct)
        {
            text = Clean(text);
            if (text.Length == 0) return;

            var voice = PickVoice();
            if (voice == null)
                throw new InvalidOperationException("нет русского голоса Windows (Параметры → Время и язык → Речь → Добавить голоса)");

            byte[] wav;
            using (var synth = new SpeechSynthesizer())
            {
                synth.Voice = voice;
                using (var stream = await synth.SynthesizeTextToStreamAsync(text))
                using (var rs = stream.AsStreamForRead())
                using (var mem = new MemoryStream())
                {
                    await rs.CopyToAsync(mem).ConfigureAwait(true);
                    wav = mem.ToArray();
                }
            }
            VoiceLog.Write("озвучка: " + voice.DisplayName + ", " + wav.Length + " байт");

            ct.ThrowIfCancellationRequested();
            using (var reader = new WaveFileReader(new MemoryStream(wav)))
            using (var wo = new WaveOutEvent())
            {
                var done = new TaskCompletionSource<bool>();
                wo.PlaybackStopped += (s, e) => done.TrySetResult(true);
                lock (L) { _out = wo; }
                wo.Init(reader);
                wo.Play();
                using (ct.Register(() => { try { wo.Stop(); } catch { } }))
                    await done.Task.ConfigureAwait(true);
                lock (L) { if (ReferenceEquals(_out, wo)) _out = null; }
            }
            ct.ThrowIfCancellationRequested();
        }
    }

    // Слово активации «привет, Вирк»: офлайн-распознавание Windows по списку фраз (без отправки звука в интернет).
    sealed class WakeWord
    {
        SpeechRecognizer _rec;
        bool _want;
        public event Action Heard;
        public bool NeedsRuSpeech;
        public string Mode = "windows";   // windows | groq
        CloudWake _cloud;

        static readonly string[] Greetings = { "привет", "эй", "слушай", "окей", "хей" };

        // имена, на которые откликаемся: AssistantName (по умолчанию «Вирк») и старые имена из WakeAliases
        public static List<string> WakeNames()
        {
            var l = new List<string>();
            var st = Settings.Current;
            Action<string> add = n =>
            {
                n = (n ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
                if (n.Length > 0 && !l.Contains(n)) l.Add(n);
            };
            add(st.AssistantName);
            if (st.WakeAliases != null) foreach (var a in st.WakeAliases) add(a);
            if (l.Count == 0) l.Add("вирк");
            return l;
        }

        // как имя может быть записано распознавалкой (Whisper пишет «Вирк», «Верк», «Вёрк», «Virk»…)
        static string NamePattern(string n)
        {
            if (n == "вирк") return @"в[иеёыо]р[кг]\w*|virk|werk";
            if (n == "остров") return @"остр\w*|ostr\w*";
            return Regex.Escape(n);
        }

        public static string NamesPattern() => NamesRx();
        public static string GreetPattern() => GreetRx();
        static string NamesRx() => string.Join("|", WakeNames().Select(NamePattern));
        static string GreetRx() => string.Join("|", Greetings.Concat(new[] { "прив", "ей", "хай", "здравствуй" }));

        // «Привет, Вирк, который час» → «который час»
        public static string StripGreeting(string text)
        {
            try
            {
                return Regex.Replace(text ?? "", @"^\s*(?:(?:" + GreetRx() + @"|ну привет)[,.!\s]+)?(?:" + NamesRx() + @")[,.!:\s]*", "",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            }
            catch { return (text ?? "").Trim(); }
        }

        // фразы, на которые откликаемся, и «приманки»: обычная речь, похожая на них. Распознавание Windows по списку
        // всегда выбирает самый близкий вариант, поэтому без приманок любой шум рано или поздно превращается в «привет, Вирк»
        static string[] BuildPhrases(int sens)
        {
            var l = new List<string>();
            foreach (var n in WakeNames())
            {
                foreach (var g in Greetings) l.Add(g + " " + n);
                l.Add("привет, " + n);
                if (sens >= 2) l.Add(n);
            }
            l.AddRange(new[] { "привет", "привет всем", "привет мир", "привет вика", "привет вера", "привет вадим", "привет кирилл",
                               "привет оскар", "привет остап", "привет осип", "привет остин", "эй ты", "слушай" });
            return l.Distinct().ToArray();
        }

        static Regex WindowsWakeRx()
        {
            string names = string.Join("|", WakeNames().Select(Regex.Escape));
            return new Regex(@"^\W*(?:" + string.Join("|", Greetings) + @")?\W*(?:" + names + @")\W*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // null — слушаем, иначе текст причины
        public async Task<string> StartAsync()
        {
            await StopAsync().ConfigureAwait(true);
            try
            {
                var lang = new Windows.Globalization.Language("ru-RU");
                NeedsRuSpeech = false;
                Mode = "windows";
                bool hasRu = SpeechRecognizer.SupportedGrammarLanguages.Any(l => l.LanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
                bool forceCloud = string.Equals(Settings.Current.WakeEngine, "groq", StringComparison.OrdinalIgnoreCase);
                if (!hasRu || forceCloud)
                {
                    NeedsRuSpeech = !hasRu;
                    if (!hasRu)
                        VoiceLog.Write("нет русского распознавания; доступны: " + string.Join(", ", SpeechRecognizer.SupportedGrammarLanguages.Select(l => l.LanguageTag)));
                    // Whisper на Groq: короткие фразы проверяет он (точнее, чем речевой пакет Windows)
                    var cloud = new CloudWake();
                    string cerr = cloud.Start();
                    if (cerr == null)
                    {
                        cloud.Heard += () => Heard?.Invoke();
                        _cloud = cloud;
                        Mode = "groq";
                        _want = true;
                        VoiceLog.Write("слово активации: слушаю (Groq Whisper)");
                        return null;
                    }
                    if (!hasRu)
                        return "в Windows нет русского речевого пакета (Язык и регион → Русский → Параметры → Распознавание речи), а запасной режим не запустился: " + cerr;
                    VoiceLog.Write("режим Groq не запустился (" + cerr + ") — слушаю средствами Windows");
                }
                var rec = new SpeechRecognizer(lang);
                rec.Constraints.Add(new SpeechRecognitionListConstraint(BuildPhrases(Settings.Current.WakeSensitivity), "wake"));
                var comp = await rec.CompileConstraintsAsync();
                if (comp.Status != SpeechRecognitionResultStatus.Success)
                {
                    rec.Dispose();
                    return "не удалось подготовить распознавание: " + comp.Status;
                }
                try { rec.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromHours(1); } catch { }
                rec.ContinuousRecognitionSession.ResultGenerated += OnResult;
                rec.ContinuousRecognitionSession.Completed += OnCompleted;
                _rec = rec;
                _want = true;
                await rec.ContinuousRecognitionSession.StartAsync();
                VoiceLog.Write("слово активации: слушаю");
                return null;
            }
            catch (Exception ex)
            {
                VoiceLog.Write("слово активации не запустилось: " + ex);
                _want = false;
                string m = ex.Message ?? "";
                if ((uint)ex.HResult == 0x80045509 || m.IndexOf("privacy", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "включи «Распознавание голоса» в Параметры → Конфиденциальность → Речь";
                if ((uint)ex.HResult == 0x80070005)
                    return "нет доступа к микрофону (Параметры → Конфиденциальность → Микрофон)";
                return m.Length > 120 ? m.Substring(0, 120) : m;
            }
        }

        void OnResult(SpeechContinuousRecognitionSession s, SpeechContinuousRecognitionResultGeneratedEventArgs a)
        {
            try
            {
                var r = a.Result;
                VoiceLog.Write("слышу: «" + r.Text + "» уверенность " + r.Confidence + " (" + r.RawConfidence.ToString("0.00") + ")");
                if (r.Text == null || !WindowsWakeRx().IsMatch(r.Text)) return;   // приманки и всё прочее — мимо
                int sens = Settings.Current.WakeSensitivity;
                bool ok = r.Confidence == SpeechRecognitionConfidence.High || r.Confidence == SpeechRecognitionConfidence.Medium
                    || (sens <= 0 ? r.RawConfidence >= 0.5 : sens == 1 ? r.RawConfidence >= 0.3 : true);
                if (ok) Heard?.Invoke();
            }
            catch { }
        }

        async void OnCompleted(SpeechContinuousRecognitionSession s, SpeechContinuousRecognitionCompletedEventArgs a)
        {
            // сессия сама заканчивается (тайм-аут, смена микрофона) — поднимаем заново, если мы всё ещё хотим слушать
            try
            {
                VoiceLog.Write("слушатель остановился: " + a.Status);
                if (!_want) return;
                await Task.Delay(1500).ConfigureAwait(false);
                if (!_want) return;
                var rec = _rec;
                if (rec != null) await rec.ContinuousRecognitionSession.StartAsync();
            }
            catch (Exception ex) { VoiceLog.Write("перезапуск слушателя: " + ex.Message); }
        }

        public async Task StopAsync()
        {
            _want = false;
            var cl = _cloud;
            _cloud = null;
            if (cl != null) { try { cl.Stop(); } catch { } }
            var rec = _rec;
            _rec = null;
            if (rec == null) return;
            try { rec.ContinuousRecognitionSession.ResultGenerated -= OnResult; } catch { }
            try { rec.ContinuousRecognitionSession.Completed -= OnCompleted; } catch { }
            try { await rec.ContinuousRecognitionSession.StopAsync(); } catch { }
            try { rec.Dispose(); } catch { }
        }
    }

    // Запасное слово активации, если в Windows нет русского речевого пакета: слушаем микрофон,
    // короткие фразы (0,35–3,5 с) отправляем в Whisper на Groq и ищем «привет, остров».
    // Длинную речь (обычный разговор) и тишину не отправляем; не больше 12 запросов в минуту.
    sealed class CloudWake
    {
        const double MaxMs = 3500, MinVoiceMs = 350, EndSilenceMs = 600;
        Regex _wake, _wakeBare;

        public event Action Heard;

        readonly object _lk = new object();
        readonly MemoryStream _seg = new MemoryStream();
        readonly Queue<byte[]> _pre = new Queue<byte[]>();
        readonly Queue<DateTime> _sent = new Queue<DateTime>();
        MMDeviceEnumerator _en;
        WasapiCapture _cap;
        WaveFormat _fmt;
        volatile bool _want;
        bool _inVoice, _tooLong;
        double _segMs, _voicedMs, _silenceMs;
        float _noise = 0.005f;
        DateTime _pauseUntil = DateTime.MinValue;
        CancellationTokenSource _cts = new CancellationTokenSource();

        // null — слушаем, иначе текст причины
        public string Start()
        {
            var groq = ChatClient.Find("groq");
            if (groq == null || KeyStore.Get(groq) == null)
                return "нужен ключ Groq (в чате: /key groq gsk_...  — взять: console.groq.com/keys)";
            string names = WakeWord.NamesPattern();
            _wake = new Regex(@"(?:" + WakeWord.GreetPattern() + @"|приве)\W+(?:" + names + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _wakeBare = new Regex(@"^\W*(?:" + names + @")\W*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _want = true;
            string err = Open();
            if (err != null) _want = false;
            return err;
        }

        string Open()
        {
            Release();
            try { _en = new MMDeviceEnumerator(); }
            catch (Exception ex) { return "микрофон: " + ex.Message; }
            foreach (var dev in ScreenRecorder.MicCandidates(_en, Settings.Current.VoiceMicDevice))
            {
                string nm = "?";
                try { nm = dev.FriendlyName; } catch { }
                WasapiCapture c = null;
                try
                {
                    c = new WasapiCapture(dev);
                    _fmt = c.WaveFormat;
                    c.DataAvailable += OnData;
                    c.RecordingStopped += OnStopped;
                    c.StartRecording();
                    _cap = c;
                    VoiceLog.Write("слово активации (Groq): микрофон " + nm + " (" + _fmt + ")");
                    return null;
                }
                catch (Exception ex)
                {
                    VoiceLog.Write("слово активации (Groq): микрофон «" + nm + "» не стартовал: " + ex.Message);
                    try { if (c != null) { c.DataAvailable -= OnData; c.RecordingStopped -= OnStopped; c.Dispose(); } } catch { }
                }
            }
            return "микрофон не запустился";
        }

        void Release()
        {
            var c = _cap; _cap = null;
            if (c != null)
            {
                try { c.DataAvailable -= OnData; } catch { }
                try { c.RecordingStopped -= OnStopped; } catch { }
                try { c.StopRecording(); } catch { }
                try { c.Dispose(); } catch { }
            }
            try { _en?.Dispose(); } catch { }
            _en = null;
            lock (_lk) { _seg.SetLength(0); _pre.Clear(); _inVoice = false; _tooLong = false; }
        }

        public void Stop()
        {
            _want = false;
            try { _cts.Cancel(); } catch { }
            Release();
        }

        async void OnStopped(object s, StoppedEventArgs e)
        {
            try
            {
                if (!_want || !ReferenceEquals(s, _cap)) return;
                VoiceLog.Write("слово активации (Groq): запись остановилась" + (e?.Exception != null ? ": " + e.Exception.Message : "") + " — перезапуск");
                await Task.Delay(3000).ConfigureAwait(false);
                if (!_want) return;
                string err = Open();
                if (err != null) VoiceLog.Write("слово активации (Groq): " + err);
            }
            catch (Exception ex) { VoiceLog.Write("слово активации (Groq) перезапуск: " + ex.Message); }
        }

        void OnData(object s, WaveInEventArgs e)
        {
            if (!_want || e.BytesRecorded <= 0 || _fmt == null) return;
            var buf = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, buf, 0, e.BytesRecorded);
            double ms = buf.Length * 1000.0 / Math.Max(1, _fmt.AverageBytesPerSecond);
            float rms = VoiceCapture.RmsOf(buf, buf.Length, _fmt);
            byte[] send = null;

            lock (_lk)
            {
                bool hi = Settings.Current.WakeSensitivity >= 2;   // высокая чувствительность: порог голоса ниже
                float thr = Math.Max(hi ? 0.006f : 0.01f, _noise * (hi ? 2.5f : 3.5f));
                bool voiced = rms > thr;
                if (!voiced) _noise = _noise * 0.98f + rms * 0.02f;   // подстраиваемся под фон

                if (!_inVoice)
                {
                    if (voiced)
                    {
                        _inVoice = true; _tooLong = false;
                        _segMs = 0; _voicedMs = 0; _silenceMs = 0;
                        _seg.SetLength(0);
                        foreach (var p in _pre) { _seg.Write(p, 0, p.Length); _segMs += p.Length * 1000.0 / Math.Max(1, _fmt.AverageBytesPerSecond); }
                        _pre.Clear();
                        _seg.Write(buf, 0, buf.Length); _segMs += ms; _voicedMs += ms;
                    }
                    else
                    {
                        _pre.Enqueue(buf);
                        while (_pre.Count > 4) _pre.Dequeue();
                    }
                }
                else
                {
                    _segMs += ms;
                    if (voiced) { _voicedMs += ms; _silenceMs = 0; } else _silenceMs += ms;

                    if (!_tooLong)
                    {
                        if (_segMs > MaxMs) { _tooLong = true; _seg.SetLength(0); }   // это разговор, а не фраза-активация
                        else _seg.Write(buf, 0, buf.Length);
                    }

                    if (_silenceMs >= EndSilenceMs)
                    {
                        if (!_tooLong && _voicedMs >= MinVoiceMs) send = _seg.ToArray();
                        _inVoice = false; _tooLong = false; _seg.SetLength(0);
                    }
                }
            }

            if (send != null)
            {
                var fmt = _fmt;
                _ = Task.Run(() => CheckAsync(send, fmt));
            }
        }

        async Task CheckAsync(byte[] raw, WaveFormat fmt)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now < _pauseUntil) return;
                lock (_sent)
                {
                    while (_sent.Count > 0 && now - _sent.Peek() > TimeSpan.FromSeconds(60)) _sent.Dequeue();
                    if (_sent.Count >= 12) return;
                    _sent.Enqueue(now);
                }
                byte[] wav = VoiceCapture.ToWhisperWav(raw, fmt);
                string text = await Stt.TranscribeAsync(wav, _cts.Token, "Голосовой помощник по имени " + Settings.Current.AssistantName + ".").ConfigureAwait(false);
                if (!_want) return;
                VoiceLog.Write("слово активации (Groq): «" + text + "»");
                if (!string.IsNullOrEmpty(text) && (_wake.IsMatch(text) || (Settings.Current.WakeSensitivity >= 2 && _wakeBare.IsMatch(text)))) Heard?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                VoiceLog.Write("слово активации (Groq): " + ex.Message);
                string m = ex.Message ?? "";
                if (m.IndexOf("rate", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("limit", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("лимит", StringComparison.OrdinalIgnoreCase) >= 0)
                    _pauseUntil = DateTime.UtcNow.AddSeconds(60);
            }
        }
    }
}
