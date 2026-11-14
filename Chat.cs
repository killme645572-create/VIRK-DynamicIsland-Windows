using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island
{
    // Одно сообщение в окне чата. Клиенты провайдеров — в Providers.cs.
    public class ChatMsg : INotifyPropertyChanged
    {
        string _text;
        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextVis)));
            }
        }
        public Visibility TextVis => string.IsNullOrEmpty(_text) ? Visibility.Collapsed : Visibility.Visible;
        public List<BitmapSource> Images { get; set; }   // миниатюры приложенных картинок
        public HorizontalAlignment Align { get; set; }
        public Brush Bg { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;

        public static ChatMsg User(string t, List<BitmapSource> images = null) =>
            new ChatMsg { Text = t, Images = images, Align = HorizontalAlignment.Right, Bg = Brush("#2F4F8F") };
        public static ChatMsg Claude(string t) => new ChatMsg { Text = t, Align = HorizontalAlignment.Left, Bg = Brush("#2A2A2A") };   // ответ модели (имя осталось от старой версии)
        public static ChatMsg Note(string t) => new ChatMsg { Text = t, Align = HorizontalAlignment.Left, Bg = Brushes.Transparent };
        static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
    }
}
