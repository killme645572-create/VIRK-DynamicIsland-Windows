using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Island
{
    // Вкладка 🚀 «Мои сайты и программы» и команда «закрой …».
    //  • Свои сайты и программы: название + адрес/путь. Открываются кнопкой ▶ или голосом «открой <название>»,
    //    закрываются кнопкой ⏹ или голосом «закрой <название>».
    //  • Программу закрываем так: просим все её окна закрыться (WM_CLOSE) — как крестиком, с вопросом «Сохранить?».
    //    Если не закрылась — повторное «закрой» / ⏹ в течение 20 секунд (или «убей …») завершает процесс принудительно.
    //  • Сайт закрываем так: ищем окно браузера, у которого в заголовке (активной вкладки) есть название сайта,
    //    переводим его на передний план и жмём Ctrl+W — закрывается только эта вкладка.
    public partial class MainWindow
    {
        // ---------- Win32 ----------

        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        const uint WM_CLOSE_MSG = 0x0010;

        sealed class TopWin
        {
            public IntPtr H;
            public uint Pid;
            public string Title, Class;
        }

        static List<TopWin> TopWindows()
        {
            var list = new List<TopWin>();
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                var title = new StringBuilder(512);
                GetWindowText(h, title, 512);
                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                GetWindowThreadProcessId(h, out uint pid);
                list.Add(new TopWin { H = h, Pid = pid, Title = title.ToString(), Class = cls.ToString() });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static string ProcNameOf(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
        }

        static readonly HashSet<string> BrowserNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "browser", "yandex", "chromium", "arc"
        };

        // эти процессы никогда не завершаем принудительно
        static readonly HashSet<string> NeverKill = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "winlogon", "csrss", "svchost", "system", "services", "lsass", "dwm", "wininit", "smss", "Island",
            "ApplicationFrameHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "sihost", "taskhostw",
            "fontdrvhost", "RuntimeBroker"
        };

        // «Заголовок страницы - Google Chrome» → «Заголовок страницы»: чтобы слово «google» не совпало с любым окном Chrome
        static string StripBrowserSuffix(string title)
        {
            return Regex.Replace(title ?? "",
                @"\s[-–—]\s(?:Google Chrome|Microsoft\W+Edge|Mozilla Firefox|Opera|Brave|Яндекс Браузер|Yandex|Vivaldi)(?:\s.*)?$",
                "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // ---------- закрытие ----------

        readonly Dictionary<string, DateTime> _closeAsked = new Dictionary<string, DateTime>();

        static bool IsSiteTarget(string target) =>
            !string.IsNullOrEmpty(target) && Regex.IsMatch(target.Trim(), @"^https?://", RegexOptions.IgnoreCase);

        // по какому слову искать вкладку сайта: «youtube» из адреса и название, если оно не слишком короткое
        static string DefaultSiteRx(string target, string name)
        {
            var parts = new List<string>();
            try
            {
                string host = new Uri(target).Host.ToLowerInvariant();
                var lab = host.Split('.');
                string label = lab.Length >= 2 ? lab[lab.Length - 2] : lab[0];
                if (label.Length >= 3) parts.Add(Regex.Escape(label));
            }
            catch { }
            string n = (name ?? "").Trim();
            if (n.Length >= 3) parts.Add(Regex.Escape(n));
            if (parts.Count == 0) parts.Add(Regex.Escape(n.Length > 0 ? n : (target ?? "")));
            return string.Join("|", parts);
        }

        async Task<string> CloseSiteTabAsync(string titleRx, string display)
        {
            Regex rx;
            try { rx = new Regex(titleRx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch { return "Не понял, как искать вкладку «" + display + "»."; }

            var win = TopWindows().FirstOrDefault(w => w.Title.Length > 0
                && BrowserNames.Contains(ProcNameOf(w.Pid))
                && rx.IsMatch(StripBrowserSuffix(w.Title)));
            if (win == null)
                return "Не вижу открытой вкладки «" + display + "». Браузер показывает в заголовке только активную вкладку — открой её и повтори.";

            if (IsIconic(win.H)) ShowWindow(win.H, 9);
            SetForegroundWindow(win.H);
            for (int i = 0; i < 4 && GetForegroundWindow() != win.H; i++) await Task.Delay(50);
            if (GetForegroundWindow() != win.H)
            {
                // Windows не отдала фокус — «нажимаем» Alt, после этого это обычно разрешено
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                SetForegroundWindow(win.H);
                for (int i = 0; i < 6 && GetForegroundWindow() != win.H; i++) await Task.Delay(50);
            }
            // если окно так и не стало активным — Ctrl+W ушёл бы в чужое окно, поэтому не жмём
            if (GetForegroundWindow() != win.H) return "Не смог перейти в окно браузера, чтобы закрыть «" + display + "».";

            await Task.Delay(120);
            keybd_event(0x11, 0, 0, UIntPtr.Zero);   // Ctrl
            keybd_event(0x57, 0, 0, UIntPtr.Zero);   // W
            keybd_event(0x57, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
            return "Закрыл вкладку «" + display + "».";
        }

        // null — такой программы сейчас нет среди запущенных
        async Task<string> CloseProcessesAsync(string[] names, string display, bool force)
        {
            int me = Process.GetCurrentProcess().Id;
            var procs = new List<Process>();
            foreach (var n0 in names)
            {
                string n = Path.GetFileNameWithoutExtension(n0 ?? "");
                if (n.Length == 0 || NeverKill.Contains(n)) continue;
                try { procs.AddRange(Process.GetProcessesByName(n)); } catch { }
            }
            procs.RemoveAll(p => p.Id == me);
            if (procs.Count == 0) return null;

            string key = display.ToLowerInvariant();
            if (!force && _closeAsked.TryGetValue(key, out var asked) && DateTime.UtcNow - asked < TimeSpan.FromSeconds(20))
                force = true;   // второй запрос подряд — значит, мягко не вышло

            var pids = new HashSet<uint>(procs.Select(p => (uint)p.Id));
            foreach (var w in TopWindows())
                if (pids.Contains(w.Pid)) PostMessage(w.H, WM_CLOSE_MSG, IntPtr.Zero, IntPtr.Zero);
            foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }

            for (int i = 0; i < 25; i++)
            {
                await Task.Delay(100);
                if (procs.All(p => { try { return p.HasExited; } catch { return true; } })) break;
            }
            var alive = procs.Where(p => { try { return !p.HasExited; } catch { return false; } }).ToList();
            if (alive.Count == 0) { _closeAsked.Remove(key); return "Закрыл " + display + "."; }

            if (force)
            {
                foreach (var p in alive) { try { p.Kill(true); } catch { } }
                _closeAsked.Remove(key);
                return "Закрыл " + display + " принудительно.";
            }
            _closeAsked[key] = DateTime.UtcNow;
            return display + " не закрылась сама — возможно, спрашивает про сохранение или свернулась в трей. Повтори «закрой» (или нажми ⏹ ещё раз), чтобы закрыть принудительно.";
        }

        // окна Проводника (папки); titleRx == null — все
        static int CloseExplorerWindows(string titleRx)
        {
            Regex rx = null;
            if (titleRx != null) { try { rx = new Regex(titleRx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); } catch { return 0; } }
            int n = 0;
            foreach (var w in TopWindows())
            {
                if (w.Class != "CabinetWClass") continue;
                if (rx != null && !rx.IsMatch(w.Title)) continue;
                PostMessage(w.H, WM_CLOSE_MSG, IntPtr.Zero, IntPtr.Zero);
                n++;
            }
            return n;
        }

        static string ResolveShortcut(string lnk)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic sh = Activator.CreateInstance(t);
                dynamic sc = sh.CreateShortcut(lnk);
                string target = sc.TargetPath;
                return target;
            }
            catch { return null; }
        }

        // как называется процесс своей программы: из поля «Как закрыть» или из пути
        static string[] CustomProcNames(CustomApp a)
        {
            if (!string.IsNullOrWhiteSpace(a.Match))
                return a.Match.Split(new[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            string t = (a.Target ?? "").Trim().Trim('"');
            if (t.Length == 0) return new string[0];
            if (t.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string exe = ResolveShortcut(t);
                if (!string.IsNullOrEmpty(exe)) t = exe;
            }
            if (Regex.IsMatch(t, @"^[a-z][a-z0-9+.-]+:(?!\\)", RegexOptions.IgnoreCase) && !Regex.IsMatch(t, @"^[a-z]:\\", RegexOptions.IgnoreCase))
                return new string[0];   // steam://..., ms-settings: — по ним процесс не узнать
            string name = Path.GetFileNameWithoutExtension(t);
            return string.IsNullOrEmpty(name) ? new string[0] : new[] { name };
        }

        async Task<string> CloseCustomAppAsync(CustomApp a, bool force)
        {
            string name = a.Name;
            string target = (a.Target ?? "").Trim().Trim('"');
            if (IsSiteTarget(target))
            {
                string rx = !string.IsNullOrWhiteSpace(a.Match) ? Regex.Escape(a.Match.Trim()) : DefaultSiteRx(target, name);
                return await CloseSiteTabAsync(rx, name);
            }
            if (Directory.Exists(target))
            {
                string folder = Path.GetFileName(target.TrimEnd('\\', '/'));
                if (folder.Length == 0) folder = target;
                int n = CloseExplorerWindows(Regex.Escape(folder));
                return n > 0 ? "Закрыл «" + name + "»." : "Окно «" + name + "» не открыто.";
            }
            var procs = CustomProcNames(a);
            if (procs.Length == 0)
                return "Не знаю, какой процесс закрывать для «" + name + "». Укажи его в поле «Как закрыть» (✎).";
            return await CloseProcessesAsync(procs, name, force) ?? "«" + name + "» сейчас не запущена.";
        }

        // ---------- поиск по названию ----------

        CustomApp FindCustomApp(string x)
        {
            x = Norm(x);
            var list = Settings.Current.CustomApps;
            if (x.Length == 0 || list == null) return null;
            foreach (var a in list)
            {
                string n = Norm(a.Name);
                if (n.Length == 0) continue;
                if (n == x) return a;
            }
            foreach (var a in list)
            {
                string n = Norm(a.Name);
                if (n.Length < 4) continue;
                // «почта» ↔ «мою почту»: сравниваем без последней буквы (падежи)
                string stem = n.Length >= 5 ? n.Substring(0, n.Length - 1) : n;
                if (x.StartsWith(stem) || x.Contains(" " + stem)) return a;
            }
            return null;
        }

        string OpenCustomApp(CustomApp a)
        {
            string tgt = (a.Target ?? "").Trim().Trim('"');
            if (tgt.Length == 0) return "Для «" + a.Name + "» не указан адрес.";
            return Launch(tgt) ? "Открываю " + a.Name + "." : "Не получилось открыть " + a.Name + ".";
        }

        // «закрой ютуб», «закрой калькулятор», «закрой мою игру», «убей телеграм», «закрой хром принудительно»
        // null — это не команда закрытия
        async Task<string> TryCloseCommandAsync(string t)
        {
            var m = Regex.Match(t, @"^(?:пожалуйста\s+)?(?<v>закрой|закрыть|закрывай|выруби|убей|прибей|заверши|завершить|выключи)\s+(?:(?:мне|нам)\s+)?(?:(?:сайт|вкладку|окно|приложение|программу|страницу|папку)\s+)*(?<x>.+)$");
            if (!m.Success) return null;
            string v = m.Groups["v"].Value;
            bool strong = v != "выключи" && v != "заверши" && v != "завершить";   // их действуем только если поняли, что закрыть
            bool force = v == "убей" || v == "прибей";
            string x = m.Groups["x"].Value.Trim();
            if (Regex.IsMatch(x, @"\s*принудительно$"))
            {
                force = true;
                x = Regex.Replace(x, @"\s*принудительно$", "").Trim();
            }
            if (x.Length == 0 || Regex.IsMatch(x, @"остров|вирк|island|^(это|эту|всё|все|его|её|их)$")) return null;

            // свои сайты и программы
            var mine = FindCustomApp(x);
            if (mine != null) return await CloseCustomAppAsync(mine, force);

            foreach (var f in OpenFolders)
                if (OpenMatch(f, x))
                {
                    int n = CloseExplorerWindows(Regex.Escape(f.Name));
                    return n > 0 ? "Закрыл " + f.Name + "." : "Окно «" + f.Name + "» не открыто.";
                }

            foreach (var s in OpenSites)
            {
                if (!OpenMatch(s, x)) continue;
                if (s.Procs != null)
                {
                    string r = await CloseProcessesAsync(s.Procs, s.Name, force);   // приложение (Telegram, Discord…) запущено?
                    if (r != null) return r;
                }
                return await CloseSiteTabAsync(s.Titles ?? DefaultSiteRx(s.Target, s.Name), s.Name);
            }

            foreach (var a in OpenSystem)
            {
                if (!OpenMatch(a, x)) continue;
                if (a.Procs != null && a.Procs.Contains("explorer"))
                {
                    int n = CloseExplorerWindows(null);
                    return n > 0 ? "Закрыл Проводник." : "Проводник не открыт.";
                }
                if (a.Procs == null) return "Не знаю, как закрыть " + a.Name + ".";
                return await CloseProcessesAsync(a.Procs, a.Name, force) ?? a.Name + " сейчас не запущен.";
            }

            foreach (var al in OpenAppAliases)
            {
                if (!OpenMatch(al, x)) continue;
                return await CloseProcessesAsync(al.Procs, al.Name, force) ?? al.Name + " сейчас не запущен.";
            }

            // программа из меню «Пуск» по названию → процесс из ярлыка
            var lnk = FindStartApp(x);
            if (lnk != null)
            {
                string exe = ResolveShortcut(lnk);
                if (!string.IsNullOrEmpty(exe))
                {
                    string r = await CloseProcessesAsync(new[] { exe }, Path.GetFileNameWithoutExtension(lnk), force);
                    if (r != null) return r;
                }
            }

            // любая запущенная программа: имя процесса совпало или заголовок окна начинается с названия
            var names = new List<string>();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        if (Norm(p.ProcessName) == x || (x.Length >= 3 && Norm(p.MainWindowTitle).StartsWith(x)))
                            names.Add(p.ProcessName);
                    }
                    catch { }
                }
            }
            catch { }
            names = names.Distinct().ToList();
            if (names.Count > 0)
            {
                string r = await CloseProcessesAsync(names.ToArray(), x, force);
                if (r != null) return r;
            }

            return strong ? "Не нашёл, что закрыть: «" + x + "»." : null;
        }

        // ---------- вкладка 🚀 ----------

        Grid _appsRoot;
        StackPanel _appsList;
        Border _appsForm;
        TextBox _appName, _appTarget, _appMatch;
        TextBlock _appsHint, _appErr;
        CustomApp _appEditing;
        DispatcherTimer _appsHintTimer;
        const string AppsHintDefault = "▶ открыть, ⏹ закрыть. Голосом: «открой …» и «закрой …» — по названию, которое ты задал.";

        void InitApps()
        {
            _appsRoot = new Grid();
            _appsRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _appsRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _appsRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // шапка: подсказка/статус и кнопка «Добавить»
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var add = ShelfChip("＋ Добавить", () => OpenAppForm(null));
            DockPanel.SetDock(add, Dock.Right);
            head.Children.Add(add);
            _appsHint = new TextBlock
            {
                Text = AppsHintDefault, Foreground = Br("#99FFFFFF"), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            };
            head.Children.Add(_appsHint);
            Grid.SetRow(head, 0);
            _appsRoot.Children.Add(head);

            // форма добавления / редактирования
            var form = new StackPanel();
            form.Children.Add(AppsField("Название — так его можно позвать голосом", out _appName));

            var targetRow = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
            var browse = ShelfChip("📁", BrowseApp);
            browse.ToolTip = "Выбрать программу или файл";
            browse.Margin = new Thickness(6, 0, 0, 6);
            DockPanel.SetDock(browse, Dock.Right);
            targetRow.Children.Add(browse);
            targetRow.Children.Add(AppsField("Сайт (youtube.com) или путь к программе", out _appTarget));
            form.Children.Add(targetRow);

            form.Children.Add(AppsField("Как закрывать (необязательно): имя процесса или слово из вкладки", out _appMatch));

            _appErr = new TextBlock { Foreground = Br("#FFFF8A80"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
            form.Children.Add(_appErr);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ShelfChip("Отмена", CloseAppForm));
            btns.Children.Add(ShelfChip("✓ Сохранить", SaveAppForm));
            form.Children.Add(btns);

            _appsForm = new Border
            {
                Background = Br("#1AFFFFFF"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 10, 10, 8),
                Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed, Child = form
            };
            Grid.SetRow(_appsForm, 1);
            _appsRoot.Children.Add(_appsForm);

            // список
            _appsList = new StackPanel();
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _appsList };
            Grid.SetRow(sv, 2);
            _appsRoot.Children.Add(sv);

            AppsView.Children.Add(_appsRoot);
            RefreshApps();
        }

        // поле ввода с подсказкой; остров по умолчанию не берёт фокус, поэтому активируем окно при клике (как в поиске по буферу)
        Grid AppsField(string hint, out TextBox box)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            var tb = new TextBox
            {
                Padding = new Thickness(10, 5, 10, 5), FontSize = 12, Background = Br("#26FFFFFF"),
                Foreground = Brushes.White, BorderThickness = new Thickness(0), CaretBrush = Brushes.White
            };
            var h = new TextBlock
            {
                Text = hint, Foreground = Br("#80FFFFFF"), FontSize = 12, Margin = new Thickness(11, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis
            };
            tb.TextChanged += (s, e) => h.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            tb.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _holdOpen = true;
                _leaveTimer.Stop();
                Activate();
                Dispatcher.BeginInvoke(new Action(() => tb.Focus()), DispatcherPriority.Input);
            };
            tb.GotKeyboardFocus += (s, e) => { _holdOpen = true; _leaveTimer.Stop(); };
            tb.LostKeyboardFocus += (s, e) =>
            {
                if (!_holdOpen) return;
                _holdOpen = false;
                if (!Pill.IsMouseOver) { _leaveTimer.Stop(); _leaveTimer.Start(); }
            };
            tb.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; CloseAppForm(); }
                else if (e.Key == Key.Enter) { e.Handled = true; SaveAppForm(); }
            };
            g.Children.Add(tb);
            g.Children.Add(h);
            box = tb;
            return g;
        }

        void OpenAppForm(CustomApp editing)
        {
            _appEditing = editing;
            _appName.Text = editing?.Name ?? "";
            _appTarget.Text = editing?.Target ?? "";
            _appMatch.Text = editing?.Match ?? "";
            _appErr.Visibility = Visibility.Collapsed;
            _appsForm.Visibility = Visibility.Visible;
            _appsList.Visibility = Visibility.Collapsed;
            _appsHint.Text = editing == null ? "Новый сайт или программа" : "Редактирование: " + editing.Name;
            _holdOpen = true;
            _leaveTimer.Stop();
            Activate();
            Dispatcher.BeginInvoke(new Action(() => _appName.Focus()), DispatcherPriority.Input);
        }

        void CloseAppForm()
        {
            _appEditing = null;
            _appsForm.Visibility = Visibility.Collapsed;
            _appsList.Visibility = Visibility.Visible;
            _appsHint.Text = AppsHintDefault;
            Keyboard.ClearFocus();
            _holdOpen = false;
            if (!Pill.IsMouseOver) { _leaveTimer.Stop(); _leaveTimer.Start(); }
        }

        void BrowseApp()
        {
            _holdOpen = true;
            _leaveTimer.Stop();
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выбери программу или файл",
                Filter = "Программы и ярлыки (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|Все файлы|*.*",
                CheckFileExists = true
            };
            bool ok = dlg.ShowDialog(this) == true;
            Activate();
            if (ok)
            {
                _appTarget.Text = dlg.FileName;
                if (_appName.Text.Trim().Length == 0) _appName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            }
            _holdOpen = false;
            if (!Pill.IsMouseOver) { _leaveTimer.Stop(); _leaveTimer.Start(); }
        }

        // «youtube.com» → https://youtube.com; пути и exe-файлы оставляем как есть
        static string NormalizeAppTarget(string raw)
        {
            string t = (raw ?? "").Trim().Trim('"');
            if (t.Length == 0) return t;
            if (Regex.IsMatch(t, @"^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase)) return t;
            if (File.Exists(t) || Directory.Exists(t)) return t;
            if (t.Contains("\\") || Regex.IsMatch(t, @"^[a-z]:", RegexOptions.IgnoreCase)) return t;
            if (!Regex.IsMatch(t, @"\.(exe|lnk|bat|cmd|msc|msi|ps1|url)$", RegexOptions.IgnoreCase)
                && Regex.IsMatch(t, @"^[^\s/\\]+\.[a-z]{2,}(?:[/?#]\S*)?$", RegexOptions.IgnoreCase))
                return "https://" + t;
            return t;
        }

        void SaveAppForm()
        {
            string name = _appName.Text.Trim();
            string target = NormalizeAppTarget(_appTarget.Text);
            string match = _appMatch.Text.Trim();
            if (name.Length == 0) { AppFormError("Дай название — по нему можно будет открыть голосом."); return; }
            if (target.Length == 0) { AppFormError("Укажи сайт или программу."); return; }

            var st = Settings.Current;
            if (st.CustomApps == null) st.CustomApps = new List<CustomApp>();
            string nn = Norm(name);
            if (st.CustomApps.Any(a => a != _appEditing && Norm(a.Name) == nn)) { AppFormError("Такое название уже есть."); return; }

            if (_appEditing != null)
            {
                _appEditing.Name = name;
                _appEditing.Target = target;
                _appEditing.Match = match.Length == 0 ? null : match;
            }
            else st.CustomApps.Add(new CustomApp { Name = name, Target = target, Match = match.Length == 0 ? null : match });
            st.Save();
            CloseAppForm();
            RefreshApps();
            SetAppsStatus("Сохранено: " + name);
        }

        void AppFormError(string text)
        {
            _appErr.Text = text;
            _appErr.Visibility = Visibility.Visible;
        }

        void SetAppsStatus(string text)
        {
            _appsHint.Text = text;
            if (_appsHintTimer == null)
            {
                _appsHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _appsHintTimer.Tick += (s, e) =>
                {
                    _appsHintTimer.Stop();
                    if (_appsForm.Visibility != Visibility.Visible) _appsHint.Text = AppsHintDefault;
                };
            }
            _appsHintTimer.Stop();
            _appsHintTimer.Start();
        }

        Button AppBtn(string text, string tip, Action click)
        {
            var b = ShelfChip(text, click);
            b.ToolTip = tip;
            b.Margin = new Thickness(4, 0, 0, 0);
            return b;
        }

        void RefreshApps()
        {
            if (_appsList == null) return;
            _appsList.Children.Clear();
            var list = Settings.Current.CustomApps;
            if (list == null || list.Count == 0)
            {
                _appsList.Children.Add(new TextBlock
                {
                    Text = "Пока пусто. Нажми «＋ Добавить», введи название и адрес сайта или путь к программе — и сможешь открывать и закрывать их кнопкой или голосом.",
                    Foreground = Br("#99FFFFFF"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0)
                });
                return;
            }

            foreach (var item in list.ToList())
            {
                var app = item;
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                info.Children.Add(new TextBlock
                {
                    Text = (IsSiteTarget(app.Target) ? "🌐 " : "🖥 ") + app.Name, Foreground = Brushes.White,
                    FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
                });
                info.Children.Add(new TextBlock
                {
                    Text = app.Target, Foreground = Br("#80FFFFFF"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis
                });
                grid.Children.Add(info);

                var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                btns.Children.Add(AppBtn("▶", "Открыть", () => SetAppsStatus(OpenCustomApp(app))));
                btns.Children.Add(AppBtn("⏹", "Закрыть (второй клик подряд — принудительно)", async () => SetAppsStatus(await CloseCustomAppAsync(app, false))));
                btns.Children.Add(AppBtn("✎", "Изменить", () => OpenAppForm(app)));
                btns.Children.Add(AppBtn("✕", "Удалить из списка", () =>
                {
                    Settings.Current.CustomApps.Remove(app);
                    Settings.Current.Save();
                    RefreshApps();
                }));
                Grid.SetColumn(btns, 1);
                grid.Children.Add(btns);

                _appsList.Children.Add(new Border
                {
                    Background = Br("#14FFFFFF"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 6, 6),
                    Margin = new Thickness(0, 0, 0, 4), Child = grid
                });
            }
        }
    }
}
