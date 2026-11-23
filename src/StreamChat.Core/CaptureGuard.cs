namespace StreamChat.Core;

public interface ICaptureWindow
{
    bool IsExcluded { get; }

    /// <summary>Layered и непрозрачность 0.</summary>
    void MakeTransparent();

    /// <returns>0 при успехе, иначе код ошибки Windows.</returns>
    int Exclude();

    void RemoveLayered();
    void Show();
    void Hide();
    void MakeOpaque();
}

public readonly record struct CaptureResult(bool Protected, int Error);

/// <summary>Пока Windows не подтвердила защиту, окно полностью прозрачно.</summary>
public static class CaptureGuard
{
    private const int LayeredConflict = 8;
    private static readonly Version FirstSupported = new(10, 0, 19041);

    public static bool IsSupported(Version version) => version >= FirstSupported;

    /// <summary>Вызывать только для скрытого окна.</summary>
    public static CaptureResult Show(ICaptureWindow window)
    {
        // порядок важен: Get работает только у layered, а не layered с защитой DWM рисует даже скрытым
        window.MakeTransparent();

        if (!window.IsExcluded)
        {
            var error = window.Exclude();
            if (error == LayeredConflict)
            {
                window.RemoveLayered();
                error = window.Exclude();
                window.MakeTransparent();
            }

            if (error != 0) return new(false, error);
        }

        // у скрытого окна Windows может не подтвердить защиту, поэтому проверка после прозрачного показа
        window.Show();
        if (!window.IsExcluded)
        {
            window.Hide();
            return new(false, 0);
        }

        window.MakeOpaque();
        return new(true, 0);
    }
}
