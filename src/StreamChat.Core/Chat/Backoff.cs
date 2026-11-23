namespace StreamChat.Core.Chat;

internal static class Backoff
{
    public static TimeSpan After(int failures, TimeSpan first, TimeSpan max) =>
        TimeSpan.FromTicks(Math.Min(max.Ticks, first.Ticks * (1L << Math.Clamp(failures - 1, 0, 10))));
}
