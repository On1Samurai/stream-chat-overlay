using StreamChat.Core;

namespace StreamChat.Tests;

public class PlacementTests
{
    private const double ScreenW = 1920, ScreenH = 1080, W = 340, H = 200;

    private static (double Left, double Top) Locate(Placement placement, double height = H) =>
        placement.Locate(ScreenW, ScreenH, W, height);

    [Theory]
    [InlineData(Position.TopLeft, 24, 24)]
    [InlineData(Position.TopCenter, 790, 24)]
    [InlineData(Position.TopRight, 1556, 24)]
    [InlineData(Position.MiddleLeft, 24, 440)]
    [InlineData(Position.MiddleRight, 1556, 440)]
    [InlineData(Position.BottomLeft, 24, 856)]
    [InlineData(Position.BottomCenter, 790, 856)]
    [InlineData(Position.BottomRight, 1556, 856)]
    public void Preset_is_aligned_to_screen(Position position, double left, double top) =>
        Assert.Equal((left, top), Locate(new Placement { Position = position }));

    [Fact]
    public void Bottom_preset_grows_upwards()
    {
        var placement = new Placement { Position = Position.BottomRight };
        Assert.Equal(1056, Locate(placement, 80).Top + 80);
        Assert.Equal(1056, Locate(placement, 300).Top + 300);
    }

    [Fact]
    public void Window_dragged_to_upper_half_keeps_its_top()
    {
        var placement = new Placement().DraggedTo(500, 100, W, H, ScreenH);
        Assert.Equal((500d, 100d), Locate(placement, 60));
    }

    [Fact]
    public void Window_dragged_to_lower_half_keeps_its_bottom()
    {
        var placement = new Placement().DraggedTo(500, 700, W, H, ScreenH);
        Assert.Equal((500d, 840d), Locate(placement, 60));
    }

    [Theory]
    [InlineData(-900, -900, 0, 0)]
    [InlineData(9000, 9000, 1580, 880)]
    public void Window_never_leaves_the_screen(double x, double y, double left, double top) =>
        Assert.Equal((left, top), Locate(new Placement { Position = Position.Custom, X = x, Y = y }));

    [Fact]
    public void Window_taller_than_screen_sticks_to_top() =>
        Assert.Equal(0, Locate(new Placement { Position = Position.BottomLeft }, 2000).Top);
}
