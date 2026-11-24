using StreamChat.Core;
using Forms = System.Windows.Forms;

namespace StreamChat;

// Трей и уведомления видны зрителям: сюда нельзя передавать текст чата и ключи
internal sealed class Tray : IDisposable
{
    private static readonly (Position Position, string Title)[] Positions =
    [
        (Position.TopLeft, "Слева сверху"),
        (Position.TopCenter, "По центру сверху"),
        (Position.TopRight, "Справа сверху"),
        (Position.MiddleLeft, "Слева посередине"),
        (Position.MiddleRight, "Справа посередине"),
        (Position.BottomLeft, "Слева снизу"),
        (Position.BottomCenter, "По центру снизу"),
        (Position.BottomRight, "Справа снизу"),
    ];

    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<Position, Forms.ToolStripMenuItem> _positionItems = [];
    private readonly Forms.ToolStripMenuItem _status = new("Запуск…") { Enabled = false };
    private readonly Forms.ToolStripMenuItem _move = new("Переместить окно");
    private readonly Forms.ToolStripMenuItem _hide = new("Скрыть или показать");
    private readonly Forms.ToolStripMenuItem _twitch = new("Twitch: не настроен") { Enabled = false };
    private readonly Forms.ToolStripMenuItem _youTube = new("YouTube: не настроен") { Enabled = false };
    private readonly Forms.ToolStripMenuItem _tikTok = new("TikTok: не настроен") { Enabled = false };
    private readonly Forms.ToolStripMenuItem _donations = new("DonationAlerts: вход не выполнен") { Enabled = false };
    private readonly Forms.ToolStripMenuItem _update = new() { Enabled = true, Visible = false };
    private readonly Forms.ToolStripMenuItem _profiles = new("Профиль игры") { Visible = false };
    private readonly Action<string> _switchProfile;

    public Tray(Action openSettings, Action reconnect, Action testMessage, Action<Position> setPosition, Action toggleEditing, Action toggleHidden,
        Action<string> switchProfile, Action openUpdate, Action exit)
    {
        _switchProfile = switchProfile;
        _update.Click += (_, _) => openUpdate();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(_twitch);
        menu.Items.Add(_youTube);
        menu.Items.Add(_tikTok);
        menu.Items.Add(_donations);
        menu.Items.Add(_update);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Настройки", null, (_, _) => openSettings());
        menu.Items.Add("Переподключить чат", null, (_, _) => reconnect());
        menu.Items.Add("Тестовое сообщение", null, (_, _) => testMessage());
        menu.Items.Add(_profiles);
        var positions = new Forms.ToolStripMenuItem("Позиция окна");
        foreach (var (position, title) in Positions)
        {
            var item = new Forms.ToolStripMenuItem(title, null, (_, _) => setPosition(position));
            _positionItems[position] = item;
            positions.DropDownItems.Add(item);
        }

        positions.DropDownItems.Add(new Forms.ToolStripSeparator());
        _positionItems[Position.Custom] = new Forms.ToolStripMenuItem("Своя, мышью", null, (_, _) => toggleEditing());
        positions.DropDownItems.Add(_positionItems[Position.Custom]);
        menu.Items.Add(positions);
        _move.Click += (_, _) => toggleEditing();
        _hide.Click += (_, _) => toggleHidden();
        menu.Items.Add(_move);
        menu.Items.Add(_hide);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "StreamChat: двойной щелчок открывает настройки",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) openSettings();
        };
    }

    public void SetSource(Platform platform, string text)
    {
        var item = platform switch
        {
            Platform.Twitch => _twitch,
            Platform.YouTube => _youTube,
            Platform.TikTok => _tikTok,
            Platform.DonationAlerts => _donations,
            _ => null,
        };
        if (item is not null) item.Text = $"{platform}: {text}";
    }

    public void SetPosition(Position position)
    {
        foreach (var (key, item) in _positionItems) item.Checked = key == position;
    }

    public void SetKeys(string move, string hide)
    {
        _move.ShortcutKeyDisplayString = move;
        _hide.ShortcutKeyDisplayString = hide;
    }

    public void SetUpdate(string version)
    {
        _update.Text = $"Вышла версия {version}: обновить";
        _update.Visible = version.Length > 0;
    }

    public void SetProfiles(IReadOnlyList<LayoutProfile> profiles, string active)
    {
        // Apply вызывается на каждое движение ползунка, меню перестраиваем только при смене списка
        var same = _profiles.DropDownItems.Count == profiles.Count
            && profiles.Select((profile, i) => _profiles.DropDownItems[i].Tag as string == profile.Name).All(equal => equal);
        if (!same)
        {
            _profiles.DropDownItems.Clear();
            foreach (var profile in profiles)
            {
                var name = profile.Name;
                // '&' в меню это подчёркнутая клавиша
                var item = new Forms.ToolStripMenuItem(name.Replace("&", "&&")) { Tag = name };
                item.Click += (_, _) => _switchProfile(name);
                _profiles.DropDownItems.Add(item);
            }
        }

        foreach (Forms.ToolStripItem item in _profiles.DropDownItems)
            ((Forms.ToolStripMenuItem)item).Checked = item.Tag as string == active;
        _profiles.Visible = profiles.Count > 0;
    }

    public void SetStatus(string text) => _status.Text = text;

    public void Warn(string text) => _icon.ShowBalloonTip(10_000, "StreamChat", text, Forms.ToolTipIcon.Warning);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
