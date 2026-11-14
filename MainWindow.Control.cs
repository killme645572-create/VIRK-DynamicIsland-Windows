using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;

namespace Island
{
    // Вкладка «Управление»: Wi-Fi (со списком сетей), Bluetooth, тёмная тема, VPN и кнопки питания.
    public partial class MainWindow
    {
        class Tile { public Border Box; public TextBlock Sub; }

        const string ThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        bool _holdOpen;   // не сворачивать остров, пока вводим пароль
        Tile _tWifi, _tBt, _tTheme, _tVpn, _tLed;
        Grid _ctlMain, _wifiPanel;
        TextBlock _ctlStatus, _wifiMsg, _pwLabel;
        StackPanel _wifiList;
        Grid _pwRow;
        PasswordBox _pw;
        WifiNet _pendingNet;
        Button _armedBtn;
        DateTime _armedAt;

        static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        void InitControl()
        {
            Deactivated += (s, e) =>
            {
                if (_holdOpen || _kbOpen) { _holdOpen = false; _kbOpen = false; SetMode(Mode.Idle); }
            };

            // ----- главный вид: плитки и питание -----
            _ctlMain = new Grid();
            _ctlMain.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _ctlMain.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _ctlMain.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tiles = new UniformGrid { Rows = 1, VerticalAlignment = VerticalAlignment.Top };
            _tWifi = MakeTile("📶", "Wi-Fi", () => ToggleRadio(RadioKind.WiFi), OpenWifiPanel);
            _tBt = MakeTile("🔷", "Bluetooth", () => ToggleRadio(RadioKind.Bluetooth));
            _tTheme = MakeTile("🌓", "Тёмная тема", ToggleTheme);
            _tVpn = MakeTile("🛡", "VPN", ToggleVpn, CycleVpnProfile);
            _tLed = MakeTile("💡", "Лента", ToggleLed, OpenLedPanel);
            tiles.Children.Add(_tWifi.Box);
            tiles.Children.Add(_tBt.Box);
            tiles.Children.Add(_tTheme.Box);
            tiles.Children.Add(_tVpn.Box);
            tiles.Children.Add(_tLed.Box);
            Grid.SetRow(tiles, 0);
            _ctlMain.Children.Add(tiles);

            var power = new UniformGrid { Rows = 1, Margin = new Thickness(0, 8, 0, 0) };
            power.Children.Add(MakePower("🔒 Блокировка", () => Lock_Click(null, null), false));
            power.Children.Add(MakePower("🌙 Сон", Sleep, false));
            power.Children.Add(MakePower("↻ Перезагрузка", () => RunShutdown("/r /t 0"), true));
            power.Children.Add(MakePower("⏻ Выключение", () => RunShutdown("/s /t 0"), true));
            Grid.SetRow(power, 1);
            _ctlMain.Children.Add(power);

            _ctlStatus = new TextBlock
            {
                Foreground = Br("#99FFFFFF"), FontSize = 11, Margin = new Thickness(4, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, Height = 16
            };
            Grid.SetRow(_ctlStatus, 2);
            _ctlMain.Children.Add(_ctlStatus);

            // ----- вид со списком Wi-Fi -----
            _wifiPanel = new Grid { Visibility = Visibility.Collapsed };
            _wifiPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _wifiPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _wifiPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var back = Chip("←", () => ShowControlMain());
            var refresh = Chip("⟳", async () => await LoadNetworks());
            DockPanel.SetDock(refresh, Dock.Right);
            _wifiMsg = new TextBlock
            {
                Foreground = Br("#99FFFFFF"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0), Text = "Сети Wi-Fi", TextTrimming = TextTrimming.CharacterEllipsis
            };
            head.Children.Add(refresh);
            head.Children.Add(back);
            head.Children.Add(_wifiMsg);
            Grid.SetRow(head, 0);
            _wifiPanel.Children.Add(head);

            _wifiList = new StackPanel();
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _wifiList };
            Grid.SetRow(sv, 1);
            _wifiPanel.Children.Add(sv);

            _pwRow = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            _pwRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _pwRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _pwRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _pwLabel = new TextBlock
            {
                Foreground = Br("#99FFFFFF"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0), MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis
            };
            _pw = new PasswordBox
            {
                Background = Br("#26FFFFFF"), Foreground = Brushes.White, CaretBrush = Brushes.White,
                BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _pw.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; _ = ConnectWithPassword(); }
                else if (e.Key == Key.Escape) { e.Handled = true; HidePassword(); }
            };
            var ok = Chip("Подключить", () => { _ = ConnectWithPassword(); });
            ok.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(_pwLabel, 0);
            Grid.SetColumn(_pw, 1);
            Grid.SetColumn(ok, 2);
            _pwRow.Children.Add(_pwLabel);
            _pwRow.Children.Add(_pw);
            _pwRow.Children.Add(ok);
            Grid.SetRow(_pwRow, 2);
            _wifiPanel.Children.Add(_pwRow);

            ControlView.Children.Add(_ctlMain);
            ControlView.Children.Add(_wifiPanel);
            InitLed();
        }

        // ---------- конструкторы элементов ----------

        Button Chip(string text, Action click)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Chip"), Content = text, FontSize = 12,
                Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 6, 4, 6)
            };
            b.Click += (s, e) => click();
            return b;
        }

        Tile MakeTile(string icon, string title, Action click, Action arrow = null)
        {
            var sub = new TextBlock { Foreground = Br("#99FFFFFF"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            stack.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(sub);

            var root = new Grid();
            root.Children.Add(new TextBlock { Text = icon, FontSize = 22, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
            root.Children.Add(stack);

            if (arrow != null)
            {
                var hit = new Border
                {
                    Background = Brushes.Transparent, Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Padding = new Thickness(10, 0, 2, 8),
                    Child = new TextBlock { Text = "›", FontSize = 20, Foreground = Br("#CCFFFFFF") }
                };
                hit.MouseLeftButtonUp += (s, e) => { e.Handled = true; arrow(); };
                root.Children.Add(hit);
            }

            var box = new Border
            {
                Background = Br("#26FFFFFF"), CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, Height = 84, Child = root
            };
            box.MouseLeftButtonUp += (s, e) => click();
            box.MouseEnter += (s, e) => box.Opacity = 0.85;
            box.MouseLeave += (s, e) => box.Opacity = 1;
            return new Tile { Box = box, Sub = sub };
        }

        static void SetTile(Tile t, bool on, string sub)
        {
            t.Box.Background = Br(on ? "#FF2F6FEB" : "#26FFFFFF");
            t.Sub.Text = sub;
        }

        Button MakePower(string text, Action act, bool confirm)
        {
            var b = Chip(text, () => { });
            b.Click += (s, e) =>
            {
                if (!confirm) { act(); return; }
                if (_armedBtn == b && DateTime.UtcNow - _armedAt < TimeSpan.FromSeconds(3))
                {
                    _armedBtn = null;
                    act();
                    return;
                }
                _armedBtn = b;
                _armedAt = DateTime.UtcNow;
                b.Content = "Точно?";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t.Tick += (s2, e2) => { t.Stop(); b.Content = text; if (_armedBtn == b) _armedBtn = null; };
                t.Start();
            };
            return b;
        }

        // ---------- показ вкладки ----------

        void ShowControlMain()
        {
            HidePassword();
            _wifiPanel.Visibility = Visibility.Collapsed;
            if (_ledPanel != null) _ledPanel.Visibility = Visibility.Collapsed;
            _ctlMain.Visibility = Visibility.Visible;
        }

        async Task RefreshControlAsync()
        {
            var wifi = await RadioControl.IsOnAsync(RadioKind.WiFi);
            string net = wifi == true ? WifiControl.CurrentSsid() : null;
            SetTile(_tWifi, wifi == true, wifi == null ? "Недоступен" : wifi == true ? (net ?? "Включён") : "Выключен");

            var bt = await RadioControl.IsOnAsync(RadioKind.Bluetooth);
            SetTile(_tBt, bt == true, bt == null ? "Недоступен" : bt == true ? "Включён" : "Выключен");

            bool dark = IsDarkTheme();
            SetTile(_tTheme, dark, dark ? "Тёмная" : "Светлая");

            var vpn = VpnControl.Name;
            if (vpn == null && !VpnControl.HasCommands)
                SetTile(_tVpn, false, "Не настроен");
            else
            {
                bool up = VpnControl.IsUp(vpn);
                SetTile(_tVpn, up, up ? "Подключён" : (vpn ?? "Выключен"));
            }

            RefreshLedTile();
        }

        void SetCtlStatus(string text) => _ctlStatus.Text = text;

        // ---------- переключатели ----------

        async void ToggleRadio(RadioKind kind)
        {
            string name = kind == RadioKind.WiFi ? "Wi-Fi" : "Bluetooth";
            var cur = await RadioControl.IsOnAsync(kind);
            if (cur == null) { SetCtlStatus(name + ": адаптер недоступен"); return; }
            bool ok = await RadioControl.SetAsync(kind, !cur.Value);
            SetCtlStatus(ok ? "" : name + ": не удалось переключить");
            await Task.Delay(400);
            await RefreshControlAsync();
        }

        static bool IsDarkTheme() => Convert.ToInt32(Registry.GetValue(ThemeKey, "AppsUseLightTheme", 1)) == 0;

        async void ToggleTheme()
        {
            int next = IsDarkTheme() ? 1 : 0;
            Registry.SetValue(ThemeKey, "AppsUseLightTheme", next, RegistryValueKind.DWord);
            Registry.SetValue(ThemeKey, "SystemUsesLightTheme", next, RegistryValueKind.DWord);
            await RefreshControlAsync();
        }

        async void ToggleVpn()
        {
            var name = VpnControl.Name;
            if (name == null && !VpnControl.HasCommands)
            {
                ImportVpn();   // профиля нет — предлагаем установить клиент или выбрать .conf
                return;
            }
            bool up = VpnControl.IsUp(name);
            _tVpn.Sub.Text = up ? "Отключаю…" : "Подключаю…";
            bool ok = await (up ? VpnControl.DisconnectAsync() : VpnControl.ConnectAsync());
            SetCtlStatus(ok ? "" : "VPN: не удалось " + (up ? "отключить" : "подключить"));
            await Task.Delay(1200);
            await RefreshControlAsync();
        }

        // стрелка на плитке VPN: переключает профиль, если их несколько
        async void CycleVpnProfile()
        {
            var list = VpnControl.Profiles();
            if (list.Count < 2) { SetCtlStatus(list.Count == 0 ? "VPN: профилей Windows нет" : "VPN: профиль один — " + list[0]); return; }
            int i = list.IndexOf(VpnControl.Name);
            var next = list[(i + 1) % list.Count];
            Settings.Current.VpnName = next;
            Settings.Current.Save();
            SetCtlStatus("VPN: выбран профиль " + next);
            await RefreshControlAsync();
        }

        // ---------- питание ----------

        void Sleep()
        {
            try { System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false); } catch { }
        }

        static void RunShutdown(string args)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true }); } catch { }
        }

        // ---------- Wi-Fi: список сетей ----------

        async void OpenWifiPanel()
        {
            _ctlMain.Visibility = Visibility.Collapsed;
            _wifiPanel.Visibility = Visibility.Visible;
            await LoadNetworks();
        }

        async Task LoadNetworks()
        {
            HidePassword();
            _wifiMsg.Text = "Ищу сети…";
            _wifiList.Children.Clear();
            var nets = await WifiControl.ScanAsync();
            _wifiList.Children.Clear();
            if (nets == null) { _wifiMsg.Text = "Wi-Fi недоступен: включи адаптер или проверь доступ к местоположению"; return; }
            if (nets.Count == 0) { _wifiMsg.Text = "Сети не найдены"; return; }
            _wifiMsg.Text = "Сети Wi-Fi";
            foreach (var n in nets) _wifiList.Children.Add(MakeNetRow(n));
        }

        UIElement MakeNetRow(WifiNet n)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = n.Ssid, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            left.Children.Add(new TextBlock
            {
                Text = n.Connected ? "Подключено" : n.Secured ? "🔒 Защищена" : "Открытая сеть",
                Foreground = Br(n.Connected ? "#FF6EA8FF" : "#99FFFFFF"), FontSize = 10
            });
            g.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(new TextBlock
            {
                Text = new string('●', Math.Min(5, n.Bars)) + new string('○', Math.Max(0, 5 - n.Bars)),
                Foreground = Br("#99FFFFFF"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center
            });
            if (n.Connected)
            {
                var off = Chip("Отключить", () => { WifiControl.Disconnect(); _wifiMsg.Text = "Отключено"; _ = DelayedReload(); });
                off.Margin = new Thickness(10, 0, 0, 0);
                right.Children.Add(off);
            }
            Grid.SetColumn(right, 1);
            g.Children.Add(right);

            var row = new Border
            {
                Background = Br("#1AFFFFFF"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 4), Cursor = n.Connected ? Cursors.Arrow : Cursors.Hand, Child = g
            };
            if (!n.Connected) row.MouseLeftButtonUp += (s, e) => { _ = ConnectNet(n); };
            return row;
        }

        async Task DelayedReload()
        {
            await Task.Delay(1500);
            if (_wifiPanel.Visibility == Visibility.Visible) await LoadNetworks();
            await RefreshControlAsync();
        }

        async Task ConnectNet(WifiNet n)
        {
            HidePassword();
            _wifiMsg.Text = "Подключаюсь к " + n.Ssid + "…";
            var st = await WifiControl.ConnectAsync(n, null);
            if (st == WiFiConnectionStatus.Success)
            {
                _wifiMsg.Text = "Подключено: " + n.Ssid;
                await DelayedReload();
                return;
            }
            if (n.Secured)
            {
                ShowPassword(n);
                return;
            }
            _wifiMsg.Text = "Не удалось подключиться к " + n.Ssid;
        }

        void ShowPassword(WifiNet n)
        {
            _pendingNet = n;
            _wifiMsg.Text = "Нужен пароль";
            _pwLabel.Text = n.Ssid;
            _pw.Clear();
            _pwRow.Visibility = Visibility.Visible;
            _holdOpen = true;
            _leaveTimer.Stop();
            Activate();   // окно по умолчанию не активируется, а для ввода пароля нужно
            Dispatcher.BeginInvoke(new Action(() => _pw.Focus()), DispatcherPriority.Input);
        }

        void HidePassword()
        {
            if (_pwRow == null) return;
            _pwRow.Visibility = Visibility.Collapsed;
            _pendingNet = null;
            _pw.Clear();
            if (_holdOpen)
            {
                _holdOpen = false;
                if (!Pill.IsMouseOver) { _leaveTimer.Stop(); _leaveTimer.Start(); }
            }
        }

        async Task ConnectWithPassword()
        {
            var n = _pendingNet;
            if (n == null) return;
            string pass = _pw.Password;
            if (string.IsNullOrEmpty(pass)) return;
            _wifiMsg.Text = "Подключаюсь к " + n.Ssid + "…";
            var st = await WifiControl.ConnectAsync(n, pass);
            if (st == WiFiConnectionStatus.Success)
            {
                HidePassword();
                _wifiMsg.Text = "Подключено: " + n.Ssid;
                await DelayedReload();
            }
            else
            {
                _wifiMsg.Text = st == WiFiConnectionStatus.InvalidCredential ? "Неверный пароль" : "Не удалось подключиться";
                _pw.Clear();
                _pw.Focus();
            }
        }
    }
}
