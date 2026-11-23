using System.Security.Cryptography;
using System.Text;

namespace StreamChat;

/// <summary>DPAPI: прочитать может только тот же пользователь Windows на том же ПК.</summary>
internal static class Secret
{
    public static string Protect(string plain) =>
        plain.Length == 0 ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    /// <returns>null, если файл настроек с другого ПК или испорчен.</returns>
    public static string? Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
