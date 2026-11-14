using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Island
{
    // Вкладки «Погода» и «Настройки».
    public partial class MainWindow
    {
        WeatherInfo _weather;
        DateTime _weatherAt = DateTime.MinValue;
        bool _settingsBuilt;

        static readonly string[] IslandColors = { "#000000", "#1C1C1E", "#0B1F44", "#2B1055", "#0E3B2E", "#4A0E1C" };

        static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        // ---------- погода ----------

        void UpdateWeatherView()
        {
            var w = _weather;
            if (w == null)
            {
                WxBody.Visibility = Visibility.Collapsed;
                WxMsg.Visibility = Visibility.Visible;
                WxMsg.Text = Settings.Current.ShowWeather ? "Нет данных о погоде" : "Погода выключена (вкладка ⚙)";
                return;
            }
            WxBody.Visibility = Visibility.Visible;
            WxMsg.Visibility = Visibility.Collapsed;

            WxTemp.Text = w.Icon + " " + (int)Math.Round(w.Temp) + "°";
            WxDesc.Text = w.Desc;
            WxFeels.Text = "Ощущается как " + (int)Math.Round(w.Feels) + "°";
            WxWind.Text = Math.Round(w.Wind) + " м/с";
            WxRot.Angle = w.WindDir + 180;   // стрелка показывает, куда дует ветер
            WxHum.Text = w.Humidity + "%";
            WxPres.Text = w.Pressure + " мм";

            WxHours.Children.Clear();
            foreach (var h in w.Hours)
            {
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                col.Children.Add(new TextBlock { Text = h.Time, Foreground = Hex("#99FFFFFF"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
                col.Children.Add(new TextBlock { Text = h.Icon, Foreground = Brushes.White, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
                col.Children.Add(new TextBlock { Text = (int)Math.Round(h.Temp) + "°", Foreground = Brushes.White, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
                WxHours.Children.Add(col);
            }
        }

        // ---------- настройки ----------

        void ApplyIslandColor(string hex, bool save)
        {
            try { Pill.Background = Hex(hex); }
            catch { Pill.Background = Brushes.Black; hex = "#000000"; }
            Skirt.Fill = Pill.Background;   // «жидкий» край того же цвета
            if (save)
            {
                Settings.Current.IslandColor = hex;
                Settings.Current.Save();
            }
        }

        void BuildSettings()
        {
            if (!_settingsBuilt)
            {
                _settingsBuilt = true;
                foreach (var hex in IslandColors)
                {
                    var dot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0),
                        Fill = Hex(hex), Cursor = Cursors.Hand, Tag = hex, StrokeThickness = 2
                    };
                    dot.MouseLeftButtonDown += (s, e) =>
                    {
                        ApplyIslandColor((string)dot.Tag, true);
                        MarkSwatches();
                    };
                    Swatches.Children.Add(dot);
                }
            }
            MarkSwatches();
            if (CharPicker.Children.Count == 0) BuildCharPicker(); else MarkChars();
            UpdateToggleLabels();
        }

        // сетка из 25 персонажей: плитка с живым превью, клик — выбрать
        void BuildCharPicker()
        {
            foreach (var spec in Characters.All)
            {
                var rig = new CharacterRig(spec);
                rig.Update(Mood.Normal, 0, 0, false, 0, true);
                rig.Root.HorizontalAlignment = HorizontalAlignment.Center;
                rig.Root.VerticalAlignment = VerticalAlignment.Center;
                var tile = new System.Windows.Controls.Border
                {
                    Width = 50, Height = 30, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 6, 6),
                    Background = Hex("#1AFFFFFF"), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent,
                    Cursor = Cursors.Hand, Tag = spec.Id, ToolTip = spec.Name, Child = rig.Root
                };
                tile.MouseLeftButtonDown += (s, e) =>
                {
                    Settings.Current.CharacterId = (string)tile.Tag;
                    Settings.Current.Save();
                    ApplyFxSettings();
                    MarkChars();
                };
                CharPicker.Children.Add(tile);
            }
            MarkChars();
        }

        void MarkChars()
        {
            string cur = Characters.Get(Settings.Current.CharacterId).Id;
            foreach (var child in CharPicker.Children)
                if (child is System.Windows.Controls.Border b)
                    b.BorderBrush = (string)b.Tag == cur ? Brushes.White : Brushes.Transparent;
        }

        static string OnOff(bool v) => v ? "вкл" : "выкл";

        void UpdateToggleLabels()
        {
            WeatherToggle.Content = "🌤 Погода в пилюле: " + OnOff(Settings.Current.ShowWeather);
            LiquidToggle.Content = "💧 Жидкая форма: " + OnOff(Settings.Current.Liquid);
            CharToggle.Content = "👀 Персонаж: " + OnOff(Settings.Current.Character);
            EqToggle.Content = "🎚 Эквалайзер: " + OnOff(Settings.Current.Equalizer);
            SmartToggle.Content = "✨ Умный буфер: " + OnOff(Settings.Current.SmartClip);
            VoiceToggle.Content = "🎙 Голосовой помощник: " + OnOff(Settings.Current.Voice);
            VoiceWakeToggle.Content = "🗣 «Привет, " + Settings.Current.AssistantName + "»: " + OnOff(Settings.Current.VoiceWake);
            WakeSensBtn.Content = "🎚 Чувствительность «привет»: " + (Settings.Current.WakeSensitivity <= 0 ? "строгая" : Settings.Current.WakeSensitivity == 1 ? "обычная" : "высокая");
            WakeEngineBtn.Content = "🧠 Слух «привет»: " + (string.Equals(Settings.Current.WakeEngine, "groq", StringComparison.OrdinalIgnoreCase) ? "Groq (точнее)" : "Windows (офлайн)");
            VoiceReplyBtn.Content = VoiceReplyLabel();
            VoiceMicBtn.Content = VoiceMicLabel();
            VoiceMicBtn.ToolTip = "Микрофон для голосового помощника и слова активации. Слово «привет» в режиме Windows (офлайн) всегда слушает микрофон Windows по умолчанию — выбранный работает в режиме Groq.";
            UpdateUpdateLabel();
        }

        void LiquidToggle_Click(object s, RoutedEventArgs e)
        {
            Settings.Current.Liquid = !Settings.Current.Liquid;
            Settings.Current.Save();
            UpdateToggleLabels();
            ApplyFxSettings();
        }

        void CharToggle_Click(object s, RoutedEventArgs e)
        {
            Settings.Current.Character = !Settings.Current.Character;
            Settings.Current.Save();
            UpdateToggleLabels();
            ApplyFxSettings();
        }

        // выбранный цвет обводим белым, остальные — бледной рамкой
        void MarkSwatches()
        {
            foreach (var child in Swatches.Children)
            {
                if (!(child is System.Windows.Shapes.Ellipse dot)) continue;
                bool on = string.Equals((string)dot.Tag, Settings.Current.IslandColor, StringComparison.OrdinalIgnoreCase);
                dot.Stroke = on ? Brushes.White : Hex("#44FFFFFF");
            }
        }

        void WeatherToggle_Click(object s, RoutedEventArgs e)
        {
            Settings.Current.ShowWeather = !Settings.Current.ShowWeather;
            Settings.Current.Save();
            UpdateToggleLabels();
            _ = RefreshWeatherAsync();   // при выключении вернёт null и уберёт погоду из пилюли
        }
    }
}
