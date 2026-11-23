using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using StreamChat.Core;

namespace StreamChat;

/// <param name="TextColor">null: цвет текста из настроек.</param>
internal sealed record BarItem(string Label, Brush LabelColor, string Text, Brush? TextColor = null);

internal sealed record BarOptions(
    string Name,
    Func<AppSettings, Placement> Placement,
    Func<AppSettings, int> FontSize,
    Func<AppSettings, bool> InColumn);

/// <summary>Строка коротких показателей: зрители, состояние OBS.</summary>
internal sealed class BarWindow : FloatingWindow
{
    private const double Gap = 14;

    private readonly StackPanel _items = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly BarOptions _options;
    private IReadOnlyList<BarItem> _shown = [];

    public BarWindow(AppSettings settings, BarOptions options)
        : base(settings, options.Placement)
    {
        _options = options;
        Title = $"StreamChat: {options.Name}";
        SizeToContent = SizeToContent.WidthAndHeight;
        LimitHintWidth(220);
        SetBody(_items);
        Restyle();
    }

    protected override string Hint => $"{_options.Name}. Тяните мышью. Готово: {HotKeys.Describe(Settings.MoveKey)}";

    public void Render(IReadOnlyList<BarItem> items)
    {
        // вызывается каждую секунду, меняется редко
        if (items.SequenceEqual(_shown)) return;
        _shown = items;
        RenderContent();
    }

    protected override void RenderContent()
    {
        var column = _options.InColumn(Settings);
        _items.Orientation = column ? Orientation.Vertical : Orientation.Horizontal;
        _items.Children.Clear();
        for (var i = 0; i < _shown.Count; i++)
        {
            var margin = i == 0 ? new Thickness(0) : column ? new Thickness(0, 4, 0, 0) : new Thickness(Gap, 0, 0, 0);
            _items.Children.Add(Cell(_shown[i], margin));
        }
    }

    private FrameworkElement Cell(BarItem item, Thickness margin)
    {
        var text = new TextBlock
        {
            Foreground = Palette.Brush(Settings.TextColor),
            FontFamily = new FontFamily(Settings.Font),
            FontSize = _options.FontSize(Settings),
            FontWeight = Settings.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = Settings.Italic ? FontStyles.Italic : FontStyles.Normal,
            UseLayoutRounding = false,
        };
        if (Settings.Shadow)
            text.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 1 };

        text.Inlines.Add(new Run($"{item.Label} ") { Foreground = item.LabelColor, FontWeight = FontWeights.SemiBold });
        var value = new Run(item.Text);
        if (item.TextColor is { } color)
        {
            value.Foreground = color;
            value.FontWeight = FontWeights.Bold;
        }

        text.Inlines.Add(value);

        if (Settings.Background != BackgroundStyle.Bubbles)
        {
            text.Margin = margin;
            return text;
        }

        return new Border
        {
            Child = text,
            Margin = margin,
            Padding = new Thickness(6, 2, 6, 2),
            Background = Palette.Brush(Settings.BackgroundColor),
        };
    }
}
