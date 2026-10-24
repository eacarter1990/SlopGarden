using System.Security.Cryptography;
using System.Text;

namespace SlopGarden.Contracts;

/// <summary>
/// Plugin signing, modelled on signed drivers: a publisher signs (pluginId, version, sha256 of the entry
/// assembly) with an ECDSA P-256 key. The public keys and approved hashes live in the server's
/// <c>plugin_signing_keys</c> / <c>plugin_catalog</c> tables and are pushed to clients.
/// </summary>
public static class PluginSigning
{
    public const string Algorithm = "ecdsa-p256-sha256";

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static byte[] Payload(string pluginId, string version, string sha256) =>
        Encoding.UTF8.GetBytes($"slopgarden-plugin\n{pluginId}\n{version}\n{sha256.ToLowerInvariant()}");

    public static (string PublicKeySpkiBase64, string PrivateKeyPkcs8Base64) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
    }

    public static string Sign(string pluginId, string version, string sha256, string privateKeyPkcs8Base64)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        return Convert.ToBase64String(ecdsa.SignData(Payload(pluginId, version, sha256), HashAlgorithmName.SHA256));
    }

    public static bool Verify(string pluginId, string version, string sha256, string signatureBase64, string publicKeySpkiBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            return ecdsa.VerifyData(Payload(pluginId, version, sha256), Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static string KeyId(string publicKeySpkiBase64) =>
        Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKeySpkiBase64)))[..16];
}

/// <summary>Contents of <c>plugin.json</c> in an external plugin folder.</summary>
public sealed record PluginManifest(
    string Id,
    string Version,
    string DisplayName,
    string EntryAssembly,
    string? Publisher,
    string? Description,
    IReadOnlyList<string>? Kinds);

/// <summary>Contents of <c>plugin.sig.json</c> next to <c>plugin.json</c>.</summary>
public sealed record PluginSignatureFile(string KeyId, string Algorithm, string Sha256, string Signature);

/// <summary>A trusted signing key distributed to clients.</summary>
public sealed record TrustedPluginKey(string KeyId, string Publisher, string PublicKeySpki, bool Revoked);
