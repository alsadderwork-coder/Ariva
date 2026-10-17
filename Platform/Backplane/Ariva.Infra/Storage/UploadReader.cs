namespace Ariva.Infra.Storage;

/// <summary>
/// Reads an upload's bytes up to a limit (ARV-018, ARV-142a, CWE-400): the read stops at the limit whatever the request
/// declared, so a body larger than its declared length is refused rather than read whole.
/// </summary>
public static class UploadReader
{
    /// <summary>The bytes, or null when the stream holds more than <paramref name="limit"/>.</summary>
    public static async Task<byte[]> ReadAtMostAsync(Stream content, long limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
