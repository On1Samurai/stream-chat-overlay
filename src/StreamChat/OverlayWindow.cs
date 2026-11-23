using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using StreamChat.Core;
using StreamChat.Core.Emotes;

namespace StreamChat;

internal sealed class OverlayWindow : FloatingWindow
{
    private const double LineSpacing = 1.35;
    private const double PictureSize = 1.25;
    private const double WidestPicture = 4;

    private readonly StackPanel _messages = new();
    private readonly Thumb _widthGrip;
    private readonly ImageStore _images;
    private IReadOnlyDictionary<string, string> _channelEmotes = new Dictionary<string, string>();
    private IReadOnlyList<ChatMessage> _shown = [];

    public OverlayWindow(AppSettings settings, ImageStore images)
        : base(settings, s => s.Placement)
    {
        _images = images;
        Width = Placement.Width;
        SizeToContent = SizeToContent.Height;

        // лента прижата к низу: если не помещается, срезаются старые сообщения, а не новые
        _messages.VerticalAlignment = VerticalAlignment.Bottom;
        SetBody(_messages);

        _widthGrip = new Thumb
        {
            Width = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Accent,
            Cursor = System.Windows.Input.Cursors.SizeWE,
            Visibility = Visibility.Collapsed,
        };
        _widthGrip.DragDelta += (_, e) => Width = Math.Clamp(Width + e.HorizontalChange, Core.Placement.MinWidth, Core.Placement.MaxWidth);
        Root.Children.Add(_widthGrip);

        Restyle();
    }

    protected override string Hint =>
        $"Чат. Тяните окно мышью, правый край меняет ширину. Готово: {HotKeys.Describe(Settings.MoveKey)}";

    protected override bool WasResized => !Near(Width, Placement.Width);

    public void Render(IReadOnlyList<ChatMessage> messages)
    {
        _shown = messages;
        RenderContent();
    }

    public void SetChannelEmotes(IReadOnlyDictionary<string, string> emotes)
    {
        _channelEmotes = emotes;
        RenderContent();
    }

    protected override void OnPlacementApplied() => Width = Placement.Width;

    protected override void OnEditingChanged() =>
        _widthGrip.Visibility = Editing ? Visibility.Visible : Visibility.Collapsed;

    protected override void RenderContent()
    {
        _messages.Children.Clear();
        foreach (var message in _shown) _messages.Children.Add(Line(message));
    }

    private FrameworkElement Line(ChatMessage message)
    {
        var settings = Settings;
        var (platformColor, label) = Palette.Of(message.Platform);
        var lineHeight = Math.Ceiling(settings.FontSize * LineSpacing);

        var text = new TextBlock
        {
            Foreground = Palette.Brush(settings.TextColor),
            FontFamily = new FontFamily(settings.Font),
            FontSize = settings.FontSize,
            FontWeight = settings.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = settings.Italic ? FontStyles.Italic : FontStyles.Normal,
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = lineHeight * settings.MaxLines,
            // округление до пикселя при 125-175% съедает последнюю строку
            UseLayoutRounding = false,
            // знаки с десятками надстрочников рисуются поверх соседних сообщений
            ClipToBounds = true,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = settings.Align switch
            {
                TextAlign.Center => TextAlignment.Center,
                TextAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };
        if (settings.Shadow)
            text.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 1 };

        var (markColor, markLabel) = Palette.Of(message.Mark);
        // App пишет только сама программа: эту метку зритель не подделает никаким ником
        var system = message.Platform == Platform.App;
        var mark = system ? MessageMark.None : settings.Highlight ? message.Mark : MessageMark.None;
        if (system) (markColor, markLabel) = (platformColor, "ПРОГРАММА");
        if (system || mark != MessageMark.None)
            text.Inlines.Add(new Run($"{markLabel} ") { Foreground = markColor, FontWeight = FontWeights.Bold, FontSize = settings.FontSize * 0.8 });
        // правила YouTube API требуют показывать источник у каждого его сообщения
        if ((settings.ShowPlatform || message.Platform == Platform.YouTube) && label.Length > 0)
            text.Inlines.Add(new Run($"{label} ") { Foreground = platformColor, FontSize = settings.FontSize * 0.8 });
        var name = new Span
        {
            Foreground = settings.NameColorByPlatform ? platformColor : Palette.Brush(settings.NameColor),
            FontWeight = settings.Bold ? FontWeights.ExtraBold : FontWeights.SemiBold,
        };
        AddParts(name.Inlines, Emoji.Split(message.Author));
        name.Inlines.Add(new Run(": "));
        text.Inlines.Add(name);
        AddParts(text.Inlines, MessageContent.Build(message, _channelEmotes, settings.EmoteImages));

        var margin = new Thickness(0, settings.Spacing / 2d, 0, settings.Spacing / 2d);
        var bubbles = settings.Background == BackgroundStyle.Bubbles;
        var marked = system || mark != MessageMark.None;
        if (!bubbles && !marked)
        {
            text.Margin = margin;
            return text;
        }

        return new Border
        {
            Child = text,
            Margin = margin,
            Padding = bubbles ? new Thickness(6, 2, 6, 2) : new Thickness(5, 0, 0, 0),
            Background = bubbles ? Palette.Brush(settings.BackgroundColor) : null,
            // полоса слева читается боковым зрением, пока смотришь в игру
            BorderBrush = markColor,
            BorderThickness = marked ? new Thickness(4, 0, 0, 0) : new Thickness(0),
            HorizontalAlignment = settings.Align switch
            {
                TextAlign.Center => HorizontalAlignment.Center,
                TextAlign.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
        };
    }

    private void AddParts(InlineCollection inlines, IEnumerable<MessagePart> parts)
    {
        var height = Math.Round(Settings.FontSize * PictureSize);
        foreach (var part in parts)
        {
            if (part.Image.Length == 0 || _images.Get(part.Image) is not { } source)
            {
                inlines.Add(new Run(part.Text));
                continue;
            }

            var picture = new Image
            {
                Source = source,
                Height = height,
                MaxWidth = height * WidestPicture,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(1, 0, 1, 0),
            };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            inlines.Add(new InlineUIContainer(picture) { BaselineAlignment = BaselineAlignment.Center });
        }
    }
}
