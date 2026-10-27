using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// A request's minimum body rate as Kestrel 10 keeps it (ARV-195, measured on Kestrel 10.0.12): over HTTP/1.x it takes and gives
/// back any rate; over HTTP/2 and HTTP/3 (<c>refuses</c>) reading it throws and setting it throws unless the value is null, since
/// the connection keeps the server's own rate. So code that reads the rate before checking the protocol fails here as it would
/// in Kestrel. <see cref="Stored"/> is what was set, read without the throw.
/// </summary>
public sealed class BodyRateFeature(bool refuses) : IHttpMinRequestBodyDataRateFeature
{
    #region Fields

    private const string Refusal = "HTTP/2 and HTTP/3 take the connection's rate.";

    #endregion

    #region Properties

    /// <summary>The rate set, for the test to read.</summary>
    public MinDataRate Stored { get; private set; }

    public MinDataRate MinDataRate
    {
        get => refuses ? throw new NotSupportedException(Refusal) : Stored;
        set => Stored = refuses && value is not null ? throw new NotSupportedException(Refusal) : value;
    }

    #endregion
}
