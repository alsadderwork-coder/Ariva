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
            // One read of the development key gives both halves, so they always belong together.
            var development = DevelopmentKey(settings.DevelopmentKeyDirectory);
            return new TokenKeys(requireSigningKey ? development : null, [PublicOf(development.ECDsa)]);
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
        return PublicOf(ecdsa);
    }

    private static ECDsaSecurityKey PublicOf(ECDsa ecdsa)
    {
        var publicOnly = ECDsa.Create(ecdsa.ExportParameters(includePrivateParameters: false));
        return new ECDsaSecurityKey(publicOnly) { KeyId = KeyIdOf(publicOnly) };
    }

    private static void RequireP256(ECDsa key, string path)
    {
        if (key.KeySize != 256)
            throw new InvalidOperationException($"{path} is not a P-256 key; ES256 needs P-256.");
    }

    /// <summary>
    /// One P-256 key per developer machine, shared by the hosts running there. The file is created exclusively
    /// (CreateNew, so exactly one host writes it, even where the file system cannot move atomically) and held
    /// unshared while it is written; the other hosts read it once it is complete, retrying for up to five seconds.
    /// </summary>
    private static ECDsaSecurityKey DevelopmentKey(string directory)
    {
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ariva")
            : directory;
        var signingPath = Path.Combine(directory, "token-signing-dev.key");
        Directory.CreateDirectory(directory);

        if (!File.Exists(signingPath))
        {
            FileStream created = null;
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                created = new FileStream(signingPath, options);
            }
            catch (IOException) when (File.Exists(signingPath))
            {
                // Another host created it first; use theirs.
            }

            if (created is not null)
            {
                try
                {
                    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    using var writer = new StreamWriter(created);
                    writer.Write(key.ExportPkcs8PrivateKeyPem());
                }
                catch
                {
                    // Never leave a half-written key behind for every later start to trip over.
                    created.Dispose();
                    File.Delete(signingPath);
                    throw;
                }
            }
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return LoadPrivate(signingPath);
            }
            catch (Exception e) when (e is IOException or ArgumentException or CryptographicException)
            {
                if (attempt >= 250)
                    throw new InvalidOperationException(
                        $"The development signing key {signingPath} is unreadable or incomplete; delete it and start again.", e);
                Thread.Sleep(20); // still being written by the host that created it
            }
        }
    }
}
