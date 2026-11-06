using System.Reflection;
using System.Reflection.Emit;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// Reads a compiled method's IL (ARV-282): each instruction with the member it names, and the method's exception regions.
/// Architecture tests read the compiled code rather than the source, so a check cannot be passed by spelling the same call
/// another way (a name qualified with global::, a target-typed new, a filter written across lines, a property pattern).
/// A token that cannot be resolved is marked, never dropped, so a check can refuse to pass by not seeing code.
/// </summary>
internal static class IlReader
{
    #region Fields

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null))
        .GroupBy(o => o.Value)
        .ToDictionary(g => g.Key, g => g.First());

    /// <summary>The short forms that name their local or argument in the op code itself.</summary>
    private static readonly Dictionary<short, int> ImplicitIndexes = new()
    {
        [OpCodes.Ldloc_0.Value] = 0, [OpCodes.Ldloc_1.Value] = 1, [OpCodes.Ldloc_2.Value] = 2, [OpCodes.Ldloc_3.Value] = 3,
        [OpCodes.Stloc_0.Value] = 0, [OpCodes.Stloc_1.Value] = 1, [OpCodes.Stloc_2.Value] = 2, [OpCodes.Stloc_3.Value] = 3,
        [OpCodes.Ldarg_0.Value] = 0, [OpCodes.Ldarg_1.Value] = 1, [OpCodes.Ldarg_2.Value] = 2, [OpCodes.Ldarg_3.Value] = 3
    };

    public const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                         BindingFlags.Static;

    #endregion

    #region Methods

    /// <summary>Every method and constructor a type declares, its compiler-generated nested types' too (lambdas, state machines).</summary>
    public static IEnumerable<MethodBase> MethodsOf(Type type) =>
        type.GetMethods(Declared).Cast<MethodBase>()
            .Concat(type.GetConstructors(Declared))
            .Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(MethodsOf));

    /// <summary>Every type of an assembly, nested ones included; <paramref name="failures"/> counts the types that did not load.</summary>
    public static IReadOnlyList<Type> TypesOf(Assembly assembly, out int failures)
    {
        try
        {
            failures = 0;
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            failures = e.Types.Count(t => t is null);
            return [.. e.Types.Where(t => t is not null)];
        }
    }

    /// <summary>The method's definition, so a constructed generic method is read from the body it shares.</summary>
    public static MethodBase Definition(MethodBase method)
    {
        try
        {
            return method.Module.ResolveMethod(method.MetadataToken) ?? method;
        }
        catch (ArgumentException)
        {
            return method;
        }
    }

    /// <summary>The method's instructions, each with its offset, the member its operand names and the local or argument it names.</summary>
    public static IReadOnlyList<Instruction> Read(MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body is null)
            return [];
        var il = body.GetILAsByteArray() ?? [];
        var typeArgs = method.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null;
        var methodArgs = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null;
        var instructions = new List<Instruction>();
        var i = 0;
        while (i < il.Length)
        {
            var offset = i;
            int value = il[i++];
            if (value == 0xFE)
                value = 0xFE00 | il[i++];
            var opCode = OpCodesByValue[unchecked((short)value)];
            MemberInfo member = null;
            var unresolved = false;
            var index = ImplicitIndexes.GetValueOrDefault(opCode.Value, -1);
            int[] targets = [];
            string literal = null;
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineVar:
                    index = il[i];
                    i += 1;
                    break;
                case OperandType.ShortInlineBrTarget:
                    targets = [i + 1 + (sbyte)il[i]];
                    i += 1;
                    break;
                case OperandType.InlineBrTarget:
                    targets = [i + 4 + BitConverter.ToInt32(il, i)];
                    i += 4;
                    break;
                case OperandType.ShortInlineI:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    index = BitConverter.ToUInt16(il, i);
                    i += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                {
                    var count = BitConverter.ToInt32(il, i);
                    var next = i + 4 + 4 * count;
                    targets = [.. Enumerable.Range(0, count).Select(n => next + BitConverter.ToInt32(il, i + 4 + 4 * n))];
                    i = next;
                    break;
                }
                case OperandType.InlineString:
                    // ARV-287a: the string an ldstr loads, so a check can tell a constant operand from a computed one.
                    literal = ResolveString(method.Module, BitConverter.ToInt32(il, i));
                    unresolved = literal is null;
                    i += 4;
                    break;
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    member = Resolve(method.Module, BitConverter.ToInt32(il, i), typeArgs, methodArgs);
                    unresolved = member is null;
                    i += 4;
                    break;
                default:
                    i += 4;
                    break;
            }

            instructions.Add(new Instruction(offset, opCode, member, index, unresolved, targets, literal));
        }

        return instructions;
    }

    /// <summary>
    /// The method's exception regions: a filter's own code, and each catch handler with the type it catches. A filtered
    /// catch's type is the one its filter tests first (the C# compiler starts a <c>catch (T e) when</c> filter with
    /// <c>isinst T</c>); a filter that does not catches anything (<see cref="object"/>). ARV-287: finally and fault handlers
    /// too, and each region with the try block it handles.
    /// </summary>
    public static IReadOnlyList<Region> Regions(MethodBase method, IReadOnlyList<Instruction> instructions)
    {
        var clauses = method.GetMethodBody()?.ExceptionHandlingClauses ?? [];
        var regions = new List<Region>();
        foreach (var clause in clauses)
        {
            var (tryStart, tryEnd) = (clause.TryOffset, clause.TryOffset + clause.TryLength);
            if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
            {
                var first = instructions.FirstOrDefault(i => i.Offset == clause.FilterOffset);
                var caught = first.OpCode == OpCodes.Isinst && first.Member is Type tested ? tested : typeof(object);
                regions.Add(new Region(RegionKind.Filter, clause.FilterOffset, clause.HandlerOffset, caught, tryStart, tryEnd));
                regions.Add(new Region(RegionKind.Catch, clause.HandlerOffset, clause.HandlerOffset + clause.HandlerLength, caught, tryStart, tryEnd));
            }
            else if (clause.Flags == ExceptionHandlingClauseOptions.Clause)
            {
                regions.Add(new Region(RegionKind.Catch, clause.HandlerOffset, clause.HandlerOffset + clause.HandlerLength, clause.CatchType, tryStart, tryEnd));
            }
            else if (clause.Flags.HasFlag(ExceptionHandlingClauseOptions.Finally) || clause.Flags.HasFlag(ExceptionHandlingClauseOptions.Fault))
            {
                var kind = clause.Flags.HasFlag(ExceptionHandlingClauseOptions.Finally) ? RegionKind.Finally : RegionKind.Fault;
                regions.Add(new Region(kind, clause.HandlerOffset, clause.HandlerOffset + clause.HandlerLength, null, tryStart, tryEnd));
            }
        }

        return regions;
    }

    /// <summary>The type of the value an instruction loads (a local, an argument, a field or a call's result), or null.</summary>
    public static Type LoadedType(MethodBase method, Instruction load)
    {
        var name = load.OpCode.Name ?? "";
        if (name.StartsWith("ldloc", StringComparison.Ordinal) && !name.StartsWith("ldloca", StringComparison.Ordinal) && load.Index >= 0)
            return method.GetMethodBody()?.LocalVariables.ElementAtOrDefault(load.Index)?.LocalType;
        if (name.StartsWith("ldarg", StringComparison.Ordinal) && !name.StartsWith("ldarga", StringComparison.Ordinal) && load.Index >= 0)
        {
            var index = method.IsStatic ? load.Index : load.Index - 1;
            return index < 0 ? method.DeclaringType : method.GetParameters().ElementAtOrDefault(index)?.ParameterType;
        }

        return load.Member switch
        {
            FieldInfo field when name is "ldfld" or "ldsfld" => field.FieldType,
            MethodInfo call when name is "call" or "callvirt" => call.ReturnType,
            _ => null
        };
    }

    private static MemberInfo Resolve(Module module, int token, Type[] typeArgs, Type[] methodArgs)
    {
        try
        {
            return module.ResolveMember(token, typeArgs, methodArgs);
        }
        catch (Exception e) when (e is ArgumentException or TypeLoadException or FileNotFoundException or MissingMemberException or BadImageFormatException)
        {
            return null;
        }
    }

    private static string ResolveString(Module module, int token)
    {
        try
        {
            return module.ResolveString(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    #endregion

    #region Types

    /// <summary>
    /// One IL instruction: its offset, its op code, the member its operand names (if any), the local or argument it names
    /// (or -1), whether its token could not be resolved, the offsets a branch or switch goes to, and the string an ldstr loads.
    /// </summary>
    public readonly record struct Instruction(int Offset, OpCode OpCode, MemberInfo Member, int Index, bool Unresolved, int[] Targets, string Literal = null)
    {
        /// <summary>The method or constructor the instruction calls, creates with or loads a pointer to.</summary>
        public MethodBase Callee => OpCode.OperandType == OperandType.InlineMethod ? Member as MethodBase : null;
    }

    /// <summary>
    /// A filter's code or a handler's (a catch, a finally, a fault), from <see cref="Start"/> up to <see cref="End"/>, with the
    /// type a catch catches, and the try block it handles (<see cref="TryStart"/> up to <see cref="TryEnd"/>).
    /// </summary>
    public readonly record struct Region(RegionKind Kind, int Start, int End, Type CatchType, int TryStart = 0, int TryEnd = 0)
    {
        public bool Holds(int offset) => offset >= Start && offset < End;

        /// <summary>Whether an instruction at <paramref name="offset"/> is in the try block this region handles.</summary>
        public bool InTry(int offset) => offset >= TryStart && offset < TryEnd;

        /// <summary>A filter or a catch: code that runs with the exception.</summary>
        public bool Catches => Kind is RegionKind.Filter or RegionKind.Catch;
    }

    public enum RegionKind
    {
        Filter,
        Catch,
        Finally,
        Fault
    }

    /// <summary>
    /// The methods a virtual or interface call can run among <paramref name="types"/>: overrides and interface
    /// implementations, indexed once, so a walk follows a call through dispatch as well as a direct one.
    /// </summary>
    public sealed class Dispatch
    {
        private readonly Dictionary<(Module, int), List<MethodBase>> targets = [];

        /// <summary>Types whose interfaces or interface maps could not be read (each would hide its implementations).</summary>
        public int Skipped { get; private set; }

        public Dispatch(IEnumerable<Type> types)
        {
            foreach (var type in types.Where(t => !t.IsInterface))
            {
                foreach (var method in type.GetMethods(Declared).Where(m => m.IsVirtual))
                {
                    var root = method.GetBaseDefinition();
                    if (root != method && root.DeclaringType is not null)
                        Add(root, method);
                }

                Type[] interfaces;
                try
                {
                    interfaces = type.GetInterfaces();
                }
                catch (TypeLoadException)
                {
                    Skipped++;
                    continue;
                }

                foreach (var contract in interfaces)
                {
                    InterfaceMapping map;
                    try
                    {
                        map = type.GetInterfaceMap(contract);
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or TypeLoadException)
                    {
                        Skipped++;
                        continue;
                    }

                    for (var k = 0; k < map.InterfaceMethods.Length; k++)
                        if (map.TargetMethods[k] is { } target && target.DeclaringType == type)
                            Add(map.InterfaceMethods[k], target);
                }
            }
        }

        /// <summary>The overrides and implementations of <paramref name="callee"/> (none for a call that is not dispatched).</summary>
        public IReadOnlyList<MethodBase> Of(MethodBase callee)
        {
            if (callee is not MethodInfo method || !method.IsVirtual)
                return [];
            var root = method.DeclaringType is { IsInterface: true } ? method : method.GetBaseDefinition();
            return targets.TryGetValue((root.Module, root.MetadataToken), out var found) ? found : [];
        }

        private void Add(MethodBase declared, MethodBase target)
        {
            var key = (declared.Module, declared.MetadataToken);
            if (!targets.TryGetValue(key, out var list))
                targets[key] = list = [];
            list.Add(target);
        }
    }

    #endregion
}
