using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Island
{
    class LedDevice
    {
        public ulong Address;
        public string Name;
        public bool Likely;   // имя похоже на ленту ELK-BLEDOM и родственные контроллеры
    }

    // Bluetooth-лента на контроллере ELK-BLEDOM (приложение Lotus Lantern и клон-контроллеры).
    // Команда — 9 байт «7E … EF», пишется в характеристику FFF3 (у части контроллеров — FFE9).
    static class LedControl
    {
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        static BluetoothLEDevice _dev;
        static GattSession _session;
        static IReadOnlyList<GattDeviceService> _services;
        static GattCharacteristic _ch;
        static ulong _addr;

        // ───────────── Поиск устройств ─────────────

        public static async Task<List<LedDevice>> ScanAsync(int ms = 5000)
        {
            var found = new Dictionary<ulong, string>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (s, e) =>
            {
                var name = e.Advertisement.LocalName;
                if (string.IsNullOrWhiteSpace(name)) return;
                lock (found) found[e.BluetoothAddress] = name;   // имя часто приходит отдельным пакетом, поэтому копим по адресу
            };
            watcher.Start();
            await Task.Delay(ms);
            watcher.Stop();

            lock (found)
                return found
                    .Select(kv => new LedDevice { Address = kv.Key, Name = kv.Value, Likely = LooksLikeStrip(kv.Value) })
                    .OrderByDescending(d => d.Likely).ThenBy(d => d.Name)
                    .ToList();
        }

        static bool LooksLikeStrip(string name)
        {
            var n = name.ToUpperInvariant();
            return n.StartsWith("ELK") || n.StartsWith("MELK") || n.Contains("BLEDOM") || n.StartsWith("LEDBLE") ||
                   n.StartsWith("TRIONES") || n.StartsWith("QHM") || n.StartsWith("LED");
        }

        // ───────────── Команды ─────────────

        static byte[] ColorCmd(byte r, byte g, byte b) => new byte[] { 0x7E, 0x07, 0x05, 0x03, r, g, b, 0x10, 0xEF };

        // У контроллеров разных партий команды питания отличаются, поэтому шлём обе известные формы; лишняя игнорируется.
        static readonly byte[] On1 = { 0x7E, 0x04, 0x04, 0xF0, 0x00, 0x01, 0xFF, 0x00, 0xEF };
        static readonly byte[] On2 = { 0x7E, 0x07, 0x04, 0xFF, 0x00, 0x01, 0x02, 0x01, 0xEF };
        static readonly byte[] Off1 = { 0x7E, 0x04, 0x04, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xEF };
        static readonly byte[] Off2 = { 0x7E, 0x07, 0x04, 0x00, 0x00, 0x00, 0x02, 0x01, 0xEF };

        // У контроллера есть собственная «аппаратная» яркость, не зависящая от цвета: она хранится в самой ленте
        // и могла остаться тусклой от приложения на телефоне. Поэтому после (пере)подключения и при включении
        // явно ставим её на максимум (LedHwBrightness), а ползунок острова дальше только масштабирует цвет.
        // Как и с питанием, шлём обе известные формы команды; лишняя игнорируется.
        static byte HwLevel => (byte)Math.Clamp(Settings.Current.LedHwBrightness, 0, 255);
        static byte[] Bright1(byte v) => new byte[] { 0x7E, 0x04, 0x01, v, 0x01, 0xFF, 0xFF, 0x00, 0xEF };
        static byte[] Bright2(byte v) => new byte[] { 0x7E, 0x04, 0x01, v, 0x01, 0xFF, 0x02, 0x01, 0xEF };
        static bool _hwSent;   // яркость контроллера уже выставлена в этом соединении

        public static Task<bool> SetColorAsync(ulong addr, byte r, byte g, byte b) =>
            SendAsync(addr, true, ColorCmd(r, g, b));

        public static Task<bool> PowerOnAsync(ulong addr, byte r, byte g, byte b)
        {
            _hwSent = false;   // при каждом включении ставим яркость контроллера заново
            return SendAsync(addr, true, On1, On2, ColorCmd(r, g, b));
        }

        // Выключение: команда питания + чёрный цвет (на случай, если контроллер команду питания не понял)
        public static Task<bool> PowerOffAsync(ulong addr) =>
            SendAsync(addr, false, Off1, Off2, ColorCmd(0, 0, 0));

        // Забыть текущее соединение (после смены ленты)
        public static async Task DisconnectAsync()
        {
            await Gate.WaitAsync();
            try { Reset(); }
            finally { Gate.Release(); }
        }

        // ───────────── Соединение ─────────────

        // bright = true: если яркость контроллера в этом соединении ещё не ставили, её команды вставляются перед последним пакетом (цветом)
        static async Task<bool> SendAsync(ulong addr, bool bright, params byte[][] packets)
        {
            await Gate.WaitAsync();
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)   // второй заход — с переподключением
                {
                    if (!await EnsureAsync(addr)) return false;
                    var list = packets.ToList();
                    bool addHw = bright && !_hwSent;
                    if (addHw) list.InsertRange(list.Count - 1, new[] { Bright1(HwLevel), Bright2(HwLevel) });
                    try
                    {
                        var opt = (_ch.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) != 0
                            ? GattWriteOption.WriteWithoutResponse
                            : GattWriteOption.WriteWithResponse;
                        bool ok = true;
                        foreach (var p in list)
                        {
                            var w = new DataWriter();
                            w.WriteBytes(p);
                            var st = await _ch.WriteValueAsync(w.DetachBuffer(), opt);
                            if (st != GattCommunicationStatus.Success) { ok = false; break; }
                            if (list.Count > 1) await Task.Delay(40);
                        }
                        if (ok) { if (addHw) _hwSent = true; return true; }
                    }
                    catch { }
                    Reset();
                }
                return false;
            }
            catch { return false; }
            finally { Gate.Release(); }
        }

        static async Task<bool> EnsureAsync(ulong addr)
        {
            if (_ch != null && _addr == addr && _dev != null && _dev.ConnectionStatus == BluetoothConnectionStatus.Connected)
                return true;
            Reset();

            var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
            if (dev == null) return false;

            var svc = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (svc.Status != GattCommunicationStatus.Success) { dev.Dispose(); return false; }

            GattCharacteristic best = null;
            foreach (var s in svc.Services)
            {
                var chars = await s.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (chars.Status != GattCommunicationStatus.Success) continue;
                foreach (var c in chars.Characteristics)
                {
                    bool writable = (c.CharacteristicProperties &
                        (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) != 0;
                    if (writable && IsKnown(c.Uuid)) { best = c; break; }
                }
                if (best != null) break;
            }
            if (best == null) { dev.Dispose(); return false; }

            _dev = dev;
            _services = svc.Services;   // держим сервисы живыми, иначе характеристика перестаёт работать
            _ch = best;
            _addr = addr;
            try
            {
                _session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
                _session.MaintainConnection = true;
            }
            catch { }
            return true;
        }

        static bool IsKnown(Guid id) =>
            id == BluetoothUuidHelper.FromShortId(0xFFF3) || id == BluetoothUuidHelper.FromShortId(0xFFE9) ||
            id == BluetoothUuidHelper.FromShortId(0xFFD9) || id == BluetoothUuidHelper.FromShortId(0xFFE1);

        static void Reset()
        {
            try { _session?.Dispose(); } catch { }
            try { if (_services != null) foreach (var s in _services) s.Dispose(); } catch { }
            try { _dev?.Dispose(); } catch { }
            _session = null; _services = null; _dev = null; _ch = null; _addr = 0;
            _hwSent = false;
        }
    }
}
