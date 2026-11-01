using Serilog.Core;
using Serilog.Events;

namespace Ariva.Api.Common.Logging;

/// <summary>
/// Protects an event's exception before the sinks write it (ARV-212, CWE-532). <see cref="RedactionEnricher"/> protects every
/// property, but Serilog hands the sinks the exception object, whose message, stack and inner exceptions an enricher cannot
/// change. This sink sits in front of every host sink (<see cref="ArivaLogging.Configure"/>). When
/// <see cref="RedactionEnricher.ProtectText"/> would change any part a sink writes, of the exception or of any exception
/// inside it (its text, message or stack), the event goes on with a <see cref="ProtectedException"/> chain in its place;
/// otherwise the event goes on as it was, the original exception included. The sinks write different parts: the console,
/// the file and OTLP's <c>exception.stacktrace</c> its <c>ToString</c>, Loki's formatter its type, message, stack and inner
/// exceptions; each part of a <see cref="ProtectedException"/> is the protected original's. It fails closed: an exception
/// whose text cannot be read or protected is replaced by one that says so, and the failure is counted
/// (<c>ariva.security.log_protection_failures</c>).
/// </summary>
/// <param name="inner">The sinks behind it.</param>
/// <param name="metrics">Counts the exceptions whose protection failed; none in tests.</param>
public sealed class ProtectedExceptionSink(ILogEventSink inner, SecurityEventMetrics metrics = null) : ILogEventSink, IDisposable, IAsyncDisposable
{
    /// <summary>The message and text of an exception whose own text could not be protected.</summary>
    public const string Unprotectable = "The exception's text could not be protected and was left out.";

    /// <summary>How deep an inner exception chain is followed; anything deeper is left out (its text is in the outer one's).</summary>
    public const int MaxDepth = 16;

    private readonly ILogEventSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var exception = logEvent.Exception;
        if (exception is null)
        {
            _inner.Emit(logEvent);
            return;
        }

        Exception replacement;
        try
        {
            replacement = Protect(exception);
        }
#pragma warning disable CA1031 // fail closed: the exception's text is left out, never written as it is, and the event goes on
        catch (Exception)
#pragma warning restore CA1031
        {
            replacement = new ProtectedException(Unprotectable, Unprotectable);
            metrics?.ProtectionFailed();
        }

        _inner.Emit(replacement is null ? logEvent : With(logEvent, replacement));
    }

    /// <summary>The exception as a protected chain, or null when protection changes no part of it or of any exception inside it.</summary>
    public static Exception Protect(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Changes(exception, 0) ? ProtectedException.Of(exception, 0) : null;
    }

    /// <summary>Whether protection changes the exception's text, message or stack, or those of an exception inside it.</summary>
    private static bool Changes(Exception exception, int depth)
    {
        if (Differs(exception.ToString()) || Differs(exception.Message) || Differs(exception.StackTrace))
            return true;
        if (depth >= MaxDepth)
            return false;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(inner => Changes(inner, depth + 1));
        return exception.InnerException is { } single && Changes(single, depth + 1);
    }

    private static bool Differs(string text) => text is not null && !ReferenceEquals(RedactionEnricher.ProtectText(text), text);

    private static LogEvent With(LogEvent logEvent, Exception exception) =>
        new(logEvent.Timestamp, logEvent.Level, exception, logEvent.MessageTemplate,
            logEvent.Properties.Select(p => new LogEventProperty(p.Key, p.Value)), logEvent.TraceId ?? default, logEvent.SpanId ?? default);

    public void Dispose() => (_inner as IDisposable)?.Dispose();

    public ValueTask DisposeAsync()
    {
        if (_inner is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();
        (_inner as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An exception <see cref="ProtectedExceptionSink"/> wrote in place of another, every part protected
/// (<see cref="RedactionEnricher.ProtectText"/>): its message is the original type's name and the original's message, as
/// <c>ToString</c> starts; its stack is the original's; its inner exception is the original's first inner exception, itself
/// protected the same way (an <see cref="AggregateException"/>'s other inner exceptions are in the text and the message);
/// its <c>ToString</c> is the original's whole text, inner exceptions and stacks included.
/// </summary>
public sealed class ProtectedException : Exception
{
    private readonly string _text;
    private readonly string _stack;

    public ProtectedException()
        : this(ProtectedExceptionSink.Unprotectable, ProtectedExceptionSink.Unprotectable)
    {
    }

    public ProtectedException(string message)
        : this(message, message)
    {
    }

    public ProtectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        _text = message;
    }

    public ProtectedException(string message, string text)
        : base(message)
    {
        _text = text ?? message;
    }

    private ProtectedException(string message, string text, string stack, Exception innerException)
        : base(message, innerException)
    {
        _text = text;
        _stack = stack;
    }

    /// <summary>The original's stack, protected; none for an exception made from text alone.</summary>
    public override string StackTrace => _stack;

    public override string ToString() => _text;

    /// <summary>The protected copy of <paramref name="original"/> and of the exceptions inside it, down to <see cref="ProtectedExceptionSink.MaxDepth"/>.</summary>
    internal static ProtectedException Of(Exception original, int depth)
    {
        var inner = original is AggregateException aggregate ? aggregate.InnerExceptions.FirstOrDefault() : original.InnerException;
        return new ProtectedException(
            original.GetType().FullName + ": " + RedactionEnricher.ProtectText(original.Message),
            RedactionEnricher.ProtectText(original.ToString()),
            original.StackTrace is null ? null : RedactionEnricher.ProtectText(original.StackTrace),
            inner is not null && depth < ProtectedExceptionSink.MaxDepth ? Of(inner, depth + 1) : null);
    }
}
