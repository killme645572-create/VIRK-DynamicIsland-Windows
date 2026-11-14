using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Island
{
    public class HourInfo
    {
        public string Time;   // "15:00"
        public double Temp;
        public int Code;
        public string Icon => WeatherInfo.IconFor(Code);
    }

    public class WeatherInfo
    {
        public double Temp, Feels, Wind, WindDir, Pressure;   // ветер м/с, давление мм рт. ст.
        public int Humidity, Code;
        public List<HourInfo> Hours = new List<HourInfo>();

        public string Icon => IconFor(Code);
        public string Desc => DescFor(Code);

        // WMO-коды погоды из Open-Meteo
        public static string IconFor(int code)
        {
            if (code == 0) return "☀";
            if (code == 1) return "🌤";
            if (code == 2) return "⛅";
            if (code == 3) return "☁";
            if (code == 45 || code == 48) return "🌫";
            if (code >= 51 && code <= 67) return "🌧";
            if (code >= 71 && code <= 77) return "❄";
            if (code >= 80 && code <= 82) return "🌦";
            if (code >= 85 && code <= 86) return "❄";
            if (code >= 95) return "⛈";
            return "🌡";
        }

        public static string DescFor(int code)
        {
            if (code == 0) return "Ясно";
            if (code == 1) return "Преимущественно ясно";
            if (code == 2) return "Переменная облачность";
            if (code == 3) return "Пасмурно";
            if (code == 45 || code == 48) return "Туман";
            if (code >= 51 && code <= 57) return "Морось";
            if (code >= 61 && code <= 67) return "Дождь";
            if (code >= 71 && code <= 77) return "Снег";
            if (code >= 80 && code <= 82) return "Ливни";
            if (code >= 85 && code <= 86) return "Снегопад";
            if (code >= 95) return "Гроза";
            return "";
        }
    }

    public static class WeatherService
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Возвращает null, если погода выключена в настройках. Бросает исключение при проблемах с сетью.
        public static async Task<WeatherInfo> GetAsync()
        {
            var s = Settings.Current;
            if (!s.ShowWeather) return null;

            if (s.Lat == null || s.Lon == null)
            {
                // один раз определяем город по IP (бесплатный сервис без ключа) и запоминаем
                var json = await Http.GetStringAsync("https://ipwho.is/");
                using var geo = JsonDocument.Parse(json);
                var r = geo.RootElement;
                if (!r.TryGetProperty("latitude", out var la) || !r.TryGetProperty("longitude", out var lo)) return null;
                s.Lat = la.GetDouble();
                s.Lon = lo.GetDouble();
                s.Save();
            }

            var url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,pressure_msl,wind_speed_10m,wind_direction_10m,weather_code" +
                "&hourly=temperature_2m,weather_code&wind_speed_unit=ms&timezone=auto&forecast_days=2",
                s.Lat.Value, s.Lon.Value);
            var body = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var cur = root.GetProperty("current");

            var w = new WeatherInfo
            {
                Temp = cur.GetProperty("temperature_2m").GetDouble(),
                Feels = cur.GetProperty("apparent_temperature").GetDouble(),
                Humidity = (int)Math.Round(cur.GetProperty("relative_humidity_2m").GetDouble()),
                Pressure = Math.Round(cur.GetProperty("pressure_msl").GetDouble() * 0.750062),
                Wind = cur.GetProperty("wind_speed_10m").GetDouble(),
                WindDir = cur.GetProperty("wind_direction_10m").GetDouble(),
                Code = cur.GetProperty("weather_code").GetInt32()
            };

            // следующие 6 часов (время в поясе города: "2026-11-11T15:00")
            var curTime = cur.GetProperty("time").GetString();
            var h = root.GetProperty("hourly");
            var times = h.GetProperty("time");
            var temps = h.GetProperty("temperature_2m");
            var codes = h.GetProperty("weather_code");
            for (int i = 0; i < times.GetArrayLength() && w.Hours.Count < 6; i++)
            {
                var t = times[i].GetString();
                if (string.CompareOrdinal(t, curTime) <= 0) continue;
                w.Hours.Add(new HourInfo { Time = t.Substring(11, 5), Temp = temps[i].GetDouble(), Code = codes[i].GetInt32() });
            }
            return w;
        }
    }
}
