using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Ariva.Api.Common.Logging;
using Ariva.Api.Cronz.Jobs;
using Ariva.Infra.Integration;
using Ariva.Infra.Resilience;
using Ariva.Infra.Security;
using Ariva.UnitTests.Architecture;
using FluentAssertions;
using TickerQ.Utilities.Base;

namespace Ariva.UnitTests.Jobs;

/// <summary>
/// ARV-285 (CWE-532, CWE-200): TickerQ keeps a failed run's exception and its dashboard shows it, outside the log pipeline, so
/// every TickerQ function runs its work through <see cref="JobRun"/>, which throws on what escapes the run protected as log
/// text is. Credentials are drawn from Ariva's own generators in each test; no row names one (ARV-252), and the checks are
/// yes or no (ARV-249).
/// </summary>
public sealed class JobRunTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Holds(string text, string part) => text is not null && text.Contains(part, StringComparison.Ordinal);

    private static Exception Thrown(Func<Exception> make)
    {
        try
        {
            throw make();
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>Every part of an exception TickerQ or a reader of the dashboard can see: its message, text and stack, and its inner exceptions'.</summary>
    private static IEnumerable<string> Parts(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;
            yield return current.ToString();
            yield return current.StackTrace;
        }
    }

    [Fact]
    public async Task RunAsync_Should_ThrowTheFailureProtected_When_ItsTextHoldsACredentialOrAnAddress()
    {
        var secret = IntegrationCredentials.NewSecret();
        var seed = Base32.Encode(RandomNumberGenerator.GetBytes(20));
        var inner = Thrown(() => new InvalidOperationException($"seed {seed} refused for anna.salem@example.com"));

        var run = () => JobRun.RunAsync("Probe", Timeout, TimeProvider.System, _ => Task.FromException(new HttpRequestException($"token exchange failed with {secret}", inner)), Ct);

        var thrown = (await run.Should().ThrowAsync<ProtectedException>()).Which;
        foreach (var part in Parts(thrown))
        {
            Holds(part, secret[IntegrationCredentials.SecretMarker.Length..]).Should().BeFalse("the client secret is masked");
            Holds(part, seed).Should().BeFalse("the TOTP seed is masked");
            Holds(part, "anna.salem@").Should().BeFalse("the address is masked");
        }

        thrown.Message.Should().StartWith("System.Net.Http.HttpRequestException: ", "the message still names the original type")
            .And.Contain(IntegrationCredentials.SecretMarker + SecretText.Redacted);
        thrown.InnerException.Should().BeOfType<ProtectedException>().Which.Message.Should().StartWith("System.InvalidOperationException: ")
            .And.Contain("a***@example.com");
        thrown.ToString().Should().Contain(nameof(Thrown), "the stack stays");
    }

    [Fact]
    public async Task RunAsync_Should_ProtectTheCutsCause_When_ARunTimeoutWrapsAFailureHoldingACredential()
    {
        var secret = IntegrationCredentials.NewSecret();

        var run = () => JobRun.RunAsync("Probe", TimeSpan.FromMilliseconds(200), TimeProvider.System, async ct =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
            }
            catch (OperationCanceledException e)
            {
                throw new HttpRequestException($"answered 401 to {secret}", e);
            }
        }, Ct);

        var thrown = (await run.Should().ThrowAsync<ProtectedException>()).Which;
        thrown.Message.Should().StartWith(typeof(RunTimeoutException).FullName + ": ", "the cut still says it was the run timeout");
        Parts(thrown).Any(part => Holds(part, secret[IntegrationCredentials.SecretMarker.Length..])).Should().BeFalse("the cause's secret is masked");
    }

    [Fact]
    public async Task RunAsync_Should_ThrowTheFailureAsItIs_When_ItsTextHoldsNothingToProtect()
    {
        var failure = Thrown(() => new TimeoutException("the database did not answer in 30 s"));

        var run = () => JobRun.RunAsync("Probe", Timeout, TimeProvider.System, _ => Task.FromException(failure), Ct);

        (await run.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(failure, "nothing to protect, so TickerQ sees the original");
    }

    [Fact]
    public async Task RunAsync_Should_StayACancellation_When_TheHostStops()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var run = () => JobRun.RunAsync("Probe", Timeout, TimeProvider.System, ct => Task.Delay(Timeout, ct), stopping.Token);

        await run.Should().ThrowAsync<OperationCanceledException>("TickerQ records a stop as a cancellation, not a failure");
    }

    [Fact]
    public async Task RunAsync_Should_ReturnTheResult_When_TheWorkSucceeds()
    {
        (await JobRun.RunAsync("Probe", Timeout, TimeProvider.System, _ => Task.FromResult(42), Ct)).Should().Be(42);
    }

    [Fact]
    public void Protected_Should_LeaveTheTextOut_When_TheExceptionsTextCannotBeRead()
    {
        var replacement = JobRun.Protected(new UnreadableException());

        replacement.Should().BeOfType<ProtectedException>().Which.Message.Should().Be(ProtectedExceptionSink.Unprotectable);
        replacement.ToString().Should().Be(ProtectedExceptionSink.Unprotectable);
    }

    [Fact]
    public void Protected_Should_ReplaceTheChain_When_ItIsDeeperThanTheSinkFollows()
    {
        var secret = IntegrationCredentials.NewSecret();
        // Each exception's own text leaves its inner one out, so only the innermost, past the sink's depth, holds the secret,
        // which TickerQ would still reach by walking to the root.
        Exception chain = new OwnTextException("refused with " + secret);
        for (var i = 0; i < ProtectedExceptionSink.MaxDepth + 4; i++)
            chain = new OwnTextException("wrapped", chain);

        var replacement = JobRun.Protected(chain);

        replacement.Should().BeOfType<ProtectedException>();
        Parts(replacement).Any(part => Holds(part, secret[IntegrationCredentials.SecretMarker.Length..])).Should().BeFalse("no part of the chain keeps the secret");
        JobRun.Protected(new OwnTextException("wrapped", new OwnTextException("no secret here"))).Should().BeNull("a short chain with nothing to protect is thrown as it is");
    }

    /// <summary>
    /// Every TickerQ function of Ariva.Api.Cronz calls <see cref="JobRun.RunAsync{T}"/> (or its form without a result) and never
    /// <see cref="RunBound"/> itself, read from the compiled code of the function's state machine, so a new job cannot skip the
    /// protection by calling the run bound directly or by awaiting work of its own outside it (ARV-285 review round 1).
    /// </summary>
    [Fact]
    public void Functions_Should_EachRunTheirWorkThroughJobRun_When_Scheduled()
    {
        var functions = typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<TickerFunctionAttribute>() is not null)
            .ToList();
        functions.Select(f => f.DeclaringType!.Name).Should().BeEquivalentTo(["AvailabilityJobs", "ReportJobs", "RetentionJobs", "SecretJobs", "StaffingJobs", "TimescaleJobs"]);

        foreach (var function in functions)
        {
            var machine = function.GetCustomAttribute<AsyncStateMachineAttribute>();
            machine.Should().NotBeNull($"{function.DeclaringType!.Name}.{function.Name} is an async function");
            var moveNext = machine!.StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
            var instructions = IlReader.Read(moveNext);
            instructions.Should().NotContain(i => i.Unresolved, "every call is read");
            var callees = instructions.Select(i => i.Callee).Where(c => c is not null).ToList();

            callees.Should().Contain(c => c.DeclaringType == typeof(JobRun) && c.Name == nameof(JobRun.RunAsync), $"{function.DeclaringType!.Name} runs its work through JobRun");
            callees.Should().NotContain(c => c.DeclaringType == typeof(RunBound), $"{function.DeclaringType!.Name} never calls the run bound itself");
            // Calls only: the work handed to JobRun is a delegate (ldftn), not a call of the function's own.
            instructions.Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt).Select(i => i.Callee).OfType<MethodInfo>()
                .Where(c => IsAwaitable(c.ReturnType)).Should()
                .OnlyContain(c => c.DeclaringType == typeof(JobRun), $"{function.DeclaringType!.Name} does all its awaited work inside JobRun");
        }
    }

    /// <summary>A task or value task: work a function would await.</summary>
    private static bool IsAwaitable(Type type) =>
        typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>));

    /// <summary>An exception whose text is its own message only, as a library's might be.</summary>
    private sealed class OwnTextException : Exception
    {
        public OwnTextException()
        {
        }

        public OwnTextException(string message)
            : base(message)
        {
        }

        public OwnTextException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public override string ToString() => Message;
    }

    private sealed class UnreadableException : Exception
    {
        public UnreadableException()
        {
        }

        public UnreadableException(string message)
            : base(message)
        {
        }

        public UnreadableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public override string Message => throw new InvalidOperationException("no text");

        public override string ToString() => throw new InvalidOperationException("no text");
    }
}
