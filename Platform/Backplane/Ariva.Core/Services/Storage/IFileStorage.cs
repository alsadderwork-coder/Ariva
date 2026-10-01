namespace Ariva.Core.Services.Storage;

/// <summary>
/// Binary files under keys Ariva generates (ARV-018): local disk in development and single-node installs, an
/// S3-compatible store later. A key is 32 hex characters, a dot and a known extension; nothing a caller sends ever
/// becomes part of a path (CWE-22).
/// </summary>
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);

    /// <summary>The content, or null when there is no file under the key.</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
