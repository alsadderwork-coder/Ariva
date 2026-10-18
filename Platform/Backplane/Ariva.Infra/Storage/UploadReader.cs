namespace Ariva.Infra.Storage;

/// <summary>
/// Reads an upload's bytes up to a limit (ARV-018, ARV-142a, CWE-400): the read stops at the limit whatever the request
/// declared, so a body larger than its declared length is refused rather than read whole.
/// </summary>
public static class UploadReader
{
    /// <summary>
    /// The bytes, or null when the stream holds more than <paramref name="limit"/>. <paramref name="expectedLength"/>, the
    /// declared length, sizes the buffer up to the limit (a 100 MB video is then read into one array, not grown by doubling);
    /// the read still stops at the limit whatever was declared.
    /// </summary>
    public static async Task<byte[]> ReadAtMostAsync(Stream content, long limit, CancellationToken ct, long expectedLength = 0)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream((int)Math.Clamp(expectedLength, 0, Math.Min(limit, Array.MaxLength)));
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
                return null;
            buffer.Write(chunk, 0, read);
        }

        // The buffer itself when the declared length was right, a copy of what was read otherwise.
        return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
    }
}
