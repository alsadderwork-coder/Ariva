using System.Security.Cryptography;
using Ariva.Infra.Settings;
using Microsoft.IdentityModel.Tokens;

namespace Ariva.Infra.Security;

/// <summary>
/// ES256 keys for access tokens (ADR-0026). Ariva.Api.Main signs with the private key from the ariva-token-signing
/// secret; every host validates with the public keys (current first, then the previous key for 90 days after a
/// rotation). The key id ("kid") is derived from the public key, so a token names the key that verifies it.
/// vm-local generates one key pair in the user profile, shared by all local hosts. Anything else without keys fails
/// startup.
/// </summary>
public sealed class TokenKeys
{
    private TokenKeys(ECDsaSecurityKey signingKey, IReadOnlyList<ECDsaSecurityKey> validationKeys)
    {
        SigningKey = signingKey;
        ValidationKeys = validationKeys;
    }

    /// <summary>Null on hosts that only validate.</summary>
    public ECDsaSecurityKey SigningKey { get; }

    public IReadOnlyList<ECDsaSecurityKey> ValidationKeys { get; }

    public static TokenKeys Load(TokenSettings settings, bool requireSigningKey)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string signingPath = settings.SigningKeyPath;
        var publicPaths = settings.PublicKeyPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        if (string.IsNullOrWhiteSpace(signingPath) && publicPaths.Count == 0 && settings.UseDevelopmentKeys)
        {
            (signingPath, var publicPath) = DevelopmentKeyPair(settings.DevelopmentKeyDirectory);
            publicPaths.Add(publicPath);
        }

        if (publicPaths.Count == 0)
        {
            throw new InvalidOperationException(
                "Access tokens need public keys: set Auth:Tokens:PublicKeyPaths (mounted from the ariva-token-public secret), " +
                "or Auth:Tokens:UseDevelopmentKeys on a developer machine.");
        }

        // The current key must exist; previous keys are optional (the secret has no previous.pem outside a rotation).
        var validation = publicPaths
            .Where((path, index) => index == 0 || File.Exists(path))
            .Select(LoadPublic)
            .ToList();
        ECDsaSecurityKey signing = null;
        if (requireSigningKey)
        {
            if (string.IsNullOrWhiteSpace(signingPath))
                throw new InvalidOperationException("Ariva.Api.Main issues tokens and needs Auth:Tokens:SigningKeyPath.");
            signing = LoadPrivate(signingPath);
            if (!validation.Any(key => key.KeyId == signing.KeyId))
                throw new InvalidOperationException("The signing key's public key is not among Auth:Tokens:PublicKeyPaths; tokens would not verify.");
        }

        return new TokenKeys(signing, validation);
    }

    /// <summary>First 16 bytes of the SHA-256 of the SubjectPublicKeyInfo, base64url.</summary>
    public static string KeyIdOf(ECDsa key) =>
        Base64UrlEncoder.Encode(SHA256.HashData(key.ExportSubjectPublicKeyInfo())[..16]);

    private static ECDsaSecurityKey LoadPrivate(string path)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(path));
        RequireP256(ecdsa, path);
        return new ECDsaSecurityKey(ecdsa) { KeyId = KeyIdOf(ecdsa) };
    }

    private static ECDsaSecurityKey LoadPublic(string path)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(path));
        RequireP256(ecdsa, path);
        var publicOnly = ECDsa.Create(ecdsa.ExportParameters(includePrivateParameters: false));
        return new ECDsaSecurityKey(publicOnly) { KeyId = KeyIdOf(publicOnly) };
    }

    private static void RequireP256(ECDsa key, string path)
    {
        if (key.KeySize != 256)
            throw new InvalidOperationException($"{path} is not a P-256 key; ES256 needs P-256.");
    }

    /// <summary>
    /// One P-256 key per developer machine, shared by the hosts running there. Created once with an atomic move, so
    /// hosts starting together cannot write two different keys; the public key is read from the same file.
    /// </summary>
    private static (string SigningPath, string PublicPath) DevelopmentKeyPair(string directory)
    {
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ariva")
            : directory;
        var signingPath = Path.Combine(directory, "token-signing-dev.key");

        if (!File.Exists(signingPath))
        {
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"token-signing-dev.{Guid.NewGuid():N}.tmp");
            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var writer = new StreamWriter(temporary, options);
                writer.Write(key.ExportPkcs8PrivateKeyPem());
            }

            try
            {
                File.Move(temporary, signingPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(signingPath))
            {
                File.Delete(temporary); // another host created it first; use theirs
            }
        }

        return (signingPath, signingPath);
    }
}
