using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Island
{
    public partial class ChatPanel : UserControl
    {
        public event EventHandler CloseRequested;

        readonly ObservableCollection<ChatMsg> _items = new ObservableCollection<ChatMsg>();
        readonly List<ChatTurn> _history = new List<ChatTurn>();
        readonly ObservableCollection<ChatImage> _pending = new ObservableCollection<ChatImage>();   // картинки, приложенные к ещё не отправленному сообщению
        const int MaxImages = 4;
        public static bool PickerOpen;   // открыто окно выбора файла — остров не должен сворачиваться
        bool _busy;
        public bool Busy => _busy;   // ИИ сейчас генерирует ответ — остров «задумывается»
        ProviderInfo _prov;
        string _pick;   // что сейчас раскрыто: "provider" | "model" | null

        public ChatPanel()
        {
            InitializeComponent();
            List.ItemsSource = _items;
            AttachList.ItemsSource = _pending;
            // перетаскивание файлов прямо на окно чата (перехватываем раньше, чем поле ввода)
            AllowDrop = true;
            PreviewDragEnter += OnDragFiles;
            PreviewDragOver += OnDragFiles;
            PreviewDrop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) { e.Handled = true; AddFiles(dropped); }
            };
            _pending.CollectionChanged += (s, e) => AttachList.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _prov = ChatClient.Current;
            RefreshHeader();
            KeySetupWindow.KeySaved += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                _prov = ChatClient.Current;
                RefreshHeader();
                _items.Add(ChatMsg.Note($"Ключ для {_prov.Name} сохранён, можно писать."));
                ScrollDown();
                _ = PrefetchAsync(_prov);
            }));
            _items.Add(ChatMsg.Note(Welcome()));
            _ = PrefetchAsync(_prov);
        }

        string Welcome()
        {
            if (_prov.KeyRequired && !KeyStore.Has(_prov))
                return $"Привет! Провайдера и модель выбирай сверху. Для {_prov.Name} нужен ключ. Напиши /key, откроется окно с подсказками (или сразу  /key {_prov.Id} {_prov.KeyHint}).  /help — все команды";
            return "Привет! О чём поговорим?  (провайдер и модель — сверху, 📎 или Ctrl+V — картинка, /help — команды, Esc — закрыть)";
        }

        public void FocusInput()
        {
            Input.Focus();
            Keyboard.Focus(Input);
        }

        void Close_Click(object s, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
        void Send_Click(object s, RoutedEventArgs e) => _ = SendAsync();

        void Input_PreviewKeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && TryPasteImage())
            {
                e.Handled = true;   // в буфере картинка — прикрепляем её, а не вставляем текст
                return;
            }
            if (e.Key == Key.Escape)
            {
                if (_pick != null) ClosePick();   // первый Esc закрывает список, второй — чат
                else CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                _ = SendAsync();
            }
        }

        // ───────────── Шапка и выпадающие списки ─────────────

        void RefreshHeader()
        {
            ProviderBtn.Content = _prov.Name + " ▾";
            ModelBtn.Content = new TextBlock
            {
                Text = ChatClient.ModelFor(_prov) + (ChatClient.IsVision(ChatClient.ModelFor(_prov)) ? " 👁" : "") + " ▾",
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        void Provider_Click(object s, RoutedEventArgs e)
        {
            if (_pick == "provider") { ClosePick(); return; }
            PickList.Children.Clear();
            foreach (var p in ChatClient.Providers)
            {
                var prov = p;
                var mark = prov == _prov ? "✓  " : "    ";
                var note = prov.KeyRequired && !KeyStore.Has(prov) ? "   · нужен ключ" : "";
                AddItem(mark + prov.Name + note, () => SelectProvider(prov));
            }
            OpenPick("provider", ProviderBtn);
        }

        async void Model_Click(object s, RoutedEventArgs e)
        {
            if (_pick == "model") { ClosePick(); return; }
            var prov = _prov;
            PickList.Children.Clear();
            AddLabel("Загружаю список моделей…");
            OpenPick("model", ModelBtn);

            List<string> list;
            try { list = await ChatClient.GetModelsAsync(prov); }
            catch { list = prov.Fallback.ToList(); }
            if (_pick != "model" || prov != _prov) return;   // пока грузилось, список закрыли или сменили провайдера

            if (ChatClient.EnsureModel(prov, list)) RefreshHeader();
            var cur = ChatClient.ModelFor(prov);
            if (!list.Contains(cur)) list = new[] { cur }.Concat(list).ToList();

            PickList.Children.Clear();
            foreach (var m in list)
            {
                var model = m;
                AddItem((model == cur ? "✓  " : "    ") + model + (ChatClient.IsVision(model) ? "  👁" : ""), () => SelectModel(model));
            }
            AddLabel("👁 — модель понимает картинки (определяется по названию, возможны неточности)");
            AddLabel("Нет нужной? Введи в чате:  /model название");
        }

        void AddItem(string text, Action onPick)
        {
            var b = new Button { Style = (Style)FindResource("PickItem"), Content = text };
            b.Click += (s, e) => { ClosePick(); onPick(); };
            PickList.Children.Add(b);
        }

        void AddLabel(string text)
        {
            PickList.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(10, 6, 10, 4),
                TextWrapping = TextWrapping.Wrap
            });
        }

        void OpenPick(string kind, FrameworkElement anchor)
        {
            _pick = kind;
            double left = 0;
            try { left = Math.Max(0, anchor.TranslatePoint(new Point(0, 0), this).X - 16); } catch { }   // 16 — внешний отступ панели
            PickOverlay.Margin = new Thickness(left, 0, 0, 0);
            PickOverlay.Visibility = Visibility.Visible;
        }

        void ClosePick()
        {
            _pick = null;
            PickOverlay.Visibility = Visibility.Collapsed;
        }

        void SelectProvider(ProviderInfo p)
        {
            _prov = p;
            ChatClient.SetProvider(p);
            RefreshHeader();
            _items.Add(ChatMsg.Note(p.KeyRequired && !KeyStore.Has(p)
                ? $"{p.Name}: нужен ключ.  /key {p.Id} {p.KeyHint}  (взять: {p.KeyUrl})"
                : $"{p.Name}, модель {ChatClient.ModelFor(p)}"));
            ScrollDown();
            _ = PrefetchAsync(p);
            FocusInput();
        }

        void SelectModel(string model)
        {
            ChatClient.SetModel(_prov, model);
            RefreshHeader();
            FocusInput();
        }

        // Подгружает список моделей в фоне и, если выбранной по умолчанию модели нет (например, у Ollama), берёт первую из списка.
        async System.Threading.Tasks.Task PrefetchAsync(ProviderInfo p)
        {
            try
            {
                var list = await ChatClient.GetModelsAsync(p);
                if (ChatClient.EnsureModel(p, list) && p == _prov) RefreshHeader();
            }
            catch { }
        }

        // ───────────── Отправка и команды ─────────────

        async System.Threading.Tasks.Task SendAsync()
        {
            var text = Input.Text.Trim();
            if ((text.Length == 0 && _pending.Count == 0) || _busy) return;
            Input.Clear();
            ClosePick();

            if (text.StartsWith("/key", StringComparison.OrdinalIgnoreCase)) { HandleKey(text.Substring(4).Trim()); return; }
            if (text.StartsWith("/model", StringComparison.OrdinalIgnoreCase)) { HandleModel(text.Substring(6).Trim()); return; }
            if (text.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                _items.Add(ChatMsg.Note(
                    "/key провайдер ключ — сохранить ключ (openrouter, groq, ollama).  " +
                    "/key ключ — для текущего провайдера.\n/model название — своя модель для текущего провайдера.\n/clear — очистить диалог.\n📎 или Ctrl+V — прикрепить картинку (можно и перетащить файл на остров). Нужна модель с пометкой 👁."));
                ScrollDown();
                return;
            }
            if (text.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                _history.Clear(); _items.Clear(); _pending.Clear();
                _items.Add(ChatMsg.Note("Диалог очищен."));
                return;
            }

            var images = _pending.ToList();
            _pending.Clear();
            var shown = text;                                   // в пузыре — только то, что написал сам
            if (text.Length == 0) text = "Что на этой картинке?";   // модели нужен хоть какой-то вопрос
            if (images.Count > 0 && !ChatClient.IsVision(ChatClient.ModelFor(_prov)))
                _items.Add(ChatMsg.Note("Эта модель, судя по названию, не понимает картинки. Попробую, но лучше выбери модель с пометкой 👁."));
            _items.Add(ChatMsg.User(shown, images.Select(i => i.Thumb).ToList()));
            _history.Add(new ChatTurn { Role = "user", Text = text, Images = images });
            var reply = ChatMsg.Claude("…");
            _items.Add(reply);
            ScrollDown();

            _busy = true;
            try
            {
                var answer = await ChatClient.AskAsync(_prov, ChatClient.ModelFor(_prov), _history);
                reply.Text = answer;
                _history.Add(new ChatTurn { Role = "assistant", Text = answer });
                while (_history.Count > 20) _history.RemoveAt(0);
            }
            catch (Exception ex)
            {
                reply.Text = "Ошибка: " + ex.Message;
                _history.RemoveAt(_history.Count - 1); // не оставляем вопрос без ответа
            }
            finally { _busy = false; ScrollDown(); }
        }

        // ───────────── Картинки ─────────────

        void OnDragFiles(object s, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        void Attach_Click(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выбери картинку",
                Multiselect = true,
                Filter = "Картинки|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|Все файлы|*.*"
            };
            PickerOpen = true;
            try { if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddFiles(dlg.FileNames); }
            finally
            {
                Dispatcher.BeginInvoke(new Action(() => PickerOpen = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                FocusInput();
            }
        }

        public void AddFiles(IEnumerable<string> paths)
        {
            int bad = 0;
            foreach (var p in paths)
            {
                if (_pending.Count >= MaxImages) { Note($"Максимум {MaxImages} картинки за раз."); break; }
                if (!File.Exists(p)) continue;
                try { _pending.Add(ChatImage.FromFile(p)); }
                catch { bad++; }
            }
            if (bad > 0) Note("Не удалось открыть " + (bad == 1 ? "файл" : bad + " файла") + " как картинку (поддерживаются jpg, png, bmp, gif, tif, webp).");
            FocusInput();
        }

        void AddImage(ChatImage img)
        {
            if (_pending.Count >= MaxImages) { Note($"Максимум {MaxImages} картинки за раз."); return; }
            _pending.Add(img);
            FocusInput();
        }

        bool TryPasteImage()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().Where(ChatImage.IsImageFile).ToList();
                    if (files.Count > 0) { AddFiles(files); return true; }
                }
                if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
                {
                    var src = Clipboard.GetImage();
                    if (src != null) { AddImage(ChatImage.FromSource(src)); return true; }
                }
            }
            catch { /* буфер занят другим приложением — пусть вставится как обычно */ }
            return false;
        }

        void RemoveAttach_Click(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.Tag is ChatImage im) _pending.Remove(im);
            FocusInput();
        }

        void Note(string t) { _items.Add(ChatMsg.Note(t)); ScrollDown(); }

        // «Спроси про экран»: снимок и вопрос сразу уходят в чат. Недописанный текст и приложенные картинки остаются на месте.
        public System.Threading.Tasks.Task AskAboutImageAsync(ChatImage img, string question)
        {
            if (_busy)
            {
                Note("Подожди, ИИ ещё отвечает на предыдущий вопрос.");
                return System.Threading.Tasks.Task.CompletedTask;
            }
            var draft = Input.Text;
            var kept = _pending.ToList();
            _pending.Clear();
            _pending.Add(img);
            Input.Text = question;
            var task = SendAsync();            // всё до первого await (забрать текст и картинку, очистить поле) выполняется сразу
            Input.Text = draft;
            foreach (var k in kept) if (_pending.Count < MaxImages) _pending.Add(k);
            FocusInput();
            return task;
        }

        // Пара «вопрос — ответ» из умного буфера: дописываем в диалог, чтобы можно было продолжить разговор.
        public void AddExchange(string question, string answer)
        {
            _items.Add(ChatMsg.User(question));
            _items.Add(ChatMsg.Claude(answer));
            _history.Add(new ChatTurn { Role = "user", Text = question });
            _history.Add(new ChatTurn { Role = "assistant", Text = answer });
            while (_history.Count > 20) _history.RemoveAt(0);
            ScrollDown();
        }

        void HandleKey(string arg)
        {
            var parts = arg.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var p = _prov;
            string key;
            if (parts.Length == 2 && ChatClient.Find(parts[0]) != null) { p = ChatClient.Find(parts[0]); key = parts[1]; }
            else if (parts.Length == 0)
            {
                KeySetupWindow.Open();   // /key без аргументов — окно настройки ключа
                return;
            }
            else if (parts.Length == 1 && ChatClient.Find(parts[0]) != null)
            {
                _items.Add(ChatMsg.Note($"Формат:  /key {p.Id} {p.KeyHint}"));
                return;
            }
            else key = arg;

            try
            {
                KeyStore.Set(p.Id, key);
                ChatClient.ForgetModels(p.Id);
                _items.Add(ChatMsg.Note($"Ключ для {p.Name} сохранён (зашифрован для твоей учётки Windows)."));
                _ = PrefetchAsync(p);
            }
            catch (Exception ex) { _items.Add(ChatMsg.Note("Не удалось сохранить ключ: " + ex.Message)); }
        }

        void HandleModel(string arg)
        {
            if (arg.Length == 0)
            {
                _items.Add(ChatMsg.Note($"Сейчас {_prov.Name}: {ChatClient.ModelFor(_prov)}.  Другая:  /model название"));
                return;
            }
            ChatClient.SetModel(_prov, arg);
            RefreshHeader();
            _items.Add(ChatMsg.Note($"{_prov.Name}: модель {arg}"));
            ScrollDown();
        }

        void ScrollDown() => Dispatcher.BeginInvoke(new Action(Scroll.ScrollToEnd), System.Windows.Threading.DispatcherPriority.Background);
    }
}
