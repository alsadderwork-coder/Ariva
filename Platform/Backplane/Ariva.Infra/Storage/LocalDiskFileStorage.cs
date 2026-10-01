using System.Text.RegularExpressions;
using Ariva.Core.Services.Storage;
using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.Storage;

/// <summary>
/// Files on a local disk (or a mounted volume) under <c>Storage:LocalRoot</c> (ARV-018). Keys are checked against one
/// pattern and the resolved path must stay under the root, so no key can reach another directory (CWE-22). Files are
/// written to a temporary name and moved into place, so a reader never sees half a file.
/// </summary>
public sealed partial class LocalDiskFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalDiskFileStorage(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration["Storage:LocalRoot"];
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "ariva-files") : configured);
        Directory.CreateDirectory(_root);
    }

    public static bool IsValidKey(string key) => key is not null && KeyPattern().IsMatch(key);

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = PathOf(key);
        var temporary = path + ".partial";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);
            File.Move(temporary, path, overwrite: false);
        }
        catch
        {
            // A cancelled or failed write leaves nothing behind.
            File.Delete(temporary);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathOf(key);
        try
        {
            return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Gone, or removed by a replacement committed a moment ago.
            return Task.FromResult<Stream>(null);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathOf(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathOf(string key)
    {
        if (!IsValidKey(key))
            throw new ArgumentException("Not a storage key.", nameof(key));
        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Not a storage key.", nameof(key));
        return path;
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.(png|jpg|svg)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyPattern();
}
