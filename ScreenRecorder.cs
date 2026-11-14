using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Island
{
    // Запись экрана: видео пишет ffmpeg (gdigrab + аппаратный кодер NVENC, при неудаче — libx264),
    // звук компьютера и микрофон пишутся отдельно через WASAPI и в конце склеиваются с видео в один MP4.
    static class ScreenRecorder
    {
        public static bool IsRecording { get; private set; }
        public static DateTime StartedAt { get; private set; }

        static Process _ff;
        static string _ffPath, _tmpVideo, _outFile, _sysWav, _micWav;
        static WasapiLoopbackCapture _loop;
        static WaveFileWriter _loopW;
        static WasapiCapture _mic;
        static WaveFileWriter _micW;
        static WasapiOut _silence;

        // диагностика звука: %AppData%\Island\audio.log + предупреждение для тоста
        static readonly StringBuilder _alog = new StringBuilder();
        static long _sysBytes, _micBytes;
        static float _sysPeak, _micPeak;
        static string _micName;
        public static string Warning { get; private set; }
        static string _audioMode;

        static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");

        public static string FindFfmpeg()
        {
            var cands = new List<string>();
            if (!string.IsNullOrWhiteSpace(Settings.Current.FfmpegPath)) cands.Add(Settings.Current.FfmpegPath);
            cands.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
            cands.Add(Path.Combine(Environment.CurrentDirectory, "ffmpeg.exe"));
            cands.Add(Path.Combine(AppDataDir, "ffmpeg.exe"));
            foreach (var c in cands) if (File.Exists(c)) return c;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var p = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        static void WriteLog(string text)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(Path.Combine(AppDataDir, "ffmpeg.log"), text);
            }
            catch { }
        }

        static void ALog(string text)
        {
            lock (_alog) _alog.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text);
        }

        static void FlushAudioLog()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                lock (_alog) File.WriteAllText(Path.Combine(AppDataDir, "audio.log"), _alog.ToString());
            }
            catch { }
        }

        static void AddWarning(string w)
        {
            Warning = string.IsNullOrEmpty(Warning) ? w : Warning + "; " + w;
        }

        // максимальная громкость в блоке (0..1), чтобы отличить «пишет тишину» от «пишет звук»
        static float PeakOf(byte[] buf, int n, WaveFormat wf)
        {
            float max = 0;
            try
            {
                var std = (wf as WaveFormatExtensible)?.ToStandardWaveFormat() ?? wf;
                if (std.Encoding == WaveFormatEncoding.IeeeFloat && std.BitsPerSample == 32)
                {
                    for (int i = 0; i + 3 < n; i += 4)
                    {
                        float v = Math.Abs(BitConverter.ToSingle(buf, i));
                        if (v > max) max = v;
                    }
                }
                else if (std.BitsPerSample == 16)
                {
                    for (int i = 0; i + 1 < n; i += 2)
                    {
                        float v = Math.Abs(BitConverter.ToInt16(buf, i) / 32768f);
                        if (v > max) max = v;
                    }
                }
                else if (std.BitsPerSample == 32)
                {
                    for (int i = 0; i + 3 < n; i += 4)
                    {
                        float v = Math.Abs(BitConverter.ToInt32(buf, i) / 2147483648f);
                        if (v > max) max = v;
                    }
                }
                else max = 1;   // 24 бита и прочее не разбираем — считаем, что звук есть
            }
            catch { max = 1; }
            return max;
        }

        // кандидаты в микрофоны: сначала выбранный в настройках (RecMicDevice), потом устройства по умолчанию, потом все активные
        internal static List<MMDevice> MicCandidates(MMDeviceEnumerator en, string preferred = null)
        {
            var list = new List<MMDevice>();
            void Add(MMDevice dd) { if (dd != null && !list.Any(x => x.ID == dd.ID)) list.Add(dd); }

            var all = new List<MMDevice>();
            try { all.AddRange(en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Cast<MMDevice>()); }
            catch (Exception ex) { ALog("не удалось получить список микрофонов: " + ex.Message); }
            foreach (var d in all)
            {
                string nm = "?"; try { nm = d.FriendlyName; } catch { }
                ALog("микрофон в системе: " + nm);
            }

            string want = string.IsNullOrWhiteSpace(preferred) ? Settings.Current.RecMicDevice : preferred;
            if (!string.IsNullOrWhiteSpace(want))
                foreach (var d in all)
                {
                    try { if (d.FriendlyName.IndexOf(want.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) Add(d); } catch { }
                }

            foreach (var role in new[] { Role.Console, Role.Communications, Role.Multimedia })
            {
                try { Add(en.GetDefaultAudioEndpoint(DataFlow.Capture, role)); }
                catch (Exception ex) { ALog("нет микрофона по умолчанию (" + role + "): " + ex.Message); }
            }
            foreach (var d in all) Add(d);
            return list;
        }

        // null — запись началась, иначе текст ошибки для тоста
        public static async Task<string> StartAsync(string audio)
        {
            if (IsRecording) return null;
            Warning = null;
            lock (_alog) _alog.Clear();
            _sysBytes = _micBytes = 0; _sysPeak = _micPeak = 0; _micName = null;
            _audioMode = audio;
            ALog("режим звука: " + audio);
            _ffPath = FindFfmpeg();
            if (_ffPath == null) return "Нет ffmpeg.exe — см. README";

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Island");
            Directory.CreateDirectory(dir);
            _outFile = Path.Combine(dir, "Island_" + stamp + ".mp4");
            _tmpVideo = Path.Combine(Path.GetTempPath(), "island_v_" + stamp + ".mp4");
            _sysWav = Path.Combine(Path.GetTempPath(), "island_sys_" + stamp + ".wav");
            _micWav = Path.Combine(Path.GetTempPath(), "island_mic_" + stamp + ".wav");

            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            string enc = string.IsNullOrWhiteSpace(Settings.Current.RecEncoder) ? "h264_nvenc" : Settings.Current.RecEncoder;
            bool ok = await TryStartFfmpeg(b, enc).ConfigureAwait(false);
            if (!ok && enc != "libx264") ok = await TryStartFfmpeg(b, "libx264").ConfigureAwait(false);
            if (!ok) return "Не удалось запустить запись (ffmpeg.log)";

            // звук стартуем, когда ffmpeg уже получил первый кадр — так рассинхрон минимален
            try { StartAudio(audio); }
            catch (Exception ex) { ALog("StartAudio: " + ex); AddWarning("звук не запустился"); }
            FlushAudioLog();
            StartedAt = DateTime.UtcNow;
            IsRecording = true;
            return null;
        }

        static async Task<bool> TryStartFfmpeg(System.Drawing.Rectangle b, string enc)
        {
            string venc = enc == "h264_nvenc" ? "-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -b:v 0"
                        : enc == "libx264" ? "-c:v libx264 -preset veryfast -crf 23"
                        : "-c:v " + enc;
            string args = "-y -f gdigrab -framerate 30 -offset_x " + b.X + " -offset_y " + b.Y +
                          " -video_size " + b.Width + "x" + b.Height + " -draw_mouse 1 -i desktop" +
                          " -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" " + venc + " -pix_fmt yuv420p \"" + _tmpVideo + "\"";

            var p = new Process
            {
                StartInfo = new ProcessStartInfo(_ffPath, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            var first = new TaskCompletionSource<bool>();
            var log = new StringBuilder("encoder: " + enc + "\r\n" + args + "\r\n");
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (log)
                {
                    log.AppendLine(e.Data);
                    if (log.Length > 8000) log.Remove(0, log.Length - 6000);
                }
                if (e.Data.TrimStart().StartsWith("frame=")) first.TrySetResult(true);
            };
            p.Exited += (s, e) => first.TrySetResult(false);

            try { p.Start(); }
            catch (Exception ex) { WriteLog(ex.ToString()); return false; }
            p.BeginErrorReadLine();

            var done = await Task.WhenAny(first.Task, Task.Delay(6000)).ConfigureAwait(false);
            bool ok = done == first.Task ? first.Task.Result : !p.HasExited;
            if (!ok || p.HasExited)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                lock (log) WriteLog(log.ToString());
                return false;
            }
            _ff = p;
            return true;
        }

        static void StartAudio(string audio)
        {
            bool wantSys = audio == "system" || audio == "both";
            bool wantMic = audio == "mic" || audio == "both";
            if (!wantSys && !wantMic) return;

            // звук ПК и микрофон стартуют независимо: сбой одного не должен ронять другой
            if (wantSys)
            {
                try
                {
                    // WASAPI loopback молчит, пока в системе тишина, и запись «съезжает» — держим выход занятым тишиной
                    try
                    {
                        _silence = new WasapiOut();
                        _silence.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)));
                        _silence.Play();
                    }
                    catch (Exception ex) { ALog("тишина в колонки не пошла: " + ex.Message); _silence = null; }

                    _loop = new WasapiLoopbackCapture();
                    ALog("звук ПК: " + _loop.WaveFormat);
                    _loopW = new WaveFileWriter(_sysWav, _loop.WaveFormat);
                    var fmt = _loop.WaveFormat;
                    _loop.DataAvailable += (s, e) =>
                    {
                        try
                        {
                            _loopW.Write(e.Buffer, 0, e.BytesRecorded);
                            _sysBytes += e.BytesRecorded;
                            float pk = PeakOf(e.Buffer, e.BytesRecorded, fmt);
                            if (pk > _sysPeak) _sysPeak = pk;
                        }
                        catch { }
                    };
                    _loop.StartRecording();
                }
                catch (Exception ex)
                {
                    ALog("звук ПК не стартовал: " + ex);
                    AddWarning("звук ПК не записывается");
                    try { _loop?.Dispose(); } catch { }
                    try { _loopW?.Dispose(); } catch { }
                    _loop = null; _loopW = null;
                }
            }

            if (wantMic)
            {
                bool started = false;
                try
                {
                    using (var en = new MMDeviceEnumerator())
                    {
                        var cands = MicCandidates(en);
                        if (cands.Count == 0) { ALog("микрофонов нет"); AddWarning("микрофон не найден"); }
                        foreach (var dev in cands)
                        {
                            string nm = "?"; try { nm = dev.FriendlyName; } catch { }
                            try
                            {
                                try { if (dev.AudioEndpointVolume.Mute) { ALog("  " + nm + ": выключен звук (mute)"); AddWarning("микрофон «" + nm + "» выключен (mute)"); } } catch { }
                                var mic = new WasapiCapture(dev);
                                var w = new WaveFileWriter(_micWav, mic.WaveFormat);
                                var fmt = mic.WaveFormat;
                                mic.DataAvailable += (s, e) =>
                                {
                                    try
                                    {
                                        w.Write(e.Buffer, 0, e.BytesRecorded);
                                        _micBytes += e.BytesRecorded;
                                        float pk = PeakOf(e.Buffer, e.BytesRecorded, fmt);
                                        if (pk > _micPeak) _micPeak = pk;
                                    }
                                    catch { }
                                };
                                mic.StartRecording();
                                _mic = mic; _micW = w; _micName = nm;
                                ALog("микрофон выбран: " + nm + " (" + fmt + ")");
                                started = true;
                                break;
                            }
                            catch (Exception ex)
                            {
                                ALog("микрофон «" + nm + "» не стартовал: " + ex);
                                if (ex is UnauthorizedAccessException || (ex is System.Runtime.InteropServices.COMException ce && (uint)ce.HResult == 0x80070005))
                                    AddWarning("нет доступа к микрофону (Параметры → Конфиденциальность → Микрофон)");
                            }
                        }
                    }
                }
                catch (Exception ex) { ALog("микрофон: " + ex); }
                if (!started && (Warning == null || Warning.IndexOf("микрофон", StringComparison.OrdinalIgnoreCase) < 0))
                    AddWarning("микрофон не запустился");
            }
        }

        static Task StopCapture(IWaveIn cap, WaveFileWriter w)
        {
            if (cap == null) return Task.CompletedTask;
            var tcs = new TaskCompletionSource<bool>();
            cap.RecordingStopped += (s, e) =>
            {
                try { w?.Dispose(); } catch { }
                try { cap.Dispose(); } catch { }
                tcs.TrySetResult(true);
            };
            try { cap.StopRecording(); } catch { tcs.TrySetResult(true); }
            return Task.WhenAny(tcs.Task, Task.Delay(3000));
        }

        // путь к готовому файлу или null
        public static async Task<string> StopAsync()
        {
            if (!IsRecording) return null;
            IsRecording = false;

            try { _ff.StandardInput.Write("q"); _ff.StandardInput.Flush(); } catch { }
            await Task.Run(() =>
            {
                try { if (!_ff.WaitForExit(20000)) _ff.Kill(); } catch { }
            }).ConfigureAwait(false);

            await StopCapture(_loop, _loopW).ConfigureAwait(false);
            await StopCapture(_mic, _micW).ConfigureAwait(false);
            try { _silence?.Stop(); _silence?.Dispose(); } catch { }
            _loop = null; _mic = null; _loopW = null; _micW = null; _silence = null;

            if (!File.Exists(_tmpVideo) || new FileInfo(_tmpVideo).Length < 1000) return null;

            bool hasSys = File.Exists(_sysWav) && new FileInfo(_sysWav).Length > 2000;
            bool hasMic = File.Exists(_micWav) && new FileInfo(_micWav).Length > 2000;
            ALog("итог: звук ПК " + _sysBytes + " байт, пик " + _sysPeak.ToString("0.000", CultureInfo.InvariantCulture) +
                 "; микрофон " + _micBytes + " байт, пик " + _micPeak.ToString("0.000", CultureInfo.InvariantCulture) + " (" + (_micName ?? "нет") + ")");
            if (_mic != null || _micName != null)
            {
                if (_micBytes == 0) AddWarning("микрофон не отдал данных");
                else if (_micPeak < 0.0005f) AddWarning("микрофон пишет тишину (проверь доступ к микрофону и громкость в Windows)");
            }
            string result = _outFile;

            if (!hasSys && !hasMic)
            {
                if (_audioMode == "system" || _audioMode == "mic" || _audioMode == "both") AddWarning("звук не записался (audio.log)");
                try { File.Move(_tmpVideo, _outFile); } catch { return null; }
            }
            else
            {
                bool ok = await MuxAsync(hasSys, hasMic).ConfigureAwait(false);
                if (!ok && hasSys && hasMic)
                {
                    ALog("склейка двух дорожек не удалась — пробую по одной");
                    ok = await MuxAsync(false, true).ConfigureAwait(false);
                    if (ok) AddWarning("звук ПК не склеился, в видео только микрофон");
                    else
                    {
                        ok = await MuxAsync(true, false).ConfigureAwait(false);
                        if (ok) AddWarning("микрофон не склеился, в видео только звук ПК");
                    }
                }
                if (!ok)
                {
                    // склейка не удалась — сохраняем хотя бы видео без звука
                    AddWarning("не удалось добавить звук в видео (audio.log)");
                    try { File.Move(_tmpVideo, _outFile); } catch { result = null; }
                }
            }
            FlushAudioLog();
            foreach (var f in new[] { _tmpVideo, _sysWav, _micWav })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            return result;
        }

        static async Task<bool> MuxAsync(bool hasSys, bool hasMic)
        {
            string off = (Settings.Current.RecAudioOffsetMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            var sb = new StringBuilder("-y -i \"" + _tmpVideo + "\"");
            int audioInputs = 0;
            if (hasSys) { sb.Append(" -itsoffset " + off + " -i \"" + _sysWav + "\""); audioInputs++; }
            if (hasMic) { sb.Append(" -itsoffset " + off + " -i \"" + _micWav + "\""); audioInputs++; }

            // все дорожки приводим к одному формату (микрофон обычно моно, звук ПК — стерео или многоканальный)
            const string fmt = "aresample=48000:async=1,aformat=sample_fmts=fltp:channel_layouts=stereo";
            if (audioInputs == 2)
                sb.Append(" -filter_complex \"[1:a]" + fmt + "[s];[2:a]" + fmt + "[m];[s][m]amix=inputs=2:duration=longest,volume=2[a]\" -map 0:v -map \"[a]\"");
            else
                sb.Append(" -filter_complex \"[1:a]" + fmt + "[a]\" -map 0:v -map \"[a]\"");
            sb.Append(" -c:v copy -c:a aac -b:a 192k -shortest \"" + _outFile + "\"");

            string args = sb.ToString();
            ALog("склейка: ffmpeg " + args);
            return await Task.Run(() =>
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo(_ffPath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
                    if (p == null) return false;
                    var err = new StringBuilder();
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) { err.AppendLine(e.Data); if (err.Length > 6000) err.Remove(0, err.Length - 4000); } };
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } ALog("склейка: таймаут"); return false; }
                    p.WaitForExit();   // дождаться вычитывания stderr
                    bool ok = p.ExitCode == 0 && File.Exists(_outFile) && new FileInfo(_outFile).Length > 1000;
                    if (!ok) { lock (err) ALog("склейка: код " + p.ExitCode + "\r\n" + err); }
                    return ok;
                }
                catch (Exception ex) { ALog("склейка: " + ex); return false; }
            }).ConfigureAwait(false);
        }
    }
}
