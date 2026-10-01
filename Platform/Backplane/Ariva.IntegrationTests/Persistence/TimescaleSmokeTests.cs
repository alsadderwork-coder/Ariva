namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// Placeholder until Testcontainers (PostgreSQL with TimescaleDB, Kafka, Redis) are added. It keeps the
/// project discoverable by the test runner and in the PR validation pipeline.
/// </summary>
public sealed class TimescaleSmokeTests
{
    [Fact(Skip = "Enabled when Testcontainers are added in story ARV-007")]
    public void OpenConnection_Should_Succeed_When_TimescaleContainerIsRunning()
    {
        // Story ARV-007 starts a timescale/timescaledb-ha container, applies Ariva.Infra/Timescale/Scripts
        // and asserts that the timescaledb extension is installed.
    }
}
