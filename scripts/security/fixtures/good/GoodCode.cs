namespace Fixtures.Good;

public class GoodCode(IHttpClientFactory httpClientFactory, IStorageProvider storage, ITotpReplayGuard replayGuard)
{
    public async Task Run(Guid id, byte[] presentedHash, byte[] storedHash, Totp totp, string code, string clientId)
    {
        var http = httpClientFactory.CreateClient("aodb");
        var response = await http.GetAsync($"flights/{id}");
        var rows = await storage.ExecuteSqlAsync<Row>("select * from flight where id = @id", new Dictionary<string, object> { ["id"] = id });
        var same = CryptographicOperations.FixedTimeEquals(presentedHash, storedHash);
        if (totp.VerifyTotp(code, out var step, VerificationWindow.RfcSpecifiedNetworkDelay) && await replayGuard.TryConsumeTimeStep(clientId, step)) { }
        if (secret == null) return;
        Span<byte> buffer = stackalloc byte[32];
        options.TokenValidationParameters = new() { ValidateLifetime = true, ValidateIssuer = true };
        cookie.SecurePolicy = CookieSecurePolicy.Always;
    }
}
