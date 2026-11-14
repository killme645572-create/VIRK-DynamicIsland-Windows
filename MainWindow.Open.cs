using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Island
{
    // Голосовые команды «открой …»: сайты, приложения, папки и поиск в ютубе / гугле / яндексе.
    // «открой ютуб», «открой телеграм», «открой калькулятор», «открой загрузки», «открой youtube.com»,
    // «найди в ютубе котиков», «загугли погоду в Москве».
    public partial class MainWindow
    {
        sealed class OpenItem
        {
            public readonly string Rx, Name, Target, App;
            public readonly string[] Procs;   // имена процессов (без .exe) — чтобы уметь «закрой …»
            public readonly string Titles;    // регулярка по заголовку вкладки браузера (для сайтов); если пусто — берём из адреса
            // App — часть названия ярлыка в меню «Пуск»: если программа установлена, открываем её, иначе — Target
            public OpenItem(string rx, string name, string target, string app = null, string procs = null, string titles = null)
            {
                Rx = rx; Name = name; Target = target; App = app; Titles = titles;
                Procs = string.IsNullOrEmpty(procs) ? null : procs.Split('|');
            }
        }

        // порядок важен: сначала более конкретные
        static readonly OpenItem[] OpenSites =
        {
            new OpenItem(@"гугл карты|google maps|^карты$", "Google Карты", "https://maps.google.com", null, null, "maps|карты"),
            new OpenItem(@"переводчик\w*|google translate|^translate$", "переводчик", "https://translate.google.com", null, null, "translate|переводчик"),
            new OpenItem(@"^почт\w*$|gmail|джимейл|гмейл|гугл почт\w*", "почту", "https://mail.google.com", null, null, "gmail|почта|inbox|входящие"),
            new OpenItem(@"ютуб\w*|ютьюб\w*|ютюб\w*|youtube|you tube", "YouTube", "https://www.youtube.com"),
            new OpenItem(@"^(гугл|google)$", "Google", "https://www.google.com", null, null, "google"),
            new OpenItem(@"яндекс\w*|yandex", "Яндекс", "https://yandex.ru"),
            new OpenItem(@"вконтакте|^вк$|\bvk\b", "ВКонтакте", "https://vk.com", null, null, "vk|вконтакте"),
            new OpenItem(@"телеграм\w*|телега|telegram", "Telegram", "https://web.telegram.org", "Telegram", "Telegram", "telegram"),
            new OpenItem(@"ватсап\w*|вацап\w*|whatsapp", "WhatsApp", "https://web.whatsapp.com", "WhatsApp", "WhatsApp", "whatsapp"),
            new OpenItem(@"дискорд\w*|discord", "Discord", "https://discord.com/app", "Discord", "Discord", "discord"),
            new OpenItem(@"спотифай|spotify", "Spotify", "https://open.spotify.com", "Spotify", "Spotify", "spotify"),
            new OpenItem(@"стим|steam", "Steam", "https://store.steampowered.com", "Steam", "steam|steamwebhelper", "steam"),
            new OpenItem(@"чат ?gpt|чат ?джи ?пи ?ти|chatgpt", "ChatGPT", "https://chatgpt.com"),
            new OpenItem(@"клод\w*|claude", "Claude", "https://claude.ai"),
            new OpenItem(@"гитхаб|гит хаб|github", "GitHub", "https://github.com"),
            new OpenItem(@"твиттер\w*|twitter", "X", "https://x.com", null, null, " / X|twitter"),
            new OpenItem(@"инстаграм\w*|instagram", "Instagram", "https://www.instagram.com"),
            new OpenItem(@"фейсбук\w*|facebook", "Facebook", "https://www.facebook.com"),
            new OpenItem(@"тикток\w*|тик ток|tiktok", "TikTok", "https://www.tiktok.com"),
            new OpenItem(@"твич\w*|twitch", "Twitch", "https://www.twitch.tv"),
            new OpenItem(@"реддит\w*|reddit", "Reddit", "https://www.reddit.com"),
            new OpenItem(@"нетфликс\w*|netflix", "Netflix", "https://www.netflix.com"),
            new OpenItem(@"кинопоиск\w*", "Кинопоиск", "https://www.kinopoisk.ru"),
            new OpenItem(@"википеди\w*|wikipedia", "Википедию", "https://ru.wikipedia.org", null, null, "википеди|wikipedia"),
            new OpenItem(@"озон|ozon", "Ozon", "https://www.ozon.ru"),
            new OpenItem(@"вайлдберриз|вайлдбериз|wildberries", "Wildberries", "https://www.wildberries.ru"),
            new OpenItem(@"авито|avito", "Авито", "https://www.avito.ru"),
        };

        // системные программы и приложения Windows (их нет в меню «Пуск» как обычных ярлыков)
        static readonly OpenItem[] OpenSystem =
        {
            new OpenItem(@"калькулятор\w*|calculator", "Калькулятор", "calc.exe", null, "CalculatorApp|Calculator|calc"),
            new OpenItem(@"блокнот\w*|notepad", "Блокнот", "notepad.exe", null, "Notepad|notepad"),
            new OpenItem(@"проводник\w*|explorer", "Проводник", "explorer.exe", null, "explorer"),
            new OpenItem(@"диспетчер задач\w*|task manager", "Диспетчер задач", "taskmgr.exe", null, "Taskmgr"),
            new OpenItem(@"диспетчер устройств\w*", "Диспетчер устройств", "devmgmt.msc", null, "mmc"),
            new OpenItem(@"настройк\w*|параметр\w*|^settings$", "Настройки", "ms-settings:", null, "SystemSettings"),
            new OpenItem(@"командн\w* строк\w*|^cmd$|консоль", "Командную строку", "cmd.exe", null, "cmd"),
            new OpenItem(@"терминал\w*|powershell|повершелл", "Терминал", "wt.exe", null, "WindowsTerminal|wt"),
            new OpenItem(@"пейнт|paint", "Paint", "mspaint.exe", null, "mspaint|PaintStudio.View"),
            new OpenItem(@"ножницы|snipping tool", "Ножницы", "snippingtool.exe", null, "SnippingTool|ScreenClippingHost"),
            new OpenItem(@"панел\w* управлени\w*|control panel", "Панель управления", "control.exe"),
            new OpenItem(@"магазин\w*|microsoft store", "Магазин", "ms-windows-store:", null, "WinStore.App"),
            new OpenItem(@"камер\w*", "Камеру", "microsoft.windows.camera:", null, "WindowsCamera"),
            new OpenItem(@"будильник\w*|^часы$", "Часы", "ms-clock:", null, "Time"),
            new OpenItem(@"календар\w*", "Календарь", "outlookcal:"),
            new OpenItem(@"браузер\w*", "Браузер", "https://www.google.com", null, "chrome|msedge|firefox|opera|brave|vivaldi|browser"),
        };

        // как называют программу вслух → часть названия ярлыка в меню «Пуск»
        static readonly OpenItem[] OpenAppAliases =
        {
            new OpenItem(@"хром|chrome", "Chrome", null, "Chrome", "chrome"),
            new OpenItem(@"эдж|edge", "Edge", null, "Edge", "msedge"),
            new OpenItem(@"фаерфокс|файрфокс|firefox", "Firefox", null, "Firefox", "firefox"),
            new OpenItem(@"опера|opera", "Opera", null, "Opera", "opera"),
            new OpenItem(@"ворд|word", "Word", null, "Word", "WINWORD"),
            new OpenItem(@"эксель|excel", "Excel", null, "Excel", "EXCEL"),
            new OpenItem(@"пауэр ?поинт|power ?point", "PowerPoint", null, "PowerPoint", "POWERPNT"),
            new OpenItem(@"аутлук|outlook", "Outlook", null, "Outlook", "OUTLOOK"),
            new OpenItem(@"вайбер|viber", "Viber", null, "Viber", "Viber"),
            new OpenItem(@"скайп|skype", "Skype", null, "Skype", "Skype"),
            new OpenItem(@"зум|zoom", "Zoom", null, "Zoom", "Zoom"),
            new OpenItem(@"обс|obs", "OBS", null, "OBS", "obs64|obs32"),
            new OpenItem(@"фотошоп|photoshop", "Photoshop", null, "Photoshop", "Photoshop"),
            new OpenItem(@"вс ?код|vs ?code|визуал студио код", "Visual Studio Code", null, "Visual Studio Code", "Code"),
            new OpenItem(@"визуал студио|visual studio", "Visual Studio", null, "Visual Studio", "devenv"),
        };

        static readonly OpenItem[] OpenFolders =
        {
            new OpenItem(@"загрузк\w*|downloads", "Загрузки", "shell:Downloads"),
            new OpenItem(@"документ\w*", "Документы", "shell:Personal"),
            new OpenItem(@"рабоч\w* стол\w*", "Рабочий стол", "shell:Desktop"),
            new OpenItem(@"картинк\w*|изображени\w*|мои фото|^фотографии$", "Изображения", "shell:My Pictures"),
            new OpenItem(@"^видео$|видеозапис\w*", "Видео", "shell:My Video"),
            new OpenItem(@"корзин\w*", "Корзину", "shell:RecycleBinFolder"),
            new OpenItem(@"этот компьютер|мой компьютер", "Этот компьютер", "shell:MyComputerFolder"),
        };

        static bool OpenMatch(OpenItem it, string target) =>
            Regex.IsMatch(target, @"\b(?:" + it.Rx + @")\b", RegexOptions.CultureInvariant);

        static bool Launch(string file, string args = null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                VoiceLog.Write("не смог открыть " + file + ": " + ex.Message);
                return false;
            }
        }

        // ярлыки из меню «Пуск» (обычные программы; приложения Магазина сюда не попадают)
        static List<KeyValuePair<string, string>> _startApps;

        static List<KeyValuePair<string, string>> StartApps()
        {
            if (_startApps != null) return _startApps;
            var list = new List<KeyValuePair<string, string>>();
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
            {
                try
                {
                    var dir = Path.Combine(root, "Programs");
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                        list.Add(new KeyValuePair<string, string>(Path.GetFileNameWithoutExtension(f), f));
                }
                catch { }
            }
            _startApps = list;
            return list;
        }

        static string FindStartApp(string term)
        {
            term = (term ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
            if (term.Length < 2) return null;
            for (int pass = 0; pass < 2; pass++)
            {
                var apps = StartApps()
                    .Where(a => !Regex.IsMatch(a.Key, @"uninstall|удал|readme|справк|documentation|license", RegexOptions.IgnoreCase))
                    .Select(a => new KeyValuePair<string, string>(a.Key.ToLowerInvariant().Replace('ё', 'е'), a.Value))
                    .ToList();
                var hit = apps.FirstOrDefault(a => a.Key == term);
                if (hit.Value == null) hit = apps.FirstOrDefault(a => a.Key.StartsWith(term));
                if (hit.Value == null) hit = apps.FirstOrDefault(a => a.Key.Contains(term));
                if (hit.Value != null) return hit.Value;
                _startApps = null;   // вдруг программу только что установили
            }
            return null;
        }

        string OpenSearch(string engine, string q)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0) return "Что искать?";
            string url, where;
            if (engine.StartsWith("ют") || engine == "youtube") { url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q); where = "в YouTube"; }
            else if (engine.StartsWith("яндекс") || engine == "yandex") { url = "https://yandex.ru/search/?text=" + Uri.EscapeDataString(q); where = "в Яндексе"; }
            else { url = "https://www.google.com/search?q=" + Uri.EscapeDataString(q); where = "в Google"; }
            return Launch(url) ? "Ищу " + where + ": " + q + "." : "Не получилось открыть браузер.";
        }

        // null — это не команда «открой …», пусть решают другие команды или ИИ
        string TryOpenCommand(string raw, string t)
        {
            // поиск: «найди в ютубе …», «загугли …»
            var sm = Regex.Match(t, @"^(?:пожалуйста\s+)?(?<v>найди|поищи|ищи|загугли|погугли|нагугли)\s+(?:(?:мне|нам)\s+)?(?:в\s+(?<e>ютубе|ютюбе|youtube|гугле|google|яндексе|yandex|интернете|сети)\s+)?(?<q>.+)$");
            if (sm.Success)
            {
                string v = sm.Groups["v"].Value, e = sm.Groups["e"].Value;
                if (e.Length > 0 || v.EndsWith("гугли")) return OpenSearch(e, sm.Groups["q"].Value);
            }

            var m = Regex.Match(t, @"^(?:пожалуйста\s+)?(?<v>открой|открыть|запусти|запустить|зайди на|зайти на|перейди на|перейти на|включи|покажи)\s+(?:(?:мне|нам)\s+)?(?:(?:сайт|приложение|программу|страницу|папку|в браузере)\s+)*(?<x>.+)$");
            if (!m.Success) return null;
            string verb = m.Groups["v"].Value;
            bool strong = verb != "включи" && verb != "покажи";   // «включи/покажи» действуем, только если поняли, что открыть
            string x = m.Groups["x"].Value.Trim();

            // «открой в ютубе котиков»
            var sx = Regex.Match(x, @"^в\s+(?<e>ютубе|ютюбе|youtube|гугле|google|яндексе|yandex)\s+(?<q>.+)$");
            if (sx.Success) return OpenSearch(sx.Groups["e"].Value, sx.Groups["q"].Value);

            // свои сайты и программы (вкладка 🚀) — имеют приоритет над встроенными названиями
            var mine = FindCustomApp(x);
            if (mine != null) return OpenCustomApp(mine);

            // адрес сайта: «открой youtube.com»
            var dm = Regex.Match(raw ?? "", @"\b((?:[a-z0-9][a-z0-9-]*\.)+(?:com|ru|org|net|io|tv|me|app|dev|ai|by|ua|kz|su|info|co|gg|fm|ly|xyz|online|pro))\b(/\S*)?", RegexOptions.IgnoreCase);
            if (dm.Success)
            {
                string host = dm.Groups[1].Value.ToLowerInvariant();
                return Launch("https://" + host + dm.Groups[2].Value.TrimEnd('.', ',', '!', '?'))
                    ? "Открываю " + host + "." : "Не получилось открыть " + host + ".";
            }

            foreach (var f in OpenFolders)
                if (OpenMatch(f, x))
                    return Launch("explorer.exe", f.Target) ? "Открываю " + f.Name + "." : "Не получилось открыть " + f.Name + ".";

            foreach (var s in OpenSites)
            {
                if (!OpenMatch(s, x)) continue;
                if (s.App != null)
                {
                    var p = FindStartApp(s.App);
                    if (p != null && Launch(p)) return "Открываю " + s.Name + ".";
                }
                return Launch(s.Target) ? "Открываю " + s.Name + "." : "Не получилось открыть " + s.Name + ".";
            }

            foreach (var a in OpenSystem)
            {
                if (!OpenMatch(a, x)) continue;
                bool ok = Launch(a.Target);
                if (!ok && a.Target == "wt.exe") ok = Launch("powershell.exe");
                return ok ? "Открываю " + a.Name + "." : "Не получилось открыть " + a.Name + ".";
            }

            foreach (var al in OpenAppAliases)
            {
                if (!OpenMatch(al, x)) continue;
                var p = FindStartApp(al.App);
                if (p != null && Launch(p)) return "Открываю " + al.Name + ".";
                return "Не нашёл " + al.Name + " среди установленных программ.";
            }

            // любая установленная программа по названию из меню «Пуск»
            var any = FindStartApp(x);
            if (any != null && Launch(any)) return "Открываю " + Path.GetFileNameWithoutExtension(any) + ".";

            return strong ? "Не нашёл «" + x + "». Скажи, например: открой ютуб." : null;
        }
    }
}
