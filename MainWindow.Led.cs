using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace Island
{
    // Управление → «Лента»: Bluetooth RGB-лента (ELK-BLEDOM). Плитка включает и выключает, стрелка › открывает
    // панель: выбор ленты, цвета, радуга, яркость. Само общение по Bluetooth — в Led.cs.
    public partial class MainWindow
    {
        Grid _ledPanel;
        TextBlock _ledMsg;
        StackPanel _ledList, _ledCtl;
        ScrollViewer _ledScroll;
        Slider _ledBright;
        Border _hueBar;
        DispatcherTimer _ledTimer;
        bool? _ledOn;            // что мы последним отправили ленте (сама лента состояние не сообщает)
        bool _ledScanning, _ledLoading;
        readonly List<Border> _ledSwatches = new List<Border>();

        static readonly string[] LedPalette =
            { "#FF0000", "#FF6A00", "#FFD400", "#00FF3C", "#00E5FF", "#0050FF", "#8A2BE2", "#FF2DAA", "#FFFFFF", "#FFB46B" };

        Color CurLedColor
        {
            get
            {
                try { return (Color)ColorConverter.ConvertFromString(Settings.Current.LedColor); }
                catch { return Colors.White; }
            }
        }

        // цвет с учётом яркости (яркость задаём масштабом RGB — работает на любых контроллерах)
        (byte r, byte g, byte b) ScaledLed()
        {
            var c = CurLedColor;
            double k = Math.Clamp(Settings.Current.LedBrightness, 5, 100) / 100.0;
            return ((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        }

        static Color FromHue(double h)
        {
            h = ((h % 360) + 360) % 360;
            double x = 1 - Math.Abs((h / 60) % 2 - 1);
            double r, g, b;
            if (h < 60) { r = 1; g = x; b = 0; }
            else if (h < 120) { r = x; g = 1; b = 0; }
            else if (h < 180) { r = 0; g = 1; b = x; }
            else if (h < 240) { r = 0; g = x; b = 1; }
            else if (h < 300) { r = x; g = 0; b = 1; }
            else { r = 1; g = 0; b = x; }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        string LedTitle(string state)
        {
            var n = string.IsNullOrEmpty(Settings.Current.LedName) ? "Лента" : Settings.Current.LedName;
            return state == null ? n : n + " · " + state;
        }

        void InitLed()
        {
            _ledPanel = new Grid { Visibility = Visibility.Collapsed };
            _ledPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _ledPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // шапка: ← название ленты … ⟳ Поиск
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var back = Chip("←", () => ShowControlMain());
            var scan = Chip("⟳ Поиск", () => { _ = ScanLedAsync(); });
            DockPanel.SetDock(scan, Dock.Right);
            _ledMsg = new TextBlock
            {
                Foreground = Br("#99FFFFFF"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0), Text = "Лента", TextTrimming = TextTrimming.CharacterEllipsis
            };
            head.Children.Add(scan);
            head.Children.Add(back);
            head.Children.Add(_ledMsg);
            Grid.SetRow(head, 0);
            _ledPanel.Children.Add(head);

            var body = new Grid();
            Grid.SetRow(body, 1);

            // список найденных устройств
            _ledList = new StackPanel();
            _ledScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _ledList, Visibility = Visibility.Collapsed
            };
            body.Children.Add(_ledScroll);

            // управление лентой
            _ledCtl = new StackPanel { Visibility = Visibility.Collapsed };

            var power = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 10) };
            power.Children.Add(Chip("💡 Включить", () => { _ = SetLedPower(true); }));
            power.Children.Add(Chip("🌑 Выключить", () => { _ = SetLedPower(false); }));
            _ledCtl.Children.Add(power);

            var sw = new WrapPanel { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var hex in LedPalette)
            {
                var h = hex;
                var dot = new Border
                {
                    Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Br(h),
                    BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                    Margin = new Thickness(3), Cursor = Cursors.Hand, Tag = h
                };
                dot.MouseLeftButtonUp += (s, e) => ApplyLedColor((Color)ColorConverter.ConvertFromString(h));
                _ledSwatches.Add(dot);
                sw.Children.Add(dot);
            }
            _ledCtl.Children.Add(sw);

            // радужная полоса — любой цвет перетаскиванием
            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            string[] hues = { "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF", "#FF0000" };
            for (int i = 0; i < hues.Length; i++)
                grad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hues[i]), i / 6.0));
            _hueBar = new Border
            {
                Height = 18, CornerRadius = new CornerRadius(9), Background = grad,
                Margin = new Thickness(3, 0, 3, 10), Cursor = Cursors.Hand
            };
            _hueBar.MouseLeftButtonDown += (s, e) => { _hueBar.CaptureMouse(); HueAt(e.GetPosition(_hueBar).X); };
            _hueBar.MouseMove += (s, e) =>
            {
                if (_hueBar.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) HueAt(e.GetPosition(_hueBar).X);
            };
            _hueBar.MouseLeftButtonUp += (s, e) => _hueBar.ReleaseMouseCapture();
            _ledCtl.Children.Add(_hueBar);

            // яркость
            var br = new DockPanel { Margin = new Thickness(3, 0, 3, 0) };
            br.Children.Add(new TextBlock
            {
                Text = "Яркость", Foreground = Br("#99FFFFFF"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
            });
            _ledBright = new Slider { Minimum = 5, Maximum = 100, Value = 100, VerticalAlignment = VerticalAlignment.Center };
            _ledBright.ValueChanged += (s, e) =>
            {
                if (_ledLoading) return;
                Settings.Current.LedBrightness = (int)_ledBright.Value;
                QueueLedSend();
            };
            br.Children.Add(_ledBright);
            _ledCtl.Children.Add(br);

            body.Children.Add(_ledCtl);
            _ledPanel.Children.Add(body);
            ControlView.Children.Add(_ledPanel);

            // чтобы не слать ленте десятки команд, пока тянешь полосу или ползунок
            _ledTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _ledTimer.Tick += async (s, e) => { _ledTimer.Stop(); await SendLedColorAsync(); };
        }

        // ---------- плитка ----------

        void RefreshLedTile()
        {
            var s = Settings.Current;
            if (s.LedAddress == 0) SetTile(_tLed, false, "Не выбрана");
            else SetTile(_tLed, _ledOn == true,
                _ledOn == null ? (string.IsNullOrEmpty(s.LedName) ? "Лента" : s.LedName) : _ledOn == true ? "Включена" : "Выключена");
        }

        // клик по плитке: включить/выключить (если лента ещё не выбрана — открыть выбор)
        async void ToggleLed()
        {
            if (Settings.Current.LedAddress == 0) { OpenLedPanel(); return; }
            await SetLedPower(!(_ledOn ?? false));
        }

        // ---------- панель ----------

        void OpenLedPanel()
        {
            _ctlMain.Visibility = Visibility.Collapsed;
            _wifiPanel.Visibility = Visibility.Collapsed;
            _ledPanel.Visibility = Visibility.Visible;
            if (Settings.Current.LedAddress == 0) _ = ScanLedAsync();
            else ShowLedControls();
        }

        void ShowLedControls()
        {
            _ledScroll.Visibility = Visibility.Collapsed;
            _ledCtl.Visibility = Visibility.Visible;
            _ledLoading = true;
            _ledBright.Value = Math.Clamp(Settings.Current.LedBrightness, 5, 100);
            _ledLoading = false;
            MarkLedSwatches();
            _ledMsg.Text = LedTitle(_ledOn == null ? null : _ledOn == true ? "включена" : "выключена");
        }

        async Task ScanLedAsync()
        {
            if (_ledScanning) return;
            _ledScanning = true;
            try
            {
                _ledCtl.Visibility = Visibility.Collapsed;
                _ledScroll.Visibility = Visibility.Visible;
                _ledList.Children.Clear();

                var bt = await RadioControl.IsOnAsync(RadioKind.Bluetooth);
                if (bt != true) { _ledMsg.Text = "Bluetooth выключен — включи плитку Bluetooth"; return; }

                _ledMsg.Text = "Ищу устройства (5 с)…";
                var list = await LedControl.ScanAsync();
                _ledList.Children.Clear();
                if (list.Count == 0) { _ledMsg.Text = "Ничего не найдено — включи ленту и нажми «Поиск»"; return; }

                _ledMsg.Text = "Выбери свою ленту (★ — похожие на ленту)";
                foreach (var d in list)
                {
                    var dev = d;
                    var btn = Chip((dev.Likely ? "★ " : "") + dev.Name, () => { _ = PickLedAsync(dev); });
                    btn.Margin = new Thickness(3, 0, 3, 4);
                    _ledList.Children.Add(btn);
                }
            }
            finally { _ledScanning = false; }
        }

        async Task PickLedAsync(LedDevice d)
        {
            var s = Settings.Current;
            s.LedAddress = d.Address;
            s.LedName = d.Name;
            s.Save();
            await LedControl.DisconnectAsync();
            _ledOn = null;
            ShowLedControls();
            _ledMsg.Text = d.Name + " · подключаюсь…";
            await SetLedPower(true);
        }

        // ---------- команды ----------

        async Task SetLedPower(bool on)
        {
            var s = Settings.Current;
            if (s.LedAddress == 0) { OpenLedPanel(); return; }
            _tLed.Sub.Text = "…";
            var (r, g, b) = ScaledLed();
            bool ok = on ? await LedControl.PowerOnAsync(s.LedAddress, r, g, b) : await LedControl.PowerOffAsync(s.LedAddress);
            if (ok)
            {
                _ledOn = on;
                SetCtlStatus("");
                _ledMsg.Text = LedTitle(on ? "включена" : "выключена");
            }
            else
            {
                SetCtlStatus("Лента: нет связи — закрой приложение на телефоне и проверь Bluetooth");
                _ledMsg.Text = "Нет связи — закрой приложение ленты на телефоне";
            }
            RefreshLedTile();
        }

        void ApplyLedColor(Color c)
        {
            Settings.Current.LedColor = c.ToString();
            MarkLedSwatches();
            QueueLedSend();
        }

        void HueAt(double x)
        {
            double h = Math.Clamp(x / Math.Max(1, _hueBar.ActualWidth), 0, 1) * 360;
            ApplyLedColor(FromHue(h));
        }

        void QueueLedSend()
        {
            if (_ledTimer == null) return;
            _ledTimer.Stop();
            _ledTimer.Start();
        }

        async Task SendLedColorAsync()
        {
            var s = Settings.Current;
            if (s.LedAddress == 0) return;
            s.Save();
            var (r, g, b) = ScaledLed();
            bool ok = await LedControl.SetColorAsync(s.LedAddress, r, g, b);
            if (ok) { _ledOn = true; _ledMsg.Text = LedTitle("включена"); }
            else _ledMsg.Text = "Нет связи — закрой приложение ленты на телефоне";
            RefreshLedTile();
        }

        void MarkLedSwatches()
        {
            var cur = CurLedColor;
            foreach (var dot in _ledSwatches)
            {
                bool sel = (Color)ColorConverter.ConvertFromString((string)dot.Tag) == cur;
                dot.BorderBrush = sel ? Brushes.White : Brushes.Transparent;
            }
        }
    }
}
