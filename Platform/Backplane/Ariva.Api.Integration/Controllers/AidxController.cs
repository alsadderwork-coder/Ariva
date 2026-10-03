using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Api.Integration.Batches;
using Ariva.Core.Flights;
using Ariva.Core.Integration;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Integration;
using Ariva.Infra.Flights.Aidx;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Integration.Controllers;

/// <summary>
/// AIDX 22.1 inbound (ARV-044, wiki 08 section 8): an AODB pushes <c>IATA_AIDX_FlightLegNotifRQ</c> messages for one
/// site. The message is read by <see cref="AidxReader"/> (no DTD, no resolver, 5 MB, 32 levels, schema validated, 500
/// legs), each FlightLeg is mapped for the site and applied through the flight intake as the client's own feed, and the
/// answer is an <c>IATA_AIDX_FlightLegRS</c> acknowledgement: <c>Success</c>, with a <c>Warning</c> per leg that was
/// refused or carried nothing new (<c>RecordID</c> is the leg's position, from 0). A message that cannot be read is a
/// 400 problem; 413 beyond 5 MB; 415 for anything but XML. <c>Idempotency-Key</c> is optional here (many AODBs cannot
/// set headers; the required TimeStamp is the message time, so a message sent again is no newer than what it would
/// overwrite and changes nothing); with one, a
/// retry gets the first answer and the same key with another message is 422.
/// </summary>
[ApiController]
[Route("api/v1/integration/sites/{siteCode}")]
[EnableRateLimiting(RateLimitingExtensions.IntegrationAidxPolicy)]
public sealed partial class AidxController(ISvcAidxIntake aidx, ISvcIntegrationIdempotency idempotency, TimeProvider timeProvider)
    : ControllerBase
{
    private const string XmlType = "application/xml; charset=utf-8";
    private static readonly XNamespace Ns = AidxReader.Namespace;

    [HttpPost("aodb/aidx")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    [RequestSizeLimit(IntegrationBatches.MaxAidxBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Push(string siteCode, CancellationToken ct)
    {
        var caller = IntegrationAuthentication.CallerOf(HttpContext);
        if (caller is null)
            return Forbid();

        var keys = Request.Headers[IntegrationBatches.IdempotencyKeyHeader];
        if (keys.Count > 1 || (keys.Count == 1 && !IntegrationBatches.IsKey(keys[0])))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid",
                detail: $"At most one {IntegrationBatches.IdempotencyKeyHeader} header, of 8 to 64 letters, digits or . _ : - characters.");
        if (!BatchBody.IsXml(Request.ContentType))
            return Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported media type", detail: "An AIDX message is application/xml or text/xml.");

        byte[] body;
        try
        {
            body = await BatchBody.ReadAsync(Request, IntegrationBatches.MaxAidxBytes, ct);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            body = null;
        }

        if (body is null)
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: "An AIDX message is at most 5 MB.");

        var (message, error) = AidxReader.Read(body);
        if (error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: error);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (message.TimeStamp is { } sent && !FlightRules.IsPlausibleSource(sent, now))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid",
                detail: "TimeStamp is UTC, at most 5 minutes ahead of Ariva's clock and 30 days behind it.");

        IdempotencyRequest claimed = null;
        if (keys.Count == 1)
        {
            var request = new IdempotencyRequest(caller.ClientId, keys[0], IntegrationBatches.Aidx, siteCode, Convert.ToHexStringLower(SHA256.HashData(body)));
            var claim = await idempotency.ClaimAsync(request, ct);
            switch (claim.Outcome)
            {
                case IdempotencyOutcome.Mismatch:
                    return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Key reused",
                        detail: $"This {IntegrationBatches.IdempotencyKeyHeader} was used for another request in the last 24 hours; use a new key for a new message.");
                case IdempotencyOutcome.Replay:
                    Response.Headers[IntegrationBatches.ReplayedHeader] = "true";
                    return new ContentResult { StatusCode = claim.StatusCode, Content = claim.ResponseBody, ContentType = XmlType };
            }

            claimed = request;
        }

        var results = await aidx.ApplyAsync(siteCode, IntegrationBatches.FeedOf(caller.ClientId), message, ct);
        var answer = Acknowledgement(message.TransactionIdentifier, results, now);
        if (claimed is not null)
            await idempotency.CompleteAsync(claimed, StatusCodes.Status200OK, answer, ct);
        return new ContentResult { StatusCode = StatusCodes.Status200OK, Content = answer, ContentType = XmlType };
    }

    /// <summary>
    /// <c>IATA_AIDX_FlightLegRS</c> in the OTA acknowledgement pattern: <c>Success</c>, then a <c>Warning</c> per leg
    /// that was refused (<c>Status="Refused"</c>, with Ariva's reasons) or changed nothing (<c>Status="Unchanged"</c>).
    /// The sender's TransactionIdentifier is echoed when it is a plain identifier.
    /// </summary>
    public static string Acknowledgement(string transactionIdentifier, IReadOnlyList<FlightItemResult> results, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(results);
        var root = new XElement(Ns + "IATA_AIDX_FlightLegRS",
            new XAttribute("Version", "22.1"),
            new XAttribute("TimeStamp", XmlConvert.ToString(DateTime.SpecifyKind(now, DateTimeKind.Utc), XmlDateTimeSerializationMode.Utc)),
            new XElement(Ns + "Success"));
        if (transactionIdentifier is not null && PlainIdentifier().IsMatch(transactionIdentifier))
            root.Add(new XAttribute("TransactionIdentifier", transactionIdentifier));
        var warnings = results.Where(r => r.HasErrors || !r.Applied).Select(r => new XElement(Ns + "Warning",
                new XAttribute("RecordID", r.Index.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("Status", r.HasErrors ? "Refused" : "Unchanged"),
                new XAttribute("ShortText", string.Join(" ", r.HasErrors ? r.Errors : r.Warnings) is { Length: > 0 } text ? text : "Not applied.")))
            .ToList();
        if (warnings.Count > 0)
            root.Add(new XElement(Ns + "Warnings", warnings));
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var writer = new Utf8StringWriter();
        document.Save(writer, SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PlainIdentifier();

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
