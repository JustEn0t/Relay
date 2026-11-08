using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;

namespace Relay {
    public static class CardRenderer {
        static Color C(int value) { return Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255); }
        static GraphicsPath Round(RectangleF r, float radius) {
            var p = new GraphicsPath(); float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        static void Panel(Graphics g, RectangleF r, int color, float radius) {
            using (var path = Round(r, radius)) using (var brush = new SolidBrush(C(color))) g.FillPath(brush, path);
        }
        static void Text(Graphics g, string text, float size, FontStyle style, int color, RectangleF r) {
            using (var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(C(color)))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit })
                g.DrawString(text ?? "", font, brush, r, format);
        }
        static void FitText(Graphics g, string text, float size, int color, RectangleF r) {
            float actual = size;
            while (actual > 25) {
                using (var font = new Font("Segoe UI", actual, FontStyle.Bold, GraphicsUnit.Pixel)) {
                    if (g.MeasureString(text, font, (int)r.Width).Height <= r.Height) break;
                }
                actual -= 2;
            }
            Text(g, text, actual, FontStyle.Bold, color, r);
        }
        static void ImageFill(Graphics g, Image image, RectangleF r, float radius) {
            var saved = g.Save();
            using (var clip = Round(r, radius)) g.SetClip(clip);
            float scale = Math.Max(r.Width / image.Width, r.Height / image.Height);
            float w = image.Width * scale, h = image.Height * scale;
            g.DrawImage(image, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            g.Restore(saved);
        }
        public static byte[] Render(string channel, StreamState state, bool live, Image frame, Image cover) {
            using (var bitmap = new Bitmap(1200, 750))
            using (var g = Graphics.FromImage(bitmap)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(C(0x14101d));
                using (var glow = new SolidBrush(C(0x21172d))) g.FillEllipse(glow, -150, 180, 860, 700);
                using (var line = new Pen(C(0x30223d), 2)) { g.DrawLine(line, 32, 96, 1168, 96); g.DrawLine(line, 32, 566, 1168, 566); }
                Text(g, "RELAY / STREAM", 24, FontStyle.Bold, 0xddd0ed, new RectangleF(42, 34, 510, 36));
                Panel(g, new RectangleF(906, 28, 252, 46), live ? state.Ongoing ? 0x203a35 : 0x3d2032 : 0x2c2538, 23);
                using (var dot = new SolidBrush(C(live ? state.Ongoing ? 0x80dfb4 : 0xff638c : 0xa994bf))) g.FillEllipse(dot, 925, 45, 12, 12);
                Text(g, live ? state.Ongoing ? "В ЭФИРЕ" : "СТРИМ НАЧАЛСЯ" : "СТРИМ ЗАВЕРШЁН", 18, FontStyle.Bold, 0xf7f0ff, new RectangleF(947, 39, 205, 28));
                Panel(g, new RectangleF(47, 142, 552, 358), 0xa88ace, 18);
                Panel(g, new RectangleF(35, 129, 552, 358), 0x30243c, 18);
                if (frame != null) {
                    ImageFill(g, frame, new RectangleF(45, 139, 532, 299), 12);
                    Text(g, live ? "ПРЕВЬЮ ЭФИРА" : "ПОСЛЕДНИЙ КАДР ЭФИРА", 16, FontStyle.Bold, 0xc9b6dc, new RectangleF(59, 451, 340, 25));
                    if (cover != null) {
                        Panel(g, new RectangleF(461, 382, 100, 133), 0xf0e8fa, 7);
                        ImageFill(g, cover, new RectangleF(466, 387, 90, 123), 4);
                    }
                } else if (cover != null) {
                    var saved = g.Save(); g.TranslateTransform(304, 304); g.RotateTransform(-6);
                    Panel(g, new RectangleF(-132, -177, 264, 354), 0xf6f0fc, 7);
                    ImageFill(g, cover, new RectangleF(-124, -169, 248, 338), 3); g.Restore(saved);
                } else {
                    Text(g, "RELAY", 92, FontStyle.Bold, 0xc4a6e3, new RectangleF(108, 220, 435, 130));
                    Text(g, "TWITCH / " + channel.ToUpperInvariant(), 20, FontStyle.Regular, 0xc9b6dc, new RectangleF(111, 350, 440, 62));
                }
                Panel(g, new RectangleF(620, 145, 552, 356), 0xac8bce, 18);
                Panel(g, new RectangleF(610, 132, 552, 356), 0xf0e9f8, 18);
                FitText(g, Engine.Short(state.Title, 180), 43, 0x292035, new RectangleF(642, 164, 484, 196));
                Text(g, "КАТЕГОРИЯ", 15, FontStyle.Bold, 0x877193, new RectangleF(643, 386, 480, 24));
                FitText(g, string.IsNullOrEmpty(state.Game) ? "Без категории" : Engine.Short(state.Game, 90), 29, 0x362746, new RectangleF(640, 416, 490, 57));
                string name = string.IsNullOrEmpty(state.DisplayName) ? channel : state.DisplayName;
                Text(g, "twitch.tv / " + name, 27, FontStyle.Bold, 0xf6f0ff, new RectangleF(43, 590, 780, 44));
                Text(g, live ? "ЗАЛЕТАЙ НА СТРИМ  →" : "ДО СЛЕДУЮЩЕГО ЭФИРА", 17, FontStyle.Bold, 0xbfa4d5, new RectangleF(874, 599, 300, 31));
                using (var band = new SolidBrush(C(0x201e25))) g.FillRectangle(band, 0, 660, 1200, 90);
                string heading = Engine.StatusTitle(channel, state, live);
                float headingSize = 37;
                while (headingSize > 22) {
                    using (var measureFont = new Font("Segoe UI", headingSize, FontStyle.Bold, GraphicsUnit.Pixel))
                        if (g.MeasureString(heading, measureFont).Width + 42 <= 1104) break;
                    headingSize -= 1;
                }
                using (var headingFont = new Font("Segoe UI", headingSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var headingBrush = new SolidBrush(C(0xf5f0fa)))
                using (var dot = new SolidBrush(C(live ? 0xf25074 : 0x9d91ae))) {
                    SizeF size = g.MeasureString(heading, headingFont);
                    float x = (1200 - size.Width - 42) / 2;
                    g.FillEllipse(dot, x, 694, 23, 23);
                    g.DrawString(heading, headingFont, headingBrush, x + 39, 660 + (90 - size.Height) / 2);
                }
                using (var bytes = new MemoryStream()) { bitmap.Save(bytes, ImageFormat.Jpeg); return bytes.ToArray(); }
            }
        }
    }
    public class ArtworkCache {
        readonly Dictionary<string, byte[]> covers = new Dictionary<string, byte[]>();
        readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relay");
        static Image Decode(byte[] bytes) {
            if (bytes == null) return null;
            try {
                using (var input = new MemoryStream(bytes)) using (var source = Image.FromStream(input)) {
                    if (source.Width > 4096 || source.Height > 4096 || source.Width < 10 || source.Height < 10) return null;
                    return new Bitmap(source);
                }
            } catch { return null; }
        }
        public static byte[] Download(string url) {
            Uri uri;
            if (!EndpointPolicy.IsArtworkEndpoint(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri)) return null;
            try {
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Timeout = 4000; request.ReadWriteTimeout = 4000; request.AllowAutoRedirect = false;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream()) {
                    byte[] buffer = new byte[16384]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                        if (output.Length + count > 4000000) return null;
                        output.Write(buffer, 0, count);
                    }
                    return output.ToArray();
                }
            } catch { return null; }
        }
        static bool UsableFrame(Image image) {
            if (image == null) return false;
            var bitmap = image as Bitmap; if (bitmap == null) return true;
            int min = 255, max = 0;
            for (int x = 1; x < 9; x++) for (int y = 1; y < 6; y++) {
                var c = bitmap.GetPixel(bitmap.Width * x / 10, bitmap.Height * y / 7);
                int value = (c.R + c.G + c.B) / 3; min = Math.Min(min, value); max = Math.Max(max, value);
            }
            return max > 20 && max - min > 12;
        }
        public byte[] Render(string channel, StreamState state, bool live, bool refreshFrame = true) {
            byte[] preview = null, cover = null;
            string framePath = Path.Combine(directory, "last-preview.jpg"), idPath = Path.Combine(directory, "last-preview-stream.txt");
            if (live && refreshFrame && !string.IsNullOrEmpty(state.ThumbnailUrl)) {
                string separator = state.ThumbnailUrl.Contains("?") ? "&" : "?";
                preview = Download(state.ThumbnailUrl + separator + "relay=" + (DateTime.UtcNow.Ticks / TimeSpan.FromSeconds(Polling.PreviewSeconds).Ticks));
                using (var candidate = Decode(preview)) if (!UsableFrame(candidate)) preview = null;
                if (preview != null) try {
                    Directory.CreateDirectory(directory); File.WriteAllBytes(framePath, preview); File.WriteAllText(idPath, state.StreamId);
                } catch { }
            }
            if (preview == null) try {
                if (File.Exists(idPath) && File.ReadAllText(idPath) == state.StreamId && File.Exists(framePath)) preview = File.ReadAllBytes(framePath);
            } catch { }
            if (!string.IsNullOrEmpty(state.GameArtUrl)) {
                if (!covers.TryGetValue(state.GameArtUrl, out cover)) {
                    cover = Download(state.GameArtUrl);
                    if (cover != null) { if (covers.Count >= 12) covers.Clear(); covers[state.GameArtUrl] = cover; }
                }
            }
            using (var frameImage = Decode(preview)) using (var coverImage = Decode(cover))
                return CardRenderer.Render(channel, state, live, frameImage, coverImage);
        }
    }
}
