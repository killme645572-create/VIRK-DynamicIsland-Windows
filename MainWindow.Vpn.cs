using System;
using System.Diagnostics;
using System.Windows;

namespace Island
{
    // Настройка VPN на новом компьютере: поставить клиент (AmneziaWG или WireGuard) и выбрать свой .conf.
    // Конфиг копируется в %AppData%\Island\vpn и дальше используется плиткой VPN.
    public partial class MainWindow
    {
        const string VpnClientPage = "https://github.com/amnezia-vpn/amneziawg-windows-client";

        void VpnImport_Click(object s, RoutedEventArgs e) => ImportVpn();

        void ImportVpn()
        {
            if (!VpnControl.HasTunnelClient)
            {
                var ask = System.Windows.MessageBox.Show(this,
                    "Чтобы кнопка VPN работала, на компьютере должен быть клиент AmneziaWG (или обычный WireGuard). " +
                    "Его нужно установить один раз.\n\nОткрыть страницу загрузки AmneziaWG? После установки нажми кнопку VPN ещё раз и выбери свой файл .conf.",
                    "Island — VPN", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ask == MessageBoxResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo(VpnClientPage) { UseShellExecute = true }); } catch { }
                }
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выбери свой файл конфигурации VPN (.conf)",
                Filter = "Конфиг VPN (*.conf)|*.conf|Все файлы|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var name = VpnControl.ImportConfig(dlg.FileName);
                System.Windows.MessageBox.Show(this,
                    $"Профиль «{name}» добавлен. Теперь плитка VPN во вкладке «Управление» включает и выключает его.\n" +
                    "При каждом переключении Windows спросит разрешение (UAC), это нормально.",
                    "Island — VPN", MessageBoxButton.OK, MessageBoxImage.Information);
                ShowTile("vpn");
                _ = RefreshControlAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, "Не удалось добавить профиль: " + ex.Message,
                    "Island — VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
