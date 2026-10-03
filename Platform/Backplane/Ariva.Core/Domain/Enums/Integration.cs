namespace Ariva.Core.Domain.Enums;

/// <summary>What kind of system an integration client is (ARV-042); immigration clients need a TOTP code on every call by default.</summary>
public enum IntegrationClientKind
{
    Aodb,
    Immigration,
    SensorGateway,
    Other
}

public enum IntegrationClientStatus
{
    Active,
    Disabled
}

/// <summary>
/// What an outbound endpoint is for: a generic connection (ARV-045), the ACRIS flight pull for one site (ARV-045), or
/// the pull of AMAN's feed from its Integration API for one site where Kafka is not shared (ARV-050).
/// </summary>
public enum OutboundEndpointPurpose
{
    Generic,
    AcrisFlights,
    AmanFeed
}

/// <summary>How Ariva authenticates to an outbound endpoint (docs/architecture/integration.md, Outbound connections).</summary>
public enum OutboundAuthKind
{
    TotpClientCredentials,
    OAuth2ClientCredentials,
    ApiKeyHeader,
    HmacSignature,
    MutualTls
}

public enum OutboundEndpointStatus
{
    Active,
    Disabled
}
