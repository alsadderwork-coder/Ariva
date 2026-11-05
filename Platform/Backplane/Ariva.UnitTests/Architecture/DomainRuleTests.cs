using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.Common;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.NHibernate.Interceptors;
using Ariva.Infra.Services.Sensing;
using Ariva.Infra.Services.Topology;
using FluentAssertions;
using Npgsql;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-282 (CWE-209, CWE-20): a domain rule refusal is told from any other exception by its type (<see cref="IDomainRule"/>),
/// never by the stack frame that threw it or by its text. The unit tests run in Debug, where nothing is inlined; in a Release
/// build the JIT can inline a small entity method into the service's async state machine, so a check of the first frame
/// (Exception.TargetSite, Exception.Source) would answer a rule as a 500 there and pass here. These checks read the compiled
/// IL of every Ariva assembly the five hosts load, so the same call spelled another way (a filter across lines, a property
/// pattern, a helper, an interface, a qualified or target-typed exception) is found as well: no host reads a frame, filters
/// on an exception's text or a rule's text, or answers an exception's text from a catch that can hold a rule; every refusal
/// the topology, zone profile, device, desk code mapping and floor plan services reach in Ariva.Core is a rule type or a
/// listed programming guard; only the three sealed rule types implement IDomainRule; and only the domain model
/// constructs them, never from an exception's text. Each check is shown to fire on fixtures written the ways it must not
/// be passed, and no token or type is skipped.
/// Values are followed through the stack, locals, arguments and fields of a method and into the helpers it hands them to,
/// so the check knows which call formats or compares the exception or its text and which is merely given it (a logger).
/// Limits, each failing closed or outside these checks: a catch body that awaits is moved by the compiler out of its handler
/// (outside the services and controllers, its text reads are not attributed to the catch); an exception read outside any
/// catch (a task's, a resilience outcome's) is checked only in the services and controllers; values are not followed into
/// the framework or through collections; framework throws (Enum.Parse, Single, checked arithmetic) and type initializers
/// are not walked by the rule walk.
/// </summary>
public sealed class DomainRuleTests
{
    #region Fields

    /// <summary>The five hosts; the assemblies checked are the Ariva assemblies they reference, directly or not.</summary>
    private static readonly string[] Hosts = ["Ariva.Api.Main", "Ariva.Api.Ingest", "Ariva.Api.Stream", "Ariva.Api.Cronz", "Ariva.Api.Integration"];

    /// <summary>
    /// Types that read an exception's stack trace for a reason other than telling a rule apart, each with that reason. A type
    /// named here may read Exception.StackTrace, TargetSite or Source, or build a StackTrace, and nothing else is let through.
    /// </summary>
    private static readonly Dictionary<string, string> FrameReaders = new(StringComparer.Ordinal)
    {
        ["Ariva.Api.Common.Logging.ProtectedException"] = "ARV-212: carries an exception's stack trace, protected, on its way to the logs; answers nothing",
        ["Ariva.Api.Common.Logging.ProtectedExceptionSink"] = "ARV-212: tells whether an exception's stack trace needs protecting before it is logged; answers nothing"
    };

    /// <summary>
    /// Catches that can hold a rule (of Exception, SystemException, ArgumentException, ArgumentOutOfRangeException,
    /// InvalidOperationException, or filtered) and read the exception's text, each with its reason: one that only logs it, or
    /// one that still answers it and the story that moves it onto the rule types.
    /// </summary>
    private static readonly Dictionary<string, string> TextReaders = new(StringComparer.Ordinal)
    {
        ["Ariva.Api.Cronz.Jobs.JobRun"] = "ARV-285: reads a job's failure text only to protect it (ProtectedExceptionSink.Protect, RedactionEnricher.ProtectText) and throws it on protected to TickerQ, whose dashboard shows that protected text (the original before ARV-285); tells no rule apart by it and answers no API caller",
        ["Ariva.Di.TopicProvisioning"] = "ARV-177: the topic provisioning command logs why it refused (through the log redaction) and answers no caller",
        ["Ariva.Infra.Border.AmanPoller"] = "records an OutboundUnavailableException's text, Ariva's own (an open circuit, a token already asked for), on the endpoint's status",
        ["Ariva.Infra.Flights.Acris.AcrisPoller"] = "records an OutboundUnavailableException's text, Ariva's own (an open circuit, a token already asked for), on the endpoint's status",
        ["Ariva.Infra.Streaming.PartitionedConsumer`1"] = "a dead letter records the exception for the operators (protected by ARV-285); answers no caller",
    };

    /// <summary>
    /// Strings an exception carries that are codes, not text: read to tell one database refusal from another (a unique
    /// index's SQLSTATE and its name), they hold no value of the caller's or the server's.
    /// </summary>
    private static readonly Dictionary<string, string> ErrorCodes = new(StringComparer.Ordinal)
    {
        ["DbException.get_SqlState"] = "the five-character SQLSTATE (PostgresException overrides DbException's), matched to tell a unique index, an outage or a serialization failure apart",
        ["PostgresException.get_ConstraintName"] = "the name of Ariva's own index or constraint, matched to answer that index's own error"
    };

    /// <summary>
    /// Plain refusals the topology, zone profile, device, desk code mapping and floor plan services (and the audit interceptor their flush runs) can reach in
    /// Ariva.Core that stay plain on purpose: each guards a value the server gives (a constant, its clock, the next number),
    /// never the caller's, so a breach is a programming error answered as a 500 without its text.
    /// </summary>
    private static readonly Dictionary<string, string> ProgrammingGuards = new(StringComparer.Ordinal)
    {
        ["AuditEntry..ctor: ArgumentException.ThrowIfNullOrWhiteSpace"] = "the action and target type are the service's own constants",
        ["AuditEntry..ctor: new ArgumentException"] = "the time is the server's clock (TimeProvider), always UTC",
        ["BaseAuditableEntity`1.EnsureUtc: new ArgumentException"] = "audit and soft delete times are the server's clock, always UTC",
        ["FloorPlan..ctor: ArgumentException.ThrowIfNullOrWhiteSpace"] = "ARV-286: the storage key is generated by the service and the content type is the one the file's bytes give (FloorPlanFiles.Inspect), never the caller's",
        ["ZoneProfile.Publish: ArgumentOutOfRangeException.ThrowIfLessThan"] = "the version is the site's next number, given by the service"
    };

    private static readonly Type[] RuleTypes = [typeof(DomainRuleException), typeof(DomainArgumentException), typeof(DomainArgumentOutOfRangeException)];

    private static readonly Lazy<Scope> Hosted = new(() => Scope.Of(HostClosure()));

    #endregion

    #region Tests

    [Fact]
    public void Hosts_Should_ReadNoStackFrameAndMatchOrAnswerNoExceptionText_When_TheyTellARuleFromAnotherException()
    {
        var scope = Hosted.Value;
        scope.Assemblies.Select(a => a.GetName().Name).Should().Contain(["Ariva.Core", "Ariva.Infra", "Ariva.Di", "Ariva.Api.Common", "Ariva.ServiceDefaults"]);
        scope.TypeLoadFailures.Should().Be(0, "a type that does not load would be skipped by every check");
        var methods = scope.Types.SelectMany(IlReader.MethodsOf).ToList();
        methods.SelectMany(m => IlReader.Read(m).Where(i => i.Unresolved).Select(_ => Owner(m).Text)).Should()
            .BeEmpty("a token that does not resolve would hide its call from every check");

        var findings = TextFindings(methods, scope).ToList();
        scope.DispatchSkipped.Should().Be(0, "a type whose interfaces cannot be read would hide its implementations from every check");
        var called = methods.SelectMany(IlReader.Read).Select(i => i.Callee).Where(c => c is not null).Select(c => $"{c.DeclaringType?.Name}.{c.Name}").ToHashSet();
        ErrorCodes.Keys.Should().BeSubsetOf(called, "a code no longer read is taken off");
        findings.Where(f => !(f.Kind == "frame" && FrameReaders.ContainsKey(f.Type)) && !(f.Kind == "catch text" && TextReaders.ContainsKey(f.Type)))
            .Select(f => f.Text).Distinct().Should()
            .BeEmpty("a rule is told apart by its type, which inlining cannot change, and no exception's text is matched or answered");
        FrameReaders.Keys.Should().OnlyContain(type => findings.Any(f => f.Kind == "frame" && f.Type == type), "an entry no longer needed is taken off");
        TextReaders.Keys.Should().OnlyContain(type => findings.Any(f => f.Kind == "catch text" && f.Type == type), "an entry no longer needed is taken off");

        // The check fires on each way it must not be passed, and not on the type and kind checks.
        var fixtures = Scope.Of([typeof(DomainRuleTests).Assembly]);
        TextFindings(IlReader.MethodsOf(typeof(Bypasses)), fixtures).Select(f => f.Owner).ToHashSet().Should().BeEquivalentTo([
            "Bypasses.MultiLineFilter", "Bypasses.PropertyPatternFilter", "Bypasses.HelperInFilter", "Bypasses.ToStringInFilter", "Bypasses.AsyncFilter",
            "Bypasses.RuleTextFilter", "Bypasses.RuleTextPatternFilter", "Bypasses.InterfaceFilter",
            "Bypasses.StackTraceText", "Bypasses.SourcePattern", "Bypasses.SourceEquals", "Bypasses.SourceReversed", "Bypasses.TargetSite",
            "Bypasses.Frames", "Bypasses.Frame",
            "Bypasses.MessageInCatch", "Bypasses.FilteredCatchAnswer", "Bypasses.BroadCatchAnswer", "Bypasses.InterpolatedAnswer", "Bypasses.ToStringAnswer",
            "Bypasses.ConcatAnswer", "Bypasses.HelperAnswer", "Bypasses.PlusAnswer", "Bypasses.ConcatMiddleAnswer", "Bypasses.FormatFirstAnswer",
            "Bypasses.DetailAnswer", "Bypasses.MessageTextFilter", "Bypasses.RuleCompareAnswer", "Bypasses.RuleContainsAnswer", "Bypasses.RuleSwitchAnswer",
            "Bypasses.CapturedFilter", "Bypasses.ObjectHelperFilter", "Bypasses.SerializedAnswer",
            // ARV-287: a rule's text taken by any call in answer code but an answer, a format or a log; values through arrays,
            // params spans, FormattableString and a lambda's closure; a finally block; a struct receiver by address; a
            // constructor's arguments; a Kafka error's text; and code that answers outside the services and controllers (a
            // hub, an exception handler, a method whose result is an answer).
            "Bypasses.RuleComparerAnswer", "Bypasses.RuleReferenceAnswer", "Bypasses.RuleIndexerAnswer", "Bypasses.RuleLookupAnswer",
            "Bypasses.RuleIndexOfAnswer", "Bypasses.FormatFourAnswer", "Bypasses.JoinAnswer", "Bypasses.FormattableAnswer", "Bypasses.CapturedRuleAnswer",
            "Bypasses.FinallyAnswer", "Bypasses.AddressAnswer", "Bypasses.ConstructorAnswer", "Bypasses.KafkaErrorAnswer", "AnswerHub.Echo",
            "AnswerHandler.TryHandleAsync", "Bypasses.ErrorTupleAnswer", "Bypasses.ResultAnswer"
        ]);
    }

    [Fact]
    public void Services_Should_ReachOnlyTheDomainsRuleTypes_When_AnEntityOrADomainHelperRefuses()
    {
        var core = typeof(IDomainRule).Assembly;
        var infra = typeof(SvcTopology).Assembly;
        var findings = PlainRefusals([typeof(SvcTopology), typeof(SvcZoneProfiles), typeof(SvcDevices), typeof(SvcDeskCodeMappings), typeof(SvcFloorPlans), typeof(AuditInterceptor)], Scope.Of([core, infra]), core).ToList();

        findings.Where(f => !ProgrammingGuards.ContainsKey(f)).Should()
            .BeEmpty("a refusal the services reach in the domain is a rule type, or a programming guard listed with its reason (a null argument is one)");
        ProgrammingGuards.Keys.Should().OnlyContain(guard => findings.Contains(guard), "a guard no longer reached is taken off");

        // The walk finds a plain refusal however it is written and reached: qualified, through a helper, a throw helper, a
        // lambda, an iterator, an async method, an override or an interface implementation.
        var self = typeof(DomainRuleTests).Assembly;
        PlainRefusals([typeof(RuleFixtures.Service)], Scope.Of([self]), self).Should().BeEquivalentTo([
            "Entity.Plain: new InvalidOperationException", "Entity.Qualified: new ArgumentException", "Entity.Refuse: new ArgumentOutOfRangeException",
            "Entity.Guard: ArgumentException.ThrowIfNullOrWhiteSpace", "Entity.InLambda: new InvalidOperationException", "Entity.Iterator: new FormatException",
            "Entity.Disposed: ObjectDisposedException.ThrowIf", "Entity.NullWithRule: new ArgumentNullException", "Entity.LaterAsync: new InvalidDataException",
            "Square.Check: new InvalidOperationException", "InterfaceRule.Refuse: new NotSupportedException"
        ]);
    }

    [Fact]
    public void RuleTypes_Should_BeTheOnlySealedImplementations_When_AnyHostAssemblyIsLoaded()
    {
        var implementers = Hosted.Value.Types.Where(t => typeof(IDomainRule).IsAssignableFrom(t) && t != typeof(IDomainRule)).ToList();

        implementers.Should().BeEquivalentTo(RuleTypes, "only the domain's rule types are answered; another type's text would be");
        RuleTypes.Should().OnlyContain(t => t.IsSealed, "no other code can derive a rule type and have its text answered");
    }

    [Fact]
    public void RuleTypes_Should_BeConstructedByTheDomainModelOnly_When_AnyHostAssemblyIsLoaded()
    {
        var builders = Hosted.Value.Types.SelectMany(IlReader.MethodsOf)
            .Select(m => (Method: m, Instructions: IlReader.Read(m)))
            .Where(x => x.Instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Callee is ConstructorInfo c && RuleTypes.Contains(c.DeclaringType)))
            .ToList();

        builders.Should().NotBeEmpty();
        builders.Select(x => Owner(x.Method)).Where(owner => owner.Assembly != typeof(IDomainRule).Assembly ||
                                                             !owner.Namespace.StartsWith("Ariva.Core.Domain", StringComparison.Ordinal))
            .Select(owner => owner.Text).Distinct().Should()
            .BeEmpty("a rule's text is written by the domain model, so no service or host passes a framework or driver message off as a rule");
        builders.Where(x => x.Instructions.Any(i => i.Callee is { } callee && ExceptionText(callee)))
            .Select(x => Owner(x.Method).Text).Distinct().Should()
            .BeEmpty("a rule is never built from an exception's text, in the domain model either");
    }

    #endregion

    #region Methods

    /// <summary>The Ariva assemblies the five hosts reference, directly or through each other.</summary>
    private static Assembly[] HostClosure()
    {
        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var queue = new Queue<AssemblyName>(Hosts.Select(h => new AssemblyName(h)));
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (name.Name is null || !name.Name.StartsWith("Ariva.", StringComparison.Ordinal) || found.ContainsKey(name.Name))
                continue;
            var assembly = Assembly.Load(name);
            found[name.Name] = assembly;
            foreach (var reference in assembly.GetReferencedAssemblies())
                queue.Enqueue(reference);
        }

        return [.. found.Values];
    }

    /// <summary>
    /// Stack frame reads anywhere (Exception.TargetSite, Source, StackTrace, a StackTrace or StackFrame built,
    /// Environment.StackTrace); in an exception filter, an exception's text or a rule's text read, formatted or compared; in a
    /// catch that can hold a rule, and anywhere in the code that answers callers (services and controllers), an exception's
    /// text read or the exception formatted, and a rule's text compared (a rule's own text is what such code answers, so
    /// reading it is not counted there); and a method whose values cannot be followed.
    /// </summary>
    private static IEnumerable<Finding> TextFindings(IEnumerable<MethodBase> methods, Scope scope)
    {
        foreach (var method in methods)
        {
            var instructions = IlReader.Read(method);
            var regions = IlReader.Regions(method, instructions);
            var owner = Owner(method);
            for (var k = 0; k < instructions.Count; k++)
                if (instructions[k].Callee is { } callee && ReadsFrame(callee))
                    yield return new Finding("frame", owner.TypeName, owner.Text, $"{owner.Text}: {callee.DeclaringType?.Name}.{callee.Name}");
            var answers = AnswersCallers(method);
            if (!regions.Any(r => r.Catches) && !answers)
                continue;
            var flow = Flow(method, instructions, regions, null);
            if (flow is null)
            {
                yield return new Finding("unfollowed", owner.TypeName, owner.Text, $"{owner.Text}: uses calli or jmp, whose values cannot be followed");
                continue;
            }

            // ARV-287: a rule's or an exception's text compared by reference (ReferenceEquals, which the compiler writes as a
            // ceq or beq) where text must not be matched.
            foreach (var (k, flags) in flow.Comparisons)
            {
                if ((flags & (IlFlow.RuleText | IlFlow.ExceptionText)) == 0)
                    continue;
                var offset = instructions[k].Offset;
                if (regions.Any(r => r.Kind == IlReader.RegionKind.Filter && r.Holds(offset)) ||
                    regions.Any(r => r.Kind == IlReader.RegionKind.Catch && CanHoldARule(r.CatchType) && r.Holds(offset)) ||
                    (answers && !regions.Any(r => r.Catches && r.Holds(offset))))
                    yield return new Finding("catch text", owner.TypeName, owner.Text, $"{owner.Text}: a text compared by reference ({instructions[k].OpCode.Name})");
            }

            for (var k = 0; k < instructions.Count; k++)
            {
                if (instructions[k].Callee is not { } callee)
                    continue;
                foreach (var region in regions.Where(r => r.Holds(instructions[k].Offset)))
                {
                    if (region.Kind == IlReader.RegionKind.Filter && Offends(method, instructions, flow, k, Mode.Filter, scope, []))
                        yield return new Finding("filter text", owner.TypeName, owner.Text, $"{owner.Text}: {callee.Name} in an exception filter");
                    if (region.Kind == IlReader.RegionKind.Catch && CanHoldARule(region.CatchType) && Offends(method, instructions, flow, k, Mode.Answer, scope, []))
                        yield return new Finding("catch text", owner.TypeName, owner.Text, $"{owner.Text}: {callee.Name} in a catch of {region.CatchType.Name}");
                }

                if (answers && !regions.Any(r => r.Catches && r.Holds(instructions[k].Offset)) && Offends(method, instructions, flow, k, Mode.Answer, scope, []))
                    yield return new Finding("catch text", owner.TypeName, owner.Text, $"{owner.Text}: {callee.Name} in code that answers callers");
            }
        }
    }

    /// <summary>
    /// Code that answers a caller: a service (Svc*) or a controller; since ARV-287 also a SignalR hub, an exception handler
    /// (IExceptionHandler), and a method whose result is an answer: a Fluentx result, an HTTP result (IResult, an action
    /// result), or a tuple with an Error member (the ingest and integration body readers), through a task too; a lambda, local
    /// function or state machine as the method holding it.
    /// </summary>
    private static bool AnswersCallers(MethodBase method)
    {
        var type = method.DeclaringType is { } declared ? Outermost(declared) : null;
        if (type is null)
            return false;
        if ((type.Name.StartsWith("Svc", StringComparison.Ordinal) && type.Namespace?.StartsWith("Ariva.Infra", StringComparison.Ordinal) == true) ||
            type.Namespace?.Contains(".Controllers", StringComparison.Ordinal) == true)
            return true;
        for (var holder = method.DeclaringType; holder is not null; holder = holder.DeclaringType)
        {
            if (Derives(holder, "Microsoft.AspNetCore.SignalR.Hub") || holder.GetInterfaces().Any(i => i.FullName == "Microsoft.AspNetCore.Diagnostics.IExceptionHandler"))
                return true;
        }

        return IsAnswer(Kickoff(method));
    }

    private static bool Derives(Type type, string baseName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.FullName == baseName)
                return true;
        return false;
    }

    /// <summary>The method a state machine's MoveNext belongs to (its kickoff method), or the method itself.</summary>
    private static MethodBase Kickoff(MethodBase method)
    {
        if (method.DeclaringType is not { DeclaringType: { } outer } machine || !IsGenerated(machine))
            return method;
        var definition = machine.IsGenericType ? machine.GetGenericTypeDefinition() : machine;
        return outer.GetMethods(IlReader.Declared).FirstOrDefault(m =>
                   m.GetCustomAttribute<System.Runtime.CompilerServices.StateMachineAttribute>() is { StateMachineType: var type } && type == definition)
               ?? method;
    }

    /// <summary>Whether a method's result is an answer to a caller: a Fluentx result, an HTTP result, or a tuple with an Error member.</summary>
    private static bool IsAnswer(MethodBase method)
    {
        if (method is not MethodInfo info)
            return false;
        var result = info.ReturnType;
        if (result.IsGenericType && result.GetGenericTypeDefinition() is var open && (open == typeof(Task<>) || open == typeof(ValueTask<>)))
            result = result.GetGenericArguments()[0];
        if (result.Namespace == "Fluentx" && result.Name.StartsWith("Result", StringComparison.Ordinal))
            return true;
        if (result.GetInterfaces().Append(result).Any(i => i.FullName is "Microsoft.AspNetCore.Http.IResult" or "Microsoft.AspNetCore.Mvc.IActionResult" or "Microsoft.AspNetCore.Mvc.Infrastructure.IConvertToActionResult"))
            return true;
        return info.ReturnParameter.GetCustomAttribute<System.Runtime.CompilerServices.TupleElementNamesAttribute>() is { } names &&
               names.TransformNames.Any(n => n is "Error" or "Errors");
    }

    private static bool CanHoldARule(Type caught) => caught is not null && RuleTypes.Any(caught.IsAssignableFrom);

    private static bool ReadsFrame(MethodBase callee) =>
        (typeof(Exception).IsAssignableFrom(callee.DeclaringType) && callee.Name is "get_TargetSite" or "get_Source" or "get_StackTrace") ||
        (callee is ConstructorInfo && (callee.DeclaringType == typeof(StackTrace) || callee.DeclaringType == typeof(StackFrame))) ||
        (callee.DeclaringType == typeof(Environment) && callee.Name == "get_StackTrace");

    /// <summary>
    /// An exception's text read through its members: its message, ToString, data, or any string it carries (a driver's
    /// detail, hint or SQL, a Kafka error's reason), apart from the <see cref="ErrorCodes"/>.
    /// </summary>
    private static bool ExceptionText(MethodBase callee) =>
        (typeof(Exception).IsAssignableFrom(callee.DeclaringType) &&
         (callee.Name is "get_Message" or "ToString" or "get_Data" ||
          (callee.Name.StartsWith("get_", StringComparison.Ordinal) && callee is MethodInfo { ReturnType: var returns } && returns == typeof(string) &&
           !ErrorCodes.ContainsKey($"{callee.DeclaringType.Name}.{callee.Name}")))) ||
        (callee.DeclaringType?.FullName == "Confluent.Kafka.Error" && callee.Name is "get_Reason" or "ToString");

    private static bool RuleText(MethodBase callee) => typeof(IDomainRule).IsAssignableFrom(callee.DeclaringType) && callee.Name == "get_Rule";

    private static bool IsExceptionValue(Type type) => type is not null && (typeof(Exception).IsAssignableFrom(type) || typeof(IDomainRule).IsAssignableFrom(type));

    private static bool ForExceptionType(MethodBase callee) => callee is MethodInfo { IsGenericMethod: true } generic && generic.GetGenericArguments().Any(IsExceptionValue);

    /// <summary>A call that formats its arguments or receiver as text.</summary>
    private static bool Formats(MethodBase callee) =>
        callee.Name == "ToString" ||
        (callee.DeclaringType == typeof(string) && callee.Name is "Concat" or "Format" or "Join") ||
        (callee.DeclaringType == typeof(Convert) && callee.Name == "ToString") ||
        (callee.DeclaringType == typeof(StringBuilder) && callee.Name is "Append" or "AppendFormat" or "AppendLine") ||
        (callee.DeclaringType == typeof(FormattableString) && callee.Name is "Invariant" or "CurrentCulture") ||
        callee.Name is "AppendFormatted" or "Serialize" or "SerializeToUtf8Bytes";

    /// <summary>
    /// ARV-287: a call that answers a rule's text (a Fluentx result made from it) or logs it, which code that answers callers may
    /// give the text to, beside a format (<see cref="Formats"/>) and a helper of the scope, whose own body is judged.
    /// </summary>
    private static bool AnswersOrLogs(MethodBase callee) =>
        (callee.DeclaringType?.Namespace == "Fluentx" && callee.DeclaringType.Name.StartsWith("Result", StringComparison.Ordinal)) ||
        callee.DeclaringType?.Namespace?.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) == true ||
        callee.GetParameters().Any(p => p.ParameterType.FullName == "Microsoft.Extensions.Logging.ILogger");

    /// <summary>A type that carries what it is made from (an array, a span, a formattable string, an object, a builder): a call returning it keeps every flag.</summary>
    private static bool Carries(Type type) =>
        type.IsArray || type == typeof(object) || type == typeof(FormattableString) || type == typeof(IFormattable) || type == typeof(StringBuilder) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() is var open && (open == typeof(Span<>) || open == typeof(ReadOnlySpan<>) ||
                                                                               open == typeof(Memory<>) || open == typeof(ReadOnlyMemory<>)));

    /// <summary>A call that compares or looks up text.</summary>
    private static bool Compares(MethodBase callee) =>
        (callee.DeclaringType == typeof(string) &&
         callee.Name is "Equals" or "op_Equality" or "op_Inequality" or "Contains" or "StartsWith" or "EndsWith" or "IndexOf" or "IndexOfAny" or "LastIndexOf" or
             "Compare" or "CompareOrdinal" or "CompareTo") ||
        (callee.DeclaringType == typeof(Regex) && callee.Name is "IsMatch" or "Match" or "Matches" or "Replace" or "Split" or "Count") ||
        (callee.DeclaringType == typeof(object) && callee.Name == "Equals") ||
        callee.Name is "Contains" or "ContainsKey" or "TryGetValue" or "SequenceEqual";

    /// <summary>The flags of a call's result for <see cref="IlFlow"/>: a rule's text, an exception's text, or text made from either.</summary>
    private static int CallResult(MethodBase callee, int[] taken)
    {
        if (RuleText(callee))
            return IlFlow.RuleText;
        if (ExceptionText(callee))
            return IlFlow.ExceptionText;
        var any = taken.Aggregate(0, (a, b) => a | b);
        // ARV-287: what an exception hands out (KafkaException.Error, whose ToString is its text) carries the exception.
        if (!callee.IsStatic && typeof(Exception).IsAssignableFrom(callee.DeclaringType) && callee is MethodInfo { ReturnType: { IsValueType: false } held } &&
            held != typeof(string) && held != typeof(Type) && !typeof(MemberInfo).IsAssignableFrom(held))
            return any | IlFlow.Exception;
        var made = callee is ConstructorInfo ? callee.DeclaringType : (callee as MethodInfo)?.ReturnType;
        if (made is not null && made.IsByRef)
            made = made.GetElementType();
        // ARV-287: an array, a span, a formattable string or a builder made from the values keeps their flags.
        if (made is not null && Carries(made))
            return any;
        if (callee is not MethodInfo { ReturnType: var returns } || returns != typeof(string))
            return 0;
        var text = any & (IlFlow.RuleText | IlFlow.ExceptionText);
        return Formats(callee) && ((any & IlFlow.Exception) != 0 || ForExceptionType(callee)) ? text | IlFlow.ExceptionText : text;
    }

    private static IlFlow.Result Flow(MethodBase method, IReadOnlyList<IlReader.Instruction> instructions, IReadOnlyList<IlReader.Region> regions, int[] seed,
        IReadOnlyDictionary<(Module, int), int> fields = null) =>
        IlFlow.Follow(method, instructions, regions, t => IsExceptionValue(t) ? IlFlow.Exception : 0, CallResult, seed, fields);

    /// <summary>
    /// Whether the call at <paramref name="k"/> reads an exception's text, formats an exception, or compares a rule's or an
    /// exception's text (in a filter, reading a rule's text counts too); or hands the exception, a rule or their text to a
    /// helper of the scope (a parameter of an exception, rule or object type, a lambda or local function, a method pointer
    /// taken) that does, called directly or through an interface or an override.
    /// </summary>
    private static bool Offends(MethodBase method, IReadOnlyList<IlReader.Instruction> instructions, IlFlow.Result flow, int k, Mode mode, Scope scope,
        HashSet<(Module, int)> seen)
    {
        var instruction = instructions[k];
        if (instruction.Callee is not { } callee)
            return false;
        var taken = flow?.Arguments.GetValueOrDefault(k) ?? [];
        var any = taken.Aggregate(0, (a, b) => a | b);
        if (ExceptionText(callee))
            return true;
        if (RuleText(callee))
            return mode == Mode.Filter;
        if (Formats(callee) && ((any & IlFlow.Exception) != 0 || ForExceptionType(callee)))
            return true;
        if (Compares(callee) && (any & (IlFlow.RuleText | IlFlow.ExceptionText)) != 0)
            return true;
        // ARV-287: in code that answers, a rule's text may only be answered, formatted or logged; any other call that takes it
        // (a comparer, a lookup, an indexer, a reference check) tells rules apart by their text, unless it is a helper of the
        // scope, whose own body is judged below.
        var targets = scope.Targets(callee).ToList();
        if (mode == Mode.Answer && (any & IlFlow.RuleText) != 0 && targets.Count == 0 && !Formats(callee) && !AnswersOrLogs(callee) && !RuleText(callee))
            return true;
        var pointer = instruction.OpCode == OpCodes.Ldftn || instruction.OpCode == OpCodes.Ldvirtftn;
        if (!pointer && any == 0 && !callee.GetParameters().Any(p => IsExceptionValue(p.ParameterType) || p.ParameterType == typeof(object)) &&
            !(callee.DeclaringType is { } owner && (IsGenerated(owner) || IsExceptionValue(owner))) && !callee.Name.StartsWith('<'))
            return false;
        foreach (var target in targets)
        {
            if (!seen.Add((target.Module, target.MetadataToken)))
                continue;
            var body = IlReader.Read(target);
            // ARV-287: a constructor made with newobj takes no receiver from the stack, so its arguments start after its own; the
            // fields start as this method left them (the closure fields a lambda reads).
            var seed = pointer ? null : instruction.OpCode == OpCodes.Newobj ? [0, .. taken] : taken;
            var targetFlow = Flow(target, body, IlReader.Regions(target, body), seed, flow?.Fields);
            if (targetFlow is null || targetFlow.Comparisons.Values.Any(flags => (flags & (IlFlow.RuleText | IlFlow.ExceptionText)) != 0))
                return true;
            for (var j = 0; j < body.Count; j++)
                if (Offends(target, body, targetFlow, j, mode, scope, seen))
                    return true;
        }

        return false;
    }

    /// <summary>
    /// Walks every method the <paramref name="services"/> call within <paramref name="scope"/> (directly, through an
    /// interface or an override, and into the lambdas, local functions and state machines they name, whether a state machine
    /// is a class as in Debug or a struct as in Release) and lists each plain refusal made in <paramref name="rules"/>: an
    /// exception other than the rule types constructed, or a .NET throw helper other than ArgumentNullException.ThrowIfNull.
    /// The read-only lists the compiler writes for collection expressions (whose mutators refuse with NotSupportedException)
    /// and an iterator's Reset are walked through but not listed: no person writes them.
    /// </summary>
    private static IEnumerable<string> PlainRefusals(Type[] services, Scope scope, Assembly rules)
    {
        var seen = new HashSet<(Module, int)>();
        var queue = new Queue<MethodBase>(services.SelectMany(IlReader.MethodsOf));
        var found = new SortedSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            if (!seen.Add((method.Module, method.MetadataToken)))
                continue;
            var owner = Owner(method);
            var listed = method.DeclaringType?.Assembly == rules && !IsCollectionWrapper(method.DeclaringType) && !IsIteratorReset(method);
            foreach (var instruction in IlReader.Read(method))
            {
                if (listed && Refusal(instruction) is { } refusal)
                    found.Add($"{owner.Text}: {refusal}");
                foreach (var generated in GeneratedTypes(instruction).Where(t => scope.Assemblies.Contains(t.Assembly)))
                    foreach (var next in IlReader.MethodsOf(generated.IsGenericType ? generated.GetGenericTypeDefinition() : generated))
                        queue.Enqueue(next);
                if (instruction.Callee is { } callee)
                    foreach (var target in scope.Targets(callee))
                        queue.Enqueue(target);
            }
        }

        return found;
    }

    /// <summary>The compiler-generated types an instruction names: a constructor's, a field's, a type token's or a generic argument.</summary>
    private static IEnumerable<Type> GeneratedTypes(IlReader.Instruction instruction)
    {
        Type[] named = instruction.Member switch
        {
            ConstructorInfo constructor => [constructor.DeclaringType],
            MethodInfo { IsGenericMethod: true } generic => generic.GetGenericArguments(),
            FieldInfo field => [field.DeclaringType],
            Type type => [type],
            _ => []
        };
        return named.Where(t => t is not null && IsGenerated(t));
    }

    /// <summary>
    /// An exception other than a rule type created (newobj; a constructor's call of its base is not one), or a .NET throw
    /// helper other than the null check.
    /// </summary>
    private static string Refusal(IlReader.Instruction instruction)
    {
        if (instruction.Callee is not { DeclaringType: { } type } callee || !typeof(Exception).IsAssignableFrom(type) || RuleTypes.Contains(type))
            return null;
        if (callee is ConstructorInfo)
            return instruction.OpCode == OpCodes.Newobj ? $"new {type.Name}" : null;
        if (callee.IsStatic && callee.Name.StartsWith("ThrowIf", StringComparison.Ordinal) && !(type == typeof(ArgumentNullException) && callee.Name == "ThrowIfNull"))
            return $"{type.Name}.{callee.Name}";
        return null;
    }

    /// <summary>The read-only list types the compiler writes at an assembly's top level for collection expressions.</summary>
    private static bool IsCollectionWrapper(Type type) => type is not null && Outermost(type).Name.StartsWith("<>z__ReadOnly", StringComparison.Ordinal);

    /// <summary>An iterator's Reset, which the compiler writes to refuse with NotSupportedException.</summary>
    private static bool IsIteratorReset(MethodBase method) =>
        method.DeclaringType is { } type && IsGenerated(type) && method.Name.EndsWith("IEnumerator.Reset", StringComparison.Ordinal);

    private static bool IsGenerated(Type type) => type.Name.StartsWith('<') || (type.DeclaringType is { } outer && IsGenerated(outer));

    private static Type Outermost(Type type) => type.DeclaringType is { } outer ? Outermost(outer) : type;

    /// <summary>The method a person wrote: a lambda, local function or state machine is named after the method holding it.</summary>
    private static OwnerName Owner(MethodBase method)
    {
        var type = method.DeclaringType;
        var name = method.Name.StartsWith('<') && method.Name.IndexOf('>') > 1 ? method.Name[1..method.Name.IndexOf('>')] : method.Name;
        while (type is { DeclaringType: not null } && type.Name.StartsWith('<'))
        {
            if (type.Name[1] != '>')
                name = type.Name[1..type.Name.IndexOf('>')];
            type = type.DeclaringType;
        }

        return new OwnerName(type?.FullName ?? "", type?.Namespace ?? "", type?.Assembly, $"{type?.Name}.{name}");
    }

    #endregion

    #region Types

    private sealed record Finding(string Kind, string Type, string Owner, string Text);

    /// <summary>A filter, where any text of an exception or rule is a match; or code that answers, where a rule's own text is the answer.</summary>
    private enum Mode
    {
        Filter,
        Answer
    }

    private sealed record OwnerName(string TypeName, string Namespace, Assembly Assembly, string Text);

    /// <summary>The assemblies a check reads, their types and the dispatch among them; a call is followed only into these.</summary>
    private sealed class Scope
    {
        private IlReader.Dispatch dispatch;

        public Assembly[] Assemblies { get; private init; }

        public IReadOnlyList<Type> Types { get; private init; }

        public int TypeLoadFailures { get; private init; }

        /// <summary>Types whose interfaces could not be read while the dispatch index was built.</summary>
        public int DispatchSkipped => (dispatch ??= new IlReader.Dispatch(Types)).Skipped;

        public static Scope Of(Assembly[] assemblies)
        {
            var failures = 0;
            var types = new List<Type>();
            foreach (var assembly in assemblies)
            {
                types.AddRange(IlReader.TypesOf(assembly, out var failed));
                failures += failed;
            }

            return new Scope { Assemblies = assemblies, Types = types, TypeLoadFailures = failures };
        }

        /// <summary>The bodies a call can run within the scope: its own, and its overrides and implementations here.</summary>
        public IEnumerable<MethodBase> Targets(MethodBase callee)
        {
            if (callee.DeclaringType is not { } declared || !Assemblies.Contains(declared.Assembly))
                yield break;
            yield return IlReader.Definition(callee);
            dispatch ??= new IlReader.Dispatch(Types);
            foreach (var target in dispatch.Of(callee).Where(t => Assemblies.Contains(t.Module.Assembly)))
                yield return IlReader.Definition(target);
        }
    }

    /// <summary>The ways a frame or text check could be written, each of which the IL check must find (and five it must not).</summary>
    private static class Bypasses
    {
        private static readonly IClassifier Classifier = new MessageClassifier();

        public static string MultiLineFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
                when (e.Message.Contains("already exists", StringComparison.Ordinal))
            {
                return "taken";
            }
        }

        public static string PropertyPatternFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is { Message: var m } && m.Length > 0)
            {
                return "rule";
            }
        }

        public static string HelperInFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (LooksLikeARule(e))
            {
                return "rule";
            }
        }

        public static string ToStringInFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (ArgumentException e) when (e.ToString().Contains("Ariva.Core", StringComparison.Ordinal))
            {
                return "rule";
            }
        }

        public static async Task<string> AsyncFilter(Func<Task> act)
        {
            try
            {
                await act();
                return null;
            }
            catch (ArgumentException e) when (e.Message.Length > 0)
            {
                return "rule";
            }
        }

        public static string RuleTextFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r && r.Rule.Contains("already exists", StringComparison.Ordinal))
            {
                return "taken";
            }
        }

        public static string RuleTextPatternFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule { Rule: "A floor number is taken." })
            {
                return "floor";
            }
        }

        public static string InterfaceFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (Classifier.IsRule(e))
            {
                return "rule";
            }
        }

        public static bool StackTraceText(Exception e) => e.StackTrace?.Contains("Ariva.Core", StringComparison.Ordinal) == true;

        public static bool SourcePattern(Exception e) => e.Source is "Ariva.Core";

        public static bool SourceEquals(Exception e) => string.Equals(e.Source, "Ariva.Core", StringComparison.Ordinal);

        public static bool SourceReversed(Exception e) => "Ariva.Core" == e.Source;

        public static bool TargetSite(Exception e) => e.TargetSite?.DeclaringType?.Assembly == typeof(IDomainRule).Assembly;

        public static int Frames(Exception e) => new StackTrace(e).FrameCount;

        public static string Frame() => new StackFrame(1).GetMethod()?.Name;

        public static string MessageInCatch(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
        }

        public static string FilteredCatchAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is not ObjectDisposedException)
            {
                return e.Message;
            }
        }

        public static string BroadCatchAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public static string InterpolatedAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (Exception e)
            {
                return $"Refused: {e}";
            }
        }

        public static string ToStringAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (ArgumentException e)
            {
                return e.ToString();
            }
        }

        public static string ConcatAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (SystemException e)
            {
                return string.Concat("Refused: ", e);
            }
        }

        public static string HelperAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return Describe(e);
            }
        }

        public static string PlusAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (Exception e)
            {
                return "Refused: " + e;
            }
        }

        public static string ConcatMiddleAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (Exception e)
            {
                return string.Concat("Refused: ", e, ".");
            }
        }

        public static string FormatFirstAnswer(Action act, int code)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", e, code);
            }
        }

        public static string SerializedAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (ArgumentException e)
            {
                return System.Text.Json.JsonSerializer.Serialize(new { e.ParamName, Error = e });
            }
        }

        public static string DetailAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (Exception e) when (e.InnerException is PostgresException)
            {
                return ((PostgresException)e.InnerException).Detail;
            }
        }

        public static string MessageTextFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (PostgresException e) when (e.MessageText.Length > 0)
            {
                return "refused";
            }
        }

        public static string RuleCompareAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                return r.Rule == "A published zone profile never changes." ? "conflict" : r.Rule;
            }
        }

        public static string RuleContainsAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                return r.Rule.Contains("already exists", StringComparison.Ordinal) ? "taken" : r.Rule;
            }
        }

        public static string RuleSwitchAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                switch (r.Rule)
                {
                    case "A floor number is taken.":
                        return "floor";
                    case "A code is taken.":
                        return "code";
                    default:
                        return r.Rule;
                }
            }
        }

        public static string CapturedFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (Check(() => e.Message.Length > 0))
            {
                return "rule";
            }
        }

        public static string ObjectHelperFilter(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (Describes(e))
            {
                return "rule";
            }
        }

        public static string FloorKindAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                return r.Kind == DomainRuleKind.FloorTaken ? "floor" : r.Rule;
            }
        }

        public static string LogWithCode(Action act, Action<Exception, string> log, int code)
        {
            try
            {
                act();
                return null;
            }
            catch (Exception e)
            {
                log(e, code.ToString(CultureInfo.InvariantCulture));
                return "refused";
            }
        }

        public static string TypeCheck(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule rule)
            {
                return rule.Rule;
            }
        }

        public static string KindCheck(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule { Kind: DomainRuleKind.CodeTaken })
            {
                return "taken";
            }
        }

        public static string LoggedOnly(Action act, Action<Exception> log)
        {
            try
            {
                act();
                return null;
            }
            catch (FormatException e)
            {
                log(e);
                return "refused";
            }
        }

        private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal) { ["A code is taken."] = "code" };

        private static readonly List<string> Known = ["A code is taken."];

        private static readonly string KnownRule = "A code is taken.";

        public static string RuleComparerAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                return StringComparer.Ordinal.Equals(r.Rule, "A code is taken.") ? "code" : r.Rule;
            }
        }

        public static string RuleReferenceAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                return ReferenceEquals(r.Rule, KnownRule) ? "code" : r.Rule;
            }
        }

        public static string RuleIndexerAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                return Codes[r.Rule];
            }
        }

        public static string RuleLookupAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                return Codes.GetValueOrDefault(r.Rule) ?? r.Rule;
            }
        }

        public static string RuleIndexOfAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                return Known.IndexOf(r.Rule) >= 0 ? "code" : r.Rule;
            }
        }

        public static string FormatFourAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", 1, 2, 3, e);
            }
        }

        public static string JoinAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return string.Join(", ", "refused", e);
            }
        }

        public static string FormattableAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return System.Runtime.CompilerServices.FormattableStringFactory.Create("Refused: {0}", e).ToString(CultureInfo.InvariantCulture);
            }
        }

        public static string CapturedRuleAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                var rule = r.Rule;
                return Known.Exists(known => known == rule) ? "known" : rule;
            }
        }

        public static string FinallyAnswer(Action act)
        {
            var answer = "refused";
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
            {
                try
                {
                    act();
                }
                finally
                {
                    answer = string.Concat("Refused: ", e);
                }
            }

            return answer;
        }

        public static string AddressAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                var pair = KeyValuePair.Create(r.Rule, 1);
                return pair.Key == "A code is taken." ? "code" : "other";
            }
        }

        public static string ConstructorAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return new Wrapper(e).Text;
            }
        }

        public static string KafkaErrorAnswer(Action act)
        {
            try
            {
                act();
                return null;
            }
            catch (Exception e)
            {
                return (e as Confluent.Kafka.KafkaException)?.Error.ToString();
            }
        }

        public static (string Value, string Error) ErrorTupleAnswer(Exception e) => (null, "Refused: " + e);

        public static Fluentx.Result<int> ResultAnswer(Exception e) => Fluentx.Result.Error<int>(e.Message);

        /// <summary>A rule's text answered as a result and logged: neither is a finding.</summary>
        public static Fluentx.Result<int> RuleAnsweredAndLogged(Action act, Microsoft.Extensions.Logging.ILogger logger)
        {
            try
            {
                act();
                return Fluentx.Result.Return(1);
            }
            catch (InvalidOperationException e) when (e is IDomainRule r)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, "Refused: {Rule}", r.Rule);
                return Fluentx.Result.Error<int>($"Refused: {r.Rule}");
            }
        }

        private sealed class Wrapper
        {
            public Wrapper(object value) => Text = "Refused: " + value;

            public string Text { get; }
        }

        private sealed class AnswerHub : Microsoft.AspNetCore.SignalR.Hub
        {
            public static string Echo(Exception e) => "Refused: " + e;
        }

        private sealed class AnswerHandler : Microsoft.AspNetCore.Diagnostics.IExceptionHandler
        {
            public ValueTask<bool> TryHandleAsync(Microsoft.AspNetCore.Http.HttpContext httpContext, Exception exception, CancellationToken cancellationToken) =>
                ValueTask.FromResult(exception.Message.Length > 0);
        }

        private static bool LooksLikeARule(Exception e) => e.Message.StartsWith("A ", StringComparison.Ordinal);

        private static bool Check(Func<bool> test) => test();

        private static bool Describes(object value) => value.ToString()?.Length > 0;

        private static string Describe(Exception e) => "Refused: " + e.Message;
    }

    private interface IClassifier
    {
        bool IsRule(Exception e);
    }

    private sealed class MessageClassifier : IClassifier
    {
        public bool IsRule(Exception e) => e.Message.EndsWith('.');
    }

    #endregion
}

/// <summary>A service and an entity whose refusals are written and reached each way the rule walk must find (and two it must not).</summary>
internal static class RuleFixtures
{
    internal static class Service
    {
        public static async Task Run(string name, Shape shape, IInterfaceRule rule)
        {
            Entity.Plain();
            Entity.Qualified();
            Entity.ViaHelper();
            Entity.Guard(name);
            Entity.InLambda();
            _ = Entity.Iterator().Count();
            Entity.Disposed(true);
            Entity.NullWithRule(name);
            Entity.Rule();
            Entity.Null(name);
            await Entity.LaterAsync();
            shape.Check();
            rule.Refuse();
        }
    }

    internal static class Entity
    {
        public static void Plain() => throw new InvalidOperationException("Plain.");

        public static void Qualified() => throw new global::System.ArgumentException("Qualified.");

        public static void ViaHelper() => throw Refuse("Helper.");

        public static void Guard(string name) => ArgumentException.ThrowIfNullOrWhiteSpace(name);

        public static void InLambda() => new List<int> { 1 }.ForEach(_ => throw new InvalidOperationException("Lambda."));

        public static IEnumerable<int> Iterator()
        {
            yield return 1;
            throw new FormatException("Iterator.");
        }

        public static void Disposed(bool disposed) => ObjectDisposedException.ThrowIf(disposed, typeof(Entity));

        public static void NullWithRule(string name)
        {
            if (name is null)
                throw new ArgumentNullException(nameof(name), "A name is required.");
        }

        public static void Rule() => throw new DomainRuleException("Rule.");

        public static void Null(string name) => ArgumentNullException.ThrowIfNull(name);

        public static async Task LaterAsync()
        {
            await Task.Yield();
            throw new InvalidDataException("Async.");
        }

        private static ArgumentOutOfRangeException Refuse(string rule) => new(nameof(rule), rule);
    }

    internal abstract class Shape
    {
        public abstract void Check();
    }

    internal sealed class Square : Shape
    {
        public override void Check() => throw new InvalidOperationException("Override.");
    }

    internal interface IInterfaceRule
    {
        void Refuse();
    }

    internal sealed class InterfaceRule : IInterfaceRule
    {
        public void Refuse() => throw new NotSupportedException("Interface.");
    }
}
