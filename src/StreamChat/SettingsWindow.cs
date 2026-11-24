using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Media;
using StreamChat.Core;
using StreamChat.Core.Chat;
using StreamChat.Core.Updates;

namespace StreamChat;

// Без ComboBox, подсказок и контекстных меню: их всплывающие окна не скрыть от захвата
internal sealed class SettingsWindow : ProtectedWindow
{
    private static readonly string[] TextSwatches =
        ["#FFFFFF", "#F4F6F8", "#B8C0CC", "#000000", "#FFE14D", "#FF9F43", "#FF6B6B", "#FF8AD8", "#BF9BFF", "#6CB6FF", "#4FE3C1", "#7BE36B"];

    private static readonly string[] BackgroundSwatches =
        ["#101318", "#000000", "#2B2F36", "#FFFFFF", "#1B2440", "#24183A", "#3A1420", "#12332E", "#3A2A12", "#4A4A4A"];

    private readonly Lazy<string[]> _fonts = new(() => Fonts.SystemFontFamilies
        .Select(family => family.Source)
        .Distinct()
        .Order(StringComparer.CurrentCultureIgnoreCase)
        .ToArray());

    private readonly Dictionary<Platform, SourceStatus> _sources = [];
    private readonly Dictionary<Platform, TextBlock> _sourceTexts = [];
    private readonly Dictionary<Platform, string> _viewers = [];
    private readonly Dictionary<Platform, TextBlock> _viewerTexts = [];
    private SourceStatus _obsStatus = new(SourceHealth.Off, "выключено");
    private TextBlock? _obsText;
    private string _updateText = "";
    private TextBlock? _updateBlock;
    private AppSettings _value = new();
    private bool _starting;
    private string _notice = "";
    private TextBlock? _noticeText;
    private TabControl? _tabs;

    public event Action<AppSettings>? Changed;
    public event Action? MoveRequested;
    public event Action? Confirmed;
    public event Action? TestRequested;
    public event Action? ConnectRequested;
    public event Action<bool>? RecordingHotKey;
    public event Action<string>? ProfileRequested;
    public event Action<bool>? SignInRequested;
    public event Action? SignOutRequested;
    public event Action? UpdateCheckRequested;

    public SettingsWindow()
    {
        Title = "StreamChat: настройки";
        Width = 860;
        Height = Math.Min(760, SystemParameters.WorkArea.Height - 20);
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 14;
        // меню поля ввода открывается раньше ContextMenuOpening, поэтому гасим сам правый щелчок
        PreviewMouseRightButtonUp += (_, e) => e.Handled = true;
        PreviewKeyUp += (_, e) => e.Handled |= e.Key == Key.Apps;
        PreviewKeyDown += (_, e) =>
            e.Handled |= e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift;
    }

    /// <param name="starting">Окно открыто перед запуском чата, а не из меню.</param>
    public void Prepare(AppSettings value, bool starting)
    {
        _value = value;
        _starting = starting;
        _notice = "";
        _tabs = null;
        Rebuild();
    }

    /// <summary>Значения поменялись снаружи: мышью или из меню значка.</summary>
    public void Update(AppSettings value)
    {
        if (value == _value) return;
        _value = value;
        Rebuild();
    }

    public void SetNotice(string notice)
    {
        _notice = notice;
        if (_noticeText is null) return;
        _noticeText.Text = notice;
        _noticeText.Visibility = notice.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetSourceStatus(Platform platform, SourceStatus status)
    {
        _sources[platform] = status;
        if (_sourceTexts.TryGetValue(platform, out var text)) ShowStatus(text, status);
    }

    public void SetObsStatus(SourceStatus status)
    {
        _obsStatus = status;
        if (_obsText is not null) ShowStatus(_obsText, status);
    }

    public void SetUpdateText(string text)
    {
        _updateText = text;
        if (_updateBlock is not null) _updateBlock.Text = text;
    }

    private static void ShowStatus(TextBlock text, SourceStatus status)
    {
        text.Text = $"Состояние: {status.Text}";
        text.Foreground = status.Health switch
        {
            SourceHealth.Connected => Brushes.SeaGreen,
            SourceHealth.Error => Brushes.Firebrick,
            SourceHealth.Waiting => Brushes.DarkGoldenrod,
            _ => Brushes.DimGray,
        };
    }

    protected override void OnShownTransparent()
    {
        // скрытое свёрнутым окно WindowState не разворачивает
        if (NativeMethods.IsIconic(Handle)) NativeMethods.ShowWindow(Handle, NativeMethods.SwRestore);
    }

    protected override void OnMessage(int message, nint lParam)
    {
        if (message != NativeMethods.WmWindowPosChanging || !IsVisible || IsActive) return;

        var position = Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam);
        // открытые позади игры настройки WPF активирует при смене DPI
        const uint keep = NativeMethods.SwpShowWindow | NativeMethods.SwpHideWindow;
        if ((position.Flags & keep) != 0 || (position.Flags & NativeMethods.SwpNoZOrder) == 0 || NativeMethods.IsIconic(Handle)) return;
        position.Flags |= NativeMethods.SwpNoActivate;
        Marshal.StructureToPtr(position, lParam, false);
    }

    public void SetViewerText(Platform platform, string text)
    {
        _viewers[platform] = text;
        if (_viewerTexts.TryGetValue(platform, out var block)) block.Text = text;
    }

    private void Change(Func<AppSettings, AppSettings> change)
    {
        var next = change(_value).Normalized();
        if (next == _value) return;
        _value = next;
        Changed?.Invoke(next);
    }

    private void Rebuild()
    {
        var selectedTab = _tabs?.SelectedIndex ?? 0;

        _sourceTexts.Clear();
        _tabs = new TabControl { Margin = new Thickness(0, 8, 0, 10), TabStripPlacement = Dock.Left };
        AddTab("Чат", ChatTab());
        AddTab("Донаты", DonationsTab());
        AddTab("OBS", ObsTab());
        AddTab("Зрители", ViewersTab());
        AddTab("Важное и фильтр", FilterTab());
        AddTab("Профили игр", ProfilesTab());
        AddTab("Расположение", PlacementTab());
        AddTab("Текст", TextTab());
        AddTab("Фон", BackgroundTab());
        AddTab("Поведение", BehaviourTab());
        AddTab("Клавиши", KeysTab());
        _tabs.SelectedIndex = selectedTab;

        var top = new StackPanel();
        top.Children.Add(Note("Изменения сразу видны на образце чата на экране. Это окно и чат скрыты от захвата."));
        _noticeText = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        top.Children.Add(_noticeText);
        SetNotice(_notice);

        var confirm = PlainButton(_starting ? "Запустить чат" : "Сохранить", () => Confirmed?.Invoke());
        confirm.FontWeight = FontWeights.Bold;
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(PlainButton("Тестовое сообщение", () => TestRequested?.Invoke()));
        Button? reset = null;
        reset = PlainButton("Сбросить оформление", () =>
        {
            // второй щелчок подтверждает: на первом запуске «Отмены» нет
            if (reset!.Tag is not true)
            {
                reset.Tag = true;
                reset.Content = "Точно сбросить? Ещё раз";
                return;
            }

            Change(s => s.ResetLook());
            Rebuild();
        });
        buttons.Children.Add(reset);
        buttons.Children.Add(PlainButton(_starting ? "Сохранить и выйти" : "Отмена", Close));
        buttons.Children.Add(confirm);

        var layout = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(buttons);
        layout.Children.Add(_tabs);
        Content = layout;
    }

    private void AddTab(string title, UIElement content) => _tabs!.Items.Add(new TabItem
    {
        Header = title,
        Padding = new Thickness(10, 5, 10, 5),
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
    });

    private StackPanel ChatTab()
    {
        var panel = TabPanel();

        panel.Children.Add(Header("Twitch"));
        var channel = Input(_value.TwitchChannel, 200, v => Change(s => s with { TwitchChannel = v }));
        InputMethod.SetIsInputMethodEnabled(channel, false);
        panel.Children.Add(Field("Имя вашего канала или ссылка на него", channel, channel.Paste, channel.SelectAll));
        panel.Children.Add(Note("Пароль, вход и токен не нужны."));
        panel.Children.Add(SourceText(Platform.Twitch));

        panel.Children.Add(Header("YouTube"));
        var link = Input(_value.YouTubeLink, 300, v => Change(s => s with { YouTubeLink = v }));
        InputMethod.SetIsInputMethodEnabled(link, false);
        panel.Children.Add(Field("Ссылка на эфир (у каждого эфира своя)", link, link.Paste, link.SelectAll));

        var key = new PasswordBox { MaxLength = 200, Padding = new Thickness(4), Password = Secret.Unprotect(_value.YouTubeKeyProtected) ?? "" };
        key.PasswordChanged += (_, _) =>
        {
            var plain = key.Password.Trim();
            Change(s => s with { YouTubeKeyProtected = Secret.Protect(plain) });
        };
        panel.Children.Add(Field("Ваш API-ключ", key, key.Paste, key.SelectAll));

        panel.Children.Add(new Expander { Header = "Как получить ключ", Content = KeyGuide(), Margin = new Thickness(0, 8, 0, 0) });

        panel.Children.Add(new Expander { Header = "Условия и конфиденциальность", Content = Privacy(), Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(Toggle("Принимаю условия YouTube и политику конфиденциальности", _value.YouTubeTermsAccepted,
            v => Change(s => s with { YouTubeTermsAccepted = v })));

        panel.Children.Add(Number("Проверять YouTube не чаще", _value.YouTubeIntervalSeconds, 5, 60, v => $"{v} с",
            v => Change(s => s with { YouTubeIntervalSeconds = v }), step: 5));
        panel.Children.Add(Note("Это же наибольшая задержка сообщений. Бесплатной квоты в сутки хватает при 15 с примерно на 8 часов эфира, при 30 с на 16 часов."));
        panel.Children.Add(SourceText(Platform.YouTube));

        panel.Children.Add(Header("TikTok"));
        panel.Children.Add(Note(
            "Официального доступа к чату TikTok не даёт, поэтому чат идёт через бесплатную программу TikFinity на этом ПК. Это неофициальный способ."));
        panel.Children.Add(new Expander { Header = "Как подключить и чем рискуете", Content = TikTokGuide(), Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(Toggle("Подключать TikTok через TikFinity, риск понимаю", _value.TikTokEnabled,
            v => Change(s => s with { TikTokEnabled = v })));
        panel.Children.Add(SourceText(Platform.TikTok));

        var connect = PlainButton("Подключить и проверить", () => ConnectRequested?.Invoke());
        connect.HorizontalAlignment = HorizontalAlignment.Left;
        connect.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(connect);

        panel.Children.Add(Header("Картинки в сообщениях"));
        panel.Children.Add(Toggle("Эмоуты Twitch, 7TV, BTTV, FFZ и стикеры TikTok", _value.EmoteImages,
            v => Change(s => s with { EmoteImages = v })));
        panel.Children.Add(Note("Качаются с серверов этих сервисов, они видят ваш IP, как при открытии чата в браузере. Обычные эмодзи встроены и работают всегда."));

        panel.Children.Add(Header("Не показывать сообщения от"));
        var ignored = Input(_value.IgnoredNames, 5000, v => Change(s => s with { IgnoredNames = v }));
        ignored.AcceptsReturn = true;
        ignored.TextWrapping = TextWrapping.Wrap;
        ignored.Height = 56;
        panel.Children.Add(ignored);
        panel.Children.Add(Note("Ники через запятую или с новой строки, например: nightbot, streamelements."));
        return panel;
    }

    private static StackPanel KeyGuide()
    {
        var guide = new StackPanel();
        guide.Children.Add(Note(
            "Бесплатно, банковская карта не нужна.\n"
            + "1. Откройте в браузере console.cloud.google.com и войдите.\n"
            + "2. Создайте проект с любым названием.\n"
            + "3. «APIs & Services» → «Library» → YouTube Data API v3 → «Enable».\n"
            + "4. «Credentials» → «Create credentials» → «API key».\n"
            + "5. В «API restrictions» выберите YouTube Data API v3. «Application restrictions» оставьте «None».\n"
            + "6. Скопируйте ключ и нажмите «Вставить» рядом с полем. Не показывайте его и никому не пересылайте."));
        guide.Children.Add(Address("https://console.cloud.google.com/apis/library/youtube.googleapis.com"));
        return guide;
    }

    private static StackPanel TikTokGuide()
    {
        var guide = new StackPanel();
        guide.Children.Add(Note(
            "1. Скачайте TikFinity Desktop App с официального сайта и запустите.\n"
            + "2. В TikFinity укажите своё имя TikTok и подключитесь к эфиру.\n"
            + "3. Не закрывайте её. Здесь поставьте галочку и нажмите «Подключить и проверить».\n"
            + "Риск: условия TikTok запрещают автоматический сбор данных, а TikFinity работает именно так. "
            + "TikTok может ограничить аккаунт, и способ может перестать работать в любой день. "
            + "StreamChat не связан ни с TikTok, ни с TikFinity и пароль TikTok не спрашивает."));
        guide.Children.Add(Address("https://tikfinity.zerody.one"));
        return guide;
    }

    private static StackPanel Privacy()
    {
        var policy = new StackPanel();
        policy.Children.Add(Note(
            "StreamChat использует YouTube API Services. Пользуясь чатом YouTube в программе, вы соглашаетесь с Условиями использования YouTube."));
        policy.Children.Add(Address("https://www.youtube.com/t/terms"));
        policy.Children.Add(Note(
            "Политика конфиденциальности StreamChat.\n"
            + "Что получает программа: публичные сообщения чатов Twitch, YouTube и TikTok, имена и номера их авторов, состояние эфира, "
            + "число зрителей, донаты DonationAlerts (новые и последние из списка, чтобы догнать пропущенные при обрыве), состояние OBS. Это нужно только для показа и держится в памяти до выхода.\n"
            + "Что хранится на этом ПК: настройки, канал, ссылка на эфир, а также зашифрованные средствами Windows ключ YouTube, "
            + "вход DonationAlerts и пароль OBS. Удалить: папка %APPDATA%\\StreamChat.\n"
            + "Куда идут запросы: Twitch (чат и картинки эмоутов), Google (YouTube), DonationAlerts, GitHub (проверка обновлений). "
            + "Номер канала Twitch уходит в 7TV, BetterTTV и FrankerFaceZ за списками эмоутов, имя канала в DecAPI (decapi.me), если включён счётчик Twitch. "
            + "TikTok читается через TikFinity, OBS через её сервер, оба только на этом ПК.\n"
            + "Своих серверов, телеметрии и рекламы нет, файлы cookie не сохраняются и не отправляются. "
            + "Входа в аккаунт YouTube программа не запрашивает. Вопросы о данных: раздел Issues репозитория программы на GitHub."));
        policy.Children.Add(Note("Политика конфиденциальности Google:"));
        policy.Children.Add(Address("https://policies.google.com/privacy"));
        return policy;
    }

    // не ссылкой: открытый браузер увидели бы зрители
    internal static TextBox Address(string address) => new()
    {
        Text = address,
        IsReadOnly = true,
        Padding = new Thickness(3),
        ContextMenu = null,
        Margin = new Thickness(0, 4, 0, 4),
    };

    private TextBlock SourceText(Platform platform)
    {
        var text = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _sourceTexts[platform] = text;
        SetSourceStatus(platform, _sources.GetValueOrDefault(platform) ?? new(SourceHealth.Off, "не настроен"));
        return text;
    }

    private static TextBox Input(string value, int max, Action<string> set)
    {
        var box = new TextBox { Text = value, MaxLength = max, Padding = new Thickness(4), ContextMenu = null };
        box.TextChanged += (_, _) => set(box.Text);
        return box;
    }

    // кнопка вместо меню по правой кнопке: меню не скрыть от захвата
    private static StackPanel Field(string title, Control input, Action paste, Action selectAll)
    {
        System.Windows.Automation.AutomationProperties.SetName(input, title);
        var button = PlainButton("Вставить", () =>
        {
            input.Focus();
            selectAll();
            paste();
        });
        button.Margin = new Thickness(6, 0, 0, 0);

        var row = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(input);

        var field = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        field.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 4, 0, 4) });
        field.Children.Add(row);
        return field;
    }

    private StackPanel DonationsTab()
    {
        var panel = TabPanel();
        panel.Children.Add(Header("DonationAlerts"));
        panel.Children.Add(Note("Донаты придут в ленту чата с суммой. Программа только читает донаты: вывести деньги или что-то изменить она не может. Пропущенные при обрыве связи покажет после переподключения."));

        panel.Children.Add(Note("Входите только до эфира и записи: откроется браузер, его увидят зрители вместе с адресом входа."));
        var offAir = new CheckBox { Content = "Сейчас нет эфира и записи", Margin = new Thickness(0, 9, 0, 0) };
        panel.Children.Add(offAir);
        panel.Children.Add(Note("Если подключена OBS, программа проверит это сама."));
        var signedIn = _value.DonationTokenProtected.Length > 0;
        panel.Children.Add(LeftButton(signedIn ? "Войти заново" : "Войти через браузер", () => SignInRequested?.Invoke(offAir.IsChecked == true)));
        if (signedIn) panel.Children.Add(LeftButton("Выйти из DonationAlerts", () => SignOutRequested?.Invoke()));
        panel.Children.Add(SourceText(Platform.DonationAlerts));
        panel.Children.Add(Note(
            "Вход хранится на этом ПК зашифрованным и действует долго. Отозвать его можно в кабинете DonationAlerts. "
            + "После входа браузер помнит адрес страницы входа в истории: удалите из неё localhost:21214."));
        return panel;
    }

    private StackPanel ObsTab()
    {
        var panel = TabPanel();
        panel.Children.Add(Header("Состояние OBS на экране"));
        panel.Children.Add(Note("Эфир и его время, запись, микрофон, потерянные кадры. Выключенный в эфире микрофон подсвечен красным."));
        panel.Children.Add(Toggle("Показывать", _value.ObsEnabled, v => Change(s => s with { ObsEnabled = v })));

        panel.Children.Add(Note(
            "В OBS: «Сервис» → «Настройки сервера WebSocket» → «Включить сервер WebSocket». "
            + "Пароль там же: «Показать сведения о подключении». Открывайте это окно не в эфире."));
        var password = new PasswordBox { MaxLength = 200, Padding = new Thickness(4), Password = Secret.Unprotect(_value.ObsPasswordProtected) ?? "" };
        password.PasswordChanged += (_, _) =>
        {
            var plain = password.Password;
            Change(s => s with { ObsPasswordProtected = Secret.Protect(plain) });
        };
        panel.Children.Add(Field("Пароль сервера", password, password.Paste, password.SelectAll));

        var port = Input($"{_value.ObsPort}", 5, v =>
        {
            if (int.TryParse(v, out var number)) Change(s => s with { ObsPort = number });
        });
        InputMethod.SetIsInputMethodEnabled(port, false);
        panel.Children.Add(Field("Порт (обычно 4455)", port, port.Paste, port.SelectAll));

        var microphone = Input(_value.ObsMicrophone, 256, v => Change(s => s with { ObsMicrophone = v }));
        panel.Children.Add(Field("Имя микрофона в OBS (пусто: найти самой)", microphone, microphone.Paste, microphone.SelectAll));

        panel.Children.Add(LeftButton("Подключить и проверить", () => ConnectRequested?.Invoke()));
        _obsText = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        ShowStatus(_obsText, _obsStatus);
        panel.Children.Add(_obsText);
        panel.Children.Add(Note("«Нет в сцене» это жёлтое, а не тревога: на сцене «Перерыв» микрофона и не должно быть."));

        panel.Children.Add(Header("Где показывать"));
        panel.Children.Add(PositionGrid("obs-position", _value.ObsPlacement.Position,
            position => Change(s => s with { ObsPlacement = s.ObsPlacement with { Position = position } })));
        panel.Children.Add(LeftButton("Перетащить мышью / готово", () => MoveRequested?.Invoke()));
        panel.Children.Add(Number("Размер", _value.ObsFontSize, 10, 40, v => $"{v}", v => Change(s => s with { ObsFontSize = v })));
        panel.Children.Add(Toggle("В столбик", _value.ObsInColumn, v => Change(s => s with { ObsInColumn = v })));
        return panel;
    }

    private StackPanel FilterTab()
    {
        var panel = TabPanel();
        panel.Children.Add(Header("Выделять важное"));
        panel.Children.Add(Toggle("Цветная полоса и метка", _value.Highlight, v => Change(s => s with { Highlight = v })));
        panel.Children.Add(Note("Обращения к вам, донаты, Super Chat, биты, подарки, подписки, рейды и первое сообщение зрителя на Twitch."));
        var names = Input(_value.MentionNames, 1000, v => Change(s => s with { MentionNames = v }));
        panel.Children.Add(Field("Как вас зовут в чате, через запятую", names, names.Paste, names.SelectAll));
        panel.Children.Add(Note("Имя канала Twitch учитывается само."));

        panel.Children.Add(Header("Не показывать"));
        var words = Input(_value.BlockedWords, 5000, v => Change(s => s with { BlockedWords = v }));
        words.AcceptsReturn = true;
        words.TextWrapping = TextWrapping.Wrap;
        words.Height = 72;
        panel.Children.Add(Field("Слова и фразы: через запятую или с новой строки", words, words.Paste, words.SelectAll));
        panel.Children.Add(Note("Звёздочка в конце: любое окончание, например казино*. Слова от двух букв."));
        panel.Children.Add(Toggle("Сообщения со ссылками", _value.HideLinks, v => Change(s => s with { HideLinks = v })));
        panel.Children.Add(Toggle("Одинаковые сообщения за 30 секунд", _value.HideRepeats, v => Change(s => s with { HideRepeats = v })));
        panel.Children.Add(Note(
            "Донаты, платные сообщения и подписки не прячутся ни фильтром, ни списком скрытых ников: фильтр убирает в них только текст зрителя. "
            + "Поток чата не вытесняет их с экрана первые полминуты. Фильтр действует на новые сообщения."));
        return panel;
    }

    private StackPanel ProfilesTab()
    {
        var panel = TabPanel();
        panel.Children.Add(Header("Профили игр"));
        panel.Children.Add(Note(
            "Профиль помнит место и размер чата, счётчика и OBS, а также непрозрачность. "
            + "Что поменяли при включённом профиле, то он и запомнит при сохранении."));

        foreach (var profile in _value.Profiles)
        {
            var name = profile.Name;
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var delete = PlainButton("Удалить", () =>
            {
                Change(s => s.DeleteProfile(name));
                Rebuild();
            });
            var use = PlainButton("Включить", () => ProfileRequested?.Invoke(name));
            use.IsEnabled = name != _value.ActiveProfile;
            DockPanel.SetDock(delete, Dock.Right);
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(delete);
            row.Children.Add(use);
            row.Children.Add(new TextBlock
            {
                Text = name == _value.ActiveProfile ? $"{name} (включён)" : name,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(row);
        }

        var game = Input("", 40, _ => { });
        panel.Children.Add(Field("Название игры", game, game.Paste, game.SelectAll));
        panel.Children.Add(LeftButton("Создать из того, что сейчас на экране", () =>
        {
            var name = game.Text;
            Change(s => s.AddProfile(name));
            Rebuild();
        }));
        panel.Children.Add(Note($"До {AppSettings.MaxProfiles} профилей. Переключать можно и правой кнопкой по значку → «Профиль игры»."));
        return panel;
    }

    private static Button LeftButton(string title, Action click)
    {
        var button = PlainButton(title, click);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 10, 0, 0);
        return button;
    }

    private StackPanel PlacementTab()
    {
        var panel = TabPanel();
        panel.Children.Add(Header("Где показывать чат"));

        panel.Children.Add(PositionGrid("position", _value.Placement.Position,
            position => Change(s => s with { Placement = s.Placement with { Position = position } })));

        var move = PlainButton("Перетащить мышью / готово", () => MoveRequested?.Invoke());
        move.HorizontalAlignment = HorizontalAlignment.Left;
        move.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(move);
        panel.Children.Add(Note(_value.Placement.Position == Position.Custom
            ? "Сейчас выбрано своё место."
            : "Нажмите, перетащите образец чата в любое место, затем нажмите ещё раз."));

        panel.Children.Add(Number("Ширина окна", (int)_value.Placement.Width, (int)Placement.MinWidth, (int)Placement.MaxWidth,
            v => $"{v}", v => Change(s => s with { Placement = s.Placement with { Width = v } }), step: 10));
        return panel;
    }

    private StackPanel ViewersTab()
    {
        var panel = TabPanel();
        _viewerTexts.Clear();

        panel.Children.Add(Header("Сколько смотрят сейчас"));
        panel.Children.Add(ViewerRow(Platform.Twitch, "Twitch", _value.ShowTwitchViewers,
            v => Change(s => s with { ShowTwitchViewers = v })));
        panel.Children.Add(Note("Официальный API требует вход, поэтому число даёт сторонний сервис DecAPI. Он увидит имя канала и ваш IP."));
        panel.Children.Add(ViewerRow(Platform.YouTube, "YouTube", _value.ShowYouTubeViewers,
            v => Change(s => s with { ShowYouTubeViewers = v })));
        panel.Children.Add(Note("Нужны ключ и ссылка с вкладки «Чат». Тратит 60 единиц квоты в час. Прочерк: число скрыто владельцем или зрителей пока нет."));
        panel.Children.Add(ViewerRow(Platform.TikTok, "TikTok", _value.ShowTikTokViewers,
            v => Change(s => s with { ShowTikTokViewers = v })));
        panel.Children.Add(Note("Присылает TikFinity во время эфира, TikTok должен быть включён на вкладке «Чат»."));
        panel.Children.Add(ViewerRow(Platform.App, "Сумма Twitch и TikTok", _value.ShowViewerTotal,
            v => Change(s => s with { ShowViewerTotal = v })));
        panel.Children.Add(Note("YouTube в сумму не входит: правила YouTube API запрещают складывать его данные с другими."));
        panel.Children.Add(Note("Обновляется раз в минуту. Новый канал или ссылку считает после «Подключить и проверить» или «Сохранить»."));

        panel.Children.Add(Header("Где показывать счётчик"));
        panel.Children.Add(PositionGrid("viewers-position", _value.ViewersPlacement.Position,
            position => Change(s => s with { ViewersPlacement = s.ViewersPlacement with { Position = position } })));
        var move = PlainButton("Перетащить мышью / готово", () => MoveRequested?.Invoke());
        move.HorizontalAlignment = HorizontalAlignment.Left;
        move.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(move);
        panel.Children.Add(Note("Счётчик отдельное окно: двигается отдельно от чата и не исчезает, когда чат молчит."));

        panel.Children.Add(Number("Размер цифр", _value.ViewersFontSize, 10, 40, v => $"{v}", v => Change(s => s with { ViewersFontSize = v })));
        panel.Children.Add(Toggle("В столбик", _value.ViewersInColumn, v => Change(s => s with { ViewersInColumn = v })));
        panel.Children.Add(Note("Шрифт, цвет, фон и непрозрачность берутся от чата."));
        return panel;
    }

    private DockPanel ViewerRow(Platform platform, string title, bool shown, Action<bool> set)
    {
        var value = new TextBlock
        {
            Text = _viewers.GetValueOrDefault(platform, "—"),
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            MinWidth = 110,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _viewerTexts[platform] = value;

        var row = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(Toggle(title, shown, set));
        return row;
    }

    private static Border PositionGrid(string group, Position selected, Action<Position> select)
    {
        (Position? Position, string Title)[] cells =
        [
            (Position.TopLeft, "Слева сверху"), (Position.TopCenter, "Сверху по центру"), (Position.TopRight, "Справа сверху"),
            (Position.MiddleLeft, "Слева посередине"), (null, ""), (Position.MiddleRight, "Справа посередине"),
            (Position.BottomLeft, "Слева снизу"), (Position.BottomCenter, "Снизу по центру"), (Position.BottomRight, "Справа снизу"),
        ];
        var screen = new System.Windows.Controls.Primitives.UniformGrid { Rows = 3, Columns = 3 };
        foreach (var (position, title) in cells)
        {
            if (position is not { } target)
            {
                screen.Children.Add(new Border());
                continue;
            }

            screen.Children.Add(Choice(group, title, selected == target, () => select(target)));
        }

        return new Border
        {
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(6), Child = screen,
        };
    }

    private StackPanel TextTab()
    {
        var panel = TabPanel();
        panel.Children.Add(Header("Шрифт"));
        var fonts = new ListBox { Height = 150, ItemsSource = _fonts.Value, SelectedItem = _value.Font };
        fonts.SelectionChanged += (_, e) =>
        {
            // иначе событие дойдёт до TabControl
            e.Handled = true;
            if (fonts.SelectedItem is string font) Change(s => s with { Font = font });
        };
        fonts.Loaded += (_, _) => fonts.ScrollIntoView(fonts.SelectedItem);
        panel.Children.Add(fonts);

        panel.Children.Add(Number("Размер", _value.FontSize, 10, 40, v => $"{v}", v => Change(s => s with { FontSize = v })));
        panel.Children.Add(Toggle("Жирный", _value.Bold, v => Change(s => s with { Bold = v })));
        panel.Children.Add(Toggle("Курсив", _value.Italic, v => Change(s => s with { Italic = v })));
        panel.Children.Add(Toggle("Тень под буквами", _value.Shadow, v => Change(s => s with { Shadow = v })));

        panel.Children.Add(Header("Выравнивание"));
        var align = new WrapPanel();
        foreach (var (value, title) in new[] { (TextAlign.Left, "Слева"), (TextAlign.Center, "По центру"), (TextAlign.Right, "Справа") })
            align.Children.Add(Choice("align", title, _value.Align == value, () => Change(s => s with { Align = value })));
        panel.Children.Add(align);

        panel.Children.Add(Header("Цвет текста"));
        panel.Children.Add(ColorPicker(_value.TextColor, TextSwatches, v => Change(s => s with { TextColor = v })));

        panel.Children.Add(Header("Ник"));
        panel.Children.Add(Toggle("Цвет ника по площадке", _value.NameColorByPlatform,
            v => Change(s => s with { NameColorByPlatform = v })));
        panel.Children.Add(Note("Свой цвет ника, действует без галочки выше:"));
        panel.Children.Add(ColorPicker(_value.NameColor, TextSwatches, v => Change(s => s with { NameColor = v })));
        panel.Children.Add(Toggle("Метка площадки TW / YT", _value.ShowPlatform, v => Change(s => s with { ShowPlatform = v })));
        return panel;
    }

    private StackPanel BackgroundTab()
    {
        var panel = TabPanel();
        panel.Children.Add(Header("Фон под текстом"));
        (BackgroundStyle Style, string Title)[] styles =
        [
            (BackgroundStyle.Panel, "Общая панель"),
            (BackgroundStyle.Bubbles, "Подложка под каждым сообщением"),
            (BackgroundStyle.None, "Без фона, только текст"),
        ];
        foreach (var (style, title) in styles)
            panel.Children.Add(Choice("background", title, _value.Background == style, () => Change(s => s with { Background = style })));
        panel.Children.Add(Note("Без фона текст читается лучше с жирным шрифтом или тенью."));

        panel.Children.Add(Header("Цвет фона"));
        panel.Children.Add(ColorPicker(_value.BackgroundColor, BackgroundSwatches, v => Change(s => s with { BackgroundColor = v })));

        panel.Children.Add(Number("Непрозрачность", _value.Opacity, 20, 100, v => $"{v}%", v => Change(s => s with { Opacity = v }), step: 5));
        panel.Children.Add(Note("Действует на всё окно, вместе с текстом: отдельная прозрачность фона ломает скрытие от захвата на Windows 10."));
        return panel;
    }

    private StackPanel BehaviourTab()
    {
        var panel = TabPanel();
        panel.Children.Add(Number("Сообщений на экране", _value.MaxMessages, 1, 15, v => $"{v}", v => Change(s => s with { MaxMessages = v })));
        panel.Children.Add(Number("Строк в сообщении, до", _value.MaxLines, 1, 10, v => $"{v}", v => Change(s => s with { MaxLines = v })));
        panel.Children.Add(Number("Расстояние между ними", _value.Spacing, 0, 20, v => $"{v}", v => Change(s => s with { Spacing = v })));
        panel.Children.Add(Number("Скрывать после тишины", _value.HideAfterSeconds, 0, 120, v => v == 0 ? "никогда" : $"{v} с",
            v => Change(s => s with { HideAfterSeconds = v }), step: 5));
        panel.Children.Add(Toggle("Открывать настройки при запуске", _value.ShowSettingsOnStart,
            v => Change(s => s with { ShowSettingsOnStart = v })));
        panel.Children.Add(Note("Настройки всегда можно открыть двойным щелчком по значку возле часов или повторным запуском программы."));

        panel.Children.Add(Header("Обновления"));
        _updateBlock = new TextBlock { Text = _updateText, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_updateBlock);
        panel.Children.Add(Address(UpdateChecker.DownloadPage));
        panel.Children.Add(Toggle("Проверять раз в сутки", _value.CheckUpdates, v => Change(s => s with { CheckUpdates = v })));
        panel.Children.Add(Note("Запрос идёт на GitHub, он видит ваш IP. О новой версии программа спросит вне эфира и обновится только по вашей кнопке."));
        panel.Children.Add(LeftButton("Проверить сейчас", () => UpdateCheckRequested?.Invoke()));
        return panel;
    }

    private StackPanel KeysTab()
    {
        var panel = TabPanel();
        panel.Children.Add(Header("Переместить окно"));
        panel.Children.Add(KeyBox(() => _value.MoveKey, () => _value.HideKey, v => Change(s => s with { MoveKey = v })));
        panel.Children.Add(Header("Скрыть или показать"));
        panel.Children.Add(KeyBox(() => _value.HideKey, () => _value.MoveKey, v => Change(s => s with { HideKey = v })));
        panel.Children.Add(Note("Щёлкните по полю и нажмите сочетание. В нём нужен Ctrl или Alt. Ctrl+Alt+0…5 оставлены для OBS. Esc отменяет."));
        return panel;
    }

    private TextBox KeyBox(Func<KeyChord> current, Func<KeyChord> other, Action<KeyChord> set)
    {
        var box = new TextBox
        {
            Text = HotKeys.Describe(current()),
            IsReadOnly = true,
            Width = 260,
            Padding = new Thickness(5),
            HorizontalAlignment = HorizontalAlignment.Left,
            ContextMenu = null,
        };
        box.GotKeyboardFocus += (_, _) =>
        {
            box.Text = "Нажмите сочетание…";
            RecordingHotKey?.Invoke(true);
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            box.Text = HotKeys.Describe(current());
            RecordingHotKey?.Invoke(false);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return;
            e.Handled = true;
            var key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                _ => e.Key,
            };
            if (key == Key.Escape)
            {
                Keyboard.ClearFocus();
                return;
            }

            uint modifiers = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= KeyChord.Alt;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= KeyChord.Control;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= KeyChord.Shift;

            var chord = new KeyChord(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
            if (!chord.IsValid) return;
            if (chord == other())
            {
                box.Text = "Занято другим действием";
                return;
            }

            set(chord);
            Keyboard.ClearFocus();
        };
        return box;
    }

    private static StackPanel ColorPicker(string current, string[] swatches, Action<string> set)
    {
        var sample = new Border
        {
            Width = 30,
            Height = 26,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = Palette.Brush(current),
            Margin = new Thickness(0, 0, 6, 0),
        };
        var code = new TextBox { Text = current, Width = 90, MaxLength = 7, Padding = new Thickness(3), ContextMenu = null };
        // окно подсказок IME не скрыть от захвата
        InputMethod.SetIsInputMethodEnabled(code, false);
        code.TextChanged += (_, _) =>
        {
            if (Rgb.Parse(code.Text) is not { } color) return;
            sample.Background = Palette.Brush(color);
            set(color.ToString());
        };

        var colors = new WrapPanel();
        foreach (var hex in swatches)
        {
            var swatch = new Button
            {
                Width = 30,
                Height = 26,
                Margin = new Thickness(0, 0, 4, 4),
                Background = Palette.Brush(hex),
                BorderBrush = Brushes.Gray,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, $"Цвет {hex}");
            swatch.Click += (_, _) => code.Text = hex;
            colors.Children.Add(swatch);
        }

        var custom = new StackPanel { Orientation = Orientation.Horizontal };
        custom.Children.Add(sample);
        custom.Children.Add(code);
        custom.Children.Add(new TextBlock
        {
            Text = "любой цвет в виде #RRGGBB",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });

        var panel = new StackPanel();
        panel.Children.Add(colors);
        panel.Children.Add(custom);
        return panel;
    }

    private static DockPanel Number(string title, int value, int min, int max, Func<int, string> format, Action<int> set, int step = 1)
    {
        var label = new TextBlock { Text = title, Width = 210, VerticalAlignment = VerticalAlignment.Center };
        var shown = new TextBlock
        {
            Text = format(value),
            Width = 70,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            SmallChange = step,
            LargeChange = step,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(slider, title);
        slider.ValueChanged += (_, _) =>
        {
            var next = (int)Math.Round(slider.Value);
            shown.Text = format(next);
            set(next);
        };

        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(shown, Dock.Right);
        row.Children.Add(label);
        row.Children.Add(shown);
        row.Children.Add(slider);
        return row;
    }

    private static CheckBox Toggle(string title, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = title, IsChecked = value, Margin = new Thickness(0, 9, 0, 0) };
        box.Click += (_, _) => set(box.IsChecked == true);
        return box;
    }

    private static RadioButton Choice(string group, string title, bool selected, Action select)
    {
        var choice = new RadioButton { GroupName = group, Content = title, IsChecked = selected, Margin = new Thickness(0, 6, 14, 6) };
        choice.Click += (_, _) => select();
        return choice;
    }

    private static Button PlainButton(string title, Action click)
    {
        var button = new Button { Content = title, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 4) };
        button.Click += (_, _) => click();
        return button;
    }

    private static StackPanel TabPanel() => new() { Margin = new Thickness(12) };

    private static TextBlock Header(string text) =>
        new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 6) };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = Brushes.DimGray,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12.5,
        Margin = new Thickness(0, 6, 0, 2),
    };
}
