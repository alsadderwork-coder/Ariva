using System.Reflection;
using System.Reflection.Emit;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// Follows values through a method's IL (ARV-282): an abstract interpretation over its branches and exception handlers that
/// marks each value on the stack, in a local, an argument or a field with flags (an exception, a rule's text, an exception's
/// text) and records, for every call, the flags of the values it takes. So a check can tell that a call formats or compares
/// the caught exception or its text, whichever argument it is in and through whichever locals it went, and that a call merely
/// given the exception (a logger) does not. A method the interpretation cannot follow (calli, jmp) is reported, not skipped.
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

    #endregion

    #region Methods

    /// <summary>
    /// For each call or newobj instruction (by index), the flags of the values it takes, the receiver first; null when the
    /// method uses calli or jmp, whose stack effect the interpretation cannot know.
    /// </summary>
    /// <param name="typeFlags">The flags a value has because of its type (an exception type is <see cref="Exception"/>).</param>
    /// <param name="callResult">The flags of a call's result, from the call and the flags of the values it takes.</param>
    /// <param name="seed">The flags of the method's arguments (the receiver first), when a caller's are known.</param>
    public static Dictionary<int, int[]> Arguments(MethodBase method, IReadOnlyList<IlReader.Instruction> instructions, IReadOnlyList<IlReader.Region> regions,
        Func<Type, int> typeFlags, Func<MethodBase, int[], int> callResult, int[] seed = null)
    {
        if (instructions.Any(i => i.OpCode == OpCodes.Calli || i.OpCode == OpCodes.Jmp))
            return null;
        var body = method.GetMethodBody();
        if (body is null || instructions.Count == 0)
            return [];
        var byOffset = instructions.Select((ins, k) => (ins.Offset, k)).ToDictionary(x => x.Offset, x => x.k);
        var localTypes = body.LocalVariables.Select(l => l.LocalType).ToArray();
        var argTypes = (method.IsStatic ? [] : new[] { method.DeclaringType }).Concat(method.GetParameters().Select(p => p.ParameterType)).ToArray();
        var returnsValue = method is MethodInfo info && info.ReturnType != typeof(void);
        var fields = new Dictionary<(Module, int), int>();
        var arguments = new Dictionary<int, int[]>();

        // Fields are followed across the whole method: the interpretation is run again until no field gains a flag.
        for (var pass = 0; pass < 6; pass++)
        {
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
            Enter(0, new State(new Stack<int>(), new int[localTypes.Length], seeded));
            foreach (var region in regions)
                if (byOffset.TryGetValue(region.Start, out var start))
                    Enter(start, new State(new Stack<int>([Exception]), new int[localTypes.Length], [.. seeded]));

            while (work.Count > 0)
            {
                var k = work.Dequeue();
                var ins = instructions[k];
                var state = states[k].Copy();
                var stack = state.Stack;
                var name = ins.OpCode.Name ?? "";
                int Pop() => stack.Count > 0 ? stack.Pop() : 0;
                var falls = true;

                if (ins.Callee is { } callee && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj))
                {
                    var count = callee.GetParameters().Length + (ins.OpCode != OpCodes.Newobj && !callee.IsStatic ? 1 : 0);
                    var taken = new int[count];
                    for (var a = count - 1; a >= 0; a--)
                        taken[a] = Pop();
                    arguments[k] = arguments.TryGetValue(k, out var seen) ? [.. seen.Zip(taken, (x, y) => x | y)] : taken;
                    var returns = ins.OpCode == OpCodes.Newobj ? callee.DeclaringType : (callee as MethodInfo)?.ReturnType;
                    if (returns is not null && returns != typeof(void))
                        stack.Push(callResult(callee, taken) | typeFlags(returns));
                }
                else if (name.StartsWith("ldloc", StringComparison.Ordinal) && !name.StartsWith("ldloca", StringComparison.Ordinal))
                {
                    stack.Push(state.Locals.ElementAtOrDefault(ins.Index) | typeFlags(localTypes.ElementAtOrDefault(ins.Index)));
                }
                else if (name.StartsWith("stloc", StringComparison.Ordinal))
                {
                    var value = Pop();
                    if (ins.Index >= 0 && ins.Index < state.Locals.Length)
                        state.Locals[ins.Index] = value;
                }
                else if (name.StartsWith("ldarg", StringComparison.Ordinal) && !name.StartsWith("ldarga", StringComparison.Ordinal))
                {
                    stack.Push(state.Arguments.ElementAtOrDefault(ins.Index) | typeFlags(argTypes.ElementAtOrDefault(ins.Index)));
                }
                else if (name.StartsWith("starg", StringComparison.Ordinal))
                {
                    var value = Pop();
                    if (ins.Index >= 0 && ins.Index < state.Arguments.Length)
                        state.Arguments[ins.Index] = value;
                }
                else if (ins.Member is FieldInfo field && name is "ldfld" or "ldsfld")
                {
                    if (name == "ldfld")
                        Pop();
                    stack.Push(fields.GetValueOrDefault((field.Module, field.MetadataToken)) | typeFlags(field.FieldType));
                }
                else if (ins.Member is FieldInfo stored && name is "stfld" or "stsfld")
                {
                    var value = Pop();
                    if (name == "stfld")
                        Pop();
                    var key = (stored.Module, stored.MetadataToken);
                    fields[key] = fields.GetValueOrDefault(key) | value;
                }
                else if (ins.OpCode == OpCodes.Dup)
                {
                    var top = Pop();
                    stack.Push(top);
                    stack.Push(top);
                }
                else if (ins.OpCode == OpCodes.Isinst || ins.OpCode == OpCodes.Castclass || ins.OpCode == OpCodes.Box || ins.OpCode == OpCodes.Unbox_Any)
                {
                    stack.Push(Pop() | (ins.Member is Type type ? typeFlags(type) : 0));
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
                        stack.Push(0);
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

        return arguments;
    }

    private static int Count(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0 or StackBehaviour.Varpop or StackBehaviour.Varpush => 0,
        _ => behaviour.ToString().Split('_').Length
    };

    #endregion

    #region Types

    /// <summary>The flags on the stack and in the locals and arguments at one instruction.</summary>
    private sealed class State(Stack<int> stack, int[] locals, int[] arguments)
    {
        public Stack<int> Stack { get; } = stack;

        public int[] Locals { get; } = locals;

        public int[] Arguments { get; } = arguments;

        public State Copy() => new(new Stack<int>(Stack.Reverse()), [.. Locals], [.. Arguments]);

        /// <summary>Adds the other state's flags to this one's; true when any flag was added.</summary>
        public bool Merge(State other)
        {
            var changed = false;
            var mine = Stack.ToArray();
            var theirs = other.Stack.ToArray();
            for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
            {
                if ((mine[i] | theirs[i]) == mine[i])
                    continue;
                mine[i] |= theirs[i];
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
