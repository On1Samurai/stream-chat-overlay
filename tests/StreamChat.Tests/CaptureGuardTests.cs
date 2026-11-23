using StreamChat.Core;

namespace StreamChat.Tests;

public class CaptureGuardTests
{
    private const int Error8 = 8;

    // Имитация Windows: Get работает только у layered-окна, зрители видят показанное непрозрачное незащищённое окно
    private sealed class FakeWindow : ICaptureWindow
    {
        public bool Affinity, Layered, Visible, Opaque;
        public bool Leaked;
        public int Error;
        public bool FailsOnlyWhenLayered, LiesAboutSuccess, HiddenReadsAsUnprotected;
        public int SetCalls;

        public bool IsExcluded => Layered && Affinity && (Visible || !HiddenReadsAsUnprotected);

        public void MakeTransparent()
        {
            Layered = true;
            Opaque = false;
            Observe();
        }

        public int Exclude()
        {
            SetCalls++;
            if (Error != 0 && (!FailsOnlyWhenLayered || Layered)) return Error;
            Affinity = !LiesAboutSuccess;
            return 0;
        }

        public void RemoveLayered()
        {
            Layered = false;
            Observe();
        }

        public void Show()
        {
            Visible = true;
            Observe();
        }

        public void Hide()
        {
            Visible = false;
            Observe();
        }

        public void MakeOpaque()
        {
            Opaque = true;
            Observe();
        }

        private void Observe() => Leaked |= Visible && (Opaque || !Layered) && !Affinity;
    }

    [Fact]
    public void Protected_window_becomes_visible_without_a_leak()
    {
        var window = new FakeWindow();
        Assert.True(CaptureGuard.Show(window).Protected);
        Assert.True(window is { Visible: true, Opaque: true, Affinity: true, Leaked: false });
    }

    [Fact]
    public void Already_protected_window_is_not_set_again()
    {
        var window = new FakeWindow { Affinity = true };
        Assert.True(CaptureGuard.Show(window).Protected);
        Assert.Equal(0, window.SetCalls);
    }

    [Fact]
    public void Success_without_confirmation_never_becomes_opaque()
    {
        var window = new FakeWindow { LiesAboutSuccess = true };
        Assert.False(CaptureGuard.Show(window).Protected);
        Assert.True(window is { Visible: false, Opaque: false, Leaked: false });
    }

    [Fact]
    public void Confirmation_may_arrive_only_after_transparent_show()
    {
        var window = new FakeWindow { HiddenReadsAsUnprotected = true };
        Assert.True(CaptureGuard.Show(window).Protected);
        Assert.True(window is { Visible: true, Leaked: false });
    }

    [Fact]
    public void Error_8_on_first_show_is_retried_without_layered_style()
    {
        var window = new FakeWindow { Error = Error8, FailsOnlyWhenLayered = true };
        Assert.True(CaptureGuard.Show(window).Protected);
        Assert.Equal(2, window.SetCalls);
        Assert.True(window is { Layered: true, Opaque: true, Leaked: false });
    }

    [Fact]
    public void Error_8_twice_stays_hidden_and_transparent()
    {
        var window = new FakeWindow { Error = Error8 };
        Assert.Equal(new CaptureResult(false, Error8), CaptureGuard.Show(window));
        Assert.True(window is { Visible: false, Layered: true, Opaque: false });
    }

    [Fact]
    public void Previously_opaque_window_is_transparent_before_show()
    {
        var window = new FakeWindow { Layered = true, Opaque = true, LiesAboutSuccess = true };
        Assert.False(CaptureGuard.Show(window).Protected);
        Assert.False(window.Leaked);
    }

    [Fact]
    public void Other_errors_are_not_retried()
    {
        var window = new FakeWindow { Error = 5 };
        Assert.Equal(new CaptureResult(false, 5), CaptureGuard.Show(window));
        Assert.Equal(1, window.SetCalls);
        Assert.False(window.Visible);
    }

    [Theory]
    [InlineData(10, 19041, true)]
    [InlineData(10, 26200, true)]
    [InlineData(10, 19040, false)]
    [InlineData(10, 18363, false)]
    [InlineData(6, 9600, false)]
    public void Exclusion_requires_build_19041(int major, int build, bool supported) =>
        Assert.Equal(supported, CaptureGuard.IsSupported(new Version(major, 0, build)));
}
