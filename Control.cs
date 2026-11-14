using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace Island
{
    // Включение/выключение Wi-Fi и Bluetooth (как переключатели в шторке Windows).
    static class RadioControl
    {
        static async Task<Radio> GetAsync(RadioKind kind)
        {
            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access != RadioAccessStatus.Allowed) return null;
                var all = await Radio.GetRadiosAsync();
                return all.FirstOrDefault(r => r.Kind == kind);
            }
            catch { return null; }
        }

        // null — адаптера нет или доступ запрещён
        public static async Task<bool?> IsOnAsync(RadioKind kind)
        {
            var r = await GetAsync(kind);
            if (r == null) return null;
            return r.State == RadioState.On;
        }

        public static async Task<bool> SetAsync(RadioKind kind, bool on)
        {
            try
            {
                var r = await GetAsync(kind);
                if (r == null) return false;
                var res = await r.SetStateAsync(on ? RadioState.On : RadioState.Off);
                return res == RadioAccessStatus.Allowed;
            }
            catch { return false; }
        }
    }

    class WifiNet
    {
        public string Ssid;
        public int Bars;          // 0..5
        public bool Secured;
        public bool Connected;
        public WiFiAvailableNetwork Raw;
    }

    // Список сетей и подключение (в том числе по паролю) без захода в параметры Windows.
    static class WifiControl
    {
        static WiFiAdapter _adapter;

        static async Task<WiFiAdapter> AdapterAsync()
        {
            if (_adapter != null) return _adapter;
            try
            {
                var access = await WiFiAdapter.RequestAccessAsync();
                if (access != WiFiAccessStatus.Allowed) return null;
                var list = await WiFiAdapter.FindAllAdaptersAsync();
                _adapter = list.FirstOrDefault();
            }
            catch { }
            return _adapter;
        }

        // null — Wi-Fi недоступен (нет адаптера, выключен или нет доступа)
        public static async Task<List<WifiNet>> ScanAsync()
        {
            var a = await AdapterAsync();
            if (a == null) return null;
            try { await a.ScanAsync(); } catch { }

            string current = null;
            try
            {
                var p = await a.NetworkAdapter.GetConnectedProfileAsync();
                current = p?.ProfileName;
            }
            catch { }

            try
            {
                return a.NetworkReport.AvailableNetworks
                    .Where(n => !string.IsNullOrEmpty(n.Ssid))
                    .GroupBy(n => n.Ssid)
                    .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                    .Select(n => new WifiNet
                    {
                        Ssid = n.Ssid,
                        Bars = n.SignalBars,
                        Secured = n.SecuritySettings.NetworkAuthenticationType != NetworkAuthenticationType.Open80211,
                        Connected = current != null && n.Ssid == current,
                        Raw = n
                    })
                    .OrderByDescending(n => n.Connected)
                    .ThenByDescending(n => n.Bars)
                    .ToList();
            }
            catch { return null; }
        }

        // Без пароля подключаются открытые сети и те, что Windows уже запомнила.
        public static async Task<WiFiConnectionStatus> ConnectAsync(WifiNet n, string password)
        {
            var a = await AdapterAsync();
            if (a == null) return WiFiConnectionStatus.UnspecifiedFailure;
            try
            {
                WiFiConnectionResult r;
                if (string.IsNullOrEmpty(password))
                    r = await a.ConnectAsync(n.Raw, WiFiReconnectionKind.Automatic);
                else
                    r = await a.ConnectAsync(n.Raw, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });
                return r.ConnectionStatus;
            }
            catch { return WiFiConnectionStatus.UnspecifiedFailure; }
        }

        public static void Disconnect()
        {
            try { _adapter?.Disconnect(); } catch { }
        }

        // имя сети, к которой подключён компьютер, или null
        public static string CurrentSsid()
        {
            try
            {
                var p = NetworkInformation.GetInternetConnectionProfile();
                if (p != null && p.IsWlanConnectionProfile) return p.ProfileName;
            }
            catch { }
            return null;
        }
    }

    // VPN: встроенные профили Windows (rasdial) или любой клиент через свои команды в settings.json.
    static class VpnControl
    {
        // ---------- AmneziaWG (форк WireGuard для Windows) ----------

        static string AmneziaDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AmneziaWG");
        static string AmneziaExe => Path.Combine(AmneziaDir, "amneziawg.exe");
        static string WireGuardExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe");

        // Клиент туннелей: AmneziaWG, а если его нет — обычный WireGuard (у них одинаковые команды /installtunnelservice).
        static string TunnelExe => File.Exists(AmneziaExe) ? AmneziaExe : File.Exists(WireGuardExe) ? WireGuardExe : null;
        public static bool HasTunnelClient => TunnelExe != null;
        public static bool UseAmnezia => !HasCommands && TunnelExe != null;

        // Свои конфиги (.conf), добавленные кнопкой «Импорт VPN» (лежат у пользователя, не в Program Files).
        public static string ImportDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "vpn");

        // Копирует выбранный .conf в папку Island и делает его текущим профилем. Возвращает имя профиля.
        public static string ImportConfig(string src)
        {
            if (!File.Exists(src)) throw new Exception("файл не найден");
            if (!src.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) throw new Exception("нужен файл конфигурации с расширением .conf");
            var text = File.ReadAllText(src);
            if (text.IndexOf("[Interface]", StringComparison.OrdinalIgnoreCase) < 0 || text.IndexOf("[Peer]", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("это не конфиг WireGuard/AmneziaWG (нет разделов [Interface] и [Peer])");

            // имя туннеля = имя файла; WireGuard допускает буквы, цифры и _ = + . - (до 32 знаков)
            var raw = Path.GetFileNameWithoutExtension(src);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in raw) sb.Append(char.IsAsciiLetterOrDigit(ch) || "_=+.-".IndexOf(ch) >= 0 ? ch : '_');
            var name = sb.ToString().Trim('.', ' ');
            if (name.Length == 0) name = "vpn";
            if (name.Length > 32) name = name.Substring(0, 32);

            Directory.CreateDirectory(ImportDir);
            File.WriteAllText(Path.Combine(ImportDir, name + ".conf"), text);

            Settings.Current.VpnName = name;
            Settings.Current.Save();
            return name;
        }

        static List<string> AmneziaProfiles()
        {
            var res = new List<string>();
            try
            {
                if (Directory.Exists(ImportDir))
                    foreach (var f in Directory.GetFiles(ImportDir, "*.conf"))
                        res.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            try
            {
                var dir = Path.Combine(AmneziaDir, "Data", "Configurations");
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var n = Path.GetFileName(f);
                        if (n.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase)) res.Add(n.Substring(0, n.Length - 11));
                        else if (n.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) res.Add(n.Substring(0, n.Length - 5));
                    }
            }
            catch { }   // папка с конфигами обычно закрыта для обычного пользователя — тогда имя берём из VpnName
            return res.Distinct().ToList();
        }

        static string AmneziaConf(string name)
        {
            var mine = Path.Combine(ImportDir, name + ".conf");   // сначала свои, импортированные в Island
            if (File.Exists(mine)) return mine;
            var dir = Path.Combine(AmneziaDir, "Data", "Configurations");
            var plain = Path.Combine(dir, name + ".conf");
            return File.Exists(plain) ? plain : Path.Combine(dir, name + ".conf.dpapi");
        }

        // установка/удаление службы туннеля требует прав администратора — Windows покажет запрос UAC
        static Task<bool> RunElevatedAsync(string file, string args) => Task.Run(() =>
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(file, args)
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                });
                if (p == null) return false;
                if (!p.WaitForExit(30000)) return false;
                return p.ExitCode == 0;
            }
            catch { return false; }   // в том числе если отказались в окне UAC
        });

        public static List<string> Profiles()
        {
            if (UseAmnezia) return AmneziaProfiles();
            var res = new List<string>();
            var files = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk")
            };
            foreach (var f in files)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    foreach (var line in File.ReadLines(f))
                    {
                        var t = line.Trim();
                        if (t.Length > 2 && t[0] == '[' && t[t.Length - 1] == ']')
                            res.Add(t.Substring(1, t.Length - 2));
                    }
                }
                catch { }
            }
            return res.Distinct().ToList();
        }

        public static bool HasCommands =>
            !string.IsNullOrWhiteSpace(Settings.Current.VpnConnectCmd);

        // имя профиля Windows (или имя сетевого адаптера стороннего клиента — по нему определяем статус)
        public static string Name => !string.IsNullOrWhiteSpace(Settings.Current.VpnName)
            ? Settings.Current.VpnName
            : Profiles().FirstOrDefault();

        public static bool IsUp(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                    string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    n.OperationalStatus == OperationalStatus.Up);
            }
            catch { return false; }
        }

        static Task<int> RunAsync(string file, string args) => Task.Run(() =>
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
                if (p == null) return -1;
                if (!p.WaitForExit(30000)) return -2;
                return p.ExitCode;
            }
            catch { return -1; }
        });

        public static async Task<bool> ConnectAsync()
        {
            if (HasCommands) return await RunAsync("cmd.exe", "/c " + Settings.Current.VpnConnectCmd) == 0;
            var name = Name;
            if (name == null) return false;
            if (UseAmnezia) return await RunElevatedAsync(TunnelExe, "/installtunnelservice \"" + AmneziaConf(name) + "\"");
            return await RunAsync("rasdial.exe", "\"" + name + "\"") == 0;
        }

        public static async Task<bool> DisconnectAsync()
        {
            if (!string.IsNullOrWhiteSpace(Settings.Current.VpnDisconnectCmd))
                return await RunAsync("cmd.exe", "/c " + Settings.Current.VpnDisconnectCmd) == 0;
            var name = Name;
            if (name == null) return false;
            if (UseAmnezia) return await RunElevatedAsync(TunnelExe, "/uninstalltunnelservice \"" + name + "\"");
            return await RunAsync("rasdial.exe", "\"" + name + "\" /disconnect") == 0;
        }
    }
}
