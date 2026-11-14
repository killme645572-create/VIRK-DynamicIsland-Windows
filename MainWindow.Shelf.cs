using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island
{
    // Полка: бросаешь файлы на остров — они ждут на полке, потом перетаскиваешь их дальше (копированием).
    // Alt+Space открывает и закрывает остров с клавиатуры.
    public partial class MainWindow
    {
        readonly List<string> _shelf = new List<string>();
        WrapPanel _shelfWrap;
        TextBlock _shelfHint;
        Button _shelfDragAll, _shelfClear;
        bool _kbOpen;   // остров открыт горячей клавишей — закроется по Esc или при потере фокуса

        const int WM_HOTKEY = 0x0312;
        const int HotkeyId = 0x4953;
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        static string ShelfFile => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "shelf.txt");

        void InitShelf()
        {
            try
            {
                if (File.Exists(ShelfFile))
                    foreach (var p in File.ReadAllLines(ShelfFile))
                        if (!string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p))) _shelf.Add(p);
            }
            catch { }

            // ----- интерфейс вкладки -----
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            _shelfClear = ShelfChip("Очистить", () => { _shelf.Clear(); SaveShelf(); RefreshShelf(); });
            DockPanel.SetDock(_shelfClear, Dock.Right);
            _shelfDragAll = ShelfChip("", null);
            DockPanel.SetDock(_shelfDragAll, Dock.Right);
            Point dragStart = new Point();
            _shelfDragAll.PreviewMouseLeftButtonDown += (s, e) => dragStart = e.GetPosition(null);
            _shelfDragAll.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _shelf.Count == 0) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.X - dragStart.X) > 6 || Math.Abs(p.Y - dragStart.Y) > 6) StartShelfDrag(_shelf.ToArray());
            };
            _shelfHint = new TextBlock
            {
                Text = "Перетащи сюда файлы", Foreground = Br("#99FFFFFF"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            head.Children.Add(_shelfClear);
            head.Children.Add(_shelfDragAll);
            head.Children.Add(_shelfHint);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            _shelfWrap = new WrapPanel();
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _shelfWrap };
            Grid.SetRow(sv, 1);
            root.Children.Add(sv);
            ShelfView.Children.Add(root);

            // ----- приём файлов на остров -----
            Pill.AllowDrop = true;
            Pill.DragEnter += Shelf_DragEnter;
            Pill.DragOver += Shelf_DragOver;
            Pill.DragLeave += (s, e) => { if (_mode == Mode.Chat) return; _leaveTimer.Stop(); _leaveTimer.Start(); };
            Pill.Drop += Shelf_Drop;

            // Esc закрывает остров, открытый с клавиатуры
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _mode == Mode.Open && !_holdOpen) { SetMode(Mode.Idle); e.Handled = true; }
            };

            RefreshShelf();
        }

        Button ShelfChip(string text, Action click)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Chip"), Content = text, FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0)
            };
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        // ---------- горячая клавиша ----------

        void RegisterHotkey()
        {
            if (!Settings.Current.Hotkey) return;
            // Alt + Space, без автоповтора
            try { RegisterHotKey(_hwnd, HotkeyId, 0x0001 | 0x4000, 0x20); } catch { }
        }

        void UnregisterHotkey()
        {
            try { UnregisterHotKey(_hwnd, HotkeyId); } catch { }
        }

        void ToggleByHotkey()
        {
            if (_mode == Mode.Open) { SetMode(Mode.Idle); return; }
            _leaveTimer.Stop();
            _toastTimer.Stop();
            SetMode(Mode.Open);
            _kbOpen = true;
            Activate();   // чтобы работал Esc и остров закрылся при клике в другое окно
        }

        // ---------- приём файлов ----------

        static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        void Shelf_DragEnter(object s, DragEventArgs e)
        {
            if (!HasFiles(e)) return;
            if (_mode == Mode.Chat) { e.Effects = DragDropEffects.Copy; e.Handled = true; return; }   // в чате файлы идут в сообщение, а не на полку
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            _leaveTimer.Stop();
            _toastTimer.Stop();
            ShowTab("Shelf");
            if (_mode != Mode.Open) SetMode(Mode.Open);
        }

        void Shelf_DragOver(object s, DragEventArgs e)
        {
            if (!HasFiles(e)) return;
            if (_mode == Mode.Chat) { e.Effects = DragDropEffects.Copy; e.Handled = true; return; }
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            _leaveTimer.Stop();
        }

        void Shelf_Drop(object s, DragEventArgs e)
        {
            if (!HasFiles(e)) return;
            e.Handled = true;
            if (_mode == Mode.Chat)
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) Chat.AddFiles(dropped);
                return;
            }
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddToShelf(files);
        }

        void AddToShelf(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (!File.Exists(p) && !Directory.Exists(p)) continue;
                if (_shelf.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) continue;
                if (_shelf.Count >= 30) break;
                _shelf.Add(p);
            }
            SaveShelf();
            RefreshShelf();
        }

        void SaveShelf()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ShelfFile));
                File.WriteAllLines(ShelfFile, _shelf);
            }
            catch { }
        }

        // ---------- отображение ----------

        void RefreshShelf()
        {
            if (_shelfWrap == null) return;
            _shelf.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));   // файлы, которые уже удалили
            _shelfWrap.Children.Clear();
            foreach (var p in _shelf) _shelfWrap.Children.Add(MakeShelfTile(p));

            bool any = _shelf.Count > 0;
            _shelfHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            _shelfClear.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _shelfDragAll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _shelfDragAll.Content = "⇪ Перетащить все (" + _shelf.Count + ")";
        }

        static ImageSource IconFor(string path)
        {
            try
            {
                using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (ic == null) return null;
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
            catch { return null; }
        }

        UIElement MakeShelfTile(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;
            bool isDir = Directory.Exists(path);

            var icon = isDir ? null : IconFor(path);
            FrameworkElement iconEl = icon != null
                ? (FrameworkElement)new Image { Source = icon, Width = 32, Height = 32 }
                : new TextBlock { Text = isDir ? "📁" : "📄", FontSize = 26, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            iconEl.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(iconEl);
            stack.Children.Add(new TextBlock
            {
                Text = name, Foreground = Brushes.White, FontSize = 10, Width = 68, MaxHeight = 28,
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
            });

            var remove = new Border
            {
                Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(4, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Убрать с полки",
                Child = new TextBlock { Text = "✕", FontSize = 10, Foreground = Br("#99FFFFFF") }
            };
            remove.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _shelf.Remove(path);
                SaveShelf();
                RefreshShelf();
            };

            var g = new Grid();
            g.Children.Add(stack);
            g.Children.Add(remove);

            var tile = new Border
            {
                Width = 80, Height = 76, CornerRadius = new CornerRadius(10), Margin = new Thickness(3),
                Padding = new Thickness(4), Background = Br("#1AFFFFFF"), ToolTip = path, Child = g, Cursor = Cursors.Hand
            };
            Point start = new Point();
            tile.PreviewMouseLeftButtonDown += (s, e) =>
            {
                start = e.GetPosition(null);
                if (e.ClickCount == 2)
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
                }
            };
            tile.MouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.X - start.X) > 6 || Math.Abs(p.Y - start.Y) > 6) StartShelfDrag(new[] { path });
            };
            tile.MouseEnter += (s, e) => tile.Background = Br("#33FFFFFF");
            tile.MouseLeave += (s, e) => tile.Background = Br("#1AFFFFFF");
            return tile;
        }

        // перетаскивание с полки: только копирование, чтобы не увезти оригиналы
        void StartShelfDrag(string[] paths)
        {
            try
            {
                var data = new DataObject(DataFormats.FileDrop, paths);
                DragDrop.DoDragDrop(Pill, data, DragDropEffects.Copy);
            }
            catch { }
            if (!Pill.IsMouseOver) { _leaveTimer.Stop(); _leaveTimer.Start(); }
        }
    }
}
