using System.Security.Cryptography;
using System.Text;
using StreamChat.Core.Updates;

namespace StreamChat.Tests.Rules;

public class UpdateSignatureTests
{
    // Вектор: временный ключ, подпись openssl для Manifest("1.2.3", "ab" * 32).
    private const string TestKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/sIBdSUdFDwbN2BF9F9/OHc7C1kMl5Q6fln/yK7Vd93zSx479sCFed1UApoyftdjliAH+RBlOifcRy2LVf5Gmg==";
    private const string TestSig = "MEUCIBzLOROQ+TbX+90dwjMt23/E9AORqr1yVvDxoSnvhQ3TAiEA/rZTD7rxKAf1PsZMNFamq6/5E9L2nDSkvdCiP4I77CU=";
    private static readonly string Hash = string.Concat(Enumerable.Repeat("ab", 32));

    private static bool Check(string version, string hash, byte[] sig) =>
        UpdateSignature.Verify(version, hash, sig, TestKey);

    [Fact]
    public void Manifest_format() =>
        Assert.Equal("StreamChat-update-v1\nversion=1.2.3\nsha256=" + new string('a', 64) + "\n",
            UpdateSignature.Manifest("1.2.3", new string('a', 64)));

    [Fact]
    public void Openssl_signature_is_accepted() =>
        Assert.True(Check("1.2.3", Hash, Convert.FromBase64String(TestSig)));

    [Fact]
    public void Other_version_is_rejected() =>
        Assert.False(Check("1.2.4", Hash, Convert.FromBase64String(TestSig)));

    [Fact]
    public void Other_hash_is_rejected() =>
        Assert.False(Check("1.2.3", "ac" + Hash[2..], Convert.FromBase64String(TestSig)));

    [Fact]
    public void Garbage_signature_is_rejected()
    {
        Assert.False(Check("1.2.3", Hash, new byte[70]));
        Assert.False(Check("1.2.3", Hash, []));
    }

    [Fact]
    public void Bad_hash_format_is_rejected()
    {
        var sig = Convert.FromBase64String(TestSig);
        Assert.False(Check("1.2.3", Hash.ToUpperInvariant(), sig));
        Assert.False(Check("1.2.3", Hash[2..], sig));
    }

    [Fact]
    public void Dotnet_signed_roundtrip()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sig = key.SignData(Encoding.UTF8.GetBytes(UpdateSignature.Manifest("2.0.0", Hash)),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.True(UpdateSignature.Verify("2.0.0", Hash, sig, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        Assert.False(UpdateSignature.Verify("2.0.0", Hash, sig, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void Embedded_key_is_valid()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateSignature.PublicKey), out _);
        Assert.Equal(256, key.KeySize);
    }
}
