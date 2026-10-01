namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// Placeholder until Testcontainers (PostgreSQL with TimescaleDB, Kafka, Redis) are used here. It keeps the
/// project discoverable by the test runner and in the PR validation pipeline.
/// </summary>
public sealed class TimescaleSmokeTests
{
    [Fact(Skip = "Enabled with the versioned script runner in story ARV-006")]
    public void OpenConnection_Should_Succeed_When_TimescaleContainerIsRunning()
    {
        // Story ARV-006 starts a timescale/timescaledb-ha container, applies Ariva.Infra/Timescale/Scripts
        // and asserts that the timescaledb extension is installed.
    }
}
