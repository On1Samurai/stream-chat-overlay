using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using StreamChat.Core;
using StreamChat.Core.Chat;
using StreamChat.Core.Obs;
using StreamChat.Core.Rules;
using StreamChat.Core.Updates;
using StreamChat.Core.Viewers;

namespace StreamChat;

internal sealed class ChatController : IDisposable
{
    // от подделки защищает не имя, а площадка App: её не пришлёт ни один чат
    private const string SystemAuthor = "StreamChat";

    private static readonly BarOptions ViewersBar = new("зрители", s => s.ViewersPlacement, s => s.ViewersFontSize, s => s.ViewersInColumn);
    private static readonly BarOptions ObsBar = new("OBS", s => s.ObsPlacement, s => s.ObsFontSize, s => s.ObsInColumn);

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamChat", "settings.json");

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly OverlayState _state = new();
    private readonly SampleChat _samples = new();
    private readonly ChatConnections _connections;
    private readonly ImageStore _images;
    private readonly ChannelEmotes _channelEmotes;
    private readonly ViewerBoard _board = new();
    private readonly ViewerPolling _polling;
    private AppSettings _connected = new();
    private BarWindow? _viewers;
    private BarWindow? _obs;
    private readonly ObsBoard _obsBoard = new();
    private readonly ObsMonitor _obsMonitor;
    private readonly DonationSignIn _signIn;
    private readonly UpdateNotifier _updates;
    private SourceStatus _obsStatus = new(SourceHealth.Off, "выключено");
    private string _updateText = $"Установлена версия {UpdateNotifier.CurrentVersion}.";
    private string _announcedUpdate = "";
    private readonly Dictionary<Platform, SourceStatus> _sources = [];
    private readonly Dictionary<Platform, SourceStatus> _announced = [];
    private readonly bool _supported = CaptureGuard.IsSupported(Environment.OSVersion.Version);
    private readonly Action _exit;
    private readonly Tray _tray;
    private readonly HotKeys _hotKeys = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AppSettings _settings = new();
    private AppSettings _beforeEditing = new();
    private OverlayWindow? _window;
    private SettingsWindow? _settingsWindow;
    private bool _settingsOpen;
    private bool _starting;
    private bool _recordingHotKey;
    private bool _messagesChanged;
    private string _failure = "";
    private string _settingsFailure = "";
    private string _keysNotice = "";
    private int _lastSecond = -1;
    private bool _stopped;
    private bool _canSave = true;

    public ChatController(Action exit)
    {
        _exit = () =>
        {
            Dispose();
            exit();
        };
        _tray = new Tray(OpenSettings, Reconnect, AddTestMessage, SetPosition, ToggleEditing, ToggleHidden, SwitchProfile, _exit);
        _timer.Tick += (_, _) =>
        {
            try
            {
                Tick();
            }
            finally
            {
                // DispatcherTimer после исключения из Tick больше не срабатывает, а с ним гаснет контроль защиты
                if (!_stopped)
                {
                    _timer.Stop();
                    _timer.Start();
                }
            }
        };
        var ui = Dispatcher.CurrentDispatcher;
        _images = new ImageStore(ui);
        _images.Loaded += () => _messagesChanged = true;
        _channelEmotes = new ChannelEmotes(ui, emotes => _window?.SetChannelEmotes(emotes));
        _connections = new ChatConnections(ui, OnChatEvent, OnSourceStatus, room => _channelEmotes.SetRoom(room),
            count => _board.Set(Platform.TikTok, new ViewerReading(ViewerState.Live, count), _clock.Elapsed));
        _polling = new ViewerPolling(ui, OnViewers, OnViewerTargetChanged);
        _obsMonitor = new ObsMonitor(ui, OnObsSnapshot, OnObsStatus);
        _signIn = new DonationSignIn(ui, OnDonationToken, status => OnSourceStatus(Platform.DonationAlerts, status));
        _updates = new UpdateNotifier(ui, OnUpdateChecked);
    }

    public void Start()
    {
        if (!_supported)
        {
            const string reason = "Нужна Windows 10 2004 (сборка 19041) или новее. Чат не будет показан.";
            _tray.SetStatus(reason);
            _tray.Warn(reason);
            return;
        }

        _clock.Restart();
        (_settings, _canSave) = AppSettings.LoadSafely(SettingsFile);
        if (!_canSave)
            _tray.Warn("Файл настроек занят или недоступен: программа работает с настройками по умолчанию и не сохранит их, чтобы не стереть ваши.");
        _state.Configure(_settings.MaxMessages, _settings.HideAfterSeconds);
        _state.SetIgnored(_settings.IgnoredNames);

        _window = new OverlayWindow(_settings, _images);
        _channelEmotes.SetEnabled(_settings.EmoteImages);
        _state.CountersEnabled = HasBars(_settings);
        _state.SetRules(new ChatRules(_settings));
        _viewers = CreateBar(ViewersBar, OnViewersDragged);
        _obs = CreateBar(ObsBar, OnObsDragged);
        _updates.SetEnabled(_settings.CheckUpdates);
        _tray.SetProfiles(_settings.Profiles, _settings.ActiveProfile);
        _window.PlacementChanged += OnDragged;
        _window.DismissRequested += () =>
        {
            if (_state.Editing) ToggleEditing();
        };
        _tray.SetPosition(_settings.Placement.Position);

        // сначала таймер: сбой первого показа не должен отключить контроль защиты
        _timer.Start();
        if (_settings.ShowSettingsOnStart) ShowSettings(starting: true);
        RegisterHotKeys();
        Connect(force: false);
        if (!_settingsOpen) AnnounceIfNotConfigured();
        Tick();
    }

    /// <summary>Повторный запуск exe: значок в трее находят не все.</summary>
    public void OpenSettings() => ShowSettings(starting: false);

    /// <summary>Любая ошибка сначала убирает окно с экрана.</summary>
    public void Fail(string reason)
    {
        if (_stopped) return;
        try
        {
            try
            {
                _window?.Conceal();
            }
            finally
            {
                try
                {
                    _viewers?.Conceal();
                }
                finally
                {
                    _obs?.Conceal();
                }
            }
        }
        finally
        {
            var first = _state.Block();
            // образцы режима настройки не должны вернуться при повторе
            _messagesChanged = true;
            foreach (var window in Windows()) window.SetEditing(false);
            if (first)
            {
                // уведомления игра часто подавляет, поэтому причина остаётся и в меню
                _failure = reason;
                ShowProblems();
                _tray.Warn($"Чат не показан: {reason}. {HotKeys.Describe(_settings.HideKey)}: повторить.");
            }
        }
    }

    public void Fail(Exception exception)
    {
        // только тип и код: в Message может оказаться текст зрителя
        var code = exception is Win32Exception native ? $", код {native.NativeErrorCode}" : "";
        Fail($"сбой программы ({exception.GetType().Name}{code})");
    }

    public void Dispose()
    {
        if (_stopped) return;
        // до _stopped: место, куда перетащили окно, ещё успеет сохраниться
        foreach (var window in Windows()) window.SetEditing(false);
        // раньше закрытия окон: во время закрытия WPF запрещает Show и Hide
        _stopped = true;
        _timer.Stop();
        _connections.Dispose();
        _polling.Dispose();
        _channelEmotes.Dispose();
        _images.Dispose();
        _obsMonitor.Dispose();
        _signIn.Dispose();
        _updates.Dispose();
        foreach (var window in Windows()) window.Conceal();
        _settingsWindow?.Conceal();
        _hotKeys.Dispose();
        _tray.Dispose();
    }

    private void Tick()
    {
        var second = (int)_clock.Elapsed.TotalSeconds;
        var newSecond = second != _lastSecond;
        _lastSecond = second;

        if (_settingsOpen && _settingsWindow is { IsVisible: true, IsExcluded: false })
        {
            // сначала с экрана: CloseSettings может бросить исключение раньше своего Conceal
            _settingsWindow.Conceal();
            CloseSettings(save: false);
            _tray.Warn("Настройки закрыты: Windows сняла с них защиту от захвата.");
        }

        // не на каждое сообщение: в большом чате их десятки в секунду
        if (_messagesChanged) RenderMessages();
        if (newSecond)
        {
            RenderViewers();
            RenderObs();
        }

        Sync();
        if (!newSecond) return;
        foreach (var window in Windows())
        {
            if (!window.IsVisible) continue;
            window.Reposition();
            window.KeepOnTop();
        }
    }

    private void ShowSettings(bool starting)
    {
        if (_stopped || _window is null) return;
        if (_settingsOpen)
        {
            if (_settingsWindow is not null)
            {
                _settingsWindow.WindowState = System.Windows.WindowState.Normal;
                _settingsWindow.Activate();
            }

            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Changed += OnSettingsChanged;
            _settingsWindow.MoveRequested += ToggleEditing;
            _settingsWindow.TestRequested += AddTestMessage;
            _settingsWindow.ConnectRequested += Reconnect;
            _settingsWindow.Confirmed += () => CloseSettings(save: true);
            _settingsWindow.DismissRequested += DismissSettings;
            _settingsWindow.ProfileRequested += SwitchProfile;
            _settingsWindow.SignInRequested += confirmed =>
            {
                var now = _clock.Elapsed;
                bool? onAir = _settings.ObsEnabled && _obsBoard.IsKnown(now) ? _obsBoard.IsOnAir(now) : null;
                _signIn.Start(onAir, confirmed);
            };
            _settingsWindow.SignOutRequested += SignOut;
            _settingsWindow.UpdateCheckRequested += () =>
            {
                _settingsWindow.SetUpdateText("Проверяю…");
                _updates.CheckNow();
            };
            _settingsWindow.RecordingHotKey += recording =>
            {
                _recordingHotKey = recording;
                RegisterHotKeys();
            };
        }

        _settingsWindow.Prepare(_settings, starting);
        _settingsWindow.SetObsStatus(_obsStatus);
        _settingsWindow.SetUpdateText(_updateText);
        foreach (var (platform, status) in _sources) _settingsWindow.SetSourceStatus(platform, status);
        _settingsWindow.WindowState = System.Windows.WindowState.Normal;
        var result = _settingsWindow.ShowProtected();
        if (!result.Protected)
        {
            _settingsFailure = $"Настройки не открыты: не включилась защита от захвата (код {result.Error}).";
            ShowProblems();
            _tray.Warn(_settingsFailure);
            return;
        }

        _settingsFailure = "";
        _settingsWindow.Activate();
        _settingsOpen = true;
        _starting = starting;
        _beforeEditing = _settings;

        _state.Reveal(_clock.Elapsed);
        _state.Previewing = true;
        ShowProblems();
        RenderMessages();
        RenderViewers();
        RenderObs();
        Sync();
    }

    private void DismissSettings()
    {
        if (_stopped || !_settingsOpen) return;
        if (!_starting)
        {
            CloseSettings(save: false);
            return;
        }

        // первый запуск: закрытое крестиком окно не должно терять введённый канал
        if (_state.Editing) ToggleEditing();
        Save();
        _exit();
    }

    private void CloseSettings(bool save)
    {
        if (_settingsWindow is null || !_settingsOpen) return;
        if (_state.Editing) ToggleEditing();

        _settingsOpen = false;
        _starting = false;
        _recordingHotKey = false;
        _settingsWindow.Conceal();
        _state.Previewing = false;
        _state.Reveal(_clock.Elapsed);

        if (save) Save();
        else Apply(_beforeEditing);
        RegisterHotKeys();
        Connect(force: false);
        AnnounceIfNotConfigured();
        RenderMessages();
        Sync();
    }

    private void OnSettingsChanged(AppSettings next)
    {
        var placementBefore = _settings.Placement;
        var viewersBefore = _settings.ViewersPlacement;
        var obsBefore = _settings.ObsPlacement;
        if (_state.Editing && (next.Placement != placementBefore || next.ViewersPlacement != viewersBefore || next.ObsPlacement != obsBefore))
        {
            ToggleEditing();
            if (next.ObsPlacement == obsBefore) next = next with { ObsPlacement = _settings.ObsPlacement };
            // ползунок ширины не должен отменять место, куда окно только что перетащили
            if (next.ViewersPlacement == viewersBefore) next = next with { ViewersPlacement = _settings.ViewersPlacement };
            var dragged = _settings.Placement;
            if (next.Placement == placementBefore)
                next = next with { Placement = dragged };
            else if (next.Placement.Position == placementBefore.Position)
                next = next with { Placement = dragged with { Width = next.Placement.Width } };
            else if (next.Placement.Width == placementBefore.Width)
                next = next with { Placement = next.Placement with { Width = dragged.Width } };
        }

        Apply(next);
        _settingsWindow?.Update(next);
        Sync();
    }

    private void OnDragged(Placement placement)
    {
        Apply(_settings with { Placement = placement });
        SaveUnlessEditingSettings();
    }

    private void OnViewersDragged(Placement placement)
    {
        Apply(_settings with { ViewersPlacement = placement });
        SaveUnlessEditingSettings();
    }

    private void OnObsDragged(Placement placement)
    {
        Apply(_settings with { ObsPlacement = placement });
        SaveUnlessEditingSettings();
    }

    private BarWindow CreateBar(BarOptions options, Action<Placement> onDragged)
    {
        var bar = new BarWindow(_settings, options);
        bar.PlacementChanged += onDragged;
        bar.DismissRequested += () =>
        {
            if (_state.Editing) ToggleEditing();
        };
        return bar;
    }

    private IEnumerable<FloatingWindow> Windows()
    {
        if (_window is not null) yield return _window;
        if (_viewers is not null) yield return _viewers;
        if (_obs is not null) yield return _obs;
    }

    private static bool HasBars(AppSettings settings) => settings.ObsEnabled || ShownViewers(settings).Count > 0;

    private void OnObsSnapshot(ObsSnapshot snapshot)
    {
        if (_stopped) return;
        _obsBoard.Update(snapshot, _clock.Elapsed);
        RenderObs();
    }

    private void OnObsStatus(SourceStatus status)
    {
        if (_stopped) return;
        _obsStatus = status;
        if (status.Health != SourceHealth.Connected) _obsBoard.Forget();
        _settingsWindow?.SetObsStatus(status);
        RenderObs();
    }

    private void RenderObs()
    {
        var neutral = Palette.Of(StatusLevel.Neutral);
        // красным и значение: МИК ВЫКЛЮЧЕН надо заметить боковым зрением
        var items = _obsBoard.Items(_clock.Elapsed).Select(item => new BarItem(
            item.Label,
            item.Level == StatusLevel.Bad ? Palette.Of(StatusLevel.Bad) : neutral,
            item.Text,
            Palette.Of(item.Level))).ToList();
        _obs?.Render(items);
    }

    private void SwitchProfile(string name)
    {
        if (_stopped || _window is null) return;
        if (_state.Editing) ToggleEditing();
        Apply(_settings.SwitchProfile(name));
        SaveUnlessEditingSettings();
        _state.Reveal(_clock.Elapsed);
        Sync();
    }

    private void OnDonationToken(string token)
    {
        if (_stopped) return;
        StoreDonationToken(Secret.Protect(token));
    }

    private void SignOut()
    {
        if (_stopped) return;
        StoreDonationToken("");
    }

    private void StoreDonationToken(string stored)
    {
        Apply(_settings with { DonationTokenProtected = stored });
        if (_settingsOpen)
        {
            // вход и выход «Отмена» не откатывает
            _beforeEditing = _beforeEditing with { DonationTokenProtected = stored };
            Write(_beforeEditing);
            _settingsWindow?.Update(_settings);
        }
        else
        {
            Save();
        }

        Connect(force: false);
    }

    private void OnUpdateChecked(UpdateResult result)
    {
        if (_stopped) return;
        var installed = $"Установлена версия {UpdateNotifier.CurrentVersion}.";
        _updateText = result.State switch
        {
            UpdateState.Available => $"Вышла версия {result.Version}. {installed} Скачайте zip по адресу ниже, закройте программу и замените exe.",
            UpdateState.UpToDate => $"{installed} Она последняя.",
            UpdateState.NoReleases => $"{installed} Выпусков на GitHub пока нет.",
            _ => $"{installed} Проверить не удалось: нет связи с GitHub.",
        };
        _settingsWindow?.SetUpdateText(_updateText);
        if (result.State != UpdateState.Available) return;

        _tray.SetUpdate(result.Version);
        if (_announcedUpdate == result.Version) return;
        _announcedUpdate = result.Version;
        Announce(Platform.App, $"вышла версия {result.Version}, адрес в настройках на вкладке «Поведение»");
    }

    private void Connect(bool force)
    {
        _connected = _settings;
        _connections.Connect(_settings, force);
        _obsMonitor.Connect(_settings, force);
        _polling.Update(_connected, _settings);
    }

    private void OnViewers(Platform platform, ViewerReading reading)
    {
        if (_stopped) return;
        _board.Set(platform, reading, _clock.Elapsed);
        RenderViewers();
    }

    private void OnViewerTargetChanged(Platform platform)
    {
        _board.Forget(platform);
        RenderViewers();
    }

    private static List<Platform> ShownViewers(AppSettings settings)
    {
        var shown = new List<Platform>();
        if (settings.ShowTwitchViewers) shown.Add(Platform.Twitch);
        if (settings.ShowYouTubeViewers) shown.Add(Platform.YouTube);
        if (settings.ShowTikTokViewers) shown.Add(Platform.TikTok);
        return shown;
    }

    private void RenderViewers()
    {
        var now = _clock.Elapsed;
        var shown = ShownViewers(_settings);
        var lines = shown.Select(platform =>
        {
            var (color, label) = Palette.Of(platform);
            return new BarItem(label, color, _board.Text(platform, now));
        }).ToList();
        // правила YouTube API запрещают складывать его данные с чужими: YouTube в сумму не входит
        var summed = shown.Where(platform => platform != Platform.YouTube).ToList();
        var total = _board.Total(summed, now);
        if (_settings.ShowViewerTotal && summed.Count > 1) lines.Add(new BarItem("Всего без YT", Palette.Of(Platform.App).Color, total));
        _viewers?.Render(lines);

        if (!_settingsOpen || _settingsWindow is null) return;
        foreach (var platform in new[] { Platform.Twitch, Platform.YouTube, Platform.TikTok })
            _settingsWindow.SetViewerText(platform, shown.Contains(platform) ? _board.Text(platform, now) : "выключено");
        _settingsWindow.SetViewerText(Platform.App, total);
    }

    private void SetPosition(Position position)
    {
        if (_window is null) return;
        if (_state.Editing) ToggleEditing();
        Apply(_settings with { Placement = _settings.Placement with { Position = position } });
        SaveUnlessEditingSettings();

        // сразу показать, куда встало окно
        _state.Reveal(_clock.Elapsed);
        AddTestMessage();
    }

    private void SaveUnlessEditingSettings()
    {
        // открытые настройки сохраняются целиком кнопкой, чтобы работала отмена
        if (_settingsOpen) _settingsWindow?.Update(_settings);
        else Save();
    }

    private void Apply(AppSettings settings)
    {
        _channelEmotes.SetEnabled(settings.EmoteImages);
        if (settings.IgnoredNames != _settings.IgnoredNames) _state.SetIgnored(settings.IgnoredNames);
        if (RulesChanged(settings, _settings)) _state.SetRules(new ChatRules(settings));
        _updates.SetEnabled(settings.CheckUpdates);
        _tray.SetProfiles(settings.Profiles, settings.ActiveProfile);
        var keysChanged = settings.MoveKey != _settings.MoveKey || settings.HideKey != _settings.HideKey;
        _settings = settings;
        _state.Configure(settings.MaxMessages, settings.HideAfterSeconds);
        _state.CountersEnabled = HasBars(settings);
        foreach (var window in Windows()) window.Apply(settings);
        _polling.Update(_connected, settings);
        RenderMessages();
        RenderViewers();
        RenderObs();
        _tray.SetPosition(settings.Placement.Position);
        if (keysChanged) RegisterHotKeys();
    }

    private static bool RulesChanged(AppSettings a, AppSettings b) =>
        a.Highlight != b.Highlight || a.MentionNames != b.MentionNames || a.BlockedWords != b.BlockedWords
        || a.HideLinks != b.HideLinks || a.HideRepeats != b.HideRepeats || a.TwitchChannel != b.TwitchChannel;

    private void Save()
    {
        _settings = _settings.WithActiveProfileStored();
        Write(_settings);
    }

    private void Write(AppSettings settings)
    {
        if (!_canSave) return;
        try
        {
            settings.Save(SettingsFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _tray.Warn("Настройки не сохранились: нет доступа к папке настроек. До выхода они действуют.");
        }
    }

    private void RegisterHotKeys()
    {
        if (_stopped || _window is null) return;
        _hotKeys.Clear();

        var move = HotKeys.Describe(_settings.MoveKey);
        var hide = HotKeys.Describe(_settings.HideKey);
        _tray.SetKeys(move, hide);
        // пока записывают сочетание, старое не должно срабатывать
        if (_recordingHotKey) return;

        var busy = new List<string>();
        if (!_hotKeys.Register(_settings.MoveKey, ToggleEditing)) busy.Add(move);
        if (!_hotKeys.Register(_settings.HideKey, ToggleHidden)) busy.Add(hide);

        _keysNotice = busy.Count == 0 ? ""
            : $"Клавиши заняты другой программой: {string.Join(", ", busy)}. Назначьте другие на вкладке «Клавиши».";
        ShowProblems();
        if (!_settingsOpen && _keysNotice.Length > 0) _tray.Warn(_keysNotice);
    }

    private void ShowProblems()
    {
        var chat = _failure.Length > 0 ? $"Чат выключен: {_failure}" : "";
        _tray.SetStatus(chat.Length > 0 ? chat : _settingsFailure.Length > 0 ? _settingsFailure : "Защита от захвата включена");
        if (_settingsOpen) _settingsWindow?.SetNotice(string.Join("\n", new[] { chat, _keysNotice }.Where(text => text.Length > 0)));
    }

    private void AddTestMessage()
    {
        _state.Add(_samples.Next(), _clock.Elapsed);
        RenderMessages();
        Sync();
    }

    private void Reconnect()
    {
        if (_stopped || _window is null) return;
        _announced.Clear();
        Connect(force: true);
    }

    private void OnChatEvent(ChatEvent change)
    {
        if (_stopped) return;
        _state.Apply(change, _clock.Elapsed);
        _messagesChanged = true;
    }

    private void OnSourceStatus(Platform platform, SourceStatus status)
    {
        if (_stopped) return;
        _sources[platform] = status;
        _tray.SetSource(platform, status.Text);
        if (platform == Platform.TikTok && status.Health != SourceHealth.Connected) OnViewerTargetChanged(platform);
        _settingsWindow?.SetSourceStatus(platform, status);

        // Connecting бывает только в начале: о новом канале надо сообщить заново
        if (status.Health is SourceHealth.Off or SourceHealth.Connecting) _announced.Remove(platform);
        if (status.Health is not (SourceHealth.Connected or SourceHealth.Error)) return;
        if (_announced.GetValueOrDefault(platform) == status) return;
        // в самом чате, а не уведомлением Windows: то увидели бы зрители
        _announced[platform] = status;
        Announce(Platform.App, $"{platform}: {status.Text}");
    }

    private void AnnounceIfNotConfigured()
    {
        if (_settings.TwitchChannel.Trim().Length == 0 && _settings.YouTubeLink.Trim().Length == 0 && !_settings.TikTokEnabled
            && _settings.DonationTokenProtected.Length == 0)
            Announce(Platform.App, "чат не подключён. Откройте настройки двойным щелчком по значку возле часов");
    }

    private void Announce(Platform platform, string text)
    {
        _state.Add(new ChatMessage(platform, SystemAuthor, text), _clock.Elapsed);
        _messagesChanged = true;
    }

    private void RenderMessages()
    {
        _messagesChanged = false;
        var messages = _state.Messages;
        if (_state.Previewing || _state.Editing)
        {
            // образцы только на экране: в ленте настоящего чата их нет
            var missing = Math.Min(SampleChat.Preview.Count, _settings.MaxMessages) - messages.Count;
            if (missing > 0) messages = [.. SampleChat.Preview.Take(missing), .. messages];
        }

        _window?.Render(messages);
    }

    private void ToggleEditing()
    {
        if (_window is null) return;
        _state.ToggleEditing(_clock.Elapsed);
        if (_state.Editing) RenderMessages();
        // выход сначала запоминает место вместе с образцами, и только потом они убираются
        Sync();
        if (!_state.Editing) RenderMessages();
    }

    private void ToggleHidden()
    {
        var empty = _state.Messages.Count == 0 && !_state.Previewing && !_state.Editing;
        _state.ToggleHidden(_clock.Elapsed);
        // в пустом чате нечего показать, а нажатие должно быть видно
        if (empty && (!_state.CountersEnabled || _state.ShouldShowCounters)) Announce(Platform.App, "чат включён, жду сообщений");
        RenderMessages();
        Sync();
    }

    private void Sync()
    {
        if (_stopped || _window is null) return;
        foreach (var window in Windows()) window.SetEditing(_state.Editing);

        // Fail в одном окне блокирует остальные: их видимость считается уже после него
        SyncWindow(_window, _state.ShouldShow(_clock.Elapsed));
        if (_viewers is not null) SyncWindow(_viewers, _state.ShouldShowCounters && ShownViewers(_settings).Count > 0);
        if (_obs is not null) SyncWindow(_obs, _state.ShouldShowCounters && _settings.ObsEnabled);
    }

    private void SyncWindow(FloatingWindow window, bool show)
    {
        if (!show)
        {
            if (window.IsVisible) window.Conceal();
            return;
        }

        if (window.IsVisible)
        {
            if (!window.IsExcluded) Fail("Windows сняла защиту от захвата");
            return;
        }

        var result = window.ShowProtected();
        if (result.Protected)
        {
            _failure = "";
            ShowProblems();
        }
        else if (result.Error == 0) Fail("Windows не подтвердила защиту от захвата (код 0)");
        else Fail($"не включилась защита от захвата (код {result.Error})");
    }
}
