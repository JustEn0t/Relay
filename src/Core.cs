using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Relay {
    public static class EndpointPolicy {
        public static bool IsApiEndpoint(string url, bool twitch) {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
            if (twitch) return (uri.Host == "api.twitch.tv" && uri.AbsolutePath.StartsWith("/helix/", StringComparison.Ordinal)) ||
                (uri.Host == "id.twitch.tv" && uri.AbsolutePath == "/oauth2/validate");
            if (uri.Host == "id.twitch.tv") return uri.AbsolutePath == "/oauth2/token" && uri.Query.Length == 0;
            return uri.Host == "api.telegram.org" && uri.Query.Length == 0 &&
                Regex.IsMatch(uri.AbsolutePath, "^/bot[0-9]+:[a-zA-Z0-9_-]+/(getMe|getChat|getChatMember|getWebhookInfo|getUpdates|sendMessage|sendPhoto|editMessageText|editMessageMedia|editMessageCaption|deleteMessage)$");
        }
        public static bool IsArtworkEndpoint(string url) {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
                uri.Host == "static-cdn.jtvnw.net" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
        }
    }
    public class Preferences {
        public string Channel = "";
        public string DisplayName = "", ProfileImageUrl = "";
        public string TelegramChannel = "";
        public string TelegramToken = "";
        public string TwitchClientId = "";
        public string TwitchClientSecret = "";
    }
    public class StreamState {
        public string StreamId = "";
        public int MessageId;
        public bool Sending;
        public string LastText = "";
        public DateTime Started;
        public DateTime LastSeen;
        public int OfflineChecks;
        public string DisplayName = "", Title = "", Game = "", GameId = "", ThumbnailUrl = "", GameArtUrl = "";
        public DateTime LastCardUpdate;
        public DateTime AnnouncedAt;
        public DateTime EndConfirmedAt;
        public bool Ongoing;
        public int CurrentViewers;
        public string LastCardSignature = "";
        public bool HasPhoto;
        public List<StreamChange> History = new List<StreamChange>();
        public Dictionary<string, int> Categories = new Dictionary<string, int>();
    }
    public class StreamChange {
        public DateTime At;
        public string Title, Game;
    }
    public static class Polling {
        public const int Seconds = 10;
        public const int PreviewSeconds = 120;
        public const int StartPhaseSeconds = 60;
        public const int DeleteAfterMinutes = 60;
    }
    public class SavedData {
        public Preferences Preferences = new Preferences();
        public StreamState Stream = new StreamState();
        public List<PendingDeletion> PendingDeletions = new List<PendingDeletion>();
    }
    public class PendingDeletion {
        public string ChatId = "", BotToken = "";
        public int MessageId, Attempts;
        public DateTime CreatedAt, DeleteAt, NextAttempt;
        public bool Failed;
        public string LastError = "";
    }
    public interface IDeletionRemote {
        void DeletePost(PendingDeletion item);
    }
    public class DeletionResult {
        public int Deleted, Deferred, Failed;
        public string LastError = "";
    }
    public class DeletionQueue {
        readonly SavedData data;
        readonly IDeletionRemote remote;
        readonly Action save;
        public DeletionQueue(SavedData data, IDeletionRemote remote, Action save) {
            this.data = data; this.remote = remote; this.save = save;
            if (data.PendingDeletions == null) data.PendingDeletions = new List<PendingDeletion>();
        }
        public static string BotId(string token) {
            if (string.IsNullOrEmpty(token)) return "";
            int separator = token.IndexOf(':'); return separator > 0 ? token.Substring(0, separator) : "";
        }
        public static bool HasDue(SavedData data, DateTime now) {
            return data.PendingDeletions != null && data.PendingDeletions.Exists(x => !x.Failed && x.DeleteAt <= now && x.NextAttempt <= now);
        }
        public static string ResolveToken(Preferences current, PendingDeletion item) {
            return BotId(item.BotToken).Length > 0 && BotId(item.BotToken) == BotId(current.TelegramToken) ? current.TelegramToken : item.BotToken;
        }
        public DeletionResult Process(DateTime now) {
            var result = new DeletionResult();
            var due = data.PendingDeletions.FindAll(x => !x.Failed && x.DeleteAt <= now && x.NextAttempt <= now);
            due.Sort((a,b) => a.DeleteAt.CompareTo(b.DeleteAt));
            foreach (var item in due.GetRange(0, Math.Min(3, due.Count))) {
                try {
                    remote.DeletePost(item); data.PendingDeletions.Remove(item); result.Deleted++; save();
                } catch (ServiceError error) {
                    item.Attempts++; item.LastError = error.Message; result.LastError = error.Message;
                    if (error.Definite && item.CreatedAt != default(DateTime) && (now - item.CreatedAt).TotalHours >= 48) {
                        item.Failed = true; result.Failed++;
                    } else {
                        int delay = Math.Max(error.RetryAfter, error.Definite ? 300 : 30);
                        item.NextAttempt = now.AddSeconds(delay); result.Deferred++;
                    }
                    save();
                }
            }
            return result;
        }
    }
    public class Store {
        public readonly string DirectoryPath;
        readonly string path;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public Store(string directory) {
            DirectoryPath = directory;
            path = Path.Combine(directory, "settings.protected");
        }
        public static Store OpenDefault(string root = null, bool migrate = true) {
            root = root ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string directory = Path.Combine(root, "Relay");
            string previous = Path.Combine(root, "EN0TStreamBot");
            if (!File.Exists(Path.Combine(directory, "settings.protected")) && File.Exists(Path.Combine(previous, "settings.protected"))) {
                if (!migrate) return new Store(previous);
                new Store(directory).Save(new Store(previous).Load());
                foreach (string name in new[] { "settings.protected.bak", "last-preview.jpg", "last-preview-stream.txt" }) {
                    string source = Path.Combine(previous, name), target = Path.Combine(directory, name);
                    try { if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target, false); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return new Store(directory);
        }
        public SavedData Load() {
            if (!File.Exists(path)) return new SavedData();
            byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return json.Deserialize<SavedData>(Encoding.UTF8.GetString(bytes));
        }
        public void Save(SavedData data) {
            Directory.CreateDirectory(DirectoryPath);
            byte[] plain = Encoding.UTF8.GetBytes(json.Serialize(data));
            byte[] bytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Array.Clear(plain, 0, plain.Length);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            } finally {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
    public class LiveInfo {
        public string Id, Title, Game, DisplayName, GameId, ThumbnailUrl, GameArtUrl;
        public DateTime Started;
        public int Viewers;
    }
    public class TelegramTarget {
        public string Id, Title;
        public override string ToString() { return Title + " (" + Id + ")"; }
    }
    public class ServiceError : Exception {
        public readonly bool Definite;
        public readonly int RetryAfter;
        public ServiceError(string message, bool definite, int retryAfter = 30) : base(message) {
            Definite = definite; RetryAfter = retryAfter;
        }
    }
    public interface IRemote {
        LiveInfo GetLive();
        int Send(string text, bool live);
        void Edit(int messageId, string text, bool live);
    }
    public interface ICardRemote {
        int SendCard(string caption, bool live, StreamState state);
        void EditCard(int messageId, string caption, bool live, StreamState state);
    }
    public interface ICaptionRemote {
        void EditCardCaption(int messageId, string caption, bool live);
    }
    public class UploadBody {
        public byte[] Bytes;
        public string ContentType;
    }
    public class Remote : IRemote, ICardRemote, ICaptionRemote, IDeletionRemote {
        readonly Preferences p;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        string accessToken;
        string lastGameId, lastGameArt;
        DateTime gameArtChecked;
        readonly ArtworkCache artwork = new ArtworkCache();
        DateTime expires, validated;
        public byte[] LatestCardBytes { get; private set; }
        public string ConnectedDisplayName { get; private set; }
        public string ConnectedProfileImageUrl { get; private set; }
        public Remote(Preferences preferences) { p = preferences; }
        static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
        static string Text(Dictionary<string, object> map, string key) {
            object value; return map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }
        Dictionary<string, object> Request(string url, string method, object body, bool twitch) {
            if (!EndpointPolicy.IsApiEndpoint(url, twitch)) throw new ServiceError("Адрес сервиса не разрешён.", true);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.AllowAutoRedirect = false;
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.UserAgent = "Relay/2.3.1";
            if (twitch) {
                request.Headers["Client-Id"] = p.TwitchClientId;
                request.Headers["Authorization"] = "Bearer " + accessToken;
            }
            if (body != null) {
                string formBody = body as string;
                var upload = body as UploadBody;
                byte[] bytes = upload != null ? upload.Bytes : Encoding.UTF8.GetBytes(formBody ?? json.Serialize(body));
                request.ContentType = upload != null ? upload.ContentType : formBody != null ? "application/x-www-form-urlencoded" : "application/json";
                request.ContentLength = bytes.Length;
                try { using (var output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length); }
                catch (WebException) { throw new ServiceError("Нет соединения с сервисом.", false); }
            }
            try {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                    return json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                }
            } catch (WebException error) {
                var response = error.Response as HttpWebResponse;
                if (response == null) throw new ServiceError("Сервис не ответил. Повторное подключение через 30 секунд.", false);
                using (response) {
                    int status = (int)response.StatusCode;
                    int retry = 30;
                    try {
                        using (var reader = new StreamReader(response.GetResponseStream())) {
                            var parsed = json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                            if (status == 400 && url.EndsWith("/deleteMessage", StringComparison.Ordinal) &&
                                Text(parsed, "description").StartsWith("Bad Request: message to delete not found", StringComparison.Ordinal))
                                return new Dictionary<string, object> { { "ok", true }, { "result", true } };
                            if (status == 400 && (url.EndsWith("/editMessageText", StringComparison.Ordinal) || url.EndsWith("/editMessageMedia", StringComparison.Ordinal) || url.EndsWith("/editMessageCaption", StringComparison.Ordinal)) &&
                                Text(parsed, "description").StartsWith("Bad Request: message is not modified", StringComparison.Ordinal))
                                return new Dictionary<string, object> { { "ok", true }, { "result", true } };
                            object parameters;
                            if (parsed.TryGetValue("parameters", out parameters))
                                retry = Math.Max(30, Convert.ToInt32(Map(parameters)["retry_after"]));
                        }
                    } catch (Exception) { }
                    string service = twitch || url.StartsWith("https://id.twitch.tv/", StringComparison.Ordinal) ? "Twitch" : "Telegram";
                    string hint = status == 401 ? "Проверь ключи подключения." :
                                  status == 403 ? "Проверь права бота в канале." :
                                  status == 429 ? "Лимит запросов. Следующая попытка позже." :
                                  "Проверь данные подключения.";
                    throw new ServiceError(service + " HTTP " + status + ". " + hint, status >= 400 && status < 500, retry);
                }
            } catch (InvalidOperationException) {
                throw new ServiceError("Сервис вернул неожиданный ответ.", false);
            } catch (ArgumentException) {
                throw new ServiceError("Не удалось разобрать ответ сервиса.", false);
            }
        }
        void Authenticate() {
            if (accessToken != null && DateTime.UtcNow < expires) {
                if ((DateTime.UtcNow - validated).TotalMinutes < 55) return;
                try {
                    Request("https://id.twitch.tv/oauth2/validate", "GET", null, true);
                    validated = DateTime.UtcNow;
                    return;
                } catch (ServiceError error) {
                    if (!error.Message.StartsWith("Twitch HTTP 401", StringComparison.Ordinal)) throw;
                    accessToken = null;
                }
            }
            var result = Request("https://id.twitch.tv/oauth2/token", "POST",
                "client_id=" + Uri.EscapeDataString(p.TwitchClientId) +
                "&client_secret=" + Uri.EscapeDataString(p.TwitchClientSecret) + "&grant_type=client_credentials", false);
            accessToken = Text(result, "access_token");
            if (string.IsNullOrEmpty(accessToken)) throw new ServiceError("Twitch не выдал ключ приложения.", true);
            expires = DateTime.UtcNow.AddSeconds(Math.Max(1, Convert.ToInt32(result["expires_in"]) - 120));
            Request("https://id.twitch.tv/oauth2/validate", "GET", null, true);
            validated = DateTime.UtcNow;
        }
        Dictionary<string, object> Helix(string path) {
            Authenticate();
            try { return Request("https://api.twitch.tv/helix/" + path, "GET", null, true); }
            catch (ServiceError error) {
                if (!error.Message.StartsWith("Twitch HTTP 401", StringComparison.Ordinal)) throw;
                accessToken = null; Authenticate();
                return Request("https://api.twitch.tv/helix/" + path, "GET", null, true);
            }
        }
        object Telegram(string method, object body) {
            var result = Request("https://api.telegram.org/bot" + p.TelegramToken + "/" + method, "POST", body, false);
            if (!result.ContainsKey("ok") || !Convert.ToBoolean(result["ok"]))
                throw new ServiceError("Telegram отклонил запрос.", true);
            return result["result"];
        }
        public static List<TelegramTarget> ChannelCandidates(IList updates) {
            var found = new Dictionary<string, TelegramTarget>();
            foreach (object value in updates) {
                var update = Map(value);
                foreach (string name in new[] { "my_chat_member", "channel_post", "edited_channel_post" }) {
                    object eventValue, chatValue;
                    if (!update.TryGetValue(name, out eventValue) || eventValue == null) continue;
                    if (!Map(eventValue).TryGetValue("chat", out chatValue) || chatValue == null) continue;
                    var chat = Map(chatValue);
                    if (Text(chat, "type") != "channel") continue;
                    string id = Text(chat, "id");
                    if (!Regex.IsMatch(id, "^-100[0-9]+$")) continue;
                    found[id] = new TelegramTarget { Id = id, Title = Text(chat, "title") };
                }
            }
            return new List<TelegramTarget>(found.Values);
        }
        public List<TelegramTarget> FindTelegramChannels() {
            var bot = Map(Telegram("getMe", new { }));
            var hook = Map(Telegram("getWebhookInfo", new { }));
            if (Text(hook, "url").Length > 0)
                throw new ServiceError("У бота подключён webhook другого приложения. Автопоиск недоступен. Укажи ID канала вручную.", true);
            // No offset: discovery does not acknowledge or discard queued updates.
            var targets = ChannelCandidates((IList)Telegram("getUpdates", new { limit = 100, timeout = 0 }));
            var allowed = new List<TelegramTarget>();
            foreach (var target in targets) {
                try {
                    var member = Map(Telegram("getChatMember", new { chat_id = target.Id, user_id = bot["id"] }));
                    object canPost;
                    if (Text(member, "status") == "administrator" && member.TryGetValue("can_post_messages", out canPost) && Convert.ToBoolean(canPost))
                        allowed.Add(target);
                } catch (ServiceError error) {
                    if (!error.Definite || error.Message.Contains("HTTP 429") || error.Message.Contains("HTTP 401")) throw;
                }
            }
            return allowed;
        }
        public string Check() {
            var users = (IList)Helix("users?login=" + Uri.EscapeDataString(p.Channel))["data"];
            if (users.Count != 1) throw new ServiceError("Канал Twitch не найден.", true);
            var identity = Map(users[0]);
            ConnectedDisplayName = Text(identity, "display_name");
            ConnectedProfileImageUrl = Text(identity, "profile_image_url");
            var bot = Map(Telegram("getMe", new { }));
            var chat = Map(Telegram("getChat", new { chat_id = p.TelegramChannel }));
            if (Text(chat, "type") != "channel") throw new ServiceError("Укажи Telegram-канал, в который публиковать анонсы.", true);
            var member = Map(Telegram("getChatMember", new { chat_id = p.TelegramChannel, user_id = bot["id"] }));
            object canPost;
            if (Text(member, "status") != "administrator" ||
                !member.TryGetValue("can_post_messages", out canPost) || !Convert.ToBoolean(canPost))
                throw new ServiceError("Добавь бота администратором канала с правом публикации.", true);
            return "Подключено: Twitch " + p.Channel + " → " + Text(chat, "title") + ". Бот @" + Text(bot, "username");
        }
        public LiveInfo GetLive() {
            var items = (IList)Helix("streams?user_login=" + Uri.EscapeDataString(p.Channel))["data"];
            if (items.Count == 0) return null;
            var item = Map(items[0]);
            string gameId = Text(item, "game_id");
            if (gameId != lastGameId || (DateTime.UtcNow - gameArtChecked).TotalMinutes >= 10) {
                lastGameArt = "";
                try {
                    if (gameId.Length > 0) {
                        var games = (IList)Helix("games?id=" + Uri.EscapeDataString(gameId))["data"];
                        if (games.Count > 0) lastGameArt = Text(Map(games[0]), "box_art_url").Replace("{width}", "300").Replace("{height}", "400");
                    }
                } catch (ServiceError) { }
                lastGameId = gameId; gameArtChecked = DateTime.UtcNow;
            }
            return new LiveInfo { Id = Text(item, "id"), Title = Text(item, "title"), Game = Text(item, "game_name"),
                DisplayName = Text(item, "user_name"), GameId = gameId, GameArtUrl = lastGameArt,
                ThumbnailUrl = Text(item, "thumbnail_url").Replace("{width}", "960").Replace("{height}", "540"),
                Viewers = Convert.ToInt32(item["viewer_count"]), Started = DateTime.Parse(Text(item, "started_at"),
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) };
        }
        public StreamState GetCardPreviewState() {
            LiveInfo live = GetLive();
            if (live != null) return new StreamState { StreamId = live.Id, DisplayName = live.DisplayName,
                Title = live.Title, Game = live.Game, GameId = live.GameId, GameArtUrl = live.GameArtUrl,
                ThumbnailUrl = live.ThumbnailUrl, Started = live.Started, LastSeen = DateTime.UtcNow, CurrentViewers = live.Viewers };
            var users = (IList)Helix("users?login=" + Uri.EscapeDataString(p.Channel))["data"];
            if (users.Count != 1) throw new ServiceError("Канал Twitch не найден.", true);
            var user = Map(users[0]);
            var channels = (IList)Helix("channels?broadcaster_id=" + Uri.EscapeDataString(Text(user, "id")))["data"];
            if (channels.Count != 1) throw new ServiceError("Нет информации о канале Twitch.", true);
            var info = Map(channels[0]); string art = "", gameId = Text(info, "game_id");
            if (gameId.Length > 0) {
                try {
                    var games = (IList)Helix("games?id=" + Uri.EscapeDataString(gameId))["data"];
                    if (games.Count > 0) art = Text(Map(games[0]), "box_art_url").Replace("{width}", "300").Replace("{height}", "400");
                } catch (ServiceError) { }
            }
            return new StreamState { StreamId = "preview", DisplayName = Text(user, "display_name"),
                Title = Text(info, "title"), Game = Text(info, "game_name"), GameId = gameId, GameArtUrl = art };
        }
        public int Send(string text, bool live) {
            var result = Map(Telegram("sendMessage", Payload(text, live, null)));
            return Convert.ToInt32(result["message_id"]);
        }
        public void Edit(int messageId, string text, bool live) {
            Telegram("editMessageText", Payload(text, live, messageId));
        }
        public static UploadBody PhotoUpload(Dictionary<string, object> fields, byte[] jpeg) {
            string boundary = "Relay" + Guid.NewGuid().ToString("N");
            using (var buffer = new MemoryStream()) {
                Action<string> write = delegate(string value) {
                    byte[] bytes = Encoding.UTF8.GetBytes(value); buffer.Write(bytes, 0, bytes.Length);
                };
                var serializer = new JavaScriptSerializer();
                foreach (var field in fields) {
                    string value = field.Value as string ?? serializer.Serialize(field.Value);
                    write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + field.Key + "\"\r\n\r\n" + value + "\r\n");
                }
                write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"card\"; filename=\"stream-card.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n");
                buffer.Write(jpeg, 0, jpeg.Length);
                write("\r\n--" + boundary + "--\r\n");
                return new UploadBody { Bytes = buffer.ToArray(), ContentType = "multipart/form-data; boundary=" + boundary };
            }
        }
        object CardRequest(string method, int? messageId, string caption, bool live, StreamState state) {
            byte[] jpeg = artwork.Render(p.Channel, state, live);
            LatestCardBytes = jpeg;
            var fields = new Dictionary<string, object> {
                { "chat_id", p.TelegramChannel },
                { "reply_markup", new { inline_keyboard = new[] { new[] {
                    new { text = live ? "▶ Смотреть стрим" : "📺 Открыть Twitch-канал", url = "https://www.twitch.tv/" + p.Channel }
                } } } }
            };
            if (messageId.HasValue) {
                fields["message_id"] = messageId.Value;
                fields["media"] = new { type = "photo", media = "attach://card", caption = caption, parse_mode = "HTML" };
            } else {
                fields["photo"] = "attach://card"; fields["caption"] = caption; fields["parse_mode"] = "HTML";
            }
            var response = Request("https://api.telegram.org/bot" + p.TelegramToken + "/" + method, "POST", PhotoUpload(fields, jpeg), false);
            if (!response.ContainsKey("ok") || !Convert.ToBoolean(response["ok"])) throw new ServiceError("Telegram отклонил карточку.", true);
            return response["result"];
        }
        public int SendCard(string caption, bool live, StreamState state) {
            return Convert.ToInt32(Map(CardRequest("sendPhoto", null, caption, live, state))["message_id"]);
        }
        public void EditCard(int messageId, string caption, bool live, StreamState state) {
            CardRequest("editMessageMedia", messageId, caption, live, state);
        }
        public void EditCardCaption(int messageId, string caption, bool live) {
            Telegram("editMessageCaption", new { chat_id = p.TelegramChannel, message_id = messageId,
                caption = caption, parse_mode = "HTML", reply_markup = new { inline_keyboard = new[] { new[] {
                    new { text = live ? "▶ Смотреть стрим" : "📺 Открыть Twitch-канал", url = "https://www.twitch.tv/" + p.Channel }
                } } } });
        }
        public void DeletePost(PendingDeletion item) {
            string token = DeletionQueue.ResolveToken(p, item);
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(item.ChatId) || item.MessageId <= 0)
                throw new ServiceError("Не хватает данных для удаления анонса.", true);
            var result = Request("https://api.telegram.org/bot" + token + "/deleteMessage", "POST", new { chat_id = item.ChatId, message_id = item.MessageId }, false);
            if (!result.ContainsKey("ok") || !Convert.ToBoolean(result["ok"])) throw new ServiceError("Telegram отклонил удаление анонса.", true);
        }
        object Payload(string text, bool live, int? messageId) {
            var result = new Dictionary<string, object> {
                { "chat_id", p.TelegramChannel }, { "text", text }, { "parse_mode", "HTML" },
                { "link_preview_options", new { is_disabled = true } },
                { "reply_markup", new { inline_keyboard = new[] { new[] {
                    new { text = live ? "Смотреть эфир" : "Открыть Twitch", url = "https://www.twitch.tv/" + p.Channel }
                } } } }
            };
            if (messageId.HasValue) result["message_id"] = messageId.Value;
            return result;
        }
    }
    public class Engine {
        readonly SavedData data;
        readonly IRemote remote;
        readonly Action save;
        public Engine(SavedData data, IRemote remote, Action save) { this.data = data; this.remote = remote; this.save = save; }
        public static string Escape(string value) {
            return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
        public static string LiveText(Preferences p, LiveInfo live) {
            string name = string.IsNullOrEmpty(live.DisplayName) ? p.Channel : live.DisplayName;
            return "🔴 <b>" + Escape(name) + " начал стрим!</b>\n\n<b>" + Escape(Short(live.Title, 180)) +
                   "</b>\n🎮 <b>Игра:</b> " + Escape(Short(live.Game, 60));
        }
        public static string StatusTitle(string channel, StreamState state, bool live) {
            string name = string.IsNullOrEmpty(state.DisplayName) ? channel : state.DisplayName;
            return Short(name, 32) + (live ? state.Ongoing ? " ведёт трансляцию" : " начал стрим" : " завершил стрим");
        }
        public static string Duration(TimeSpan duration) {
            int minutes = (int)Math.Max(0, duration.TotalMinutes);
            return minutes < 60 ? minutes + " мин" : (minutes / 60) + " ч " + (minutes % 60) + " мин";
        }
        public static string LiveCaption(StreamState state) {
            string result = "📝 <b>Название:</b> " + Escape(Short(state.Title, 180)) + "\n🎮 <b>Категория:</b> " + Escape(Short(state.Game, 60));
            if (state.Ongoing)
                result += "\n👀 <b>Онлайн:</b> " + state.CurrentViewers + "\n⏱ <b>Длительность:</b> " + Duration(state.LastSeen - state.Started);
            else result += "\n🕓 <b>Старт трансляции:</b> " + state.Started.ToUniversalTime().AddHours(3).ToString("dd.MM.yyyy, HH:mm") + " МСК";
            return result + HistoryText(state);
        }
        public static string Short(string value, int limit) {
            value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (value.Length <= limit) return value;
            int count = limit - 1;
            if (count > 0 && char.IsHighSurrogate(value[count - 1])) count--;
            return value.Substring(0, count) + "…";
        }
        public static string HistoryText(StreamState state) {
            if (state.History == null || state.History.Count < 2) return "";
            var text = new StringBuilder("\n\n<blockquote expandable><b>Последние изменения · МСК</b>");
            for (int i = Math.Max(0, state.History.Count - 5); i < state.History.Count; i++) {
                var change = state.History[i];
                text.Append("\n" + change.At.ToUniversalTime().AddHours(3).ToString("HH:mm") + " · " + Escape(Short(change.Game, 28)) + " — " + Escape(Short(change.Title, 48)));
            }
            return text + "</blockquote>";
        }
        public static string MainCategory(StreamState state) {
            string result = state.Game; int best = -1;
            if (state.Categories != null) foreach (var category in state.Categories) {
                if (category.Value > best) { result = category.Key; best = category.Value; }
            }
            return string.IsNullOrEmpty(result) ? "Без категории" : result;
        }
        public string EndText() {
            return "⚫ <b>" + Escape(StatusTitle(data.Preferences.Channel, data.Stream, false)) + "</b>\n\n" + EndCaption();
        }
        public string EndCaption() {
            var state = data.Stream;
            var duration = state.LastSeen - state.Started;
            string elapsed = ((int)Math.Max(0, duration.TotalHours)) + " ч " + Math.Max(0, duration.Minutes) + " мин";
            return "⏱ <b>Длительность:</b> ~" + elapsed +
                   "\n🎮 <b>Основная игра:</b> " + Escape(Short(MainCategory(state), 60)) + HistoryText(state) +
                   "\n\n<i>Спасибо, что были на эфире! Пост удалится через час.</i>";
        }
        void Finish(DateTime now) {
            ServiceError finalError = null;
            if (data.Stream.MessageId != 0) {
                if (data.PendingDeletions == null) data.PendingDeletions = new List<PendingDeletion>();
                if (data.Stream.EndConfirmedAt == default(DateTime)) data.Stream.EndConfirmedAt = now;
                string botId = DeletionQueue.BotId(data.Preferences.TelegramToken);
                if (!data.PendingDeletions.Exists(x => x.ChatId == data.Preferences.TelegramChannel && x.MessageId == data.Stream.MessageId && DeletionQueue.BotId(x.BotToken) == botId))
                    data.PendingDeletions.Add(new PendingDeletion { ChatId = data.Preferences.TelegramChannel, BotToken = data.Preferences.TelegramToken,
                        MessageId = data.Stream.MessageId, CreatedAt = data.Stream.AnnouncedAt == default(DateTime) ? now : data.Stream.AnnouncedAt,
                        DeleteAt = data.Stream.EndConfirmedAt.AddMinutes(Polling.DeleteAfterMinutes) });
                save();
                var cards = remote as ICardRemote;
                try {
                    if (cards != null) cards.EditCard(data.Stream.MessageId, EndCaption(), false, data.Stream);
                    else remote.Edit(data.Stream.MessageId, EndText(), false);
                } catch (ServiceError error) { finalError = error; }
            }
            data.Stream = new StreamState(); save();
            if (finalError != null) throw new ServiceError("Итог не удалось обновить. Удаление через час сохранено. " + finalError.Message, false, finalError.RetryAfter);
        }
        public string Step(DateTime now) {
            if (data.Stream.Sending)
                throw new ServiceError("Отправка прошлого анонса не подтверждена. Проверь канал перед повтором.", false);
            LiveInfo live = remote.GetLive();
            if (live == null) {
                if (data.Stream.StreamId.Length == 0) return "Канал офлайн · проверка каждые 10 секунд";
                data.Stream.OfflineChecks++;
                save();
                if (data.Stream.OfflineChecks >= 3) { Finish(now); return "Эфир завершён. Анонс удалится через час."; }
                return "Проверяю завершение эфира…";
            }
            if (data.Stream.StreamId.Length > 0 && data.Stream.StreamId != live.Id) Finish(now);
            if (data.Stream.StreamId.Length == 0) {
                data.Stream = new StreamState { StreamId = live.Id, Started = live.Started };
            }
            var state = data.Stream;
            if (state.Started == default(DateTime)) state.Started = live.Started;
            if (state.MessageId == 0) state.AnnouncedAt = now;
            else if (state.AnnouncedAt == default(DateTime)) state.AnnouncedAt = state.Started;
            state.Ongoing = state.Ongoing || (state.MessageId != 0 && (now - state.AnnouncedAt).TotalSeconds >= Polling.StartPhaseSeconds);
            state.CurrentViewers = live.Viewers;
            if (state.History == null) state.History = new List<StreamChange>();
            if (state.Categories == null) state.Categories = new Dictionary<string, int>();
            string title = live.Title ?? "", game = live.Game ?? "";
            if (state.History.Count == 0 || state.Title != title || state.Game != game) {
                state.History.Add(new StreamChange { At = now, Title = title, Game = game });
                if (state.History.Count > 50) state.History.RemoveAt(0);
            }
            state.DisplayName = live.DisplayName ?? data.Preferences.Channel;
            state.Title = title; state.Game = game; state.GameId = live.GameId ?? "";
            state.ThumbnailUrl = live.ThumbnailUrl ?? ""; state.GameArtUrl = live.GameArtUrl ?? "";
            int categorySamples;
            state.Categories.TryGetValue(game, out categorySamples); state.Categories[game] = categorySamples + 1;
            state.OfflineChecks = 0;
            state.LastSeen = now;
            var cardRemote = remote as ICardRemote;
            var captionRemote = remote as ICaptionRemote;
            string text = cardRemote != null ? LiveCaption(state) : "🔴 <b>" + Escape(StatusTitle(data.Preferences.Channel, state, true)) + "</b>\n\n" + LiveCaption(state);
            string signature = state.Ongoing + "\n" + state.DisplayName + "\n" + state.Title + "\n" + state.Game;
            if (state.MessageId == 0) {
                state.Sending = true; save();
                try { state.MessageId = cardRemote != null ? cardRemote.SendCard(text, true, state) : remote.Send(text, true); }
                catch (ServiceError error) {
                    if (error.Definite) { state.Sending = false; save(); }
                    throw;
                }
                state.Sending = false; state.LastText = text; state.HasPhoto = cardRemote != null; state.LastCardUpdate = now; state.LastCardSignature = signature; save();
            } else if (cardRemote != null && (!state.HasPhoto || state.LastCardSignature != signature || (now - state.LastCardUpdate).TotalSeconds >= Polling.PreviewSeconds)) {
                cardRemote.EditCard(state.MessageId, text, true, state);
                state.LastText = text; state.HasPhoto = true; state.LastCardUpdate = now; state.LastCardSignature = signature; save();
            } else if (state.LastText != text) {
                if (cardRemote != null && captionRemote != null) captionRemote.EditCardCaption(state.MessageId, text, true);
                else if (cardRemote != null) { cardRemote.EditCard(state.MessageId, text, true, state); state.LastCardUpdate = now; }
                else remote.Edit(state.MessageId, text, true);
                state.LastText = text; save();
            } else save();
            return "В эфире · " + live.Game + " · зрителей " + live.Viewers + " · анонс опубликован";
        }
    }
}
