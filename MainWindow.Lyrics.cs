using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace Island
{
    // Строка со словами песни под названием трека (синхронно с позицией воспроизведения).
    public partial class MainWindow
    {
        List<LrcLine> _lyrics;
        string _lyricKey;
        int _lyricIdx = -2;

        void ClearLyrics()
        {
            _lyricKey = null;
            _lyrics = null;
            _lyricIdx = -2;
            LyricText.Visibility = Visibility.Collapsed;
        }

        void StartLyrics(string artist, string title)
        {
            if (!Settings.Current.Lyrics || string.IsNullOrWhiteSpace(title)) { ClearLyrics(); return; }
            string key = (artist ?? "") + "|" + title;
            if (key == _lyricKey) return;
            _lyricKey = key;
            _lyrics = null;
            _lyricIdx = -2;
            LyricText.Visibility = Visibility.Collapsed;
            double dur = TryGetTimeline(out _, out var d, out _) ? d.TotalSeconds : 0;
            _ = FetchLyricsAsync(key, artist, title, dur);
        }

        async Task FetchLyricsAsync(string key, string artist, string title, double dur)
        {
            var l = await LyricsService.GetAsync(artist, title, dur);
            if (key != _lyricKey) return;   // трек уже сменился
            _lyrics = l;
        }

        void UpdateLyricLine(System.TimeSpan pos, bool has)
        {
            if (_lyrics == null || _lyrics.Count == 0 || !has)
            {
                if (LyricText.Visibility != Visibility.Collapsed) LyricText.Visibility = Visibility.Collapsed;
                _lyricIdx = -2;
                return;
            }
            int idx = -1;
            for (int i = 0; i < _lyrics.Count; i++)
            {
                if (_lyrics[i].Time <= pos) idx = i; else break;
            }
            if (idx == _lyricIdx && LyricText.Visibility == Visibility.Visible) return;
            _lyricIdx = idx;
            string text = idx >= 0 ? _lyrics[idx].Text : "";
            LyricText.Text = string.IsNullOrWhiteSpace(text) ? "♪" : text;
            LyricText.Visibility = Visibility.Visible;
        }
    }
}
