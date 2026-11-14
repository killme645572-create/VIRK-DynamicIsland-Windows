using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Island
{
    // Окно «Подключи ИИ»: выбрать провайдера, открыть страницу с ключом, вставить ключ, проверить и сохранить.
    // Открывается само при первом запуске (если нет ни одного ключа), командой /key без аргументов и кнопкой в ⚙.
    class KeySetupWindow : Window
    {
        static KeySetupWindow _open;

        // Срабатывает после успешного сохранения ключа (чат обновляет заголовок и список моделей).
        public static event Action KeySaved;

        public static void Open()
        {
            if (_open != null)
            {
                _open.Activate();
                return;
            }
            _open = new KeySetupWindow();
            _open.Show();
            _open.Activate();
        }

        readonly RadioButton[] _radios;
        readonly TextBlock _hint;
        readonly TextBlock _status;
        readonly PasswordBox _box;
        readonly Button _save;

        static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
        static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(120, 220, 140));
        static readonly Brush Err = new SolidColorBrush(Color.FromRgb(255, 130, 130));

        KeySetupWindow()
        {
            Title = "Island — ключ для ИИ";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 26));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            var stack = new StackPanel { Margin = new Thickness(22) };

            stack.Children.Add(new TextBlock { Text = "Подключи ИИ-чат", FontSize = 20, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(new TextBlock
            {
                Text = "Чат, «Спроси про экран» и умный буфер работают через ключ от сервиса ИИ. " +
                       "Ключ бесплатный, получить его можно за пару минут. Он хранится только на этом компьютере в зашифрованном виде.",
                TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 13, Margin = new Thickness(0, 8, 0, 14)
            });

            stack.Children.Add(new TextBlock { Text = "Где взять ключ", FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 0, 0, 6) });

            var current = ChatClient.Current;
            _radios = ChatClient.Providers.Select(p => new RadioButton
            {
                Content = Label(p),
                Tag = p,
                GroupName = "prov",
                Foreground = Brushes.White,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 5),
                IsChecked = p == current
            }).ToArray();
            foreach (var r in _radios)
            {
                r.Checked += (s, e) => UpdateHint();
                stack.Children.Add(r);
            }

            var open = new Button
            {
                Content = "1.  Открыть страницу ключа в браузере",
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            open.Click += (s, e) => OpenKeyPage();
            stack.Children.Add(open);

            _hint = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) };
            stack.Children.Add(_hint);

            stack.Children.Add(new TextBlock { Text = "2.  Вставь ключ сюда", FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 0, 0, 4) });
            _box = new PasswordBox { Padding = new Thickness(8, 7, 8, 7), FontSize = 13 };
            _box.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) _ = SaveAsync(); };
            stack.Children.Add(_box);

            _status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MinHeight = 32 };
            stack.Children.Add(_status);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var later = new Button { Content = "Позже", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0) };
            later.Click += (s, e) => Close();
            _save = new Button { Content = "Проверить и сохранить", Padding = new Thickness(16, 7, 16, 7), IsDefault = false };
            _save.Click += (s, e) => _ = SaveAsync();
            row.Children.Add(later);
            row.Children.Add(_save);
            stack.Children.Add(row);

            Content = stack;
            UpdateHint();

            Closed += (s, e) => _open = null;
            Loaded += (s, e) => _box.Focus();
        }

        static string Label(ProviderInfo p)
        {
            switch (p.Id)
            {
                case "groq": return "Groq: быстро и бесплатно (рекомендую)";
                case "openrouter": return "OpenRouter: бесплатные модели от разных авторов";
                case "ollama": return "Ollama Cloud: модели в облаке ollama.com";
                default: return p.Name;
            }
        }

        ProviderInfo Selected => (ProviderInfo)(_radios.FirstOrDefault(r => r.IsChecked == true)?.Tag ?? ChatClient.Providers[0]);

        void UpdateHint()
        {
            if (_hint == null) return;
            var p = Selected;
            _hint.Text = $"Страница: {p.KeyUrl}. Зарегистрируйся, нажми «Create API key» (или похожую кнопку) и скопируй ключ, он выглядит как {p.KeyHint}";
        }

        void OpenKeyPage()
        {
            try { Process.Start(new ProcessStartInfo("https://" + Selected.KeyUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Say("Не удалось открыть браузер: " + ex.Message + "  Адрес: " + Selected.KeyUrl, Err); }
        }

        void Say(string text, Brush color)
        {
            _status.Text = text;
            _status.Foreground = color;
        }

        async Task SaveAsync()
        {
            var p = Selected;
            var key = (_box.Password ?? "").Trim().Trim('"', '\'');
            if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = key.Substring(7).Trim();
            if (key.Length == 0) { Say("Вставь ключ в поле выше.", Err); return; }

            _save.IsEnabled = false;
            Say("Проверяю ключ…", Dim);

            var (state, msg) = await ChatClient.CheckKeyAsync(p, key);
            if (state == KeyCheck.Bad)
            {
                Say("Сервис не принял этот ключ. Проверь, что выбран тот провайдер, с чьей страницы взят ключ, и что он скопирован целиком.", Err);
                _save.IsEnabled = true;
                return;
            }

            try
            {
                KeyStore.Set(p.Id, key);
                ChatClient.ForgetModels(p.Id);
                ChatClient.SetProvider(p);
            }
            catch (Exception ex)
            {
                Say("Не удалось сохранить ключ: " + ex.Message, Err);
                _save.IsEnabled = true;
                return;
            }

            KeySaved?.Invoke();
            bool verified = state == KeyCheck.Ok;
            Say(verified ? "✓ Готово. Ключ принят и сохранён, можно открывать чат." : "Ключ сохранён, но проверить его не удалось (" + msg + "). Если чат ответит ошибкой, нажми /key в чате и введи ключ заново.",
                verified ? Ok : Dim);
            await Task.Delay(verified ? 1200 : 3500);
            Close();
        }
    }
}
