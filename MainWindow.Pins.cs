using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Island
{
    // Закреплённые записи буфера. Хранятся между запусками в %AppData%\Island\pins.bin, зашифрованные DPAPI
    // (прочитать файл может только твоя учётка Windows), как и ключи ИИ.
    static class ClipPins
    {
        public const int MaxPins = 50;
        public const int MaxLen = 5000;

        static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "pins.bin");

        public static readonly List<string> Items = Load();

        static List<string> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var raw = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                    var l = JsonSerializer.Deserialize<List<string>>(Encoding.UTF8.GetString(raw));
                    if (l != null) return l;
                }
            }
            catch { }
            return new List<string>();
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var json = JsonSerializer.Serialize(Items);
                File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));
            }
            catch { }
        }
    }

    // Вкладка 📋: закреплённое сверху, ниже недавние копирования, сверху строка поиска.
    public partial class MainWindow
    {
        static bool ClipMatches(string text, string q) =>
            q.Length == 0 || text.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;

        void RefreshClip()
        {
            if (ClipList == null) return;
            ClipList.Children.Clear();

            var q = (ClipSearch.Text ?? "").Trim();
            var pins = ClipPins.Items.Where(t => ClipMatches(t, q)).ToList();
            var recent = _clipHistory.Where(t => !ClipPins.Items.Contains(t) && ClipMatches(t, q)).ToList();

            bool nothingAtAll = ClipPins.Items.Count == 0 && _clipHistory.Count == 0;
            ClipEmpty.Text = nothingAtAll ? "Здесь появится то, что ты скопируешь" : "Ничего не найдено";
            ClipEmpty.Visibility = pins.Count + recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (pins.Count > 0)
            {
                ClipList.Children.Add(ClipHeader("📌 Закреплённое"));
                foreach (var t in pins) ClipList.Children.Add(ClipRow(t, true));
            }
            if (recent.Count > 0)
            {
                if (pins.Count > 0) ClipList.Children.Add(ClipHeader("Недавнее"));
                foreach (var t in recent) ClipList.Children.Add(ClipRow(t, false));
            }
        }

        static TextBlock ClipHeader(string text) => new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(Color.FromArgb(153, 255, 255, 255)),
            FontSize = 11, Margin = new Thickness(4, 4, 0, 2)
        };

        Grid ClipRow(string t, bool pinned)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tb = new TextBlock
            {
                Text = t.Replace("\r", " ").Replace("\n", " ").Trim(),
                Foreground = Brushes.White, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis, Width = 335
            };
            var main = new Button { Style = (Style)FindResource("Chip"), Content = tb, Tag = t };
            main.Click += ClipItem_Click;
            Grid.SetColumn(main, 0);

            var pin = new Button
            {
                Style = (Style)FindResource("Chip"), Content = "📌", Tag = t, Margin = new Thickness(4, 0, 0, 0),
                Opacity = pinned ? 1.0 : 0.4, ToolTip = pinned ? "Открепить" : "Закрепить"
            };
            pin.Click += ClipPin_Click;
            Grid.SetColumn(pin, 1);

            var del = new Button
            {
                Style = (Style)FindResource("Chip"), Content = "✕", Tag = t, Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Убрать из списка"
            };
            del.Click += ClipDelete_Click;
            Grid.SetColumn(del, 2);

            row.Children.Add(main);
            row.Children.Add(pin);
            row.Children.Add(del);
            return row;
        }

        async void ClipPin_Click(object s, RoutedEventArgs e)
        {
            if (!(s is Button b) || !(b.Tag is string t)) return;
            if (ClipPins.Items.Remove(t))
            {
                ClipPins.Save();
                RefreshClip();
                return;
            }
            if (t.Length > ClipPins.MaxLen)
            {
                b.Content = "✗";
                b.ToolTip = "Слишком длинный текст для закрепления";
                await Task.Delay(900);
                RefreshClip();
                return;
            }
            ClipPins.Items.Insert(0, t);
            while (ClipPins.Items.Count > ClipPins.MaxPins) ClipPins.Items.RemoveAt(ClipPins.Items.Count - 1);
            ClipPins.Save();
            RefreshClip();
        }

        void ClipDelete_Click(object s, RoutedEventArgs e)
        {
            if (!(s is Button b) || !(b.Tag is string t)) return;
            _clipHistory.Remove(t);
            if (ClipPins.Items.Remove(t)) ClipPins.Save();
            RefreshClip();
        }

        // ---------- поиск ----------

        void ClipSearch_TextChanged(object s, TextChangedEventArgs e)
        {
            if (ClipSearchHint != null) ClipSearchHint.Visibility = ClipSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshClip();
        }

        // окно по умолчанию не активируется, а для ввода с клавиатуры нужно (как с паролем Wi-Fi)
        void ClipSearch_PreviewMouseDown(object s, MouseButtonEventArgs e)
        {
            _holdOpen = true;
            _leaveTimer.Stop();
            Activate();
            Dispatcher.BeginInvoke(new Action(() => ClipSearch.Focus()), DispatcherPriority.Input);
        }

        void ClipSearch_GotFocus(object s, KeyboardFocusChangedEventArgs e)
        {
            _holdOpen = true;
            _leaveTimer.Stop();
        }

        void ClipSearch_LostFocus(object s, KeyboardFocusChangedEventArgs e)
        {
            if (!_holdOpen) return;
            _holdOpen = false;
            if (!Pill.IsMouseOver) { _leaveTimer.Stop(); _leaveTimer.Start(); }
        }

        void ClipSearch_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (ClipSearch.Text.Length > 0) { ClipSearch.Clear(); return; }
            _holdOpen = false;
            Keyboard.ClearFocus();
            SetMode(Mode.Idle);
        }
    }
}
