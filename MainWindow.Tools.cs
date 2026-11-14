using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Island
{
    // Вкладки раскрытого острова, история буфера и инструменты (скриншот, OCR, пипетка).
    public partial class MainWindow
    {
        readonly List<string> _clipHistory = new List<string>();
        DateTime _suppressClipUntil = DateTime.MinValue;   // чтобы не реагировать на собственные записи в буфер
        string _tab = "Home";

        // ---------- вкладки ----------

        void Tab_Click(object s, RoutedEventArgs e)
        {
            if (s is Button b && b.Tag is string name) ShowTab(name);
        }

        void ShowTab(string name)
        {
            _tab = name;
            HomeView.Visibility = name == "Home" ? Visibility.Visible : Visibility.Collapsed;
            ClipView.Visibility = name == "Clip" ? Visibility.Visible : Visibility.Collapsed;
            ToolsView.Visibility = name == "Tools" ? Visibility.Visible : Visibility.Collapsed;
            WeatherView.Visibility = name == "Weather" ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            ControlView.Visibility = name == "Control" ? Visibility.Visible : Visibility.Collapsed;
            ShelfView.Visibility = name == "Shelf" ? Visibility.Visible : Visibility.Collapsed;
            AppsView.Visibility = name == "Apps" ? Visibility.Visible : Visibility.Collapsed;
            SetTabBg(TabApps, name == "Apps");
            SetTabBg(TabShelf, name == "Shelf");
            if (_ctlMain != null) SetTabBg(TabControlBtn, name == "Control");
            SetTabBg(TabHome, name == "Home");
            SetTabBg(TabWeather, name == "Weather");
            SetTabBg(TabClip, name == "Clip");
            SetTabBg(TabTools, name == "Tools");
            SetTabBg(TabSettings, name == "Settings");
            if (name == "Clip") RefreshClip();
            if (name == "Weather")
            {
                UpdateWeatherView();
                if (DateTime.UtcNow - _weatherAt > TimeSpan.FromMinutes(5)) _ = RefreshWeatherAsync();
            }
            if (name == "Settings") BuildSettings();
            if (name == "Shelf") RefreshShelf();
            if (name == "Apps") RefreshApps();
            if (name == "Control" && _ctlMain != null) { ShowControlMain(); _ = RefreshControlAsync(); }
        }

        static void SetTabBg(Button b, bool on) =>
            b.Background = (Brush)new BrushConverter().ConvertFromString(on ? "#59FFFFFF" : "#26FFFFFF");

        // ---------- история буфера ----------

        void AddHistory(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            _clipHistory.Remove(t);
            _clipHistory.Insert(0, t);
            while (_clipHistory.Count > 30) _clipHistory.RemoveAt(_clipHistory.Count - 1);
            if (_tab == "Clip") RefreshClip();
        }

        async void ClipItem_Click(object s, RoutedEventArgs e)
        {
            if (!(s is Button b) || !(b.Tag is string t)) return;
            SetClipboardText(t);
            if (b.Content is TextBlock tb) tb.Text = "✓ Скопировано";
            await Task.Delay(800);
            AddHistory(t);
            if (_tab == "Clip") RefreshClip();
        }

        void SetClipboardText(string t)
        {
            _suppressClipUntil = DateTime.UtcNow.AddMilliseconds(1000);
            try { Clipboard.SetText(t); } catch { }
        }

        // ---------- инструменты ----------

        // прячет остров и делает снимок экрана (остров после этого нужно вернуть: Opacity = 1)
        async Task<System.Drawing.Bitmap> GrabScreenAsync()
        {
            SetMode(Mode.Idle);
            Opacity = 0;
            await Task.Delay(350);   // дождаться, пока остров исчезнет с экрана
            return Shot.Capture();
        }

        static System.Drawing.Rectangle? SelectRegion(System.Drawing.Bitmap full)
        {
            var w = new RegionSelector(full, false);
            w.ShowDialog();
            return w.Result;
        }

        void SaveShot(System.Drawing.Bitmap bmp)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "Island_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            _suppressClipUntil = DateTime.UtcNow.AddMilliseconds(1500);
            try { Clipboard.SetImage(Shot.ToSource(bmp)); } catch { }
            ShowToast("Скриншот в буфере и в Pictures");
        }

        async void ToolArea_Click(object s, RoutedEventArgs e)
        {
            try
            {
                using var full = await GrabScreenAsync();
                var rect = SelectRegion(full);
                Opacity = 1;
                if (rect == null) return;
                using var crop = full.Clone(rect.Value, full.PixelFormat);
                if (Settings.Current.EditShots)
                {
                    var ed = new ScreenshotEditor(crop, rect.Value, full.Width, full.Height);
                    ed.ShowDialog();
                    if (ed.Result == null) return;   // Esc — отмена
                    using (ed.Result) SaveShot(ed.Result);
                }
                else SaveShot(crop);
            }
            catch (Exception ex) { Opacity = 1; ShowToast("Ошибка: " + ex.Message); }
        }

        async void ToolScreen_Click(object s, RoutedEventArgs e)
        {
            try
            {
                using var full = await GrabScreenAsync();
                Opacity = 1;
                SaveShot(full);
            }
            catch (Exception ex) { Opacity = 1; ShowToast("Ошибка: " + ex.Message); }
        }

        static async Task<string> OcrAsync(System.Drawing.Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            using var ras = ms.AsRandomAccessStream();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
            using var sb = await decoder.GetSoftwareBitmapAsync();
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return null;
            var res = await engine.RecognizeAsync(sb);
            return res.Text;
        }

        async void ToolOcr_Click(object s, RoutedEventArgs e)
        {
            try
            {
                using var full = await GrabScreenAsync();
                var rect = SelectRegion(full);
                Opacity = 1;
                if (rect == null) return;
                using var crop = full.Clone(rect.Value, full.PixelFormat);
                var text = (await OcrAsync(crop))?.Trim();
                if (string.IsNullOrEmpty(text)) { ShowToast("Текст не найден"); return; }
                SetClipboardText(text);
                AddHistory(text);
                ShowToast("Текст скопирован");
            }
            catch (Exception ex) { Opacity = 1; ShowToast("Ошибка: " + ex.Message); }
        }

        async void ToolPick_Click(object s, RoutedEventArgs e)
        {
            try
            {
                using var full = await GrabScreenAsync();
                var w = new RegionSelector(full, true);
                w.ShowDialog();
                Opacity = 1;
                if (w.Picked == null) return;
                var c = full.GetPixel(w.Picked.Value.X, w.Picked.Value.Y);
                var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                SetClipboardText(hex);
                AddHistory(hex);
                ShowToast("Цвет " + hex + " скопирован");
            }
            catch (Exception ex) { Opacity = 1; ShowToast("Ошибка: " + ex.Message); }
        }

        void ToolQr_Click(object s, RoutedEventArgs e)
        {
            SetMode(Mode.Idle);
            string t;
            try { t = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null; }
            catch { ShowToast("Буфер занят, попробуй ещё раз"); return; }

            if (string.IsNullOrEmpty(t)) { ShowToast("Скопируй текст или ссылку — сделаю QR"); return; }
            var m = QrCode.Encode(t);
            if (m == null) { ShowToast("Слишком длинный текст для QR"); return; }

            var w = new QrWindow(t, m);
            w.Show();
            w.Activate();
        }

        void ToolPlain_Click(object s, RoutedEventArgs e)
        {
            SetMode(Mode.Idle);
            try
            {
                if (!Clipboard.ContainsText()) { ShowToast("В буфере нет текста"); return; }
                SetClipboardText(Clipboard.GetText());   // запись только текста сбрасывает форматирование
                ShowToast("Остался чистый текст");
            }
            catch { ShowToast("Буфер занят, попробуй ещё раз"); }
        }
    }
}
