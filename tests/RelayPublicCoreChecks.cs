using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Drawing;
using Relay;
class FakeRemote : IRemote {
    public LiveInfo Live;
    public int Sends, Edits;
    public bool Ambiguous, Rejected;
    public LiveInfo GetLive() { return Live; }
    public int Send(string text, bool live) {
        Sends++;
        if (Ambiguous) throw new ServiceError("Timeout", false);
        if (Rejected) throw new ServiceError("Rejected", true);
        return 25;
    }
    public void Edit(int id, string text, bool live) { Edits++; }
}
class FakeCardRemote : FakeRemote, ICardRemote, ICaptionRemote {
    public int CardSends, CardEdits, CaptionEdits, CaptionFailures;
    public bool LastLive;
    public string LastCaption;
    public int SendCard(string text, bool live, StreamState state) { CardSends++; LastLive = live; LastCaption = text; return Send(text, live); }
    public void EditCard(int id, string text, bool live, StreamState state) { CardEdits++; LastLive = live; LastCaption = text; Edit(id, text, live); }
    public void EditCardCaption(int id, string text, bool live) { CaptionEdits++; if(CaptionFailures-- > 0) throw new ServiceError("Caption acknowledgement lost",false); LastLive=live; LastCaption=text; Edit(id,text,live); }
}
class StreamBotChecks {
    static void Check(bool good, string message) { if (!good) throw new Exception(message); }
    static void Main() {
        try { Run(); }
        catch (Exception error) {
            Console.WriteLine("FAIL: " + error.GetType().Name + ": " + error.Message);
            Environment.ExitCode = 1;
        }
    }
    static void Run() {
        var serializer = new JavaScriptSerializer();
        var json = serializer.Deserialize<Dictionary<string, object>>("{\"data\":[{\"id\":\"a\"}]}");
        Console.WriteLine("JSON arrays deserialize as " + json["data"].GetType().Name);
        Check(json["data"] is IList, "Helix array type mismatch");
        var events = (IList)serializer.DeserializeObject("[{\"my_chat_member\":{\"chat\":{\"id\":-1001234567890,\"type\":\"channel\",\"title\":\"Test channel\"}}},{\"channel_post\":{\"chat\":{\"id\":-1001234567890,\"type\":\"channel\",\"title\":\"Test channel\"}}},{\"channel_post\":{\"chat\":{\"id\":12345,\"type\":\"private\"}}}]");
        var targets = Remote.ChannelCandidates(events);
        Check(targets.Count == 1 && targets[0].Id == "-1001234567890", "Channel discovery must preserve large IDs, deduplicate and ignore private chats");
        var data = new SavedData();
        var remote = new FakeRemote { Live = new LiveInfo { Id = "stream1", Title = "<hello>&", Game = "Dota 2", Started = DateTime.UtcNow, Viewers = 3 } };
        var engine = new Engine(data, remote, delegate { });
        DateTime now = DateTime.UtcNow;
        engine.Step(now); engine.Step(now.AddSeconds(30));
        Check(remote.Sends == 1 && remote.Edits == 0, "Duplicate start notice");
        Check(data.Stream.CurrentViewers == 3 && data.Stream.Categories["Dota 2"] == 2, "Current viewers/category observations");
        remote.Live.Game = "Fortnite";
        engine.Step(now.AddSeconds(60));
        Check(remote.Edits == 1 && remote.Sends == 1, "Category change must edit");
        var state = serializer.Deserialize<SavedData>(serializer.Serialize(data));
        engine = new Engine(state, remote, delegate { });
        engine.Step(now.AddSeconds(90));
        Check(remote.Sends == 1, "Restart published duplicate");
        remote.Live = null;
        engine.Step(now.AddSeconds(120)); engine.Step(now.AddSeconds(150));
        Check(remote.Edits == 1, "Premature offline");
        engine.Step(now.AddSeconds(180));
        Check(remote.Edits == 2 && state.Stream.StreamId == "", "Offline finalization");
        remote.Live = new LiveInfo { Id = "stream2", Title = "new", Game = "GTA", Started = now, Viewers = 2 };
        remote.Ambiguous = true;
        try { engine.Step(now.AddSeconds(210)); } catch (ServiceError) { }
        Check(state.Stream.Sending, "Uncertain send must persist");
        int sends = remote.Sends;
        try { engine.Step(now.AddSeconds(240)); } catch (ServiceError) { }
        Check(remote.Sends == sends, "Uncertain send was repeated");
        var rejectedData = new SavedData();
        remote.Ambiguous = false; remote.Rejected = true;
        var rejectedEngine = new Engine(rejectedData, remote, delegate { });
        try { rejectedEngine.Step(now); } catch (ServiceError) { }
        Check(!rejectedData.Stream.Sending && rejectedData.Stream.MessageId == 0, "Known rejection must be retryable");
        Check(Engine.Escape("<x>&\"") == "&lt;x&gt;&amp;&quot;", "Unsafe Telegram markup");
        var cardData = new SavedData();
        var cardRemote = new FakeCardRemote { Live = new LiveInfo { Id = "cards", DisplayName = "SampleChannel", Title = "Hello", Game = "Dota 2", Started = now, Viewers = 8 } };
        var cardEngine = new Engine(cardData, cardRemote, delegate { });
        cardEngine.Step(now); cardEngine.Step(now.AddSeconds(10)); cardEngine.Step(now.AddSeconds(59));
        Check(cardRemote.CardSends == 1 && cardRemote.CardEdits == 0, "Card updated too often");
        Check(!cardData.Stream.Ongoing && cardRemote.LastCaption.Contains("Старт трансляции") && !cardRemote.LastCaption.Contains("Онлайн:"), "Start phase changed before one minute");
        cardEngine.Step(now.AddSeconds(60));
        Check(cardRemote.CardEdits == 1 && cardData.Stream.Ongoing && cardRemote.LastCaption.Contains("Онлайн:") && Engine.StatusTitle("",cardData.Stream,true).Contains("ведёт трансляцию"), "Ongoing transition at 60 seconds missing");
        cardRemote.Live.Viewers=13;cardRemote.CaptionFailures=1;
        try { cardEngine.Step(now.AddSeconds(70)); } catch(ServiceError) { }
        Check(!cardData.Stream.Sending && cardRemote.CardSends==1 && cardRemote.CardEdits==1,"Caption retry must not publish another photo");
        cardEngine.Step(now.AddSeconds(80));
        Check(cardRemote.CaptionEdits==2&&cardRemote.LastCaption.Contains("13")&&cardRemote.CardEdits==1,"Online changes must update only the caption");
        cardData=serializer.Deserialize<SavedData>(serializer.Serialize(cardData));
        cardEngine=new Engine(cardData,cardRemote,delegate{});cardEngine.Step(now.AddSeconds(90));
        Check(cardData.Stream.Ongoing && cardRemote.CardSends==1 && cardRemote.CardEdits==1,"Restart reset phase or duplicated photo");
        cardEngine.Step(now.AddSeconds(179));
        Check(cardRemote.CardEdits==1,"Caption edits must not reset or shorten image refresh interval");
        cardEngine.Step(now.AddSeconds(180));
        Check(cardRemote.CardEdits == 2 && cardData.Stream.HasPhoto, "Preview refresh or photo state missing");
        cardRemote.Live.Game = "Fortnite"; cardEngine.Step(now.AddSeconds(190));
        Check(cardRemote.CardEdits == 3 && cardData.Stream.History.Count == 2, "Immediate game card update or history missing");
        Check(Engine.MainCategory(cardData.Stream) == "Dota 2", "Main category should use observed time, not last game");
        cardRemote.Live.Title = new string('<', 500); cardRemote.Live.Game = new string('&', 250);
        for (int i = 0; i < 12; i++) { cardRemote.Live.Title += "!"; cardEngine.Step(now.AddSeconds(200 + i * 10)); }
        string plainCaption = WebUtility.HtmlDecode(Regex.Replace(cardRemote.LastCaption, "<[^>]+>", ""));
        Check(plainCaption.Length <= 1024 && cardRemote.LastCaption.Contains("<blockquote expandable>"), "Telegram live caption limit/history");
        Check(WebUtility.HtmlDecode(Regex.Replace(cardEngine.EndText(), "<[^>]+>", "")).Length <= 1024, "Telegram end caption limit");
        var oldData = serializer.Deserialize<SavedData>("{\"Stream\":{\"StreamId\":\"cards\",\"MessageId\":42,\"LastText\":\"old\"}}");
        var oldCardEngine = new Engine(oldData, cardRemote, delegate { });
        int beforeSends = cardRemote.CardSends;
        oldCardEngine.Step(now.AddMinutes(5));
        Check(oldData.Stream.HasPhoto && cardRemote.CardSends == beforeSends, "Legacy text message should migrate by edit");
        cardRemote.Live = null;
        cardEngine.Step(now.AddMinutes(6)); cardEngine.Step(now.AddMinutes(6).AddSeconds(10));
        Check(cardRemote.LastLive, "Card finalized after only two offline checks");
        cardEngine.Step(now.AddMinutes(6).AddSeconds(20));
        Check(!cardRemote.LastLive && cardData.Stream.StreamId == "", "End card was not finalized");
        Check(!cardRemote.LastCaption.Contains("Онлайн") && !cardRemote.LastCaption.Contains("онлайн") && !cardRemote.LastCaption.Contains("Пиковый") && !cardRemote.LastCaption.Contains("Средний"), "Viewer statistics remain in the end caption");
        Check(cardData.PendingDeletions.Count == 1 && cardData.PendingDeletions[0].DeleteAt == now.AddMinutes(66).AddSeconds(20), "End announcement deletion was not scheduled for one hour later");
        var legacyStatistics = serializer.Deserialize<SavedData>("{\"Stream\":{\"ViewerSum\":400,\"Samples\":5,\"Peak\":100,\"CurrentViewers\":12}}");
        Check(legacyStatistics.Stream.CurrentViewers == 12, "Old settings with removed statistics must still load");
        byte[] cardBytes = CardRenderer.Render("sample_channel", new StreamState { Title = "Тестовый стрим · проверяем оформление", Game = "Fortnite", DisplayName = "SampleChannel" }, true, null, null);
        using (var image = Image.FromStream(new MemoryStream(cardBytes))) Check(image.Width == 1200 && image.Height == 750, "Invalid card dimensions");
        var upload = Remote.PhotoUpload(new Dictionary<string, object> { { "chat_id", "-1001234567890" }, { "media", new { type = "photo", media = "attach://card", caption = "Test <x>" } } }, cardBytes);
        var multipart = Encoding.UTF8.GetString(upload.Bytes);
        Check(multipart.Contains("attach://card") && multipart.Contains("name=\"card\"; filename=\"stream-card.jpg\"") && multipart.Contains("Content-Type: image/jpeg"), "Multipart upload contract");
        Check(Polling.Seconds == 10 && Polling.PreviewSeconds == 120, "Polling cadence");
        string directory = Path.GetFullPath(Path.Combine("work", "streambot-dpapi-" + Guid.NewGuid().ToString("N")));
        var store = new Store(directory);
        var protectedData = new SavedData();
        protectedData.Preferences.TelegramToken = "FAKE_SECRET_FOR_TESTING";
        store.Save(protectedData);
        Check(store.Load().Preferences.TelegramToken == "FAKE_SECRET_FOR_TESTING", "DPAPI round trip");
        Check(!File.ReadAllText(Path.Combine(directory,"settings.protected")).Contains("FAKE_SECRET_FOR_TESTING"), "Secret plaintext in file");
        store.Save(protectedData);
        Check(File.Exists(Path.Combine(directory,"settings.protected.bak")), "Atomic replacement backup");
        Console.WriteLine("PASS: 60-second phase transition, caption-only online updates, caption retry, phase persistence, centered card size, legacy migration, caption limits, history, offline confirmation, uncertain send, multipart JPEG, encrypted storage.");
    }
}

