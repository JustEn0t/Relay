using System;
using Relay;
class RelaySecurityChecks {
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static int Main() {
        try {
            Check(EndpointPolicy.IsApiEndpoint("https://api.twitch.tv/helix/streams?user_login=streamer", true), "Helix rejected");
            Check(EndpointPolicy.IsApiEndpoint("https://id.twitch.tv/oauth2/validate", true), "Validate rejected");
            Check(EndpointPolicy.IsApiEndpoint("https://id.twitch.tv/oauth2/token", false), "Token endpoint rejected");
            Check(EndpointPolicy.IsApiEndpoint("https://api.telegram.org/bot123:FAKE_TEST/deleteMessage", false), "Telegram endpoint rejected");
            foreach (string url in new[] {
                "http://api.twitch.tv/helix/streams", "https://api.twitch.tv.evil.invalid/helix/streams",
                "https://api.twitch.tv@evil.invalid/helix/streams", "https://api.twitch.tv:444/helix/streams",
                "https://api.twitch.tv/helix/streams#fragment", "https://localhost/helix/streams",
                "file:///C:/Windows/win.ini", "https://api.telegram.org/bot123:FAKE_TEST/getMe"
            }) Check(!EndpointPolicy.IsApiEndpoint(url, true), "Credential destination allowed: " + url);
            Check(!EndpointPolicy.IsApiEndpoint("https://id.twitch.tv/oauth2/token?client_secret=FAKE", false), "Secret query accepted");
            Check(!EndpointPolicy.IsApiEndpoint("https://api.telegram.org/bot123:FAKE_TEST/setWebhook", false), "Unneeded bot API method allowed");
            Check(!EndpointPolicy.IsApiEndpoint("https://api.twitch.tv/helix/streams", false), "Missing Twitch auth policy");
            Check(EndpointPolicy.IsArtworkEndpoint("https://static-cdn.jtvnw.net/ttv-boxart/test.jpg?relay=1"), "Artwork rejected");
            foreach (string url in new[] { "http://static-cdn.jtvnw.net/a", "https://static-cdn.jtvnw.net.evil.invalid/a",
                "https://user:pass@static-cdn.jtvnw.net/a", "https://static-cdn.jtvnw.net:444/a", "file:///C:/a", "https://127.0.0.1/a" }) {
                Check(!EndpointPolicy.IsArtworkEndpoint(url), "Artwork destination allowed: " + url);
                Check(ArtworkCache.Download(url) == null, "Disallowed image attempted");
            }
            Check(Engine.Escape("<a href=\"https://evil.invalid\">&") == "&lt;a href=&quot;https://evil.invalid&quot;&gt;&amp;", "Caption HTML injection");
            Check(new Preferences().TelegramToken == "" && new Preferences().TwitchClientSecret == "" && new Preferences().Channel == "", "Nonempty distributed credentials");
            Console.WriteLine("PASS: HTTPS/host/port/userinfo policies, credential destinations, bot method allowlist, image restrictions, caption escaping, empty defaults.");
            return 0;
        } catch (Exception error) { Console.WriteLine("FAIL: " + error.Message); return 1; }
    }
}
