using Ariva.Infra.Services.Reports;
using FluentAssertions;
using NHibernate.Exceptions;
using Npgsql;

namespace Ariva.UnitTests.Reports;

/// <summary>
/// ARV-291: a scheduled delivery counts the attempt of a read that failed in the database (a database exception in the failure or
/// under it, as NHibernate wraps them), never of one its round's cancellation cut (ARV-096e) nor of a failure of another kind
/// (that one is marked Failed in the delivery's own transaction, which is still usable).
/// </summary>
public sealed class ReportDeliveryFailureTests
{
    public static TheoryData<string, Exception, bool> Failures() => new()
    {
        { "a statement timeout", new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled), true },
        { "the client's command timeout", new NpgsqlException("Exception while reading from stream", new TimeoutException()), true },
        { "a terminated session, through NHibernate", new GenericADOException("could not execute query", new PostgresException("terminating connection", "FATAL", "FATAL", PostgresErrorCodes.AdminShutdown)), true },
        { "a lost connection, twice wrapped", new InvalidOperationException("read", new GenericADOException("could not execute query", new NpgsqlException("reset", new IOException("reset")))), true },
        { "the round cancelled", new OperationCanceledException("cancelled", new PostgresException("canceling statement due to user request", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled)), false },
        { "the round cancelled, through NHibernate", new GenericADOException("could not execute query", new OperationCanceledException("cancelled", new NpgsqlException("cancelled"))), false },
        { "a failure of another kind", new InvalidOperationException("The report reader answered an error other than its read bounds."), false },
        { "a mapping failure through NHibernate", new GenericADOException("could not execute query", new FormatException("not a number")), false }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void FailedInDatabase_Should_TellADatabaseFailureFromACancelledRound_When_TheReadFails(string what, Exception failure, bool counted) =>
        SvcReportDeliveries.FailedInDatabase(failure).Should().Be(counted, what);
}
