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

/// <summary>What an outbound endpoint is for (ARV-045): a generic connection, or the ACRIS flight pull for one site.</summary>
public enum OutboundEndpointPurpose
{
    Generic,
    AcrisFlights
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
