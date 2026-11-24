using System.Security.Cryptography;
using System.Text;

namespace StreamChat.Core.Updates;

/// <summary>Проверка подписи выпуска: ECDSA P-256 + SHA-256 над текстом с версией и хешем архива.</summary>
public static class UpdateSignature
{
    /// <summary>Открытый ключ (SubjectPublicKeyInfo, base64). Закрытый хранится только у автора.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE7fKe1qVSIr2I3z4SU3ZnObohmiQT/od0s0xVl5rX+nwQI2JbG3ZJxmnYaMyLBTi/w4j6KpUtKytTO6qBGPUmPA==";

    public static string Manifest(string version, string sha256Hex) =>
        $"StreamChat-update-v1\nversion={version}\nsha256={sha256Hex}\n";

    public static bool Verify(string version, string sha256Hex, ReadOnlySpan<byte> signature) =>
        Verify(version, sha256Hex, signature, PublicKey);

    public static bool Verify(string version, string sha256Hex, ReadOnlySpan<byte> signature, string publicKeyBase64)
    {
        if (sha256Hex.Length != 64 || !sha256Hex.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(Manifest(version, sha256Hex)), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
