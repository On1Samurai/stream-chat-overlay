using System.Reflection;
using System.Text.RegularExpressions;

namespace StreamChat.Core.Updates;

/// <summary>Репозиторий выпусков. Единственное место с адресом по умолчанию — Default; пустое свойство сборки даёт его. build.sh всегда передаёт свойство: пустое для выпуска, свой репозиторий только для тестовой сборки.</summary>
public static partial class UpdateRepository
{
    public const string Default = "On1Samurai/stream-chat-overlay";

    [GeneratedRegex(@"\A[A-Za-z0-9-]+/[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Valid();

    public static string Name { get; } = Pick(typeof(UpdateRepository).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepository")?.Value);

    /// <summary>Неверное значение молча заменяется на основной репозиторий.</summary>
    public static string Pick(string? value) => value is not null && Valid().IsMatch(value) ? value : Default;
}
