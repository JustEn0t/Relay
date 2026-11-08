using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Relay {
    public class ActivityEntry {
        public string Time { get; set; }
        public string Text { get; set; }
    }
    public class RelayWindow {
        readonly Store store;
        readonly SavedData data;
        readonly bool previewMode;
        readonly ObservableCollection<ActivityEntry> activity = new ObservableCollection<ActivityEntry>();
        readonly DispatcherTimer timer = new DispatcherTimer();
        readonly Brush accent = new SolidColorBrush(Color.FromRgb(185,160,255));
        Remote remote;
        Engine engine;
        bool running, busy, closeRequested, connected, endedPreview, ongoingPreviewSelected, cleanupBusy;
        DateTime nextCheck, checkedAt, nextCleanup;
        byte[] livePreview, ongoingPreview, endPreview, dashboardPreview, latestRemoteImage;
        readonly TextBox channel, destination, clientId;
        readonly PasswordBox telegram, secret;
        public readonly Window Window;

        T Get<T>(string name) where T : class { return Window.FindName(name) as T; }
        Button Button(string name) { return Get<Button>(name); }
        void Text(string name, string value) { Get<TextBlock>(name).Text = value; }
        void Click(string name, Action action) { Button(name).Click += delegate { action(); }; }
        void ClickAsync(string name, Func<Task> action) { Button(name).Click += async delegate { await action(); }; }
        static byte[] Resource(string name) {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) {
                if (input == null) return null;
                using (var output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
            }
        }
        static BitmapImage Image(byte[] bytes) {
            if (bytes == null) return null;
            using (var input = new MemoryStream(bytes)) {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }
        }
        public RelayWindow(Store store, SavedData data, bool resume, bool previewMode, string capturePath = null, string capturePage = "Overview") {
            this.store = store; this.data = data; this.previewMode = previewMode;
            byte[] shell = Resource("Relay.Shell.xaml");
            using (var input = new MemoryStream(shell)) Window = (Window)XamlReader.Load(input);
            WindowChrome.SetWindowChrome(Window, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            var icon = Resource("Relay.Icon.ico"); if (icon != null) Window.Icon = BitmapFrame.Create(new MemoryStream(icon));
            Get<System.Windows.Controls.Image>("HeroImage").Source = Image(Resource("Relay.Cover.png"));
            livePreview = Resource("Relay.SampleLive.jpg"); endPreview = Resource("Relay.SampleEnded.jpg");
            ongoingPreview = Resource("Relay.SampleOngoing.jpg"); dashboardPreview = livePreview;
            ShowImages();
            channel = Get<TextBox>("ChannelField"); destination = Get<TextBox>("DestinationField"); clientId = Get<TextBox>("ClientIdField");
            telegram = Get<PasswordBox>("TelegramField"); secret = Get<PasswordBox>("ClientSecretField");
            channel.Text = data.Preferences.Channel; destination.Text = data.Preferences.TelegramChannel; clientId.Text = data.Preferences.TwitchClientId;
            telegram.Password = data.Preferences.TelegramToken; secret.Password = data.Preferences.TwitchClientSecret;
            UpdateIdentity(null);
            Click("NavOverview", () => Navigate("Overview")); Click("NavConnections", () => Navigate("Connections"));
            Click("NavDesign", () => Navigate("Design")); Click("OpenDesign", () => Navigate("Design"));
            Click("NavGuide", () => Navigate("Guide")); Click("HeroGuide", () => Navigate("Guide"));
            Click("GuideConnections", () => Navigate("Connections"));
            Click("GuideBotFather", () => OpenLink("https://t.me/BotFather"));
            Click("GuideTwitch", () => OpenLink("https://dev.twitch.tv/console/apps"));
            Click("GuideDocs", () => OpenLink("https://dev.twitch.tv/docs/authentication/register-app/"));
            Click("CopyRedirect", () => { try { Clipboard.SetText("https://localhost"); SetStatus("Redirect URL скопирован.", false); } catch { SetStatus("Не удалось скопировать. Redirect URL: https://localhost", true); } });
            Click("SidebarAccount", () => Navigate("Connections"));
            Click("WindowMin", () => Window.WindowState = WindowState.Minimized);
            Click("WindowMax", () => Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            Click("WindowClose", () => Window.Close());
            Get<Grid>("TitleBar").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (e.OriginalSource is Button) return;
                if (e.ClickCount == 2) Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else if (e.ButtonState == MouseButtonState.Pressed) Window.DragMove();
            };
            ClickAsync("StartButton", ToggleMonitoring);
            Click("StopForSettings", Stop);
            ClickAsync("CheckButton", async () => { await Verify(false); });
            ClickAsync("FindButton", FindChannel);
            ClickAsync("RefreshPreview", RefreshPreview);
            ClickAsync("TestButton", TestPost);
            Click("RecoverButton", Recover);
            ClickAsync("RetryDeletionButton", RetryDeletions);
            Click("LiveTab", () => { endedPreview = false; ongoingPreviewSelected = false; ShowImages(); });
            Click("OngoingTab", () => { endedPreview = false; ongoingPreviewSelected = true; ShowImages(); });
            Click("EndedTab", () => { endedPreview = true; ongoingPreviewSelected = false; ShowImages(); });
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += async delegate {
                RefreshCountdown();
                if (!busy && !previewMode && DateTime.UtcNow >= nextCleanup && DeletionQueue.HasDue(data, DateTime.UtcNow)) await Cleanup();
                if (running && !busy && DateTime.UtcNow >= nextCheck) await Poll();
            };
            Window.Closing += delegate(object sender, CancelEventArgs e) {
                running = false;
                if (busy) { closeRequested = true; e.Cancel = true; SetStatus("Завершаю текущий запрос…", false); }
            };
            Window.Closed += delegate { timer.Stop(); };
            Window.ContentRendered += async delegate {
                if (resume && !previewMode) await Verify(true);
                if (!string.IsNullOrEmpty(capturePath)) {
                    Navigate(capturePage); Window.UpdateLayout();
                    var content = Window.Content as FrameworkElement;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(capturePath)) encoder.Save(output);
                }
            };
            Navigate(HasKeys() ? "Overview" : "Guide");
            SetStatus(data.Stream.Sending ? "Проверь канал: отправка предыдущего анонса не подтверждена." : "Настройки загружены. RELAY готов к работе.", false);
            AddActivity("Настройки загружены с этого компьютера.");
            if (previewMode) AddActivity("Просмотр нового интерфейса. Мониторинг работает в текущей версии.");
            RefreshControls(); timer.Start();
        }
        void Navigate(string page) {
            foreach (string name in new[] { "Overview", "Connections", "Design", "Guide" }) {
                Get<FrameworkElement>(name + "Page").Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
                Button("Nav" + name).Background = name == page ? new SolidColorBrush(Color.FromRgb(47,39,65)) : Brushes.Transparent;
                Button("Nav" + name).Foreground = name == page ? accent : new SolidColorBrush(Color.FromRgb(167,157,183));
            }
            Text("Breadcrumb", "Рабочее пространство  /  " + (page == "Overview" ? "Обзор" : page == "Connections" ? "Подключения" : page == "Design" ? "Карточки" : "Как подключить"));
        }
        void OpenLink(string url) {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { SetStatus("Не удалось открыть браузер. Ссылка есть в инструкции RELAY.", true); }
        }
        void UpdateIdentity(byte[] avatar) {
            string login = data.Preferences.Channel ?? "";
            string name = string.IsNullOrWhiteSpace(data.Preferences.DisplayName) ? login : data.Preferences.DisplayName;
            Text("SidebarChannel", string.IsNullOrEmpty(name) ? "Подключить Twitch" : name);
            Text("SidebarIdentityHint", string.IsNullOrEmpty(login) ? "Канал не выбран" : "@" + login);
            Text("SidebarInitial", string.IsNullOrEmpty(name) ? "+" : name.Substring(0, 1).ToUpperInvariant());
            BitmapImage picture = null;
            try { if (avatar != null) picture = Image(avatar); } catch { }
            Get<System.Windows.Controls.Image>("SidebarAvatar").Source = picture;
            Get<System.Windows.Controls.Image>("SidebarAvatar").Visibility = picture == null ? Visibility.Collapsed : Visibility.Visible;
        }
        void AddActivity(string message) {
            activity.Insert(0, new ActivityEntry { Time = DateTime.Now.ToString("HH:mm"), Text = message });
            while (activity.Count > 20) activity.RemoveAt(activity.Count - 1);
            Get<ListBox>("ActivityList").ItemsSource = activity;
        }
        void SetStatus(string message, bool error) {
            Text("StatusText", message);
            Get<TextBlock>("StatusText").Foreground = error ? new SolidColorBrush(Color.FromRgb(237,153,193)) : new SolidColorBrush(Color.FromRgb(157,147,171));
            Get<System.Windows.Shapes.Ellipse>("StatusDot").Fill = error ? new SolidColorBrush(Color.FromRgb(237,153,193)) : accent;
        }
        void RefreshControls() {
            bool editable = !running && !busy && !previewMode;
            foreach (var input in new Control[] { channel, destination, clientId, telegram, secret }) input.IsEnabled = editable;
            foreach (string name in new[] { "CheckButton", "FindButton", "TestButton" }) Button(name).IsEnabled = editable;
            Button("RefreshPreview").IsEnabled = !busy && !previewMode;
            Button("StartButton").IsEnabled = !previewMode && (!busy || running);
            Button("StartButton").Content = running ? "Остановить бота" : HasKeys() ? "Включить анонсы" : "Подключить каналы";
            Button("StartButton").Background = running ? new SolidColorBrush(Color.FromRgb(52,40,62)) : accent;
            Button("StartButton").Foreground = running ? new SolidColorBrush(Color.FromRgb(232,216,251)) : new SolidColorBrush(Color.FromRgb(23,18,31));
            Button("StopForSettings").IsEnabled = !previewMode;
            Get<Border>("RunningNotice").Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            Get<Border>("RecoveryPanel").Visibility = data.Stream.Sending ? Visibility.Visible : Visibility.Collapsed;
            Button("RecoverButton").IsEnabled = editable;
            Button("RetryDeletionButton").IsEnabled = !busy && !previewMode;
            string liveId = data.Stream.StreamId ?? "";
            Text("MonitorValue", busy && !running ? cleanupBusy ? "Удаляем анонс…" : "Подключаем…" : running ? liveId.Length > 0 ? "В эфире" : "Ждёт эфир" : "На паузе");
            Text("MonitorPillText", running ? liveId.Length > 0 ? "В ЭФИРЕ" : "СЛЕДИТ ЗА ЭФИРОМ" : "НА ПАУЗЕ");
            Get<Border>("MonitorPill").Background = new SolidColorBrush(running ? Color.FromRgb(39,58,52) : Color.FromRgb(51,40,61));
            Text("ConnectionValue", connected ? "Подключено" : HasKeys() ? "Сохранено" : "Нужна настройка");
            Text("ConnectionHint", connected ? data.Preferences.Channel + " → Telegram" : HasKeys() ? "Проверим при запуске" : "Открой подключения");
            Text("GuideProgress", connected ? "Подключения проверены. Включи анонсы в разделе «Обзор»." : HasKeys() ? "Ключи сохранены. Проверь подключение перед первым запуском." : "Сначала подключи своего бота и канал Twitch.");
            RefreshCountdown();
        }
        bool HasKeys() { return !string.IsNullOrWhiteSpace(data.Preferences.TelegramToken) && !string.IsNullOrWhiteSpace(data.Preferences.TwitchClientId) && !string.IsNullOrWhiteSpace(data.Preferences.TwitchClientSecret) && !string.IsNullOrWhiteSpace(data.Preferences.TelegramChannel); }
        void RefreshCountdown() {
            Text("CheckedValue", checkedAt == default(DateTime) ? "—" : checkedAt.ToLocalTime().ToString("HH:mm:ss"));
            Text("NextCheckHint", busy ? cleanupBusy ? "Удаляем старый анонс…" : "Проверяем подключение…" : running ? "Следующая через " + Math.Max(0, (int)Math.Ceiling((nextCheck - DateTime.UtcNow).TotalSeconds)) + " сек" : "Включи анонсы для старта");
            // The worker may be adding/removing deletion jobs; refresh this list only after it finishes.
            if (busy) return;
            var pending = data.PendingDeletions ?? new List<PendingDeletion>();
            int failures = pending.Count(x => x.Failed);
            var waiting = pending.Where(x => !x.Failed).OrderBy(x => x.NextAttempt > x.DeleteAt ? x.NextAttempt : x.DeleteAt).ToList();
            string deletionHint = "Автоудаление анонса — через 1 ч после эфира";
            if (waiting.Count > 0) {
                DateTime due = waiting[0].NextAttempt > waiting[0].DeleteAt ? waiting[0].NextAttempt : waiting[0].DeleteAt;
                deletionHint = "К удалению: " + waiting.Count + " · " + (due <= DateTime.UtcNow ? "при ближайшей проверке" : "ближайшее " + due.ToLocalTime().ToString("HH:mm"));
            }
            if (failures > 0) deletionHint += " · не удалось удалить: " + failures;
            Text("DeletionHint", deletionHint);
            Button("RetryDeletionButton").Visibility = failures > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void ShowImages() {
            Get<System.Windows.Controls.Image>("DashboardPreview").Source = Image(dashboardPreview ?? livePreview);
            Get<System.Windows.Controls.Image>("DesignPreview").Source = Image(endedPreview ? endPreview : ongoingPreviewSelected ? ongoingPreview : livePreview);
            Button("LiveTab").Background = !endedPreview && !ongoingPreviewSelected ? new SolidColorBrush(Color.FromRgb(58,44,80)) : new SolidColorBrush(Color.FromRgb(37,36,47));
            Button("OngoingTab").Background = ongoingPreviewSelected ? new SolidColorBrush(Color.FromRgb(58,44,80)) : new SolidColorBrush(Color.FromRgb(37,36,47));
            Button("EndedTab").Background = endedPreview ? new SolidColorBrush(Color.FromRgb(58,44,80)) : new SolidColorBrush(Color.FromRgb(37,36,47));
            Text("DesignHint", endedPreview ? "Итог: длительность, основная игра и история. Через час пост удалится." : ongoingPreviewSelected ? "Через минуту после анонса: «ведёт трансляцию», текущий онлайн и длительность." : "Первая минута после анонса: «начал стрим» и время старта трансляции.");
        }
        bool SavePreferences() {
            if (previewMode) return false;
            var next = new Preferences { Channel = channel.Text.Trim().ToLowerInvariant(), TelegramChannel = destination.Text.Trim(),
                TelegramToken = telegram.Password.Trim(), TwitchClientId = clientId.Text.Trim(), TwitchClientSecret = secret.Password.Trim() };
            if (data.Preferences.Channel == next.Channel) {
                next.DisplayName = data.Preferences.DisplayName; next.ProfileImageUrl = data.Preferences.ProfileImageUrl;
            }
            if (!Regex.IsMatch(next.Channel, "^[a-z0-9_]{1,25}$") || !Regex.IsMatch(next.TelegramChannel, "^(@[a-zA-Z0-9_]{5,}|-100[0-9]{5,})$") ||
                !Regex.IsMatch(next.TelegramToken, "^[0-9]+:[a-zA-Z0-9_-]+$") || !Regex.IsMatch(next.TwitchClientId, "^[a-zA-Z0-9]+$") || !Regex.IsMatch(next.TwitchClientSecret, "^[a-zA-Z0-9]+$")) {
                SetStatus("Заполни канал Twitch, Telegram-канал и ключи подключения.", true); Navigate("Connections"); return false;
            }
            var old = data.Preferences;
            if (!string.IsNullOrEmpty(data.Stream.StreamId) && (old.Channel != next.Channel || old.TelegramChannel != next.TelegramChannel || old.TelegramToken != next.TelegramToken)) {
                SetStatus("Дождись завершения текущего эфира перед сменой канала или бота.", true); return false;
            }
            try {
                data.Preferences = next; store.Save(data);
                remote = new Remote(next); engine = new Engine(data, remote, () => store.Save(data));
                UpdateIdentity(null); return true;
            } catch { data.Preferences = old; SetStatus("Не удалось сохранить защищённые настройки.", true); return false; }
        }
        async Task<bool> Work(Func<string> action, bool monitorAction = true) {
            if (busy || previewMode) return false;
            busy = true; RefreshControls(); bool success = false; int delay = Polling.Seconds;
            try {
                string result = await Task.Run(action); if (monitorAction) checkedAt = DateTime.UtcNow; SetStatus(result, false); success = true;
                if (remote != null && remote.LatestCardBytes != null && !object.ReferenceEquals(latestRemoteImage, remote.LatestCardBytes)) {
                    latestRemoteImage = remote.LatestCardBytes; dashboardPreview = latestRemoteImage; ShowImages();
                }
            } catch (ServiceError error) {
                SetStatus(error.Message, true); AddActivity(error.Message); delay = Math.Min(3600, error.RetryAfter);
                if (data.Stream.Sending) running = false;
            } catch (IOException) {
                running = false; SetStatus("Мониторинг остановлен: не удалось сохранить состояние.", true); AddActivity("Ошибка сохранения. Мониторинг на паузе.");
            } catch {
                running = false; SetStatus("Операция остановлена. Проверь подключение и доступ к файлам.", true); AddActivity("Операция не выполнена. Проверь подключение.");
            } finally {
                busy = false; if (monitorAction) nextCheck = DateTime.UtcNow.AddSeconds(delay); RefreshControls();
                if (closeRequested) Window.Close();
            }
            return success;
        }
        async Task<bool> Verify(bool begin) {
            if (!SavePreferences()) return false;
            SetStatus("Проверяем Twitch и Telegram…", false);
            byte[] avatar = null;
            bool success = await Work(() => {
                string result = remote.Check();
                data.Preferences.DisplayName = remote.ConnectedDisplayName;
                data.Preferences.ProfileImageUrl = remote.ConnectedProfileImageUrl;
                store.Save(data); avatar = ArtworkCache.Download(remote.ConnectedProfileImageUrl);
                return result;
            }); connected = success;
            if (success) { UpdateIdentity(avatar); AddActivity("Twitch и Telegram подключены."); }
            RefreshControls();
            if (success && !closeRequested) await LoadPreviews();
            if (success && begin && !closeRequested) {
                if (data.Stream.Sending) { SetStatus("Проверь прошлый анонс перед разрешением повтора.", true); return false; }
                running = true; AddActivity("Мониторинг включён. Проверка каждые 10 секунд."); RefreshControls(); await Poll();
            }
            return success;
        }
        async Task ToggleMonitoring() { if (running) Stop(); else if (!HasKeys()) Navigate("Guide"); else await Verify(true); }
        void Stop() { running = false; SetStatus(busy ? "Завершаем текущий запрос…" : "Мониторинг на паузе.", false); AddActivity("Мониторинг остановлен."); RefreshControls(); }
        async Task Poll() {
            string beforeId = data.Stream.StreamId, beforeTitle = data.Stream.Title, beforeGame = data.Stream.Game;
            bool success = await Work(() => engine.Step(DateTime.UtcNow));
            if (!success) return;
            if (beforeId != data.Stream.StreamId) AddActivity(string.IsNullOrEmpty(data.Stream.StreamId) ? "Эфир завершён. Анонс удалится через час." : "Стрим начался. Анонс опубликован.");
            else if (beforeTitle != data.Stream.Title || beforeGame != data.Stream.Game) AddActivity("Название или игра изменились. Карточка обновлена.");
        }
        async Task Cleanup() {
            if (busy || previewMode) return;
            DeletionResult result = null; cleanupBusy = true;
            bool success = await Work(() => {
                var queue = new DeletionQueue(data, new Remote(data.Preferences), () => store.Save(data));
                result = queue.Process(DateTime.UtcNow);
                return result.Deleted > 0 ? "Автоудаление: обработано анонсов " + result.Deleted : "Очередь удаления проверена.";
            }, false);
            cleanupBusy = false; nextCleanup = DateTime.UtcNow.AddSeconds(Polling.Seconds); RefreshControls();
            if (!success || result == null) return;
            if (result.Deleted > 0) AddActivity("Автоматически удалено анонсов: " + result.Deleted + ".");
            if (result.Deferred > 0 || result.Failed > 0) {
                string warning = result.Failed > 0 ? "Telegram не разрешил удалить старый анонс. Проверь канал и права бота." : "Удаление анонса отложено. " + result.LastError;
                SetStatus(warning, true); AddActivity(warning);
            }
        }
        async Task RetryDeletions() {
            if (busy || previewMode || data.PendingDeletions == null) return;
            DateTime now = DateTime.UtcNow;
            foreach (var item in data.PendingDeletions) if (item.Failed && item.DeleteAt <= now) { item.Failed = false; item.NextAttempt = now; }
            try { store.Save(data); } catch { SetStatus("Не удалось сохранить повтор удаления.", true); return; }
            await Cleanup();
        }
        async Task RefreshPreview() {
            if (previewMode || busy) return;
            if (!running && !SavePreferences()) return;
            await LoadPreviews();
        }
        async Task LoadPreviews() {
            byte[] startCard = null, duringCard = null, finishCard = null;
            bool success = await Work(() => {
                var state = remote.GetCardPreviewState(); var images = new ArtworkCache();
                startCard = images.Render(data.Preferences.Channel, state, true);
                state.Ongoing = true; duringCard = images.Render(data.Preferences.Channel, state, true, false);
                finishCard = images.Render(data.Preferences.Channel, state, false);
                return "Карточки обновлены. Предпросмотр без публикации.";
            });
            if (success) { livePreview = startCard; ongoingPreview = duringCard; endPreview = finishCard;
                dashboardPreview = !string.IsNullOrEmpty(data.Stream.StreamId) && data.Stream.Ongoing ? duringCard : startCard;
                ShowImages(); AddActivity("Предпросмотр обновлён из данных Twitch."); }
        }
        async Task TestPost() {
            if (!SavePreferences()) return;
            if (MessageBox.Show(Window, "Отправить один тестовый пост в " + data.Preferences.TelegramChannel + "?", "Проверка RELAY", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await Work(() => { remote.Check(); remote.Send("🧪 <b>Проверка RELAY</b>\n\nАнонсы канала " + Engine.Escape(data.Preferences.Channel) + " подключены.", false); return "Тестовый пост отправлен."; });
        }
        void Recover() {
            if (previewMode || busy || running) return;
            if (MessageBox.Show(Window, "Проверь Telegram-канал. Прошлый анонс мог уже появиться. Разрешить повторную отправку с риском дубля?", "Повтор анонса", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            data.Stream.Sending = false;
            try { store.Save(data); SetStatus("Повтор разрешён. Включи анонсы.", false); }
            catch { data.Stream.Sending = true; SetStatus("Не удалось сохранить разрешение на повтор.", true); }
            RefreshControls();
        }
        async Task FindChannel() {
            if (previewMode || busy || running) return;
            string token = telegram.Password.Trim();
            if (!Regex.IsMatch(token, "^[0-9]+:[a-zA-Z0-9_-]+$")) { SetStatus("Введи актуальный токен BotFather.", true); return; }
            if (!string.IsNullOrEmpty(data.Stream.StreamId)) { SetStatus("Дождись завершения текущего эфира перед сменой канала.", true); return; }
            List<TelegramTarget> targets = null;
            if (!await Work(() => { targets = new Remote(new Preferences { TelegramToken = token }).FindTelegramChannels(); return "Поиск канала завершён."; })) return;
            if (targets.Count == 0) { SetStatus("Добавь бота администратором, опубликуй любой пост в канале и повтори поиск.", true); return; }
            var selected = targets[0];
            if (targets.Count > 1) {
                var dialog = new Window { Title = "Выбери Telegram-канал", Owner = Window, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 520, Height = 180, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(25,25,32)) };
                var panel = new StackPanel { Margin = new Thickness(20) };
                var choices = new ComboBox { ItemsSource = targets, SelectedIndex = 0, Margin = new Thickness(0,0,0,18), MinHeight = 32 };
                var accept = new Button { Content = "Выбрать", MinHeight = 34 };
                accept.Click += delegate { dialog.DialogResult = true; }; panel.Children.Add(choices); panel.Children.Add(accept); dialog.Content = panel;
                if (dialog.ShowDialog() != true) return; selected = (TelegramTarget)choices.SelectedItem;
            }
            string oldToken = data.Preferences.TelegramToken, oldChannel = data.Preferences.TelegramChannel;
            try { data.Preferences.TelegramToken = token; data.Preferences.TelegramChannel = selected.Id; store.Save(data); destination.Text = selected.Id; SetStatus("Telegram-канал найден: " + selected.Title, false); AddActivity("Подключён Telegram-канал «" + selected.Title + "»."); }
            catch { data.Preferences.TelegramToken = oldToken; data.Preferences.TelegramChannel = oldChannel; SetStatus("Не удалось сохранить найденный канал.", true); }
        }
    }
    public class RelayApp {
        [STAThread] public static int Main(string[] args) {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            bool first, preview = Array.IndexOf(args, "--preview-ui") >= 0 || Array.IndexOf(args, "--check-ui") >= 0;
            // Reuse the existing singleton and settings so upgrading cannot run two publishers.
            using (var mutex = new Mutex(true, preview ? "Local\\RelayUIReview" : "Local\\EN0TStreamBotV1", out first)) {
                if (!first) { MessageBox.Show("RELAY уже запущен.", "RELAY"); return 0; }
                try {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    bool fresh = Array.IndexOf(args, "--fresh") >= 0;
                    var store = fresh ? new Store(Path.Combine(Path.GetTempPath(), "RelayFreshPreview")) : Store.OpenDefault(null, !preview);
                    int captureIndex = Array.IndexOf(args, "--capture-ui");
                    string capturePath = captureIndex >= 0 && captureIndex + 1 < args.Length ? args[captureIndex + 1] : null;
                    int pageIndex = Array.IndexOf(args, "--capture-page");
                    string capturePage = pageIndex >= 0 && pageIndex + 1 < args.Length ? args[pageIndex + 1] : "Overview";
                    if (!new[] { "Overview", "Connections", "Design", "Guide" }.Contains(capturePage)) capturePage = "Overview";
                    var ui = new RelayWindow(store, fresh ? new SavedData() : store.Load(), Array.IndexOf(args, "--resume") >= 0, preview, capturePath, capturePage);
                    if (Array.IndexOf(args, "--check-ui") >= 0) {
                        ui.Window.Show(); ui.Window.UpdateLayout(); ui.Window.Close(); Console.WriteLine("PASS: RELAY WPF layout, resources and saved settings loaded."); return 0;
                    }
                    app.MainWindow = ui.Window; app.Run(ui.Window); return 0;
                } catch (Exception error) {
                    if (Array.IndexOf(args, "--check-ui") >= 0) Console.WriteLine(error.GetType().Name + ": " + error.Message);
                    else MessageBox.Show("Не удалось открыть RELAY. Проверь доступ к защищённым настройкам Windows.", "RELAY");
                    return 1;
                }
            }
        }
    }
}
