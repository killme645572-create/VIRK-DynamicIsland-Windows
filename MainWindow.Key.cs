using System.Linq;
using System.Windows;

namespace Island
{
    // Окно «Подключи ИИ»: само при первом запуске и по кнопке в ⚙.
    public partial class MainWindow
    {
        void KeySetup_Click(object s, RoutedEventArgs e) => KeySetupWindow.Open();

        void FirstRunKeySetup()
        {
            var st = Settings.Current;
            if (st.KeySetupShown) return;
            st.KeySetupShown = true;
            st.Save();
            // если ключ уже есть (файл или переменная окружения) — окно не нужно
            if (ChatClient.Providers.Any(p => KeyStore.Has(p))) return;
            KeySetupWindow.Open();
        }
    }
}
