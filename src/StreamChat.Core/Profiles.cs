namespace StreamChat.Core;

/// <summary>Вид и место окон для одной игры.</summary>
public sealed record LayoutProfile
{
    public string Name { get; init; } = "";
    public Placement Placement { get; init; } = new();
    public Placement ViewersPlacement { get; init; } = new();
    public Placement ObsPlacement { get; init; } = new();
    public int FontSize { get; init; } = 15;
    public int ViewersFontSize { get; init; } = 18;
    public int ObsFontSize { get; init; } = 16;
    public int Opacity { get; init; } = 75;
}

/// <summary>Сравнивается по содержимому: на равенстве настроек держатся «Отмена» и живой образец. После создания не менять.</summary>
public sealed class ProfileList : List<LayoutProfile>
{
    public override bool Equals(object? other) => other is ProfileList list && this.SequenceEqual(list);

    public override int GetHashCode() => Count;
}
