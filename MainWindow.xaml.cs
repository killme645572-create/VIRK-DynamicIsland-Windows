using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;

namespace Island
{
    public partial class MainWindow : Window
    {
        enum Mode { Idle, Toast, Open, Chat, Timer, Alarm, Pomodoro, ClipAct, Answer }

        const int WM_CLIPBOARDUPDATE = 0x031D;
        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        Mode _mode = Mode.Idle;
        System.Windows.Forms.NotifyIcon _tray;
        readonly DispatcherTimer _alarmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        bool _hasGlow;
        bool _volDrag;
        readonly DispatcherTimer _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        string _weatherText = "";
        readonly DispatcherTimer _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        readonly DispatcherTimer _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        readonly DispatcherTimer _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        GlobalSystemMediaTransportControlsSessionManager _mgr;
        GlobalSystemMediaTransportControlsSession _session;
        IntPtr _hwnd;

        public MainWindow()
        {
            InitializeComponent();
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 6;
            Pill.SizeChanged += (s, e) => Pill.CornerRadius = new CornerRadius(Math.Min(Pill.ActualHeight / 2, 28));
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); if (_mode == Mode.Toast || _mode == Mode.ClipAct) SetMode(Mode.Idle); };
            _leaveTimer.Tick += (s, e) => { _leaveTimer.Stop(); SetMode(Mode.Idle); };
            Loaded += async (s, e) => { _ = RefreshWeatherAsync(); FirstRunKeySetup(); await InitMediaAsync(); };
            _clockTimer.Tick += (s, e) => UpdateIdleText();
            _clockTimer.Start();
            _posTimer.Tick += (s, e) => UpdatePlayer();
            _posTimer.Start();
            _weatherTimer.Tick += async (s, e) => await RefreshWeatherAsync();
            _weatherTimer.Start();
            Deactivated += (s, e) => { if ((_mode == Mode.Chat || _mode == Mode.Timer || _mode == Mode.Pomodoro) && !ChatPanel.PickerOpen) SetMode(Mode.Idle); };
            _alarmTimer.Tick += (s, e) => { _alarmTimer.Stop(); if (_mode == Mode.Alarm) SetMode(Mode.Idle); };
            IslandTimer.Instance.Changed += UpdateIdleText;
            IslandTimer.Instance.Finished += OnTimerFinished;
            ApplyIslandColor(Settings.Current.IslandColor, false);
            ShowTab("Home");
            InitFx();
            InitControl();
            InitTiles();
            InitRecord();
            InitShelf();
            InitApps();
            InitClaude();
            InitEq();
            InitSmartClip();
            InitPomodoro();
            InitVoice();
            InitTray();
            InitUpdates();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            AddClipboardFormatListener(_hwnd);
            RegisterHotkey();
            RegisterAskHotkey();
            RegisterVoiceHotkey();
            Dispatcher.BeginInvoke(new Action(() => _ = StartWakeAsync()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        protected override void OnClosed(EventArgs e)
        {
            UnregisterHotkey();
            UnregisterAskHotkey();
            UnregisterVoiceHotkey();
            try { _wake?.StopAsync().Wait(3000); } catch { }
            Tts.Stop();
            try { _spectrum?.Dispose(); } catch { }
            RemoveClipboardFormatListener(_hwnd);
            try { if (ScreenRecorder.IsRecording) ScreenRecorder.StopAsync().Wait(30000); } catch { }
            ClaudeHookServer.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            base.OnClosed(e);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
                Dispatcher.BeginInvoke(new Action(OnClipboardChanged));
            else if (msg == WM_HOTKEY && w.ToInt32() == HotkeyId)
            {
                Dispatcher.BeginInvoke(new Action(ToggleByHotkey));
                handled = true;
            }
            else if (msg == WM_HOTKEY && w.ToInt32() == VoiceHotkeyId)
            {
                Dispatcher.BeginInvoke(new Action(OnVoiceHotkey));
                handled = true;
            }
            else if (msg == WM_HOTKEY && w.ToInt32() == AskHotkeyId)
            {
                Dispatcher.BeginInvoke(new Action(() => _ = AskAboutScreenAsync()));
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---------- состояния острова ----------

        void SetMode(Mode m)
        {
            _mode = m;
            if (m != Mode.Open) { _holdOpen = false; _kbOpen = false; }
            double w = 170, h = 34;
            if (m == Mode.Toast) { w = 320; h = 46; }
            if (m == Mode.Idle && _eqShown) w += EqExtra;   // место под эквалайзер
            if (m == Mode.Open)  { w = 520; h = 296; }
            if (m == Mode.Timer) { w = 380; h = 200; }
            if (m == Mode.Pomodoro) { w = 400; h = 210; }
            if (m == Mode.Alarm) { w = 400; h = 70; }
            if (m == Mode.Chat)  { w = 460; h = 390; }
            if (m == Mode.ClipAct) { w = 440; h = 92; }
            if (m == Mode.Answer)  { w = 470; h = 260; }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(300);
            Pill.BeginAnimation(WidthProperty,  new DoubleAnimation(w, dur) { EasingFunction = ease });
            Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });

            Fade(ToastText, m == Mode.Toast ? 1 : 0);
            Fade(OpenPanel, m == Mode.Open ? 1 : 0);
            OpenPanel.IsHitTestVisible = m == Mode.Open;
            Fade(Chat, m == Mode.Chat ? 1 : 0);
            ShowGlow(m == Mode.Open && _hasGlow);
            Chat.IsHitTestVisible = m == Mode.Chat;
            Fade(TimerView, m == Mode.Timer ? 1 : 0);
            TimerView.IsHitTestVisible = m == Mode.Timer;
            Fade(AlarmPanel, m == Mode.Alarm ? 1 : 0);
            AlarmPanel.IsHitTestVisible = m == Mode.Alarm;
            Fade(PomoView, m == Mode.Pomodoro ? 1 : 0);
            PomoView.IsHitTestVisible = m == Mode.Pomodoro;
            Fade(ClipActPanel, m == Mode.ClipAct ? 1 : 0);
            ClipActPanel.IsHitTestVisible = m == Mode.ClipAct;
            Fade(AnswerPanel, m == Mode.Answer ? 1 : 0);
            AnswerPanel.IsHitTestVisible = m == Mode.Answer;
            Fade(Eyes, Settings.Current.Character && (m == Mode.Idle || m == Mode.Alarm) ? 1 : 0);
            UpdateIdleText();
        }

        static void Fade(UIElement el, double to)
        {
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(200)));
        }

        void ShowToast(string text, double secs = 2)
        {
            if (_mode != Mode.Idle && _mode != Mode.Toast) return;
            ToastText.Text = text;
            SetMode(Mode.Toast);
            _toastTimer.Interval = TimeSpan.FromSeconds(secs);
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        void Pill_MouseEnter(object s, System.Windows.Input.MouseEventArgs e)
        {
            if (_mode == Mode.ClipAct || _mode == Mode.Answer)   // не раскрываем остров: нужно нажать кнопку или прочитать ответ
            {
                _smartLeave.Stop();
                _toastTimer.Stop();
                return;
            }
            if (_mode == Mode.Chat || _mode == Mode.Timer || _mode == Mode.Pomodoro || _mode == Mode.Alarm) return;
            _leaveTimer.Stop();
            _toastTimer.Stop();
            if (ScreenRecorder.IsRecording) ShowTab("Tools");   // чтобы сразу была кнопка «Остановить запись»
            SetMode(Mode.Open);
        }

        void Pill_MouseLeave(object s, System.Windows.Input.MouseEventArgs e)
        {
            if (_mode == Mode.ClipAct || _mode == Mode.Answer)
            {
                _smartLeave.Stop();
                _smartLeave.Interval = TimeSpan.FromSeconds(_mode == Mode.Answer ? 8 : 2);
                _smartLeave.Start();
                return;
            }
            if (_mode == Mode.Chat || _mode == Mode.Timer || _mode == Mode.Pomodoro || _mode == Mode.Alarm || _holdOpen) return;
            _leaveTimer.Stop();
            _leaveTimer.Start();
        }

        // ---------- буфер обмена ----------

        void OnClipboardChanged()
        {
            if (DateTime.UtcNow < _suppressClipUntil) return;   // это мы сами что-то положили в буфер
            try
            {
                string t = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                if (t != null) AddHistory(t);
                if (t != null && TryShowClipActions(t)) return;   // умный буфер: кнопки «Объяснить / Перевести / …»
                if (t != null)
                {
                    t = t.Replace("\r", " ").Replace("\n", " ").Trim();
                    if (t.Length > 28) t = t.Substring(0, 28) + "…";
                    ShowToast("Скопировано: " + t);
                }
                else ShowToast("Скопировано");
            }
            catch { /* буфер занят другим приложением */ }
        }

        // ---------- медиа ----------

        async Task InitMediaAsync()
        {
            try
            {
                _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mgr.CurrentSessionChanged += (s, e) => Dispatcher.Invoke(AttachSession);
                AttachSession();
            }
            catch { }
        }

        void AttachSession()
        {
            _session = _mgr?.GetCurrentSession();
            if (_session != null)
                _session.MediaPropertiesChanged += async (s, e) => await Dispatcher.InvokeAsync(RefreshTrackAsync);
            _ = RefreshTrackAsync();
        }

        async Task RefreshTrackAsync()
        {
            if (_session == null) { TrackTitle.Text = "Ничего не играет"; TrackArtist.Text = ""; ClearLyrics(); CoverBox.Visibility = Visibility.Collapsed; _hasGlow = false; ShowGlow(false); return; }
            try
            {
                var p = await _session.TryGetMediaPropertiesAsync();
                TrackTitle.Text = string.IsNullOrEmpty(p.Title) ? "Без названия" : p.Title;
                TrackArtist.Text = p.Artist;
                await LoadCoverAsync(p);
                StartLyrics(p.Artist, p.Title);
            }
            catch { }
        }

        // ---------- градиент по цветам обложки ----------

        void ShowGlow(bool on)
        {
            if (on)
            {
                Glow.Visibility = Visibility.Visible;
                Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, TimeSpan.FromMilliseconds(400)));
            }
            else
            {
                var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
                a.Completed += (s, e) => { if (!(_mode == Mode.Open && _hasGlow)) Glow.Visibility = Visibility.Collapsed; };
                Glow.BeginAnimation(OpacityProperty, a);
            }
        }

        static double Dist(Color a, Color b) =>
            Math.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));

        static Color Brighten(Color c)
        {
            double m = Math.Max(c.R, Math.Max(c.G, c.B));
            if (m < 170 && m > 0)
            {
                double k = Math.Min(170 / m, 3.0);
                return Color.FromRgb((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k));
            }
            return c;
        }

        static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d < 1e-9) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        static Color FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        // делаем цвет ярким и насыщенным, сохраняя оттенок обложки
        static Color Vivid(Color c)
        {
            ToHsv(c, out var h, out var s, out var v);
            if (s < 0.12) return FromHsv(h, s, Math.Max(v, 0.7)); // обложка реально серая
            return FromHsv(h, Math.Max(s, 0.65), Math.Max(v, 0.9));
        }

        void ApplyGlowFrom(BitmapSource bmp)
        {
            try
            {
                var small = new TransformedBitmap(bmp, new ScaleTransform(32.0 / bmp.PixelWidth, 32.0 / bmp.PixelHeight));
                var fc = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = fc.PixelWidth, h = fc.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                fc.CopyPixels(px, stride, 0);

                var buckets = new Dictionary<int, (double r, double g, double b, double n)>();
                // сначала берём только цветные пиксели; если их нет, то любые не слишком тёмные
                foreach (double minSat in new[] { 0.18, 0.0 })
                {
                    for (int i = 0; i + 3 < px.Length; i += 4)
                    {
                        double b = px[i], g = px[i + 1], r = px[i + 2];
                        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                        if (max < 60) continue;
                        double sat = (max - min) / max;
                        if (sat < minSat) continue;
                        int key = (((int)r >> 5) << 6) | (((int)g >> 5) << 3) | ((int)b >> 5);
                        buckets.TryGetValue(key, out var v);
                        double wgt = 0.2 + sat * sat * 2;
                        buckets[key] = (v.r + r * wgt, v.g + g * wgt, v.b + b * wgt, v.n + wgt);
                    }
                    if (buckets.Count > 0) break;
                }

                var pick = new List<Color>();
                foreach (var v in buckets.Values.OrderByDescending(x => x.n))
                {
                    var c = Color.FromRgb((byte)(v.r / v.n), (byte)(v.g / v.n), (byte)(v.b / v.n));
                    if (pick.All(q => Dist(q, c) > 60)) pick.Add(c);
                    if (pick.Count == 3) break;
                }
                if (pick.Count == 0) { _hasGlow = false; return; }
                while (pick.Count < 3) pick.Add(pick[pick.Count - 1]);

                SetBlob(Blob1, Vivid(pick[0]), new Point(0.15, 0.3), new Point(0.65, 0.8), 0.85, 7);
                SetBlob(Blob2, Vivid(pick[1]), new Point(0.9, 0.2), new Point(0.4, 0.75), 0.8, 9);
                SetBlob(Blob3, Vivid(pick[2]), new Point(0.5, 0.95), new Point(0.85, 0.25), 0.75, 11);
                _hasGlow = true;
            }
            catch { _hasGlow = false; }
        }

        static void SetBlob(Border blob, Color c, Point from, Point to, double radius, double seconds)
        {
            var brush = new RadialGradientBrush { Center = from, GradientOrigin = from, RadiusX = radius, RadiusY = radius };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(235, c.R, c.G, c.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));

            var anim = new PointAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            brush.BeginAnimation(RadialGradientBrush.CenterProperty, anim);
            brush.BeginAnimation(RadialGradientBrush.GradientOriginProperty, anim.Clone());
            blob.Background = brush;
        }

        async Task LoadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties p)
        {
            try
            {
                if (p?.Thumbnail == null) { CoverBox.Visibility = Visibility.Collapsed; _hasGlow = false; ShowGlow(false); return; }
                using var ras = await p.Thumbnail.OpenReadAsync();
                using var src = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await src.CopyToAsync(ms);
                ms.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                Cover.Source = bmp;
                ApplyGlowFrom(bmp);
                CoverBox.Visibility = Visibility.Visible;
                ShowGlow(_mode == Mode.Open && _hasGlow);
            }
            catch { CoverBox.Visibility = Visibility.Collapsed; _hasGlow = false; ShowGlow(false); }
        }

        // ---------- прогресс трека и громкость ----------

        static string Fmt(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        // текущая позиция и длительность; false — если трек без длительности (стрим) или нет сессии
        bool TryGetTimeline(out TimeSpan pos, out TimeSpan dur, out bool playing)
        {
            pos = dur = TimeSpan.Zero; playing = false;
            if (_session == null) return false;
            try
            {
                var tl = _session.GetTimelineProperties();
                var info = _session.GetPlaybackInfo();
                playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                dur = tl.EndTime - tl.StartTime;
                pos = tl.Position - tl.StartTime;
                // плеер обновляет позицию редко, поэтому достраиваем её по времени
                if (playing) pos += DateTimeOffset.Now - tl.LastUpdatedTime;
                if (pos > dur) pos = dur;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                return dur > TimeSpan.Zero;
            }
            catch { return false; }
        }

        void UpdatePlayer()
        {
            if (_mode != Mode.Open) return;

            bool has = TryGetTimeline(out var pos, out var dur, out var playing);
            PlayBtn.Content = playing ? "⏸" : "▶";
            PosText.Text = has ? Fmt(pos) : "";
            DurText.Text = has ? Fmt(dur) : "";
            SeekFill.Width = has ? SeekHit.ActualWidth * (pos.TotalSeconds / dur.TotalSeconds) : 0;
            UpdateLyricLine(pos, has);

            if (!_volDrag)
            {
                float v = VolumeControl.Get();
                if (v >= 0) VolFill.Width = VolHit.ActualWidth * v;
            }
        }

        async void Seek_Down(object s, MouseButtonEventArgs e)
        {
            if (!TryGetTimeline(out _, out var dur, out _)) return;
            double r = Math.Max(0, Math.Min(1, e.GetPosition(SeekHit).X / SeekHit.ActualWidth));
            try
            {
                var start = _session.GetTimelineProperties().StartTime;
                await _session.TryChangePlaybackPositionAsync((start + TimeSpan.FromSeconds(dur.TotalSeconds * r)).Ticks);
                SeekFill.Width = SeekHit.ActualWidth * r;
            }
            catch { }
        }

        void SetVolFrom(MouseEventArgs e)
        {
            double r = Math.Max(0, Math.Min(1, e.GetPosition(VolHit).X / VolHit.ActualWidth));
            VolFill.Width = VolHit.ActualWidth * r;
            VolumeControl.Set((float)r);
        }

        void Vol_Down(object s, MouseButtonEventArgs e) { _volDrag = true; VolHit.CaptureMouse(); SetVolFrom(e); }
        void Vol_Move(object s, MouseEventArgs e) { if (_volDrag) SetVolFrom(e); }
        void Vol_Up(object s, MouseButtonEventArgs e) { _volDrag = false; VolHit.ReleaseMouseCapture(); }

        async void Play_Click(object s, RoutedEventArgs e) { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
        async void Next_Click(object s, RoutedEventArgs e) { if (_session != null) await _session.TrySkipNextAsync(); }
        async void Prev_Click(object s, RoutedEventArgs e) { if (_session != null) await _session.TrySkipPreviousAsync(); }

        // ---------- быстрые переключатели ----------

        void Wifi_Click(object s, RoutedEventArgs e) => OpenUri("ms-settings:network-wifi");
        void Bt_Click(object s, RoutedEventArgs e) => OpenUri("ms-settings:bluetooth");

        static void OpenUri(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
        }

        void Lock_Click(object s, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = false });
        }

        void Theme_Click(object s, RoutedEventArgs e)
        {
            const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            int light = Convert.ToInt32(Registry.GetValue(key, "AppsUseLightTheme", 1));
            int next = light == 1 ? 0 : 1;
            Registry.SetValue(key, "AppsUseLightTheme", next, RegistryValueKind.DWord);
            Registry.SetValue(key, "SystemUsesLightTheme", next, RegistryValueKind.DWord);
            ShowToast(next == 0 ? "Тёмная тема" : "Светлая тема");
        }

        // ---------- чат с Claude ----------

        void Chat_Click(object s, RoutedEventArgs e)
        {
            _leaveTimer.Stop();
            SetMode(Mode.Chat);
            Activate(); // окно по умолчанию не активируется, а для ввода с клавиатуры нужно
            Dispatcher.BeginInvoke(new Action(Chat.FocusInput), DispatcherPriority.Input);
        }

        void Chat_Close(object s, EventArgs e) => SetMode(Mode.Idle);

        // ---------- таймер ----------

        void Timer_Click(object s, RoutedEventArgs e)
        {
            _leaveTimer.Stop();
            SetMode(Mode.Timer);
            Activate();
        }

        void Timer_Close(object s, EventArgs e) => SetMode(Mode.Idle);

        void UpdateIdleText()
        {
            var t = IslandTimer.Instance;
            if (_mode == Mode.Idle)
            {
                // в покое: идущий таймер, иначе время и погода
                IdleText.Text = RecStatusText() ?? (t.Active
                    ? "⏱ " + IslandTimer.Format(t.Remaining)
                    : (PomoStatusText() ?? ClaudeStatusText() ?? DateTime.Now.ToString("HH:mm") + _weatherText));
                IdleText.Opacity = 1;
            }
            else IdleText.Opacity = 0;
        }

        async Task RefreshWeatherAsync()
        {
            try
            {
                var w = await WeatherService.GetAsync();
                _weather = w;
                _weatherAt = DateTime.UtcNow;
                _weatherText = w == null ? "" : "  " + w.Icon + " " + (int)Math.Round(w.Temp) + "°";
            }
            catch { /* нет сети — просто без погоды */ }
            UpdateIdleText();
            UpdateWeatherView();
        }

        void OnTimerFinished()
        {
            var total = IslandTimer.Instance.Total;
            AlarmSub.Text = total.TotalMinutes >= 1
                ? $"Таймер · {Math.Round(total.TotalMinutes, 1)} мин"
                : $"Таймер · {(int)total.TotalSeconds} сек";
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            _toastTimer.Stop();
            SetMode(Mode.Alarm);
            _alarmTimer.Stop();
            _alarmTimer.Start();
        }

        void AlarmAgain_Click(object s, RoutedEventArgs e)
        {
            _alarmTimer.Stop();
            IslandTimer.Instance.Start(IslandTimer.Instance.Total);
            SetMode(Mode.Idle);
        }

        void AlarmDone_Click(object s, RoutedEventArgs e)
        {
            _alarmTimer.Stop();
            SetMode(Mode.Idle);
        }

        // ---------- иконка в трее и автозапуск ----------

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool IsAutostart()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Island") != null;
        }

        bool SetAutostart(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (on)
                {
                    var exe = Environment.ProcessPath;
                    if (exe == null || !Path.GetFileNameWithoutExtension(exe).Equals("Island", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowToast("Автозапуск работает только из собранного Island.exe");
                        return false;
                    }
                    k.SetValue("Island", "\"" + exe + "\"");
                }
                else k.DeleteValue("Island", false);
                return true;
            }
            catch { return false; }
        }

        void ToggleVisible()
        {
            Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        }

        void InitTray()
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(32, 32);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(System.Drawing.Color.Transparent);
                    void Pill(System.Drawing.Brush br, int x, int y, int w, int h)
                    {
                        g.FillEllipse(br, x, y, h, h);
                        g.FillEllipse(br, x + w - h, y, h, h);
                        g.FillRectangle(br, x + h / 2, y, w - h, h);
                    }
                    Pill(System.Drawing.Brushes.White, 1, 10, 30, 12);
                    Pill(System.Drawing.Brushes.Black, 3, 12, 26, 8);
                }

                _tray = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon()),
                    Text = "Island",
                    Visible = true
                };

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Показать / скрыть остров").Click += (s, e) => ToggleVisible();

                var auto = new System.Windows.Forms.ToolStripMenuItem("Запускать с Windows") { Checked = IsAutostart() };
                auto.Click += (s, e) =>
                {
                    SetAutostart(!IsAutostart());
                    auto.Checked = IsAutostart();
                };
                menu.Items.Add(auto);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("Проверить обновления").Click += (s, e) => _ = CheckUpdateAsync(true);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("Выход").Click += (s, e) => Application.Current.Shutdown();

                _tray.ContextMenuStrip = menu;
                _tray.BalloonTipClicked += (s, e) => { if (_update != null) OpenUpdatePage(); };
                _tray.MouseClick += (s, e) =>
                {
                    if (e.Button == System.Windows.Forms.MouseButtons.Left) ToggleVisible();
                };
            }
            catch { }
        }

        void Exit_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();
    }
}
