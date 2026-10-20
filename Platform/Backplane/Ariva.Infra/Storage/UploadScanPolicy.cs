namespace Ariva.Infra.Storage;

/// <summary>
/// What an upload does when the malware scan gives no verdict (ARV-092b): refused in every environment but vm-local (fail
/// closed); in vm-local, where many developer runs and unit tests have no scanner, let through with a warning (fail open).
/// Decided once at start from <c>Application:Environment</c>, which ARV-098 holds to the host's own environment.
/// </summary>
internal sealed record UploadScanPolicy(bool FailOpen)
{
    public static UploadScanPolicy For(string applicationEnvironment) => new(string.Equals(applicationEnvironment, "vm-local", StringComparison.Ordinal));
}
