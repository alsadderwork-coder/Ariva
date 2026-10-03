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
