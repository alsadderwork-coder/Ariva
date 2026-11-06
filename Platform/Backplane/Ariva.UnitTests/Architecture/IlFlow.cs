using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// Follows values through a method's IL (ARV-282): an abstract interpretation over its branches and exception handlers that
/// marks each value on the stack, in a local, an argument or a field with flags (an exception, a rule's text, an exception's
/// text) and records, for every call, the flags of the values it takes. So a check can tell that a call formats or compares
/// the caught exception or its text, whichever argument it is in and through whichever locals it went, and that a call merely
/// given the exception (a logger) does not. A method the interpretation cannot follow (calli, jmp) is reported, not skipped.
/// ARV-287: each value also knows its homes (the locals, arguments, fields and made objects it may be, or whose address it
/// is), so a value stored into an array, through an address (a span's element, a ref local, an out argument) or by a call
/// into what it was given (an array, a span, a builder, a collection, a struct taken by address) adds its flags to every
/// alias of that home; a call into the scope is summarised (what it returns, what it stores into its arguments and fields,
/// through a delegate's target too) when it takes a flagged value; finally, fault and filter blocks are interpreted with the
/// edges that reach and leave them; and the field iteration throws when it has not converged. ARV-287a: each value also knows
/// whether it is a constant on every path (a literal, or a field a caller names constant) and which, recorded for every call,
/// and the delegates a method leaves in its fields reach a caller that follows them on (a state machine's).
/// </summary>
internal static class IlFlow
{
    #region Fields

    /// <summary>The value is an exception, or a rule (<see cref="Ariva.Core.Domain.Common.IDomainRule"/>).</summary>
    public const int Exception = 1;

    /// <summary>The value is a rule's text (IDomainRule.Rule), or text made from it.</summary>
    public const int RuleText = 2;

    /// <summary>The value is an exception's text (its message, a string it carries, the exception formatted), or text made from it.</summary>
    public const int ExceptionText = 4;

    /// <summary>ARV-287: the value is something an exception hands out (a Kafka error, a driver's command, a validation result), whose text is the exception's.</summary>
    public const int ExceptionPart = 8;

    /// <summary>ARV-287: the value holds a rule's text (an array, a collection, a result or an object made from it); what is read out of it is the rule's text.</summary>
    public const int RuleHeld = 16;

    /// <summary>The most passes over a method while its fields gain flags; more is a bug in the interpretation, reported.</summary>
    public const int MaxPasses = 64;

    private const int MadeHomes = -1_000_000;
    private const int FieldHomes = -2_000_000;

    #endregion

    #region Methods

    /// <summary>The flags of a value put into a container: a rule's text becomes a value holding it.</summary>
    public static int Held(int flags) => (flags & ~RuleText) | ((flags & (RuleText | RuleHeld)) != 0 ? RuleHeld : 0);

    /// <summary>The flags of a value read out of a container: what a holder of a rule's text gives back is that text.</summary>
    public static int Read(int flags) => flags | ((flags & RuleHeld) != 0 ? RuleText : 0);

    /// <summary>
    /// The method's flow: for each call or newobj instruction (by index) the flags of the values it takes, the receiver first;
    /// the flags each field gained; the reference comparisons; the delegate targets invoked; and what the method returns and
    /// stores into its arguments. Null when the method uses calli or jmp.
    /// </summary>
    /// <param name="typeFlags">The flags a value has because of its type (an exception type is <see cref="Exception"/>).</param>
    /// <param name="callResult">The flags of a call's result, from the call, the flags of the values it takes, and whether its body was followed (summarised).</param>
    /// <param name="seed">The flags of the method's arguments (the receiver first), when a caller's are known.</param>
    /// <param name="fieldSeed">The fields' flags as a caller left them (the closure fields a lambda reads).</param>
    /// <param name="summarize">What a call into the scope returns and stores, or null when it is not followed.</param>
    /// <param name="maxPasses">The most passes before the iteration is reported as not converging.</param>
    /// <param name="seedRuns">ARV-287a: the methods each argument's delegate may run, when a caller's are known (a lambda it passes).</param>
    /// <param name="constantField">ARV-287a: whether a static field loaded is a constant (an error text, a set of them), as a literal is.</param>
    /// <param name="fieldRunsSeed">ARV-287a: the methods each field's delegate may run, as a caller left them (a state machine's kickoff).</param>
    public static Result Follow(MethodBase method, IReadOnlyList<IlReader.Instruction> instructions, IReadOnlyList<IlReader.Region> regions,
        Func<Type, int> typeFlags, Func<MethodBase, int[], bool, int> callResult, int[] seed = null, IReadOnlyDictionary<(Module, int), int> fieldSeed = null,
        Summarizer summarize = null, int maxPasses = MaxPasses, IReadOnlyList<IReadOnlyCollection<MethodBase>> seedRuns = null,
        Func<FieldInfo, bool> constantField = null, IReadOnlyDictionary<(Module, int), IReadOnlyCollection<MethodBase>> fieldRunsSeed = null)
    {
        if (instructions.Any(i => i.OpCode == OpCodes.Calli || i.OpCode == OpCodes.Jmp))
            return null;
        var fields = fieldSeed is null ? new Dictionary<(Module, int), int>() : new Dictionary<(Module, int), int>(fieldSeed);
        var body = method.GetMethodBody();
        var argTypes = (method.IsStatic ? [] : new[] { method.DeclaringType }).Concat(method.GetParameters().Select(p => p.ParameterType)).ToArray();
        if (body is null || instructions.Count == 0)
            return new Result([], fields, [], [], [], 0, new int[argTypes.Length], [], new Dictionary<(Module, int), IReadOnlyCollection<MethodBase>>());
        var byOffset = instructions.Select((ins, k) => (ins.Offset, k)).ToDictionary(x => x.Offset, x => x.k);
        var argumentRuns = Enumerable.Range(0, argTypes.Length)
            .Select(a => seedRuns is not null && a < seedRuns.Count && seedRuns[a] is { Count: > 0 } given ? given.ToImmutableHashSet() : null).ToArray();
        var localTypes = body.LocalVariables.Select(l => l.LocalType).ToArray();
        var returnsValue = method is MethodInfo info && info.ReturnType != typeof(void);
        var fieldKeys = new List<(Module, int)>();
        int FieldHome((Module, int) key)
        {
            var index = fieldKeys.IndexOf(key);
            if (index < 0)
            {
                fieldKeys.Add(key);
                index = fieldKeys.Count - 1;
            }

            return FieldHomes - index;
        }

        // A finally block, at its end, goes on to where each leave that crossed it was going.
        var exits = regions.Where(r => r.Kind == IlReader.RegionKind.Finally).ToDictionary(r => r, r => instructions
            .Where(i => (i.OpCode == OpCodes.Leave || i.OpCode == OpCodes.Leave_S) && r.InTry(i.Offset))
            .SelectMany(i => i.Targets).Where(t => !r.InTry(t)).Distinct().ToArray());

        var arguments = new Dictionary<int, int[]>();
        var comparisons = new Dictionary<int, int>();
        var invoked = new Dictionary<int, HashSet<MethodBase>>();
        var passed = new Dictionary<int, HashSet<MethodBase>>();
        var constants = new Dictionary<int, ImmutableHashSet<object>[]>();
        var fieldAliases = new Dictionary<(Module, int), ImmutableHashSet<int>>();
        var fieldTargets = fieldRunsSeed is null ? new Dictionary<(Module, int), ImmutableHashSet<MethodBase>>()
            : fieldRunsSeed.Where(p => p.Value is { Count: > 0 }).ToDictionary(p => p.Key, p => p.Value.ToImmutableHashSet());
        var returned = 0;
        var stored = new int[argTypes.Length];
        // Fields are followed across the whole method: the interpretation is run again until no field gains a flag.
        for (var pass = 0; ; pass++)
        {
            if (pass == maxPasses)
                throw new InvalidOperationException($"The flow of {method.DeclaringType?.Name}.{method.Name} did not converge in {maxPasses} passes.");
            var before = Signature();
            var states = new State[instructions.Count];
            // The lowest instruction first: a try block is followed to its end before its handler, and a handler before what
            // follows it, so each is interpreted about once rather than again for every change in the block before it.
            var work = new PriorityQueue<int, int>();
            var queued = new bool[instructions.Count];
            void Queue(int k)
            {
                if (queued[k])
                    return;
                queued[k] = true;
                work.Enqueue(k, k);
            }

            void Enter(int k, State state)
            {
                if (k < 0 || k >= instructions.Count)
                    return;
                if (states[k] is null)
                {
                    states[k] = state;
                    Queue(k);
                }
                else if (states[k].Merge(state))
                {
                    Queue(k);
                }
            }

            void EnterAt(int offset, State state)
            {
                if (byOffset.TryGetValue(offset, out var at))
                    Enter(at, state);
            }

            var seeded = new int[argTypes.Length];
            for (var a = 0; a < Math.Min(seeded.Length, seed?.Length ?? 0); a++)
                seeded[a] = seed[a];
            Enter(0, State.Start(localTypes.Length, seeded));

            while (work.Count > 0)
            {
                var k = work.Dequeue();
                queued[k] = false;
                var ins = instructions[k];
                // ARV-287: a handler can start from any instruction of its try block, with that instruction's locals and
                // arguments: a catch or filter with the exception on its stack, a finally or fault with none.
                foreach (var region in regions)
                {
                    if (!region.InTry(ins.Offset) || !byOffset.TryGetValue(region.Start, out var handler))
                        continue;
                    if (states[handler] is null)
                        Enter(handler, states[k].With(region.Catches ? [new Slot(Exception, [])] : []));
                    else if (states[handler].MergeLocals(states[k]))
                        Queue(handler);
                }

                var state = states[k].Copy();
                var stack = state.Stack;
                var name = ins.OpCode.Name ?? "";
                Slot Pop() => stack.Count > 0 ? stack.Pop() : new Slot(0, []);

                // Adds flags to every value that may be the one at these homes: the locals, arguments, fields and stack values
                // that alias it, and the arguments it was given through (what the method stores into them).
                void Store(ImmutableHashSet<int> homes, int flags)
                {
                    if (flags == 0 || homes.IsEmpty)
                        return;
                    foreach (var home in homes)
                    {
                        if (home >= 0 && home < state.Locals.Length)
                        {
                            state.Locals[home] |= flags;
                        }
                        else if (home < 0 && home > MadeHomes && -1 - home < state.Arguments.Length)
                        {
                            state.Arguments[-1 - home] |= flags;
                            stored[-1 - home] |= flags;
                        }
                        else if (home <= FieldHomes && FieldHomes - home < fieldKeys.Count)
                        {
                            var key = fieldKeys[FieldHomes - home];
                            fields[key] = fields.GetValueOrDefault(key) | flags;
                        }
                    }

                    for (var i = 0; i < state.Locals.Length; i++)
                        if (!state.LocalHomes[i].IsEmpty && (state.Locals[i] | flags) != state.Locals[i] && state.LocalHomes[i].Overlaps(homes))
                            state.Locals[i] |= flags;
                    for (var a = 0; a < state.Arguments.Length; a++)
                        if ((state.Arguments[a] | flags) != state.Arguments[a] && state.ArgumentHomes[a].Overlaps(homes))
                        {
                            state.Arguments[a] |= flags;
                            stored[a] |= flags;
                        }

                    foreach (var (key, aliases) in fieldAliases)
                        if (aliases.Overlaps(homes))
                            fields[key] = fields.GetValueOrDefault(key) | flags;
                    var slots = stack.ToArray();
                    stack.Clear();
                    foreach (var slot in slots.Reverse())
                        stack.Push(slot.Homes.Overlaps(homes) ? slot with { Flags = slot.Flags | flags } : slot);
                }

                // Makes the locals at these homes (a struct taken by address) alias what it was made from too.
                void Alias(ImmutableHashSet<int> homes, ImmutableHashSet<int> more)
                {
                    if (more.IsEmpty)
                        return;
                    foreach (var home in homes.Where(h => h >= 0 && h < state.Locals.Length))
                        state.LocalHomes[home] = state.LocalHomes[home].Union(more);
                }

                var falls = true;

                if (ins.Callee is { } callee && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj))
                {
                    var created = ins.OpCode == OpCodes.Newobj;
                    var instance = !created && !callee.IsStatic;
                    var parameters = callee.GetParameters();
                    var count = parameters.Length + (instance ? 1 : 0);
                    var slots = new Slot[count];
                    for (var a = count - 1; a >= 0; a--)
                        slots[a] = Pop();
                    var taken = slots.Select(s => s.Flags).ToArray();
                    arguments[k] = arguments.TryGetValue(k, out var seen) ? [.. seen.Zip(taken, (x, y) => x | y)] : taken;
                    // ARV-287a: which arguments are a constant on every path that reaches the call, and which constants.
                    var constant = slots.Select(s => s.Constants).ToArray();
                    constants[k] = constants.TryGetValue(k, out var known) ? [.. known.Zip(constant, Both)] : constant;
                    // ARV-287: the delegates a call is given (a predicate, a selector), by the method each runs.
                    if (!(created && typeof(Delegate).IsAssignableFrom(callee.DeclaringType)))
                        foreach (var given in slots.Where(s => s.Runs is { IsEmpty: false }).SelectMany(s => s.Runs))
                            (passed.TryGetValue(k, out var those) ? those : passed[k] = []).Add(given);
                    // ARV-287a: the delegates each argument holds, from this stack position on, for the callee's summary.
                    IReadOnlyCollection<MethodBase>[] Runs(int from) => [.. slots.Skip(from).Select(s => (IReadOnlyCollection<MethodBase>)s.Runs ?? [])];
                    int Others(int except) => slots.Where((_, a) => a != except).Aggregate(0, (x, s) => x | s.Flags);
                    // The async machinery (a builder, an awaiter, a task) moves the state machine and its awaiters by reference, not
                    // values into each other.
                    var containers = IsMachinery(callee.DeclaringType) ? [] : Enumerable.Range(0, count)
                        .Where(a => instance && a == 0 ? IsContainer(callee.DeclaringType) : IsContainer(parameters[a - (instance ? 1 : 0)].ParameterType))
                        .ToArray();

                    // Each summary with the stack position its first argument is at (a static delegate target takes no receiver).
                    var summaries = new List<(Summary Summary, int Shift)>();
                    if (summarize is not null)
                    {
                        if (instance && callee.Name == "Invoke" && typeof(Delegate).IsAssignableFrom(callee.DeclaringType) && slots[0].Runs is { IsEmpty: false } runs)
                        {
                            // A delegate's call runs each method it may hold, given the delegate's receiver (a closure) unless the
                            // method is static.
                            (invoked.TryGetValue(k, out var those) ? those : invoked[k] = []).UnionWith(runs);
                            foreach (var target in runs)
                                if (summarize(target, false, taken[(target.IsStatic ? 1 : 0)..], fields, Runs(target.IsStatic ? 1 : 0)) is { } one)
                                    summaries.Add((one, target.IsStatic ? 1 : 0));
                        }
                        else if (summarize(callee, created, taken, fields, Runs(0)) is { } one)
                        {
                            summaries.Add((one, 0));
                        }
                    }

                    foreach (var (one, shift) in summaries)
                    {
                        foreach (var (key, flags) in one.Fields)
                            fields[key] = fields.GetValueOrDefault(key) | flags;
                        for (var a = 0; a < one.ArgumentsOut.Length && a + shift < count; a++)
                            Store(slots[a + shift].Homes, one.ArgumentsOut[a]);
                    }

                    var summary = summaries.Count == 0 ? null : new Summary(summaries.Aggregate(0, (x, y) => x | y.Summary.Returned), [], new Dictionary<(Module, int), int>());
                    if (summaries.Count == 0)
                    {
                        // A call outside the scope keeps what it is given in what it was given it with: an array, a span, a
                        // builder, a collection, a by-ref argument or a struct taken by address.
                        foreach (var a in containers)
                            Store(slots[a].Homes, Held(Others(a)));
                    }

                    // ARV-287: a setter keeps its value in its object (an answer body's member, a result's value), and so does a
                    // helper of the scope whose summary flags a field its receiver's type declares; an exception kept so makes the
                    // object a part of it.
                    if (instance && !IsMachinery(callee.DeclaringType) && callee.DeclaringType?.IsPrimitive == false)
                    {
                        var kept = callee.Name.StartsWith("set_", StringComparison.Ordinal) ? Others(0) : 0;
                        foreach (var (one, _) in summaries)
                            foreach (var key in FieldsOf(callee.DeclaringType))
                                kept |= one.Fields.GetValueOrDefault(key);
                        if (kept != 0)
                            Store(slots[0].Homes, (Held(kept) & ~Exception) | ((kept & (Exception | ExceptionPart)) != 0 ? ExceptionPart : 0));
                    }

                    var bound = containers.Where(a => !(instance && a == 0)).Aggregate(ImmutableHashSet<int>.Empty, (h, a) => h.Union(slots[a].Homes));
                    if (instance && callee is ConstructorInfo && callee.DeclaringType?.IsValueType == true)
                        Alias(slots[0].Homes, bound);
                    var returns = created ? callee.DeclaringType : (callee as MethodInfo)?.ReturnType;
                    if (returns is not null && returns != typeof(void))
                    {
                        // ARV-287a: a call whose body was followed returns what its summary says, beside what a specific rule
                        // gives its result (a rule's text read, an exception's text); a call outside the scope keeps what it takes.
                        var flags = callResult(callee, taken, summary is not null) | typeFlags(returns.IsByRef ? returns.GetElementType() : returns) | (summary?.Returned ?? 0);
                        // A ref a call returns is into what it was given; a fluent call returns its receiver; a span or memory
                        // made from an array is that array; a made object is its own.
                        var homes = returns.IsByRef ? slots.Aggregate(ImmutableHashSet<int>.Empty, (h, s) => h.Union(s.Homes))
                            : instance && returns == callee.DeclaringType ? slots[0].Homes
                            : IsContainer(returns) ? bound.Union(instance ? slots[0].Homes : [])
                            : [];
                        if (created)
                            homes = homes.Add(MadeHomes - k);
                        var made = created && typeof(Delegate).IsAssignableFrom(callee.DeclaringType) && count > 1 ? slots[1].Runs : null;
                        stack.Push(new Slot(flags, homes, false, made, returns.IsByRef));
                    }
                }
                else if (ins.OpCode == OpCodes.Ldftn || ins.OpCode == OpCodes.Ldvirtftn)
                {
                    if (ins.OpCode == OpCodes.Ldvirtftn)
                        Pop();
                    stack.Push(new Slot(0, [], false, [ins.Callee]));
                }
                else if (name.StartsWith("ldloca", StringComparison.Ordinal))
                {
                    var i = ins.Index;
                    stack.Push(new Slot(state.Locals.ElementAtOrDefault(i) | typeFlags(localTypes.ElementAtOrDefault(i)),
                        i >= 0 && i < state.Locals.Length ? state.LocalHomes[i].Add(i) : []));
                }
                else if (name.StartsWith("ldloc", StringComparison.Ordinal))
                {
                    var i = ins.Index;
                    var known = i >= 0 && i < state.Locals.Length;
                    stack.Push(new Slot(state.Locals.ElementAtOrDefault(i) | typeFlags(localTypes.ElementAtOrDefault(i)), known ? state.LocalHomes[i] : [],
                        false, known ? state.LocalRuns[i] : null));
                }
                else if (name.StartsWith("stloc", StringComparison.Ordinal))
                {
                    var value = Pop();
                    var i = ins.Index;
                    if (i >= 0 && i < state.Locals.Length)
                    {
                        state.Locals[i] = value.Flags;
                        // A value with no home of its own is the object this store made it.
                        state.LocalHomes[i] = value.Homes.IsEmpty ? [MadeHomes - k] : value.Homes;
                        state.LocalRuns[i] = value.Runs ?? ImmutableHashSet<MethodBase>.Empty;
                    }
                }
                else if (name.StartsWith("ldarga", StringComparison.Ordinal))
                {
                    var a = ins.Index;
                    stack.Push(new Slot(state.Arguments.ElementAtOrDefault(a) | typeFlags(argTypes.ElementAtOrDefault(a)),
                        a >= 0 && a < state.Arguments.Length ? state.ArgumentHomes[a].Add(-1 - a) : []));
                }
                else if (name.StartsWith("ldarg", StringComparison.Ordinal))
                {
                    var a = ins.Index;
                    stack.Push(new Slot(state.Arguments.ElementAtOrDefault(a) | typeFlags(argTypes.ElementAtOrDefault(a)),
                        a >= 0 && a < state.Arguments.Length ? state.ArgumentHomes[a] : [], false, a >= 0 && a < argumentRuns.Length ? argumentRuns[a] : null));
                }
                else if (name.StartsWith("starg", StringComparison.Ordinal))
                {
                    var value = Pop();
                    var a = ins.Index;
                    if (a >= 0 && a < state.Arguments.Length)
                    {
                        state.Arguments[a] = value.Flags;
                        state.ArgumentHomes[a] = value.Homes.IsEmpty ? [MadeHomes - k] : value.Homes;
                    }
                }
                else if (ins.Member is FieldInfo field && name is "ldfld" or "ldsfld" or "ldflda" or "ldsflda")
                {
                    var receiver = name is "ldfld" or "ldflda" ? Pop() : new Slot(0, []);
                    var key = (field.Module, field.MetadataToken);
                    var flags = fields.GetValueOrDefault(key) | typeFlags(field.FieldType);
                    ImmutableHashSet<object> named = name == "ldsfld" && constantField?.Invoke(field) == true ? [field] : null;
                    // ARV-287: a field of a struct or a public field (a tuple's item, a StrongBox's value) is part of the value it
                    // is read from; a closure's or a state machine's fields are followed by themselves.
                    if (field.DeclaringType is { } holder && !IsClosureOrMachine(holder) && (holder.IsValueType || field.IsPublic))
                        flags |= Read(receiver.Flags);
                    stack.Push(new Slot(flags, fieldAliases.GetValueOrDefault(key, ImmutableHashSet<int>.Empty).Add(FieldHome(key)), false, fieldTargets.GetValueOrDefault(key),
                        false, named));
                }
                else if (ins.Member is FieldInfo written && name is "stfld" or "stsfld")
                {
                    var value = Pop();
                    var receiver = name == "stfld" ? Pop() : new Slot(0, []);
                    var key = (written.Module, written.MetadataToken);
                    // ARV-287: a field written keeps its value in its object (an answer body's public field, a struct's), as a
                    // setter does; a closure's or a state machine's fields are followed by themselves. Into `this`, it reaches
                    // the caller through what the method stores into its arguments.
                    if (name == "stfld" && written.DeclaringType is { } holder && !IsClosureOrMachine(holder) && value.Flags != 0)
                        Store(receiver.Homes, (Held(value.Flags) & ~Exception) | ((value.Flags & (Exception | ExceptionPart)) != 0 ? ExceptionPart : 0));
                    fields[key] = fields.GetValueOrDefault(key) | value.Flags;
                    if (!value.Homes.IsEmpty)
                        fieldAliases[key] = fieldAliases.GetValueOrDefault(key, ImmutableHashSet<int>.Empty).Union(value.Homes);
                    if (value.Runs is { IsEmpty: false })
                        fieldTargets[key] = fieldTargets.TryGetValue(key, out var runs) ? runs.Union(value.Runs) : value.Runs;
                    Store([FieldHome(key)], value.Flags);
                }
                else if (ins.OpCode == OpCodes.Newarr)
                {
                    Pop();
                    stack.Push(new Slot(0, [MadeHomes - k]));
                }
                else if (name.StartsWith("stelem", StringComparison.Ordinal))
                {
                    var value = Pop();
                    Pop();
                    Store(Pop().Homes, Held(value.Flags));
                }
                else if (name.StartsWith("ldelema", StringComparison.Ordinal))
                {
                    Pop();
                    var array = Pop();
                    stack.Push(array with { Flags = Read(array.Flags), Runs = null, Element = true });
                }
                else if (name.StartsWith("ldelem", StringComparison.Ordinal))
                {
                    Pop();
                    stack.Push(new Slot(Read(Pop().Flags) | (ins.Member is Type element ? typeFlags(element) : 0), []));
                }
                else if (name.StartsWith("stind", StringComparison.Ordinal) || ins.OpCode == OpCodes.Stobj)
                {
                    // Through an element's address (an array's, a span's, a list's) the value goes into its container, as stelem.
                    var value = Pop();
                    var address = Pop();
                    Store(address.Homes, address.Element ? Held(value.Flags) : value.Flags);
                }
                else if (name.StartsWith("ldind", StringComparison.Ordinal) || ins.OpCode == OpCodes.Ldobj)
                {
                    var address = Pop();
                    stack.Push(new Slot(address.Flags | (ins.Member is Type loaded ? typeFlags(loaded) : 0), [], false, address.Runs));
                }
                else if (ins.OpCode == OpCodes.Ldnull)
                {
                    stack.Push(new Slot(0, [], true));
                }
                else if (ins.OpCode == OpCodes.Ldstr)
                {
                    stack.Push(new Slot(0, [], false, null, false, ins.Literal is null ? null : [ins.Literal]));
                }
                else if (ins.OpCode == OpCodes.Ceq || ins.OpCode == OpCodes.Beq || ins.OpCode == OpCodes.Beq_S || ins.OpCode == OpCodes.Bne_Un || ins.OpCode == OpCodes.Bne_Un_S)
                {
                    // ARV-287: two values compared by reference (the compiler writes ReferenceEquals so), a null check apart; a
                    // value that is null on one path only is not a null.
                    var right = Pop();
                    var left = Pop();
                    if (!left.IsNull && !right.IsNull && (left.Flags | right.Flags) != 0)
                        comparisons[k] = comparisons.GetValueOrDefault(k) | left.Flags | right.Flags;
                    if (ins.OpCode == OpCodes.Ceq)
                        stack.Push(new Slot(0, []));
                }
                else if (ins.OpCode == OpCodes.Dup)
                {
                    var top = Pop();
                    stack.Push(top);
                    stack.Push(top);
                }
                else if (ins.OpCode == OpCodes.Isinst || ins.OpCode == OpCodes.Castclass || ins.OpCode == OpCodes.Box || ins.OpCode == OpCodes.Unbox_Any ||
                         ins.OpCode == OpCodes.Unbox)
                {
                    var value = Pop();
                    stack.Push(value with { Flags = value.Flags | (ins.Member is Type type ? typeFlags(type) : 0) });
                }
                else if (ins.OpCode == OpCodes.Leave || ins.OpCode == OpCodes.Leave_S)
                {
                    // ARV-287: a leave runs each finally block it crosses before it reaches its target.
                    stack.Clear();
                    foreach (var region in regions.Where(r => r.Kind == IlReader.RegionKind.Finally && r.InTry(ins.Offset) && ins.Targets.Any(t => !r.InTry(t))))
                        EnterAt(region.Start, state.With([]));
                    falls = false;
                }
                else if (ins.OpCode == OpCodes.Endfinally)
                {
                    foreach (var region in regions.Where(r => r.Kind == IlReader.RegionKind.Finally && r.Holds(ins.Offset)))
                        foreach (var exit in exits[region])
                            EnterAt(exit, state.With([]));
                    falls = false;
                }
                else if (ins.OpCode == OpCodes.Endfilter)
                {
                    // ARV-287: a filter that accepts goes on to its handler, with the locals it set.
                    Pop();
                    foreach (var region in regions.Where(r => r.Kind == IlReader.RegionKind.Filter && r.Holds(ins.Offset)))
                        EnterAt(region.End, state.With([new Slot(Exception, [])]));
                    falls = false;
                }
                else if (ins.OpCode == OpCodes.Ret)
                {
                    if (returnsValue)
                        returned |= Pop().Flags;
                    falls = false;
                }
                else if (ins.OpCode == OpCodes.Throw)
                {
                    Pop();
                    falls = false;
                }
                else if (ins.OpCode == OpCodes.Rethrow)
                {
                    falls = false;
                }
                else
                {
                    var pops = Count(ins.OpCode.StackBehaviourPop);
                    for (var p = 0; p < pops; p++)
                        Pop();
                    var pushes = Count(ins.OpCode.StackBehaviourPush);
                    for (var p = 0; p < pushes; p++)
                        stack.Push(new Slot(0, []));
                    if (ins.OpCode == OpCodes.Br || ins.OpCode == OpCodes.Br_S)
                        falls = false;
                }

                foreach (var target in ins.Targets)
                    EnterAt(target, state.Copy());
                if (falls)
                    Enter(k + 1, state);
            }

            if (Signature() == before)
                break;
        }

        return new Result(arguments, fields, comparisons, invoked.ToDictionary(p => p.Key, p => (IReadOnlyCollection<MethodBase>)p.Value),
            passed.ToDictionary(p => p.Key, p => (IReadOnlyCollection<MethodBase>)p.Value), returned, stored, constants,
            fieldTargets.ToDictionary(p => p.Key, p => (IReadOnlyCollection<MethodBase>)p.Value));

        // Everything the passes carry over only grows, so an unchanged signature means nothing was gained.
        long Signature() => fields.Values.Sum(v => (long)v) + fieldAliases.Values.Sum(h => (long)h.Count) + fieldTargets.Values.Sum(t => 7L * t.Count) + returned + stored.Sum(v => (long)v);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (Module, int)[]> InstanceFields = new();

    /// <summary>The instance fields a type and its bases declare, by their keys.</summary>
    private static (Module, int)[] FieldsOf(Type type) => InstanceFields.GetOrAdd(type, t =>
    {
        var keys = new List<(Module, int)>();
        for (var current = t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t; current is not null; current = current.BaseType)
            keys.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(f => (f.Module, f.MetadataToken)));
        return [.. keys];
    });

    /// <summary>
    /// A closure's or a state machine's type, whose fields are followed by themselves; ARV-287a: an anonymous type holds the
    /// values it is made from, as a tuple does, so its fields are its object's.
    /// </summary>
    private static bool IsClosureOrMachine(Type type) => type.Name.StartsWith('<') && !type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal);

    /// <summary>The compiler's async and iterator machinery and the tasks it awaits; the interpolated string handler is a builder, not machinery.</summary>
    internal static bool IsMachinery(Type type) =>
        type is not null && ((type.Namespace == "System.Runtime.CompilerServices" && type.Name != "DefaultInterpolatedStringHandler") ||
                             type == typeof(Task) || type == typeof(ValueTask) ||
                             (type.IsGenericType && type.GetGenericTypeDefinition() is var open && (open == typeof(Task<>) || open == typeof(ValueTask<>))));

    /// <summary>A type that keeps what it is given: a by-ref, an array, a span or memory, a builder, a collection, or a struct that is not a primitive.</summary>
    private static bool IsContainer(Type type)
    {
        if (type is null || IsMachinery(type))
            return false;
        if (type.IsByRef)
            return !IsPlain(type.GetElementType());
        if (type.IsArray)
            return !IsPlain(type.GetElementType());
        if (type == typeof(Array) || type == typeof(System.Text.StringBuilder))
            return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var open &&
            (open == typeof(Span<>) || open == typeof(ReadOnlySpan<>) || open == typeof(Memory<>) || open == typeof(ReadOnlyMemory<>)))
            return !IsPlain(type.GetGenericArguments()[0]);
        if (type.IsValueType)
            return !IsPlain(type);
        return type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    /// <summary>A value that holds no text: a number, a flag, a character, an enum, a time.</summary>
    internal static bool IsPlain(Type type) =>
        type is null || type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(CancellationToken) ||
        (Nullable.GetUnderlyingType(type) is { } underlying && IsPlain(underlying));

    private static int Count(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0 or StackBehaviour.Varpop or StackBehaviour.Varpush => 0,
        _ => behaviour.ToString().Split('_').Length
    };

    #endregion

    #region Types

    /// <summary>
    /// What a call into the scope does, for a given seed: the flags of what it returns, the flags it stores into each argument it
    /// takes (by stack position, the receiver first), and the fields' flags it leaves.
    /// </summary>
    public delegate Summary Summarizer(MethodBase callee, bool created, int[] taken, IReadOnlyDictionary<(Module, int), int> fields,
        IReadOnlyList<IReadOnlyCollection<MethodBase>> runs);

    /// <summary>
    /// A method's flow: the flags each call takes (by instruction index), the flags each field gained, the flags of the values
    /// each ceq, beq or bne.un compares by reference (none when either is a null), the delegate targets invoked and the ones
    /// each call is given (by instruction index), and the flags it returns and stores into each argument. ARV-287a: for each
    /// call, the constants each argument is on every path (the literals, the constant fields), null for one that is not; and
    /// the delegates each field may hold.
    /// </summary>
    public sealed record Result(Dictionary<int, int[]> Arguments, Dictionary<(Module, int), int> Fields, Dictionary<int, int> Comparisons,
        Dictionary<int, IReadOnlyCollection<MethodBase>> Invoked, Dictionary<int, IReadOnlyCollection<MethodBase>> Passed, int Returned, int[] ArgumentsOut,
        Dictionary<int, ImmutableHashSet<object>[]> Constants, IReadOnlyDictionary<(Module, int), IReadOnlyCollection<MethodBase>> FieldRuns);

    /// <summary>What a call does: the flags it returns, stores into each argument, and leaves in the fields.</summary>
    public sealed record Summary(int Returned, int[] ArgumentsOut, IReadOnlyDictionary<(Module, int), int> Fields);

    /// <summary>
    /// A value on the stack: its flags; its homes (the locals, arguments, fields and made objects it may be or point into); whether
    /// it is a null on every path; the methods a delegate or method pointer may run; whether it is the address of a container's
    /// element; and (ARV-287a) the constants it is on every path (literals, constant fields), or null when it may be computed.
    /// </summary>
    private readonly record struct Slot(int Flags, ImmutableHashSet<int> Homes, bool IsNull = false, ImmutableHashSet<MethodBase> Runs = null, bool Element = false,
        ImmutableHashSet<object> Constants = null);

    /// <summary>ARV-287a: the constants a value is on both paths: their union, or null when it may be computed on either.</summary>
    private static ImmutableHashSet<object> Both(ImmutableHashSet<object> mine, ImmutableHashSet<object> theirs) =>
        mine is null || theirs is null ? null : Union(mine, theirs);

    /// <summary>The union of two sets (of homes, of methods): the first itself when it holds every member of the second.</summary>
    private static ImmutableHashSet<T> Union<T>(ImmutableHashSet<T> mine, ImmutableHashSet<T> theirs) =>
        theirs is null || ReferenceEquals(mine, theirs) || theirs.IsEmpty ? mine : mine is null || mine.IsEmpty ? theirs : mine.IsSupersetOf(theirs) ? mine : mine.Union(theirs);

    /// <summary>The values on the stack and in the locals and arguments at one instruction, with their homes.</summary>
    private sealed class State
    {
        public Stack<Slot> Stack { get; private init; }

        public int[] Locals { get; private init; }

        public ImmutableHashSet<int>[] LocalHomes { get; private init; }

        public ImmutableHashSet<MethodBase>[] LocalRuns { get; private init; }

        public int[] Arguments { get; private init; }

        public ImmutableHashSet<int>[] ArgumentHomes { get; private init; }

        /// <summary>The state on entry: each argument its own home, each local none.</summary>
        public static State Start(int locals, int[] arguments) => new()
        {
            Stack = new Stack<Slot>(),
            Locals = new int[locals],
            LocalHomes = Enumerable.Range(0, locals).Select(_ => ImmutableHashSet<int>.Empty).ToArray(),
            LocalRuns = Enumerable.Range(0, locals).Select(_ => ImmutableHashSet<MethodBase>.Empty).ToArray(),
            Arguments = [.. arguments],
            ArgumentHomes = Enumerable.Range(0, arguments.Length).Select(a => ImmutableHashSet.Create(-1 - a)).ToArray()
        };

        public State Copy() => With(Stack.Reverse());

        /// <summary>This state's locals and arguments with another stack.</summary>
        public State With(IEnumerable<Slot> stack) => new()
        {
            Stack = new Stack<Slot>(stack),
            Locals = [.. Locals],
            LocalHomes = [.. LocalHomes],
            LocalRuns = [.. LocalRuns],
            Arguments = [.. Arguments],
            ArgumentHomes = [.. ArgumentHomes]
        };

        /// <summary>Adds the other state's flags and homes to this one's; true when anything was added.</summary>
        public bool Merge(State other)
        {
            var changed = false;
            var mine = Stack.ToArray();
            var theirs = other.Stack.ToArray();
            for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
            {
                var merged = new Slot(mine[i].Flags | theirs[i].Flags, Union(mine[i].Homes, theirs[i].Homes), mine[i].IsNull && theirs[i].IsNull,
                    Union(mine[i].Runs, theirs[i].Runs), mine[i].Element || theirs[i].Element, Both(mine[i].Constants, theirs[i].Constants));
                if (merged.Flags == mine[i].Flags && ReferenceEquals(merged.Homes, mine[i].Homes) && merged.IsNull == mine[i].IsNull &&
                    ReferenceEquals(merged.Runs, mine[i].Runs) && merged.Element == mine[i].Element && ReferenceEquals(merged.Constants, mine[i].Constants))
                    continue;
                mine[i] = merged;
                changed = true;
            }

            if (changed)
            {
                Stack.Clear();
                foreach (var value in mine.Reverse())
                    Stack.Push(value);
            }

            return MergeLocals(other) || changed;
        }

        /// <summary>Adds the other state's locals and arguments to this one's, its stack apart; true when anything was added.</summary>
        public bool MergeLocals(State other)
        {
            var changed = false;
            for (var i = 0; i < Locals.Length; i++)
            {
                if ((Locals[i] | other.Locals[i]) != Locals[i])
                {
                    Locals[i] |= other.Locals[i];
                    changed = true;
                }

                var homes = Union(LocalHomes[i], other.LocalHomes[i]);
                if (!ReferenceEquals(homes, LocalHomes[i]))
                {
                    LocalHomes[i] = homes;
                    changed = true;
                }

                var runs = Union(LocalRuns[i], other.LocalRuns[i]);
                if (!ReferenceEquals(runs, LocalRuns[i]))
                {
                    LocalRuns[i] = runs;
                    changed = true;
                }
            }

            for (var i = 0; i < Arguments.Length; i++)
            {
                if ((Arguments[i] | other.Arguments[i]) != Arguments[i])
                {
                    Arguments[i] |= other.Arguments[i];
                    changed = true;
                }

                var homes = Union(ArgumentHomes[i], other.ArgumentHomes[i]);
                if (!ReferenceEquals(homes, ArgumentHomes[i]))
                {
                    ArgumentHomes[i] = homes;
                    changed = true;
                }
            }

            return changed;
        }
    }

    #endregion
}
