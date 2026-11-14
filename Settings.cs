using System;
using System.IO;
using System.Text.Json;

namespace Island
{
    // Свой сайт или программа: имя, по которому её можно открыть и закрыть кнопкой или голосом
    public class CustomApp
    {
        public string Name { get; set; }     // как называть: «Моя почта», «Игра»
        public string Target { get; set; }   // адрес сайта (https://…) или путь к программе / файлу / папке
        public string Match { get; set; }    // необязательно: имя процесса (для программы) или слово из заголовка вкладки (для сайта) — по нему закрываем
    }

    // Настройки лежат в %AppData%\Island\settings.json — можно править руками.
    public class Settings
    {
        public bool ShowWeather { get; set; } = true;
        public int ClaudePort { get; set; } = 47831;   // порт, на который хуки Claude Code шлют статус
        public bool Liquid { get; set; } = true;       // «жидкий» край острова
        public bool Lyrics { get; set; } = true;       // слова песни под названием трека (LRCLIB)
        public bool EditShots { get; set; } = true;    // после выделения области открывать редактор (стрелки, рамки, рисование)
        public string RecAudio { get; set; } = "system";   // звук записи: none | system | mic | both
        public bool Voice { get; set; } = true;            // голосовой помощник: Ctrl+Alt+V и кнопка «🎙 Голос»
        public bool VoiceWake { get; set; } = true;        // слово активации «привет остров» (офлайн-распознавание Windows)
        public string VoiceReply { get; set; } = "both";   // как отвечает помощник: text | voice | both
        public string AssistantName { get; set; } = "Вирк";   // имя помощника: на него откликается слово активации («привет, Вирк»), так он называет себя в ответах
        public System.Collections.Generic.List<string> WakeAliases { get; set; } = new System.Collections.Generic.List<string> { "остров" };   // другие имена, на которые тоже откликаться (пустой список — только AssistantName)
        public int WakeSensitivity { get; set; } = 1;      // «привет остров»: 0 строгая | 1 обычная | 2 высокая (ловит тише и неразборчивее, чуть больше ложных срабатываний)
        public string WakeEngine { get; set; } = "auto";   // «привет остров»: auto — речевой пакет Windows (офлайн), groq — Whisper на Groq (точнее, но короткие фразы уходят в интернет)
        public string VoiceLang { get; set; } = "ru";      // язык речи для Whisper (пусто — определять самому)
        public string VoiceName { get; set; }              // часть названия голоса Windows для озвучки (пусто — первый русский)
        public string VoiceMicDevice { get; set; }         // часть названия микрофона для голосового помощника и слова «привет» (пусто — микрофон по умолчанию)
        public bool UpdateCheck { get; set; } = true;      // раз в несколько часов смотреть на GitHub, нет ли новой версии
        public string UpdateRepo { get; set; }             // репозиторий GitHub в виде «владелец/репозиторий» (если пусто — Updater.DefaultRepo)
        public string UpdateNotified { get; set; }         // версия, о которой уже сообщили, чтобы не повторяться
        public string RecMicDevice { get; set; }           // часть названия микрофона для записи (если пусто — микрофон по умолчанию)
        public string RecEncoder { get; set; } = "h264_nvenc";   // видеокодер ffmpeg; если не запустится — libx264
        public int RecAudioOffsetMs { get; set; } = 0;     // подстройка синхронизации звука (+ задержать, − опередить)
        public string FfmpegPath { get; set; }             // путь к ffmpeg.exe, если он не рядом с островом и не в PATH
        public bool Hotkey { get; set; } = true;       // Alt+Space открывает и закрывает остров
        public bool Character { get; set; } = true;    // персонаж в пилюле
        public bool AskHotkey { get; set; } = true;    // Ctrl+Alt+Space: «Спроси про экран» (выделил область, снимок улетел в чат)
        public string AskPrompt { get; set; } = "Это снимок части моего экрана. Объясни, что на нём. Если это ошибка — скажи, в чём причина и как исправить; если график — опиши, что он показывает; если текст на иностранном языке — переведи его на русский.";
        public bool Equalizer { get; set; } = true;    // столбики-эквалайзер в пилюле, пока играет музыка
        public bool SmartClip { get; set; } = true;    // кнопки «Объяснить / Перевести / Исправить / Коротко» после копирования текста
        public string TranslateTo { get; set; } = "русский";   // на какой язык переводит «Перевести» (русский текст переводится на английский)
        public int PomoFocus { get; set; } = 25;       // помодоро: минут фокуса
        public int PomoBreak { get; set; } = 5;        // короткий перерыв
        public int PomoLong { get; set; } = 15;        // большой перерыв после последней сессии
        public int PomoRounds { get; set; } = 4;       // сколько фокус-сессий в цикле
        public string CharacterId { get; set; } = "classic";   // какой из 25 персонажей (список — в Characters.cs)
        public string VpnName { get; set; }            // профиль VPN Windows (или имя адаптера стороннего клиента)
        public string VpnConnectCmd { get; set; }      // команда подключения для стороннего VPN-клиента (необязательно)
        public string VpnDisconnectCmd { get; set; }   // команда отключения (необязательно)
        public System.Collections.Generic.List<string> HiddenTiles { get; set; } = new System.Collections.Generic.List<string>();   // скрытые плитки «Управления»: wifi | bt | theme | vpn | led
        public bool TilesInit { get; set; }            // первичная раскладка плиток уже сделана (ненастроенные VPN и лента скрываются один раз)
        public bool KeySetupShown { get; set; }        // окно «Подключи ИИ» уже показывали при первом запуске
        public string ChatProvider { get; set; } = "openrouter";   // выбранный провайдер чата: openrouter | groq | ollama
        public System.Collections.Generic.Dictionary<string, string> ChatModels { get; set; } = new System.Collections.Generic.Dictionary<string, string>();   // выбранная модель по каждому провайдеру
        public string OllamaHost { get; set; } = "https://ollama.com";   // облако Ollama; ключ — командой /key ollama ...
        public ulong LedAddress { get; set; }          // Bluetooth-адрес RGB-ленты (выбирается в Управление → Лента)
        public string LedName { get; set; }
        public string LedColor { get; set; } = "#FF2DAA";   // последний цвет ленты
        public int LedBrightness { get; set; } = 100;       // яркость ленты, 5..100 (ползунок: масштабирует цвет)
        public int LedHwBrightness { get; set; } = 100;     // яркость самого контроллера ленты, 0..100; если на максимуме лента светит тускло, попробуй 255
        public string IslandColor { get; set; } = "#000000";   // цвет острова
        public System.Collections.Generic.List<CustomApp> CustomApps { get; set; } = new System.Collections.Generic.List<CustomApp>();   // свои сайты и программы (вкладка 🚀)
        public double? Lat { get; set; }   // если пусто — определяется по IP один раз и запоминается
        public double? Lon { get; set; }

        static readonly string FilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "settings.json");

        public static Settings Current { get; } = Load();

        static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
