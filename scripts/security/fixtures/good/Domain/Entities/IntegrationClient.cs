namespace Fixtures.Good.Domain.Entities;

public class IntegrationClient
{
    public string ClientSecretHash { get; protected set; }
    public string TotpSecretEncrypted { get; protected set; }
}
