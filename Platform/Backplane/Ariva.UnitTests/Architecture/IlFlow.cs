using System.Reflection;
using System.Reflection.Emit;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// Follows values through a method's IL (ARV-282): an abstract interpretation over its branches and exception handlers that
/// marks each value on the stack, in a local, an argument or a field with flags (an exception, a rule's text, an exception's
/// text) and records, for every call, the flags of the values it takes. So a check can tell that a call formats or compares
/// the caught exception or its text, whichever argument it is in and through whichever locals it went, and that a call merely
/// given the exception (a logger) does not. A method the interpretation cannot follow (calli, jmp) is reported, not skipped.
/// ARV-287: each value on the stack also knows its home (the local, argument or field it was loaded from or whose address it
/// is, or the instruction that made the array), so a value stored into an array, through an address (a span's element, an
/// inline array's, a ref a call returned) or by a call on a struct or builder taken by address adds its flags to that home;
/// an address carries its value's flags; finally and fault handlers are interpreted; a handler starts with the locals of
/// every instruction of its try block merged; and the fields' flags can be seeded from a caller's, so a lambda followed from a
/// method starts with the flags of the closure fields that method set.
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

    /// <summary>The most passes over a method while its fields gain flags; more is a bug in the interpretation, reported.</summary>
    public const int MaxPasses = 64;

    private const int NoHome = int.MinValue;
    private const int NullHome = int.MinValue + 1;
    private const int MadeHomes = -1_000_000;
    private const int FieldHomes = -2_000_000;

    #endregion

    #region Methods

    /// <summary>
    /// For each call or newobj instruction (by index), the flags of the values it takes, the receiver first; null when the
    /// method uses calli or jmp, whose stack effect the interpretation cannot know.
    /// </summary>
    public static Dictionary<int, int[]> Arguments(MethodBase method, IReadOnlyList<IlReader.Instruction> instructions, IReadOnlyList<IlReader.Region> regions,
        Func<Type, int> typeFlags, Func<MethodBase, int[], int> callResult, int[] seed = null) =>
        Follow(method, instructions, regions, typeFlags, callResult, seed)?.Arguments;

    /// <summary>
    /// The method's flow: for each call or newobj instruction (by index) the flags of the values it takes, the receiver first,
    /// and the flags each field gained; null when the method uses calli or jmp.
    /// </summary>
    /// <param name="typeFlags">The flags a value has because of its type (an exception type is <see cref="Exception"/>).</param>
    /// <param name="callResult">The flags of a call's result, from the call and the flags of the values it takes.</param>
    /// <param name="seed">The flags of the method's arguments (the receiver first), when a caller's are known.</param>
    /// <param name="fieldSeed">The fields' flags as a caller left them (the closure fields a lambda reads).</param>
    public static Result Follow(MethodBase method, IReadOnlyList<IlReader.Instruction> instructions, IReadOnlyList<IlReader.Region> regions,
        Func<Type, int> typeFlags, Func<MethodBase, int[], int> callResult, int[] seed = null, IReadOnlyDictionary<(Module, int), int> fieldSeed = null)
    {
        if (instructions.Any(i => i.OpCode == OpCodes.Calli || i.OpCode == OpCodes.Jmp))
            return null;
        var fields = fieldSeed is null ? new Dictionary<(Module, int), int>() : new Dictionary<(Module, int), int>(fieldSeed);
        var body = method.GetMethodBody();
        if (body is null || instructions.Count == 0)
            return new Result([], fields, []);
        var byOffset = instructions.Select((ins, k) => (ins.Offset, k)).ToDictionary(x => x.Offset, x => x.k);
        var localTypes = body.LocalVariables.Select(l => l.LocalType).ToArray();
        var argTypes = (method.IsStatic ? [] : new[] { method.DeclaringType }).Concat(method.GetParameters().Select(p => p.ParameterType)).ToArray();
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

        var arguments = new Dictionary<int, int[]>();
        var comparisons = new Dictionary<int, int>();
        // Fields are followed across the whole method: the interpretation is run again until no field gains a flag.
        for (var pass = 0; ; pass++)
        {
            if (pass == MaxPasses)
                throw new InvalidOperationException($"The flow of {method.DeclaringType?.Name}.{method.Name} did not converge in {MaxPasses} passes.");
            var fieldsBefore = fields.Values.Sum();
            var states = new State[instructions.Count];
            var work = new Queue<int>();
            void Enter(int k, State state)
            {
                if (k < 0 || k >= instructions.Count)
                    return;
                if (states[k] is null)
                {
                    states[k] = state;
                    work.Enqueue(k);
                }
                else if (states[k].Merge(state))
                {
                    work.Enqueue(k);
                }
            }

            var seeded = new int[argTypes.Length];
            for (var a = 0; a < Math.Min(seeded.Length, seed?.Length ?? 0); a++)
                seeded[a] = seed[a];
            Enter(0, new State(new Stack<Slot>(), new int[localTypes.Length], seeded));

            while (work.Count > 0)
            {
                var k = work.Dequeue();
                var ins = instructions[k];
                // ARV-287: a handler can start from any instruction of its try block, with that instruction's locals and
                // arguments: a catch or filter with the exception on its stack, a finally or fault with none.
                foreach (var region in regions.Where(r => r.InTry(ins.Offset)))
                {
                    var thrown = region.Kind is IlReader.RegionKind.Catch or IlReader.RegionKind.Filter ? new Stack<Slot>([new Slot(Exception, NoHome)]) : new Stack<Slot>();
                    if (byOffset.TryGetValue(region.Start, out var handler))
                        Enter(handler, new State(thrown, [.. states[k].Locals], [.. states[k].Arguments]));
                }

                var state = states[k].Copy();
                var stack = state.Stack;
                var name = ins.OpCode.Name ?? "";
                Slot Pop() => stack.Count > 0 ? stack.Pop() : new Slot(0, NoHome);
                void Store(int home, int flags)
                {
                    if (flags == 0 || home == NoHome)
                        return;
                    if (home >= 0 && home < state.Locals.Length)
                        state.Locals[home] |= flags;
                    else if (home < 0 && home > MadeHomes && -1 - home < state.Arguments.Length)
                        state.Arguments[-1 - home] |= flags;
                    else if (home <= FieldHomes && FieldHomes - home < fieldKeys.Count)
                        fields[fieldKeys[FieldHomes - home]] = fields.GetValueOrDefault(fieldKeys[FieldHomes - home]) | flags;
                    // The same home's other copies on the stack (an array or address duplicated before the store).
                    var slots = stack.ToArray();
                    stack.Clear();
                    foreach (var slot in slots.Reverse())
                        stack.Push(slot.Home == home ? slot with { Flags = slot.Flags | flags } : slot);
                }

                var falls = true;

                if (ins.Callee is { } callee && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj))
                {
                    var count = callee.GetParameters().Length + (ins.OpCode != OpCodes.Newobj && !callee.IsStatic ? 1 : 0);
                    var slots = new Slot[count];
                    for (var a = count - 1; a >= 0; a--)
                        slots[a] = Pop();
                    var taken = slots.Select(s => s.Flags).ToArray();
                    arguments[k] = arguments.TryGetValue(k, out var seen) ? [.. seen.Zip(taken, (x, y) => x | y)] : taken;
                    // A call on a struct taken by address (an interpolated string handler) or on a StringBuilder keeps what it is given.
                    if (ins.OpCode != OpCodes.Newobj && !callee.IsStatic && count > 0 &&
                        (callee.DeclaringType?.IsValueType == true || callee.DeclaringType == typeof(System.Text.StringBuilder)))
                        Store(slots[0].Home, taken.Skip(1).Aggregate(0, (x, y) => x | y));
                    var returns = ins.OpCode == OpCodes.Newobj ? callee.DeclaringType : (callee as MethodInfo)?.ReturnType;
                    if (returns is not null && returns != typeof(void))
                    {
                        // A ref a call returns (a span's or an inline array's element) is the home of what it was taken from.
                        var home = (returns.IsByRef || (!callee.IsStatic && returns == callee.DeclaringType && ins.OpCode != OpCodes.Newobj)) && count > 0 ? slots[0].Home
                            : ins.OpCode == OpCodes.Newobj ? MadeHomes - k : NoHome;
                        stack.Push(new Slot(callResult(callee, taken) | typeFlags(returns.IsByRef ? returns.GetElementType() : returns), home));
                    }
                }
                else if (name.StartsWith("ldloca", StringComparison.Ordinal))
                {
                    stack.Push(new Slot(state.Locals.ElementAtOrDefault(ins.Index), ins.Index));
                }
                else if (name.StartsWith("ldloc", StringComparison.Ordinal))
                {
                    stack.Push(new Slot(state.Locals.ElementAtOrDefault(ins.Index) | typeFlags(localTypes.ElementAtOrDefault(ins.Index)), ins.Index));
                }
                else if (name.StartsWith("stloc", StringComparison.Ordinal))
                {
                    var value = Pop();
                    if (ins.Index >= 0 && ins.Index < state.Locals.Length)
                        state.Locals[ins.Index] = value.Flags;
                }
                else if (name.StartsWith("ldarga", StringComparison.Ordinal))
                {
                    stack.Push(new Slot(state.Arguments.ElementAtOrDefault(ins.Index), -1 - ins.Index));
                }
                else if (name.StartsWith("ldarg", StringComparison.Ordinal))
                {
                    stack.Push(new Slot(state.Arguments.ElementAtOrDefault(ins.Index) | typeFlags(argTypes.ElementAtOrDefault(ins.Index)), -1 - ins.Index));
                }
                else if (name.StartsWith("starg", StringComparison.Ordinal))
                {
                    var value = Pop();
                    if (ins.Index >= 0 && ins.Index < state.Arguments.Length)
                        state.Arguments[ins.Index] = value.Flags;
                }
                else if (ins.Member is FieldInfo field && name is "ldfld" or "ldsfld" or "ldflda" or "ldsflda")
                {
                    if (name is "ldfld" or "ldflda")
                        Pop();
                    var key = (field.Module, field.MetadataToken);
                    var flags = fields.GetValueOrDefault(key) | (name.EndsWith('a') ? 0 : typeFlags(field.FieldType));
                    stack.Push(new Slot(flags, FieldHome(key)));
                }
                else if (ins.Member is FieldInfo stored && name is "stfld" or "stsfld")
                {
                    var value = Pop();
                    if (name == "stfld")
                        Pop();
                    var key = (stored.Module, stored.MetadataToken);
                    fields[key] = fields.GetValueOrDefault(key) | value.Flags;
                }
                else if (ins.OpCode == OpCodes.Newarr)
                {
                    Pop();
                    stack.Push(new Slot(0, MadeHomes - k));
                }
                else if (name.StartsWith("stelem", StringComparison.Ordinal))
                {
                    var value = Pop();
                    Pop();
                    Store(Pop().Home, value.Flags);
                }
                else if (name.StartsWith("ldelema", StringComparison.Ordinal))
                {
                    Pop();
                    var array = Pop();
                    stack.Push(array);
                }
                else if (name.StartsWith("ldelem", StringComparison.Ordinal))
                {
                    Pop();
                    stack.Push(new Slot(Pop().Flags | (ins.Member is Type element ? typeFlags(element) : 0), NoHome));
                }
                else if (name.StartsWith("stind", StringComparison.Ordinal) || ins.OpCode == OpCodes.Stobj)
                {
                    var value = Pop();
                    Store(Pop().Home, value.Flags);
                }
                else if (name.StartsWith("ldind", StringComparison.Ordinal) || ins.OpCode == OpCodes.Ldobj)
                {
                    stack.Push(new Slot(Pop().Flags | (ins.Member is Type loaded ? typeFlags(loaded) : 0), NoHome));
                }
                else if (ins.OpCode == OpCodes.Ldnull)
                {
                    stack.Push(new Slot(0, NullHome));
                }
                else if (ins.OpCode == OpCodes.Ceq || ins.OpCode == OpCodes.Beq || ins.OpCode == OpCodes.Beq_S || ins.OpCode == OpCodes.Bne_Un || ins.OpCode == OpCodes.Bne_Un_S)
                {
                    // ARV-287: two values compared by reference (the compiler writes ReferenceEquals so), a null check apart.
                    var right = Pop();
                    var left = Pop();
                    if (left.Home != NullHome && right.Home != NullHome && (left.Flags | right.Flags) != 0)
                        comparisons[k] = comparisons.GetValueOrDefault(k) | left.Flags | right.Flags;
                    if (ins.OpCode == OpCodes.Ceq)
                        stack.Push(new Slot(0, NoHome));
                }
                else if (ins.OpCode == OpCodes.Dup)
                {
                    var top = Pop();
                    stack.Push(top);
                    stack.Push(top);
                }
                else if (ins.OpCode == OpCodes.Isinst || ins.OpCode == OpCodes.Castclass || ins.OpCode == OpCodes.Box || ins.OpCode == OpCodes.Unbox_Any)
                {
                    var value = Pop();
                    stack.Push(new Slot(value.Flags | (ins.Member is Type type ? typeFlags(type) : 0), value.Home));
                }
                else if (ins.OpCode == OpCodes.Leave || ins.OpCode == OpCodes.Leave_S)
                {
                    stack.Clear();
                    falls = false;
                }
                else if (ins.OpCode == OpCodes.Ret)
                {
                    if (returnsValue)
                        Pop();
                    falls = false;
                }
                else if (ins.OpCode == OpCodes.Throw || ins.OpCode == OpCodes.Endfilter)
                {
                    Pop();
                    falls = false;
                }
                else if (ins.OpCode == OpCodes.Rethrow || ins.OpCode == OpCodes.Endfinally)
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
                        stack.Push(new Slot(0, NoHome));
                    if (ins.OpCode == OpCodes.Br || ins.OpCode == OpCodes.Br_S)
                        falls = false;
                }

                foreach (var target in ins.Targets)
                    if (byOffset.TryGetValue(target, out var next))
                        Enter(next, state.Copy());
                if (falls)
                    Enter(k + 1, state);
            }

            if (fields.Values.Sum() == fieldsBefore)
                break;
        }

        return new Result(arguments, fields, comparisons);
    }

    private static int Count(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0 or StackBehaviour.Varpop or StackBehaviour.Varpush => 0,
        _ => behaviour.ToString().Split('_').Length
    };

    #endregion

    #region Types

    /// <summary>
    /// A method's flow: the flags each call takes (by instruction index), the flags each field gained, and the flags of the
    /// values each ceq, beq or bne.un compares by reference (by instruction index; none when either is a null).
    /// </summary>
    public sealed record Result(Dictionary<int, int[]> Arguments, Dictionary<(Module, int), int> Fields, Dictionary<int, int> Comparisons);

    /// <summary>A value on the stack: its flags and its home (a local, an argument, a field, the instruction that made it, or none).</summary>
    private readonly record struct Slot(int Flags, int Home);

    /// <summary>The flags on the stack and in the locals and arguments at one instruction.</summary>
    private sealed class State(Stack<Slot> stack, int[] locals, int[] arguments)
    {
        public Stack<Slot> Stack { get; } = stack;

        public int[] Locals { get; } = locals;

        public int[] Arguments { get; } = arguments;

        public State Copy() => new(new Stack<Slot>(Stack.Reverse()), [.. Locals], [.. Arguments]);

        /// <summary>Adds the other state's flags to this one's; true when any flag was added. A home that differs is kept as this one's.</summary>
        public bool Merge(State other)
        {
            var changed = false;
            var mine = Stack.ToArray();
            var theirs = other.Stack.ToArray();
            for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
            {
                if ((mine[i].Flags | theirs[i].Flags) == mine[i].Flags)
                    continue;
                mine[i] = mine[i] with { Flags = mine[i].Flags | theirs[i].Flags };
                changed = true;
            }

            if (changed)
            {
                Stack.Clear();
                foreach (var value in mine.Reverse())
                    Stack.Push(value);
            }

            for (var i = 0; i < Locals.Length; i++)
                if ((Locals[i] | other.Locals[i]) != Locals[i])
                {
                    Locals[i] |= other.Locals[i];
                    changed = true;
                }

            for (var i = 0; i < Arguments.Length; i++)
                if ((Arguments[i] | other.Arguments[i]) != Arguments[i])
                {
                    Arguments[i] |= other.Arguments[i];
                    changed = true;
                }

            return changed;
        }
    }

    #endregion
}
