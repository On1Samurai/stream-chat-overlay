namespace StreamChat.Core;

public enum Position { TopLeft, TopCenter, TopRight, MiddleLeft, MiddleRight, BottomLeft, BottomCenter, BottomRight, Custom }

/// <summary>Где стоит окно. Размеры в единицах WPF.</summary>
public sealed record Placement
{
    public const double MinWidth = 220;
    public const double MaxWidth = 800;
    private const double Margin = 24;

    public Position Position { get; init; }
    public double X { get; init; }

    /// <summary>Верх окна, а при <see cref="GrowsUp"/> его низ.</summary>
    public double Y { get; init; }

    public bool GrowsUp { get; init; }
    public double Width { get; init; } = 340;

    public (double Left, double Top) Locate(double screenW, double screenH, double width, double height)
    {
        var right = screenW - width - Margin;
        var bottom = screenH - height - Margin;
        var centerX = (screenW - width) / 2;
        var centerY = (screenH - height) / 2;

        var (left, top) = Position switch
        {
            Position.TopCenter => (centerX, Margin),
            Position.TopRight => (right, Margin),
            Position.MiddleLeft => (Margin, centerY),
            Position.MiddleRight => (right, centerY),
            Position.BottomLeft => (Margin, bottom),
            Position.BottomCenter => (centerX, bottom),
            Position.BottomRight => (right, bottom),
            Position.Custom => (X, GrowsUp ? Y - height : Y),
            _ => (Margin, Margin),
        };

        // Max последним: окно больше экрана прижимается к верхнему левому краю
        return (Math.Max(0, Math.Min(left, screenW - width)), Math.Max(0, Math.Min(top, screenH - height)));
    }

    public Placement DraggedTo(double left, double top, double width, double height, double screenH)
    {
        var lowerHalf = top + height / 2 > screenH / 2;
        return new Placement
        {
            Position = Position.Custom,
            X = left,
            Y = lowerHalf ? top + height : top,
            GrowsUp = lowerHalf,
            Width = width,
        };
    }
}
