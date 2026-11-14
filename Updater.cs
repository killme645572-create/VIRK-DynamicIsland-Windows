using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Island
{
    sealed class UpdateInfo
    {
        public Version Version;
        public string Tag;
        public string PageUrl;       // страница релиза на GitHub
        public string DownloadUrl;   // установщик (.exe) или архив из файлов релиза; если файлов нет — null
        public string Notes;
    }

    sealed class UpdateResult
    {
        public UpdateInfo Info;      // самый свежий релиз (есть всегда, если Error == null)
        public bool Newer;           // он новее установленной версии
        public string Error;
    }

    // Проверка обновлений через GitHub Releases: https://api.github.com/repos/<владелец>/<репозиторий>/releases/latest
    // Чтобы выпустить обновление: на GitHub создай Release с тегом выше текущей версии (например v1.3.0)
    // и приложи к нему файл установщика Island-Setup-1.3.0.exe. Версия самой программы задаётся в Island.csproj (<Version>).
    static class Updater
    {
        // Впиши сюда свой репозиторий «владелец/репозиторий» — или задай UpdateRepo в settings.json
        public const string DefaultRepo = "OWNER/Island";

        static readonly HttpClient Http = MakeHttp();

        static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Island-Updater");
            h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return h;
        }

        public static string Repo
        {
            get
            {
                string r = Settings.Current.UpdateRepo;
                return (string.IsNullOrWhiteSpace(r) ? DefaultRepo : r).Trim().Trim('/');
            }
        }

        public static bool Configured => Regex.IsMatch(Repo, @"^[\w.-]+/[\w.-]+$") && !Repo.StartsWith("OWNER/", StringComparison.OrdinalIgnoreCase);

        public static Version Current
        {
            get
            {
                try
                {
                    var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
                    return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
                }
                catch { return new Version(1, 0, 0); }
            }
        }

        public static string CurrentText => Current.ToString(3);

        // «v1.3.0», «1.3», «Island 1.3.0-beta» → 1.3.0
        static Version ParseTag(string tag)
        {
            var m = Regex.Match(tag ?? "", @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            try
            {
                int a = int.Parse(m.Groups[1].Value);
                int b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                int c = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                return new Version(a, b, c);
            }
            catch { return null; }
        }

        public static async Task<UpdateResult> CheckAsync()
        {
            if (!Configured)
                return new UpdateResult { Error = "не указан репозиторий GitHub (UpdateRepo в settings.json)" };
            try
            {
                using (var resp = await Http.GetAsync("https://api.github.com/repos/" + Repo + "/releases/latest").ConfigureAwait(false))
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        return new UpdateResult { Error = "в репозитории " + Repo + " ещё нет релизов" };
                    if (!resp.IsSuccessStatusCode)
                        return new UpdateResult { Error = "GitHub ответил " + (int)resp.StatusCode };

                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                        var ver = ParseTag(tag);
                        if (ver == null) return new UpdateResult { Error = "не разобрал номер версии «" + tag + "»" };

                        var info = new UpdateInfo
                        {
                            Version = ver,
                            Tag = tag,
                            PageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : "https://github.com/" + Repo + "/releases",
                            Notes = root.TryGetProperty("body", out var b) ? b.GetString() : null
                        };

                        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            var list = assets.EnumerateArray()
                                .Select(a => new
                                {
                                    Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                                    Url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null
                                })
                                .Where(a => !string.IsNullOrEmpty(a.Url))
                                .ToList();
                            var pick = list.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                       ?? list.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                            if (pick != null) info.DownloadUrl = pick.Url;
                        }

                        return new UpdateResult { Info = info, Newer = ver > Current };
                    }
                }
            }
            catch (TaskCanceledException) { return new UpdateResult { Error = "GitHub не ответил" }; }
            catch (HttpRequestException) { return new UpdateResult { Error = "нет связи с GitHub" }; }
            catch (Exception ex) { return new UpdateResult { Error = ex.Message }; }
        }
    }
}
