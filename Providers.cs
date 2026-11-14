using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Island
{
    public enum ApiKind { OpenAi, Ollama }

    public enum KeyCheck { Ok, Bad, Unknown }

    // Один провайдер чата: OpenRouter, Groq, Ollama.
    public class ProviderInfo
    {
        public string Id;             // короткое имя для команды /key
        public string Name;           // как показывать в списке
        public ApiKind Kind;
        public string BaseUrl;        // для OpenAI-совместимых: адрес до /chat/completions (без него)
        public string EnvVar;         // переменная окружения с ключом (необязательно)
        public string KeyUrl;         // где взять ключ
        public string KeyHint;        // как выглядит ключ
        public string DefaultModel;
        public bool KeyRequired = true;
        public string[] Fallback = new string[0];   // список моделей, если загрузить настоящий не удалось
    }

    // Ключи хранятся одним файлом keys.bin, зашифрованным DPAPI (расшифровать может только твоя учётка Windows).
    public static class KeyStore
    {
        static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");
        static readonly string FilePath = Path.Combine(Dir, "keys.bin");

        static Dictionary<string, string> _keys;

        static Dictionary<string, string> Keys()
        {
            if (_keys != null) return _keys;
            _keys = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                {
                    var raw = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(raw));
                    if (d != null) _keys = d;
                }
            }
            catch { }
            return _keys;
        }

        public static string Get(ProviderInfo p)
        {
            if (!string.IsNullOrEmpty(p.EnvVar))
            {
                var env = Environment.GetEnvironmentVariable(p.EnvVar);
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            }
            if (Keys().TryGetValue(p.Id, out var k) && !string.IsNullOrWhiteSpace(k)) return k;

            return null;
        }

        public static bool Has(ProviderInfo p) => Get(p) != null;

        public static void Set(string providerId, string key)
        {
            var d = Keys();
            d[providerId] = key.Trim();
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(d);
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, enc);
        }
    }

    public static class ChatClient
    {
        static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // таймауты — через CancellationToken

        static string SystemPrompt => "Тебя зовут " + Settings.Current.AssistantName + ". Ты ИИ-ассистент в маленьком окне на рабочем столе Windows. " +
                                    "Отвечай коротко и по делу, обычным текстом без markdown-разметки, на языке пользователя.";

        public static readonly ProviderInfo[] Providers =
        {
            new ProviderInfo
            {
                Id = "openrouter", Name = "OpenRouter", Kind = ApiKind.OpenAi,
                BaseUrl = "https://openrouter.ai/api/v1",
                EnvVar = "OPENROUTER_API_KEY", KeyUrl = "openrouter.ai/keys", KeyHint = "sk-or-...",
                DefaultModel = "meta-llama/llama-3.3-70b-instruct:free",
                Fallback = new[] { "meta-llama/llama-3.3-70b-instruct:free" }
            },
            new ProviderInfo
            {
                Id = "groq", Name = "Groq", Kind = ApiKind.OpenAi,
                BaseUrl = "https://api.groq.com/openai/v1",
                EnvVar = "GROQ_API_KEY", KeyUrl = "console.groq.com/keys", KeyHint = "gsk_...",
                DefaultModel = "llama-3.3-70b-versatile",
                Fallback = new[] { "llama-3.3-70b-versatile", "llama-3.1-8b-instant" }
            },
            new ProviderInfo
            {
                Id = "ollama", Name = "Ollama", Kind = ApiKind.Ollama,
                EnvVar = "OLLAMA_API_KEY", KeyUrl = "ollama.com/settings/keys", KeyHint = "ключ",
                DefaultModel = "gpt-oss:120b",
                Fallback = new[] { "gpt-oss:120b", "gpt-oss:20b", "deepseek-v3.1:671b", "qwen3-coder:480b" }
            },
        };

        public static ProviderInfo Find(string id) =>
            Providers.FirstOrDefault(p => string.Equals(p.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

        public static ProviderInfo Current => Find(Settings.Current.ChatProvider) ?? Providers[0];

        public static void SetProvider(ProviderInfo p)
        {
            Settings.Current.ChatProvider = p.Id;
            Settings.Current.Save();
        }

        public static string ModelFor(ProviderInfo p)
        {
            var d = Settings.Current.ChatModels;
            if (d != null && d.TryGetValue(p.Id, out var m) && !string.IsNullOrWhiteSpace(m)) return m;
            return p.DefaultModel;
        }

        public static void SetModel(ProviderInfo p, string model)
        {
            var s = Settings.Current;
            if (s.ChatModels == null) s.ChatModels = new Dictionary<string, string>();
            s.ChatModels[p.Id] = model.Trim();
            s.Save();
        }

        // Если модель ещё ни разу не выбирали, а модели по умолчанию нет в настоящем списке (например, у Ollama
        // не скачана qwen2.5:14b) — берём первую из списка. Возвращает true, если выбор поменялся.
        public static bool EnsureModel(ProviderInfo p, List<string> list)
        {
            if (list == null || list.Count == 0) return false;
            var s = Settings.Current;
            bool chosen = s.ChatModels != null && s.ChatModels.ContainsKey(p.Id);
            if (chosen || list.Contains(p.DefaultModel)) return false;
            SetModel(p, list[0]);
            return true;
        }

        // Модели, которые понимают картинки: у OpenRouter это видно из каталога, у остальных — по названию (примерно).
        static readonly HashSet<string> VisionIds = new HashSet<string>();
        static readonly string[] VisionHints =
            { "vision", "-vl", "vl:", "vl-", "llava", "scout", "maverick", "pixtral", "gemma3", "gemma-3", "minicpm-v", "mistral-small3", "gpt-4o", "gemini", "claude" };

        public static bool IsVision(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            if (VisionIds.Contains(model)) return true;
            var l = model.ToLowerInvariant();
            return VisionHints.Any(h => l.Contains(h));
        }

        static string BaseUrl(ProviderInfo p) =>
            p.Kind == ApiKind.Ollama
                ? (string.IsNullOrWhiteSpace(Settings.Current.OllamaHost) ? "https://ollama.com" : Settings.Current.OllamaHost.Trim().TrimEnd('/'))
                : p.BaseUrl;

        // Проверка ключа лёгким запросом. Bad — сервис ответил 401/403; Unknown — нет сети или странный ответ (ключ всё равно можно сохранить).
        public static async Task<(KeyCheck state, string message)> CheckKeyAsync(ProviderInfo p, string key)
        {
            try
            {
                string url = p.Id == "openrouter" ? BaseUrl(p) + "/auth/key"     // у OpenRouter каталог моделей открытый, ключ проверяет только /auth/key
                           : p.Kind == ApiKind.Ollama ? BaseUrl(p) + "/api/tags"
                           : BaseUrl(p) + "/models";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                var (code, _) = await Send(req, cts.Token);
                int c = (int)code;
                if (IsOk(code)) return (KeyCheck.Ok, null);
                if (c == 401 || c == 403) return (KeyCheck.Bad, c.ToString());
                return (KeyCheck.Unknown, "сервер ответил " + c);
            }
            catch (OperationCanceledException) { return (KeyCheck.Unknown, "сервер долго не отвечает"); }
            catch (Exception ex) { return (KeyCheck.Unknown, ex.Message); }
        }

        // ───────────── Список моделей ─────────────

        static readonly Dictionary<string, List<string>> ModelCache = new Dictionary<string, List<string>>();

        public static void ForgetModels(string providerId) => ModelCache.Remove(providerId);

        public static async Task<List<string>> GetModelsAsync(ProviderInfo p)
        {
            if (ModelCache.TryGetValue(p.Id, out var cached)) return cached;
            List<string> list = null;
            try { list = await FetchModels(p); } catch { }
            if (list == null || list.Count == 0) return p.Fallback.ToList();
            ModelCache[p.Id] = list;
            return list;
        }

        static async Task<List<string>> FetchModels(ProviderInfo p)
        {
            var key = KeyStore.Get(p);
            bool publicCatalog = p.Id == "openrouter";
            if (key == null && p.KeyRequired && !publicCatalog) return null;

            string url;
            if (p.Kind == ApiKind.Ollama) url = BaseUrl(p) + "/api/tags";
            else url = BaseUrl(p) + "/models";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            var (code, json) = await Send(req, cts.Token);
            if (!IsOk(code)) return null;

            var ids = new List<string>();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement arr;
            string field = "id";
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (p.Kind == ApiKind.Ollama && root.TryGetProperty("models", out arr)) field = "name";
            else if (!root.TryGetProperty("data", out arr)) return null;
            if (arr.ValueKind != JsonValueKind.Array) return null;

            foreach (var item in arr.EnumerateArray())
            {
                var id = Str(item, field);
                if (string.IsNullOrEmpty(id)) continue;
                var low = id.ToLowerInvariant();
                if (p.Id == "openrouter" && !low.EndsWith(":free")) continue;   // в списке OpenRouter — только бесплатные
                if (low.Contains("embed") || low.Contains("whisper") || low.Contains("tts") ||
                    low.Contains("guard") || low.Contains("orpheus")) continue;   // не чат-модели
                if (p.Id == "openrouter" && item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object &&
                    arch.TryGetProperty("input_modalities", out var mods) && mods.ValueKind == JsonValueKind.Array &&
                    mods.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "image"))
                    VisionIds.Add(id);
                ids.Add(id);
                if (ids.Count >= 80) break;
            }
            return ids;
        }

        // ───────────── Запрос к модели ─────────────

        public static async Task<string> AskAsync(ProviderInfo p, string model, List<ChatTurn> history)
        {
            var key = KeyStore.Get(p);
            if (key == null && p.KeyRequired)
                throw new InvalidOperationException(
                    $"нет ключа для {p.Name}. Введи в чате:  /key {p.Id} {p.KeyHint}   (взять: {p.KeyUrl})");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(p.Kind == ApiKind.Ollama ? 180 : 90));
            try
            {
                string text;
                switch (p.Kind)
                {
                    case ApiKind.Ollama: text = await AskOllama(p, key, model, history, cts.Token); break;
                    default: text = await AskOpenAi(p, key, model, history, cts.Token); break;
                }
                text = Regex.Replace(text ?? "", @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();   // «рассуждения» r1-моделей
                return text.Length == 0 ? "(пустой ответ — попробуй другую модель)" : text;
            }
            catch (OperationCanceledException)
            {
                throw new Exception("таймаут — модель слишком долго не отвечает");
            }
            catch (HttpRequestException) when (p.Kind == ApiKind.Ollama)
            {
                throw new Exception($"нет связи с Ollama ({BaseUrl(p)}) — проверь интернет");
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"нет связи с {p.Name}: {ex.Message}");
            }
        }

        // Картинки уходят только из двух последних сообщений, где они были, — иначе запрос раздувается с каждым ходом.
        static object[] BuildMessages(List<ChatTurn> history, bool ollama)
        {
            var msgs = new List<object> { new Dictionary<string, object> { ["role"] = "system", ["content"] = SystemPrompt } };
            var withImg = new List<int>();
            for (int i = 0; i < history.Count; i++)
                if (history[i].Images != null && history[i].Images.Count > 0) withImg.Add(i);
            var keep = new HashSet<int>(withImg.Skip(Math.Max(0, withImg.Count - 2)));

            for (int i = 0; i < history.Count; i++)
            {
                var t = history[i];
                bool hasImg = t.Images != null && t.Images.Count > 0;
                if (!hasImg)
                {
                    msgs.Add(new Dictionary<string, object> { ["role"] = t.Role, ["content"] = t.Text });
                }
                else if (!keep.Contains(i))
                {
                    msgs.Add(new Dictionary<string, object> { ["role"] = t.Role, ["content"] = t.Text + " [к сообщению была приложена картинка]" });
                }
                else if (ollama)
                {
                    msgs.Add(new Dictionary<string, object>
                    {
                        ["role"] = t.Role,
                        ["content"] = t.Text,
                        ["images"] = t.Images.Select(im => im.Base64).ToArray()
                    });
                }
                else
                {
                    var parts = new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = t.Text } };
                    foreach (var im in t.Images)
                        parts.Add(new Dictionary<string, object>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object> { ["url"] = im.DataUrl }
                        });
                    msgs.Add(new Dictionary<string, object> { ["role"] = t.Role, ["content"] = parts });
                }
            }
            return msgs.ToArray();
        }

        static async Task<string> AskOpenAi(ProviderInfo p, string key, string model, List<ChatTurn> history, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new { model, messages = BuildMessages(history, false), stream = false });

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl(p) + "/chat/completions");
            if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (p.Id == "openrouter") req.Headers.Add("X-Title", "Island");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var (code, json) = await Send(req, ct);
            if (!IsOk(code)) throw new Exception(ErrorText(code, json, history.Any(t => t.Images != null && t.Images.Count > 0)));

            using var doc = ParseBody(code, json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _))   // OpenRouter иногда отвечает 200, но с ошибкой внутри
                throw new Exception(ErrorText(HttpStatusCode.BadGateway, json));
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                throw new Exception("пустой ответ от сервера");
            return Str(choices[0].GetProperty("message"), "content");
        }

        static async Task<string> AskOllama(ProviderInfo p, string key, string model, List<ChatTurn> history, CancellationToken ct)
        {
            // Облако Ollama (ollama.com): модель крутится на их серверах, видеокарта ПК не нужна
            var body = JsonSerializer.Serialize(new { model, messages = BuildMessages(history, true), stream = false });

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl(p) + "/api/chat");
            if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var (code, json) = await Send(req, ct);
            if (!IsOk(code)) throw new Exception(ErrorText(code, json, history.Any(t => t.Images != null && t.Images.Count > 0)));

            using var doc = ParseBody(code, json);
            return Str(doc.RootElement.GetProperty("message"), "content");
        }

        // ───────────── Мелочи ─────────────

        static async Task<(HttpStatusCode code, string json)> Send(HttpRequestMessage req, CancellationToken ct)
        {
            using var resp = await Http.SendAsync(req, ct);
            // запоминаем, кто на самом деле ответил (пригодится, если придёт не JSON)
            var finalUri = resp.RequestMessage?.RequestUri;
            string via = resp.Headers.Via != null && resp.Headers.Via.Count > 0 ? string.Join(",", resp.Headers.Via) : null;
            LastReply = $"{finalUri?.Host}{finalUri?.AbsolutePath}"
                      + (resp.Headers.Server.Count > 0 ? $" · server: {resp.Headers.Server}" : "")
                      + (via != null ? $" · via: {via}" : "")
                      + $" · type: {resp.Content.Headers.ContentType?.MediaType ?? "—"}";
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }

        static string LastReply;   // сведения о последнем ответе сервера

        // Разбор JSON с понятной ошибкой: если сервер вернул не JSON (прокси, VPN, заглушка), покажем, что именно пришло.
        static JsonDocument ParseBody(HttpStatusCode code, string body)
        {
            try { return JsonDocument.Parse(body); }
            catch (JsonException)
            {
                var snip = Regex.Replace(body ?? "", @"\s+", " ").Trim();
                if (snip.Length == 0) snip = "(пусто)";
                if (snip.Length > 200) snip = snip.Substring(0, 200) + "…";
                throw new Exception($"сервер ответил не JSON ({(int)code}): {snip}\n[{LastReply}]\nЭто ответ не от самого сервиса, а от чего-то по дороге (прокси, VPN, антивирус, программа обхода блокировок).");
            }
        }

        static bool IsOk(HttpStatusCode c) => (int)c >= 200 && (int)c < 300;

        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        static string ErrorText(HttpStatusCode code, string json, bool images = false)
        {
            string msg = json ?? "";
            try
            {
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.String) msg = e.GetString();
                    else if (e.ValueKind == JsonValueKind.Object && Str(e, "message") != null) msg = Str(e, "message");
                }
            }
            catch { }
            msg = Regex.Replace(msg ?? "", @"\s+", " ").Trim();
            if (msg.Length > 300) msg = msg.Substring(0, 300) + "…";

            string hint = "";
            int c = (int)code;
            if (c == 402) hint = " — на аккаунте нет кредитов или подписки для этой модели: выбери другую (полегче) или другого провайдера";
            else if (c == 429) hint = " — лимит исчерпан, подожди или выбери другую модель";
            else if (c == 401 || c == 403) hint = " — проверь ключ (/key ...)";
            else if (c == 404) hint = " — такой модели нет, выбери другую из списка";
            if (images)
                hint += " — если дело в картинке: эта модель, возможно, не умеет их читать. Выбери модель с пометкой 👁";
            return $"{c}: {msg}{hint}";
        }
    }
}
