using System.Security.Cryptography;
using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Services.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.Infra.Storage;

/// <summary>
/// The malware screening of every upload (ARV-092b, ASVS 5.0 V5.4.3), called by each upload service after its size check
/// and before anything parses, stores or imports the bytes, on exactly the bytes it then uses:
/// <list type="bullet">
/// <item>clean: the upload goes on;</item>
/// <item>a detection: refused with <see cref="UploadScanErrors.Malware"/> (400), audited in a transaction of its own (action
/// <see cref="RefusedAction"/>, target type the upload kind, target name the signature, the summary with the kind, the
/// signature, the byte count and the content's SHA-256, never the file or its name) and logged as security event 9131
/// (the caller, the kind and the signature only);</item>
/// <item>no verdict: outside vm-local refused with <see cref="UploadScanErrors.ScannerUnavailable"/> (503) and logged as
/// security event 9132 with a fixed reason (fail closed); in vm-local let through with a warning, and with no scanner
/// configured there not scanned at all (<see cref="UploadScanStartNotice"/> says so once).</item>
/// </list>
/// </summary>
internal sealed class UploadScreening(
    IMalwareScanner scanner,
    IOptions<MalwareScanSettings> settings,
    UploadScanPolicy policy,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IServiceScopeFactory scopes,
    ILogger<UploadScreening> logger)
{
    #region Fields

    /// <summary>The audit action of an upload refused for malware.</summary>
    public const string RefusedAction = "Upload.MalwareRefused";

    public static readonly EventId MalwareRefused = new(9131, "SecurityEvent.UploadMalwareRefused");

    public static readonly EventId ScanUnavailable = new(9132, "SecurityEvent.UploadScanUnavailable");

    #endregion

    #region Screening

    /// <summary>Screens bytes already read (and size-checked) by the upload service.</summary>
    public async Task<Result> ScreenAsync(UploadKind kind, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var stream = new MemoryStream(content, writable: false);
        return await ScreenAsync(kind, stream, ct);
    }

    /// <summary>
    /// Screens the content from its position to its end; the stream is read through, so a caller that reads it again
    /// rewinds or reopens it.
    /// </summary>
    public async Task<Result> ScreenAsync(UploadKind kind, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var scanSettings = settings.Value;
        if (!scanSettings.Configured && policy.FailOpen)
            return Result.Return();

        MalwareScan scan;
        using var observed = new ObservedStream(content);
        if (content.CanSeek && content.Length - content.Position > scanSettings.MaxStreamBytes)
            scan = MalwareScan.Unavailable(MalwareScanReasons.TooLarge);
        else
            scan = await scanner.ScanAsync(observed, ct);

        switch (scan.Verdict)
        {
            case MalwareVerdict.Clean:
                return Result.Return();
            case MalwareVerdict.Infected:
                var signature = MalwareScan.SafeSignature(scan.Signature);
                await AuditAsync(kind, signature, observed.Bytes, observed.Sha256());
                logger.LogWarning(MalwareRefused, "A {UploadKind} upload by {UserId} was refused: the malware scanner found {Signature}", kind, currentUser.Id, signature);
                return Result.Error(UploadScanErrors.Malware);
            default:
                return NoVerdict(kind, scan.Reason ?? MalwareScanReasons.ReplyNotUnderstood);
        }
    }

    /// <summary>
    /// Screens the parts of one upload (a site configuration bundle's decoded parts, ARV-122a) in order under one deadline for
    /// them all, <c>MalwareScan:TimeoutSeconds</c> linked to the caller's token, as one file gets (CWE-400): the parts come
    /// from one body of at most 20 MB, the size of one floor plan, which one scan's budget covers, so a bundle of many slow
    /// parts cannot hold the host's one upload slot for a budget per part. The first refusal ends it; the deadline passed is
    /// no verdict (<see cref="UploadScanErrors.ScannerUnavailable"/> outside vm-local; in vm-local the parts not yet scanned
    /// are taken unscanned, with a warning).
    /// </summary>
    public Task<Result> ScreenAllAsync(UploadKind kind, IEnumerable<byte[]> parts, CancellationToken ct) =>
        ScreenAllAsync(kind, parts,
            TimeSpan.FromSeconds(Math.Clamp(settings.Value.TimeoutSeconds, MalwareScanSettings.MinTimeoutSeconds, MalwareScanSettings.MaxTimeoutSeconds)), ct);

    /// <summary>As <see cref="ScreenAllAsync(UploadKind, IEnumerable{byte[]}, CancellationToken)"/> with the deadline given (tests).</summary>
    internal async Task<Result> ScreenAllAsync(UploadKind kind, IEnumerable<byte[]> parts, TimeSpan budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (!settings.Value.Configured && policy.FailOpen)
            return Result.Return();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        foreach (var part in parts)
        {
            Result screened;
            try
            {
                screened = await ScreenAsync(kind, part, deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The bundle's deadline, not the caller: no verdict for the rest.
                return NoVerdict(kind, MalwareScanReasons.TimedOut);
            }

            if (screened.HasErrors)
                return screened;
        }

        return Result.Return();
    }

    /// <summary>No verdict: refused and logged as event 9132 with its fixed reason (fail closed), or in vm-local taken with a warning.</summary>
    private Result NoVerdict(UploadKind kind, string reason)
    {
        if (policy.FailOpen)
        {
            logger.LogWarning("The malware scanner gave no verdict on a {UploadKind} upload ({Reason}); it is taken unscanned, which only vm-local allows", kind, reason);
            return Result.Return();
        }

        logger.LogWarning(ScanUnavailable, "A {UploadKind} upload by {UserId} was refused: the malware scanner gave no verdict ({Reason})", kind, currentUser.Id, reason);
        return Result.Error(UploadScanErrors.ScannerUnavailable);
    }

    #endregion

    #region Audit

    /// <summary>
    /// The refusal's audit entry in a transaction of its own, as this request's caller (the new scope is not the request's),
    /// so it stays whatever the upload's own unit of work does. A failure to write it is logged; the upload is refused anyway.
    /// </summary>
    private async Task AuditAsync(UploadKind kind, string signature, long bytes, string sha256)
    {
        var summary = JsonSerializer.Serialize(new { kind = kind.ToString(), signature, bytes, sha256 }, JsonSerializerOptions.Web);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            try
            {
                await new AuditTrail(unitOfWork, currentUser, timeProvider).RecordAsync(RefusedAction, kind.ToString(), null, signature, null, summary, CancellationToken.None);
                unitOfWork.PromiseToCommit();
                await unitOfWork.EndAsync(CancellationToken.None);
            }
            catch
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "The audit entry of a {UploadKind} upload refused by the malware scan could not be written", kind);
        }
    }

    #endregion

    #region Observed stream

    /// <summary>
    /// The content as the scanner reads it, counted and hashed on the way (a detection's audit entry names its size and
    /// SHA-256 without a second read). Read-only and forward-only; the caller keeps the inner stream.
    /// </summary>
    private sealed class ObservedStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long Bytes { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public string Sha256() => Convert.ToHexStringLower(_hash.GetCurrentHash());

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Observe(buffer[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Observe(buffer.Span[..read]);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _hash.Dispose();
            base.Dispose(disposing);
        }

        private void Observe(ReadOnlySpan<byte> read)
        {
            _hash.AppendData(read);
            Bytes += read.Length;
        }
    }

    #endregion
}
