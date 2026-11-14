using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Island
{
    // Какие плитки показывать во вкладке «Управление». Настраивается в ⚙ → «Плитки в «Управлении»»
    // (`HiddenTiles` в settings.json). При самом первом запуске ненастроенные VPN и лента скрываются сами,
    // чтобы у нового человека не висели плитки «Не настроен» и «Не выбрана».
    public partial class MainWindow
    {
        static readonly string[][] TileDefs =
        {
            new[] { "wifi", "📶 Wi-Fi" },
            new[] { "bt", "🔷 Bluetooth" },
            new[] { "theme", "🌓 Тёмная тема" },
            new[] { "vpn", "🛡 VPN" },
            new[] { "led", "💡 Лента" },
        };

        readonly Dictionary<string, Button> _tileToggles = new Dictionary<string, Button>();

        Tile TileById(string id)
        {
            switch (id)
            {
                case "wifi": return _tWifi;
                case "bt": return _tBt;
                case "theme": return _tTheme;
                case "vpn": return _tVpn;
                case "led": return _tLed;
                default: return null;
            }
        }

        void InitTiles()
        {
            var st = Settings.Current;
            if (st.HiddenTiles == null) st.HiddenTiles = new List<string>();

            if (!st.TilesInit)
            {
                st.TilesInit = true;
                if (st.LedAddress == 0 && !st.HiddenTiles.Contains("led")) st.HiddenTiles.Add("led");
                if (!VpnControl.HasCommands && VpnControl.Name == null && !st.HiddenTiles.Contains("vpn")) st.HiddenTiles.Add("vpn");
                st.Save();
            }

            foreach (var d in TileDefs)
            {
                var id = d[0];
                var b = Chip("", () => ToggleTile(id));
                b.Margin = new Thickness(0, 0, 8, 8);
                b.Padding = new Thickness(10, 5, 10, 5);
                _tileToggles[id] = b;
                TilePicker.Children.Add(b);
            }
            ApplyTiles();
        }

        void ApplyTiles()
        {
            var hidden = Settings.Current.HiddenTiles;
            foreach (var d in TileDefs)
            {
                bool shown = !hidden.Contains(d[0]);
                var t = TileById(d[0]);
                if (t != null) t.Box.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                if (_tileToggles.TryGetValue(d[0], out var b)) b.Content = d[1] + ": " + (shown ? "показана" : "скрыта");
            }
        }

        void ToggleTile(string id)
        {
            var hidden = Settings.Current.HiddenTiles;
            if (hidden.Contains(id)) hidden.Remove(id); else hidden.Add(id);
            Settings.Current.Save();
            ApplyTiles();
        }

        void ShowTile(string id)
        {
            if (Settings.Current.HiddenTiles.Remove(id))
            {
                Settings.Current.Save();
                ApplyTiles();
            }
        }
    }
}
