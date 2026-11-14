using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Island
{
    class LrcLine
    {
        public TimeSpan Time;
        public string Text;
    }

    // Слова песен с таймкодами из открытой базы LRCLIB (lrclib.net, без ключей).
    // Туда уходит только исполнитель, название и длительность трека.
    static class LyricsService
    {
        static readonly HttpClient Http = CreateHttp();
        static readonly Dictionary<string, List<LrcLine>> Cache = new Dictionary<string, List<LrcLine>>();
        static readonly Regex Stamp = new Regex(@"\[(\d+):(\d+(?:[.,]\d+)?)\]", RegexOptions.Compiled);
        static readonly Regex Junk = new Regex(@"\s*[\(\[].*?[\)\]]|\s+-\s+(remaster|live|radio|single|version).*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static HttpClient CreateHttp()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Island/1.0 (Windows dynamic island)");
            return c;
        }

        // null — текста нет (или он не синхронизированный)
        public static async Task<List<LrcLine>> GetAsync(string artist, string title, double durationSec)
        {
            artist = artist ?? "";
            string key = (artist + "|" + title).ToLowerInvariant();
            lock (Cache) { if (Cache.TryGetValue(key, out var hit)) return hit; }

            string lrc = null;
            try
            {
                string url = "https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(artist) +
                             "&track_name=" + Uri.EscapeDataString(title) +
                             (durationSec > 0 ? "&duration=" + (int)Math.Round(durationSec) : "");
                var r = await Http.GetAsync(url);
                if (r.IsSuccessStatusCode)
                {
                    using (var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
                        lrc = Synced(d.RootElement);
                }
            }
            catch { }

            if (lrc == null)
            {
                try
                {
                    string q = Junk.Replace(artist + " " + title, "").Trim();
                    var r = await Http.GetAsync("https://lrclib.net/api/search?q=" + Uri.EscapeDataString(q));
                    if (r.IsSuccessStatusCode)
                    {
                        using (var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
                        {
                            if (d.RootElement.ValueKind == JsonValueKind.Array)
                                foreach (var item in d.RootElement.EnumerateArray())
                                {
                                    lrc = Synced(item);
                                    if (lrc != null) break;
                                }
                        }
                    }
                }
                catch { }
            }

            var lines = lrc == null ? null : Parse(lrc);
            if (lines != null && lines.Count == 0) lines = null;
            lock (Cache) { Cache[key] = lines; }
            return lines;
        }

        static string Synced(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object &&
                e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
            {
                var t = s.GetString();
                return string.IsNullOrWhiteSpace(t) ? null : t;
            }
            return null;
        }

        static List<LrcLine> Parse(string lrc)
        {
            var res = new List<LrcLine>();
            foreach (var raw in lrc.Split('\n'))
            {
                var line = raw.Trim();
                var ms = Stamp.Matches(line);
                if (ms.Count == 0) continue;
                string text = line.Substring(ms[ms.Count - 1].Index + ms[ms.Count - 1].Length).Trim();
                foreach (Match m in ms)
                {
                    double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double sec = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                    res.Add(new LrcLine { Time = TimeSpan.FromSeconds(min * 60 + sec), Text = text });
                }
            }
            return res.OrderBy(l => l.Time).ToList();
        }
    }
}
