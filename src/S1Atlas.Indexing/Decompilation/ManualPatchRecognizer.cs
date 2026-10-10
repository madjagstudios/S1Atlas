using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using S1Atlas.Core;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Decompilation;

internal sealed record CapturedInstruction(OpCode Opcode, string? Detail, long Number, int Offset);

/// <summary>A forwarding patch or constant target helper, interpreted at each call site.</summary>
internal sealed record PatchHelperBody(
    IReadOnlyList<CapturedInstruction> Instructions,
    IReadOnlySet<int> BranchTargets,
    bool HasThis,
    bool ReturnsTarget = false);

internal sealed record ManualPatchAnalysis(
    IReadOnlyList<ManagedPatchFact> Patches,
    IReadOnlyList<ManagedReflectionFact> Reflections,
    IReadOnlySet<string> InlinedHelpers);

/// <summary>
/// Recognizes constant manual Harmony patches in one method body:
/// harmony.Patch(AccessTools.Method(...), prefix/postfix/transpiler/finalizer:
/// new HarmonyMethod(...), ...). A straight-line abstract interpreter tracks constant
/// types, strings, and Type arrays; anything else stays unresolved with a reason and
/// is never guessed. Branch instructions and branch-target merge points clear the
/// stack and every local that could hold different values on different paths, so one
/// arm can never leak into the merged flow. A local stored exactly once and never
/// address-taken keeps its value: definite assignment means every read sees that store.
/// Control flow and untracked instructions degrade to unrecognized-manual-shape;
/// non-constant inputs without them degrade to non-constant-arguments.
/// </summary>
internal static class ManualPatchRecognizer
{
    private const string AccessToolsName = "HarmonyLib.AccessTools";
    private const string HarmonyName = "HarmonyLib.Harmony";
    private const string HarmonyMethodName = "HarmonyLib.HarmonyMethod";

    private static readonly HarmonyPatchKind[] KindByPosition =
    [
        HarmonyPatchKind.Prefix,
        HarmonyPatchKind.Postfix,
        HarmonyPatchKind.Transpiler,
        HarmonyPatchKind.Finalizer,
    ];

    public static IReadOnlyList<ManagedPatchFact> Recognize(
        IReadOnlyList<CapturedInstruction> instructions,
        IReadOnlySet<int> branchTargets) => Analyze(instructions, branchTargets).Patches;

    public static ManualPatchAnalysis Analyze(
        IReadOnlyList<CapturedInstruction> instructions,
        IReadOnlySet<int> branchTargets,
        Func<string, PatchHelperBody?>? helpers = null)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(branchTargets);

        var state = new InterpreterState(
            branchTargets, PinnedLocalSlots(instructions), new ArrayTable(),
            arguments: null, hasThis: false, writtenArguments: new HashSet<int>(), helpers);
        state.Run(instructions);
        return new ManualPatchAnalysis(state.Facts, state.Reflections, state.InlinedHelpers);
    }

    public static bool IsConstantTargetHelper(
        IReadOnlyList<CapturedInstruction> instructions,
        IReadOnlySet<int> branchTargets)
    {
        var state = new InterpreterState(
            branchTargets, PinnedLocalSlots(instructions), new ArrayTable(),
            arguments: null, hasThis: false, writtenArguments: new HashSet<int>(), helpers: null,
            captureReturn: true);
        state.Run(instructions);
        return state.Facts.Count == 0 && state.ReturnedTarget is MethodReferenceValue;
    }

    /// <summary>
    /// A forwarding helper passes its own parameters into Harmony.Patch: it reads a
    /// declared parameter and none of its standalone patches is complete. Helpers that
    /// patch constants on their own keep their facts and are never inlined.
    /// </summary>
    public static bool IsForwardingPatchHelper(
        IReadOnlyList<CapturedInstruction> instructions,
        IReadOnlySet<int> branchTargets,
        bool hasThis,
        Func<string, PatchHelperBody?>? targetHelpers = null)
    {
        var firstDeclared = hasThis ? 1 : 0;
        if (!instructions.Any(instruction =>
                TryArgumentSlot(instruction.Opcode, instruction, out var slot) && slot >= firstDeclared))
            return false;
        var patches = Analyze(instructions, branchTargets, targetHelpers).Patches;
        return patches.Count > 0 && patches.All(patch =>
            patch.Reason is not null || patch.TargetSignature is null ||
            patch.PatchMethodType is null || patch.PatchMethodName is null);
    }

    private abstract record StackValue
    {
        public static readonly StackValue Unknown = new UnknownValue();
        public static readonly StackValue Null = new NullValue();

        private sealed record UnknownValue : StackValue;

        private sealed record NullValue : StackValue;
    }

    private sealed record TypeValue(string TypeName) : StackValue;

    private sealed record StringValue(string Text) : StackValue;

    private sealed record IntValue(long Value) : StackValue;

    private sealed record HandleValue(string Identity) : StackValue;

    private sealed record ArrayReference(int Id) : StackValue;

    private sealed record MethodReferenceValue(string Type, string Method, IReadOnlyList<string>? ArgumentTypes) : StackValue;

    private sealed record PatchMethodValue(string? Type, string? Method, IReadOnlyList<string>? ArgumentTypes) : StackValue;

    private sealed class ArrayTable
    {
        private readonly Dictionary<int, TrackedArray> _arrays = [];
        private int _nextId;

        public ArrayReference Add(TrackedArray array)
        {
            var id = _nextId++;
            _arrays[id] = array;
            return new ArrayReference(id);
        }

        public bool TryGet(StackValue value, [NotNullWhen(true)] out TrackedArray? array)
        {
            array = null;
            return value is ArrayReference reference && _arrays.TryGetValue(reference.Id, out array);
        }
    }

    private sealed class TrackedArray
    {
        public TrackedArray(long length, object owner, int epoch)
        {
            Length = length;
            Owner = owner;
            Epoch = epoch;
        }

        public long Length { get; }

        // The interpreter that created the array, and its epoch then. A store from another
        // interpreter or a later epoch may run on only some paths, so it poisons the array.
        public object Owner { get; }

        public int Epoch { get; }

        public List<(long? Index, StackValue Value)> Elements { get; } = [];

        public bool Poisoned { get; set; }
    }

    private sealed class InterpreterState
    {
        private readonly List<StackValue> _stack = [];
        private readonly Dictionary<int, StackValue> _locals = [];
        private readonly List<ManagedPatchFact> _facts = [];
        private readonly List<ManagedReflectionFact> _reflections = [];
        private readonly IReadOnlySet<int> _mergeOffsets;
        private readonly IReadOnlySet<int> _pinnedSlots;
        private readonly ArrayTable _arrayTable;
        private readonly IReadOnlyList<StackValue>? _arguments;
        private readonly bool _hasThis;
        private readonly IReadOnlySet<int> _writtenArguments;
        private readonly Func<string, PatchHelperBody?>? _helpers;
        private readonly HashSet<string> _inlinedHelpers = new(StringComparer.Ordinal);
        private readonly bool _captureReturn;
        private readonly List<StackValue> _returns = [];
        private int _epoch;
        private bool _sawBranch;
        private bool _lostPrecision;

        public InterpreterState(
            IReadOnlySet<int> mergeOffsets,
            IReadOnlySet<int> pinnedSlots,
            ArrayTable arrayTable,
            IReadOnlyList<StackValue>? arguments,
            bool hasThis,
            IReadOnlySet<int> writtenArguments,
            Func<string, PatchHelperBody?>? helpers,
            bool captureReturn = false)
        {
            _mergeOffsets = mergeOffsets;
            _pinnedSlots = pinnedSlots;
            _arrayTable = arrayTable;
            _arguments = arguments;
            _hasThis = hasThis;
            _writtenArguments = writtenArguments;
            _helpers = helpers;
            _captureReturn = captureReturn;
        }

        public IReadOnlySet<string> InlinedHelpers => _inlinedHelpers;

        public IReadOnlyList<ManagedPatchFact> Facts => _facts;
        public IReadOnlyList<ManagedReflectionFact> Reflections => _reflections;

        // Every normal return must carry the same known method. Throwing paths have
        // no return value; unknown or differing returns invalidate the whole helper.
        public StackValue ReturnedTarget => _returns.Count > 0 &&
            _returns[0] is MethodReferenceValue target && _returns.All(value => value == target)
                ? target : StackValue.Unknown;

        public void Run(IReadOnlyList<CapturedInstruction> instructions)
        {
            for (var i = 0; i < instructions.Count; i++)
            {
                if (_stack.Count > 0 && _stack[^1] is MethodReferenceValue &&
                    TryThrowGuard(instructions, i, out var continuation))
                {
                    // `dup; brtrue keep; pop; ...; throw; keep:` leaves the original
                    // lookup on the sole surviving path. Do not merge the throwing
                    // arm into it or clear the stack before its local store.
                    TryPop();
                    i = continuation;
                    Step(instructions[i], preserveStack: true);
                }
                else
                    Step(instructions[i]);
            }
        }

        private bool TryThrowGuard(IReadOnlyList<CapturedInstruction> instructions, int index, out int continuation)
        {
            continuation = -1;
            var branch = instructions[index];
            if (index == 0 || instructions[index - 1].Opcode != OpCodes.Dup ||
                (branch.Opcode != OpCodes.Brtrue && branch.Opcode != OpCodes.Brtrue_S) ||
                _mergeOffsets.Contains(branch.Offset) || index + 1 >= instructions.Count ||
                instructions[index + 1].Opcode != OpCodes.Pop)
                return false;

            // Additional incoming edges or control flow in the throwing arm would
            // make this a real merge. Switch destinations are not captured individually.
            if (instructions.Any(instruction => instruction.Opcode == OpCodes.Switch) ||
                instructions.Count(instruction => IsBranch(instruction.Opcode) && instruction.Number == branch.Number) != 1)
                return false;
            for (var i = index + 1; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                if (instruction.Offset == branch.Number)
                {
                    if (instructions[i - 1].Opcode != OpCodes.Throw)
                        return false;
                    continuation = i;
                    return true;
                }
                if (_mergeOffsets.Contains(instruction.Offset) || IsBranch(instruction.Opcode) ||
                    instruction.Opcode == OpCodes.Ret || instruction.Opcode == OpCodes.Rethrow ||
                    (instruction.Opcode == OpCodes.Throw &&
                        (i + 1 >= instructions.Count || instructions[i + 1].Offset != branch.Number)))
                    return false;
                // Skip only a simple exception construction. Arbitrary calls (including
                // another Harmony.Patch) in the throwing arm still need interpretation.
                if (instruction.Opcode != OpCodes.Throw && !IsThrowGuardOperand(instruction))
                    return false;
            }
            return false;
        }

        private static bool IsThrowGuardOperand(CapturedInstruction instruction)
        {
            var opcode = instruction.Opcode;
            if (opcode == OpCodes.Nop || opcode == OpCodes.Pop || opcode == OpCodes.Ldstr ||
                opcode == OpCodes.Ldtoken || opcode == OpCodes.Ldnull)
                return true;
            if (!TryParseCallIdentity(instruction.Detail, out var owner, out var name, out _, out _))
                return false;
            return ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) &&
                    owner == "System.Type" && name is "GetTypeFromHandle" or "get_FullName") ||
                (opcode == OpCodes.Newobj && name == ".ctor" &&
                    owner.StartsWith("System.", StringComparison.Ordinal) && owner.EndsWith("Exception", StringComparison.Ordinal));
        }

        private void Step(CapturedInstruction instruction, bool preserveStack = false)
        {
            // Values must not flow across a branch merge: whichever arm the linear walk
            // happens to visit last would otherwise win, recording one arm as certain.
            if (!preserveStack && _mergeOffsets.Contains(instruction.Offset))
            {
                Clear();
                _lostPrecision = true;
            }

            var opcode = instruction.Opcode;
            if (opcode == OpCodes.Nop || IsPrefix(opcode))
                return;
            if (opcode == OpCodes.Ldstr)
            {
                Push(instruction.Detail is null ? StackValue.Unknown : new StringValue(instruction.Detail));
                return;
            }
            if (TryLoadConstant(instruction, out var constant))
            {
                Push(constant);
                return;
            }
            if (opcode == OpCodes.Ldnull)
            {
                Push(StackValue.Null);
                return;
            }
            if (opcode == OpCodes.Dup)
            {
                Push(TryPeek());
                return;
            }
            if (opcode == OpCodes.Pop)
            {
                TryPop();
                return;
            }
            if (TryLocalSlot(opcode, instruction, load: true, out var loadSlot))
            {
                if (_locals.TryGetValue(loadSlot, out var loaded))
                    Push(loaded);
                else
                {
                    _lostPrecision = true;
                    Push(StackValue.Unknown);
                }

                return;
            }
            if (TryLocalSlot(opcode, instruction, load: false, out var storeSlot))
            {
                // The stack is empty after every clear, so a known value here was computed
                // in this block from constants. Only a pinned slot may keep it past a branch.
                var stored = TryPop();
                _locals[storeSlot] = _sawBranch && !_pinnedSlots.Contains(storeSlot) ? StackValue.Unknown : stored;
                return;
            }
            if (TryArgumentSlot(opcode, instruction, out var argumentSlot))
            {
                Push(ArgumentValue(argumentSlot));
                return;
            }
            if (IsLoadArgument(opcode))
            {
                Push(StackValue.Unknown);
                return;
            }
            if (opcode == OpCodes.Ldtoken)
            {
                Push(TokenValue(instruction.Detail));
                return;
            }
            if (opcode == OpCodes.Ldsfld)
            {
                Push(StaticFieldValue(instruction.Detail));
                return;
            }
            if (opcode == OpCodes.Stsfld)
            {
                TryPop();
                return;
            }
            if (opcode == OpCodes.Ldfld || opcode == OpCodes.Ldflda)
            {
                TryPop();
                Push(StackValue.Unknown);
                return;
            }
            if (opcode == OpCodes.Stfld)
            {
                TryPop();
                TryPop();
                return;
            }
            if (opcode == OpCodes.Newarr)
            {
                var length = TryPop();
                if (instruction.Detail is null || instruction.Detail.StartsWith("unresolved:", StringComparison.Ordinal) ||
                    length is not IntValue { Value: >= 0 and <= 1024 } size)
                {
                    _lostPrecision = true;
                    Push(StackValue.Unknown);
                    return;
                }

                Push(_arrayTable.Add(new TrackedArray(size.Value, this, _epoch)));
                return;
            }
            if (opcode == OpCodes.Stelem_Ref)
            {
                var value = TryPop();
                var index = TryPop();
                var array = TryPop();
                if (_arrayTable.TryGet(array, out var tracked))
                {
                    if (ReferenceEquals(tracked.Owner, this) && tracked.Epoch == _epoch &&
                        index is IntValue at && at.Value >= 0 && at.Value < tracked.Length &&
                        !tracked.Elements.Any(element => element.Index == at.Value))
                        tracked.Elements.Add((at.Value, value));
                    else
                        tracked.Poisoned = true;
                }

                return;
            }
            if (IsBranch(opcode))
            {
                Clear();
                _sawBranch = true;
                return;
            }
            if (opcode == OpCodes.Ret || opcode == OpCodes.Throw || opcode == OpCodes.Rethrow ||
                opcode == OpCodes.Endfinally || opcode == OpCodes.Endfilter)
            {
                if (opcode == OpCodes.Ret && _captureReturn)
                    _returns.Add(TryPop());
                Clear();
                return;
            }
            if (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj)
            {
                Invoke(instruction, opcode);
                return;
            }
            if (opcode == OpCodes.Ldftn || opcode == OpCodes.Ldvirtftn)
            {
                TryPop();
                Push(StackValue.Unknown);
                return;
            }
            if (IsPassThrough(opcode))
            {
                Push(TryPop());
                return;
            }
            if (TryStackEffect(opcode, out var pop, out var push))
            {
                for (var i = 0; i < pop; i++)
                    TryPop();
                if (push)
                    Push(StackValue.Unknown);
                return;
            }

            Clear();
            _lostPrecision = true;
        }

        private void Invoke(CapturedInstruction instruction, OpCode opcode)
        {
            if (!TryParseCallIdentity(instruction.Detail, out var owner, out var name, out var parameters, out var returnsValue))
            {
                Clear();
                _lostPrecision = true;
                Push(StackValue.Unknown);
                return;
            }

            if (string.Equals(owner, "System.Type", StringComparison.Ordinal) &&
                string.Equals(name, "GetTypeFromHandle", StringComparison.Ordinal))
            {
                var handle = TryPop();
                if (handle is HandleValue token && !token.Identity.Contains("::", StringComparison.Ordinal))
                    Push(new TypeValue(token.Identity));
                else
                {
                    _lostPrecision = true;
                    Push(StackValue.Unknown);
                }

                return;
            }

            if (string.Equals(owner, AccessToolsName, StringComparison.Ordinal) &&
                TryReflectionKind(name, out var accessKind))
            {
                Push(AccessToolsResult(accessKind, parameters));
                return;
            }

            if (string.Equals(owner, "System.Type", StringComparison.Ordinal) &&
                name is "GetMethod" or "GetField" or "GetProperty")
            {
                Push(TypeLookupResult(name, parameters));
                return;
            }

            if (string.Equals(owner, HarmonyMethodName, StringComparison.Ordinal) &&
                string.Equals(name, ".ctor", StringComparison.Ordinal))
            {
                Push(HarmonyMethodResult(parameters));
                return;
            }

            if (string.Equals(owner, HarmonyName, StringComparison.Ordinal) &&
                string.Equals(name, "Patch", StringComparison.Ordinal))
            {
                PatchCall(parameters, opcode);
                Push(StackValue.Unknown);
                return;
            }

            if (opcode == OpCodes.Call && _helpers is not null && _helpers(instruction.Detail!) is { } helper)
            {
                InlineHelper(instruction.Detail!, helper, parameters.Count, returnsValue);
                return;
            }

            // Newobj carries only the constructor arguments; the instance does not exist yet,
            // so unlike Call/.ctor and Callvirt there is no receiver to pop.
            var pop = parameters.Count + (opcode == OpCodes.Callvirt ||
                (opcode == OpCodes.Call && string.Equals(name, ".ctor", StringComparison.Ordinal)) ? 1 : 0);
            for (var i = 0; i < pop; i++)
                TryPop();
            if (opcode == OpCodes.Newobj || returnsValue)
                Push(StackValue.Unknown);
        }

        private StackValue ArgumentValue(int slot)
        {
            if (_arguments is null || _writtenArguments.Contains(slot))
                return StackValue.Unknown;
            var index = slot - (_hasThis ? 1 : 0);
            return index >= 0 && index < _arguments.Count ? _arguments[index] : StackValue.Unknown;
        }

        private void InlineHelper(string identity, PatchHelperBody helper, int parameterCount, bool returnsValue)
        {
            var arguments = new StackValue[parameterCount];
            for (var i = parameterCount - 1; i >= 0; i--)
                arguments[i] = TryPop();
            if (helper.HasThis)
                TryPop();

            // One level only: the helper sees this caller's values through its parameters
            // but cannot inline further, so chains and recursion stay bounded. Its patch
            // facts belong to this caller, which is where an unresolved one is reported.
            var inlined = new InterpreterState(
                helper.BranchTargets, PinnedLocalSlots(helper.Instructions), _arrayTable,
                arguments, helper.HasThis, WrittenArgumentSlots(helper.Instructions), helpers: null,
                captureReturn: helper.ReturnsTarget);
            inlined.Run(helper.Instructions);
            _facts.AddRange(inlined.Facts);
            if (!helper.ReturnsTarget)
                _inlinedHelpers.Add(identity);
            if (returnsValue)
                Push(helper.ReturnsTarget ? inlined.ReturnedTarget : StackValue.Unknown);
        }

        private StackValue AccessToolsResult(ManagedReflectionKind kind, IReadOnlyList<string> parameters)
        {
            if (kind == ManagedReflectionKind.Method && parameters.Count == 4 &&
                parameters[0] == "System.Type" && parameters[1] == "System.String" &&
                parameters[2] == "System.Type[]" && parameters[3] == "System.Type[]")
            {
                // Generic instantiation is outside the fact's identity. Only an
                // explicit null lets the ordinary method signature stay exact.
                if (TryPop() == StackValue.Null)
                    return AccessToolsResult(kind, parameters.Take(3).ToArray());
                for (var i = 0; i < 3; i++)
                    TryPop();
                return StackValue.Unknown;
            }

            if (kind == ManagedReflectionKind.Constructor && parameters.Count == 3 &&
                parameters[0] == "System.Type" && parameters[1] == "System.Type[]" &&
                parameters[2] == "System.Boolean")
            {
                var staticFlag = TryPop();
                var types = TryPop();
                var type = TryPop();
                var argumentTypes = ResolveTypeArray(types);
                if (type is TypeValue target && argumentTypes is not null && staticFlag is IntValue { Value: 0 or 1 } flag)
                    _reflections.Add(new ManagedReflectionFact(
                        target.TypeName, flag.Value == 1 ? ".cctor" : ".ctor", kind, argumentTypes));
                return StackValue.Unknown;
            }

            if (kind == ManagedReflectionKind.Constructor && parameters.Count == 2 &&
                parameters[0] == "System.Type" && parameters[1] == "System.Type[]")
            {
                var types = TryPop();
                var type = TryPop();
                var argumentTypes = ResolveTypeArray(types);
                if (type is TypeValue target && argumentTypes is not null)
                    _reflections.Add(new ManagedReflectionFact(target.TypeName, ".ctor", kind, argumentTypes));
                return StackValue.Unknown;
            }

            if (parameters.Count == 2 && parameters[0] == "System.Type" && parameters[1] == "System.String")
            {
                var memberName = TryPop();
                var type = TryPop();
                if (type is TypeValue target && memberName is StringValue member)
                {
                    _reflections.Add(new ManagedReflectionFact(target.TypeName, member.Text, kind));
                    // Accessor lookups return the accessor MethodInfo, so they can be patched.
                    return kind switch
                    {
                        ManagedReflectionKind.Method => new MethodReferenceValue(target.TypeName, member.Text, null),
                        ManagedReflectionKind.PropertyGetter => new MethodReferenceValue(target.TypeName, "get_" + member.Text, null),
                        ManagedReflectionKind.PropertySetter => new MethodReferenceValue(target.TypeName, "set_" + member.Text, null),
                        _ => StackValue.Unknown
                    };
                }
                return StackValue.Unknown;
            }

            if (kind == ManagedReflectionKind.Method && parameters.Count == 3 &&
                parameters[0] == "System.Type" && parameters[1] == "System.String" && parameters[2] == "System.Type[]")
            {
                var types = TryPop();
                var memberName = TryPop();
                var type = TryPop();
                if (type is not TypeValue target || memberName is not StringValue member)
                    return StackValue.Unknown;
                // Only an explicit null widens to an unparameterized target; an
                // unreadable array stays unknown so it can never resolve by luck.
                if (types == StackValue.Null)
                {
                    _reflections.Add(new ManagedReflectionFact(target.TypeName, member.Text, kind));
                    return new MethodReferenceValue(target.TypeName, member.Text, null);
                }
                var argumentTypes = ResolveTypeArray(types);
                if (argumentTypes is null)
                    return StackValue.Unknown;
                _reflections.Add(new ManagedReflectionFact(target.TypeName, member.Text, kind, argumentTypes));
                return new MethodReferenceValue(target.TypeName, member.Text, argumentTypes);
            }

            for (var i = 0; i < parameters.Count; i++)
                TryPop();
            _lostPrecision = true;
            return StackValue.Unknown;
        }

        private StackValue TypeLookupResult(string name, IReadOnlyList<string> parameters)
        {
            var arguments = new StackValue[parameters.Count];
            for (var i = parameters.Count - 1; i >= 0; i--)
                arguments[i] = TryPop();
            var receiver = TryPop();
            if (receiver is not TypeValue target || parameters.Count == 0 ||
                parameters[0] != "System.String" || arguments[0] is not StringValue member)
                return StackValue.Unknown;
            var kind = name switch
            {
                "GetMethod" => ManagedReflectionKind.Method,
                "GetField" => ManagedReflectionKind.Field,
                _ => ManagedReflectionKind.Property
            };
            if (parameters.Count == 1)
            {
                _reflections.Add(new ManagedReflectionFact(target.TypeName, member.Text, kind));
                return kind == ManagedReflectionKind.Method
                    ? new MethodReferenceValue(target.TypeName, member.Text, null)
                    : StackValue.Unknown;
            }

            if (parameters.Count == 2 && parameters[1] == "System.Reflection.BindingFlags")
            {
                _reflections.Add(new ManagedReflectionFact(target.TypeName, member.Text, kind));
                return kind == ManagedReflectionKind.Method
                    ? new MethodReferenceValue(target.TypeName, member.Text, null)
                    : StackValue.Unknown;
            }

            if (kind == ManagedReflectionKind.Method && parameters.Count == 2 &&
                parameters[1] == "System.Type[]")
            {
                var argumentTypes = ResolveTypeArray(arguments[1]);
                if (argumentTypes is not null)
                {
                    _reflections.Add(new ManagedReflectionFact(target.TypeName, member.Text, kind, argumentTypes));
                    return new MethodReferenceValue(target.TypeName, member.Text, argumentTypes);
                }
            }

            return StackValue.Unknown;
        }

        private StackValue HarmonyMethodResult(IReadOnlyList<string> parameters)
        {
            var popped = new List<StackValue>(parameters.Count);
            for (var i = 0; i < parameters.Count; i++)
                popped.Add(TryPop());
            popped.Reverse();

            if (parameters.Count > 0 && string.Equals(parameters[0], "System.Reflection.MethodInfo", StringComparison.Ordinal))
            {
                return popped[0] is MethodReferenceValue method
                    ? new PatchMethodValue(method.Type, method.Method, method.ArgumentTypes)
                    : new PatchMethodValue(null, null, null);
            }

            if (parameters.Count == 2)
            {
                return new PatchMethodValue(
                    popped[0] is TypeValue target ? target.TypeName : null,
                    popped[1] is StringValue method ? method.Text : null,
                    null);
            }

            if (parameters.Count == 3)
            {
                IReadOnlyList<string>? argumentTypes = null;
                if (popped[2] != StackValue.Null)
                {
                    argumentTypes = ResolveTypeArray(popped[2]);
                    if (argumentTypes is null)
                        return new PatchMethodValue(null, null, null);
                }

                return new PatchMethodValue(
                    popped[0] is TypeValue target ? target.TypeName : null,
                    popped[1] is StringValue method ? method.Text : null,
                    argumentTypes);
            }

            return new PatchMethodValue(null, null, null);
        }

        private void PatchCall(IReadOnlyList<string> parameters, OpCode opcode)
        {
            var underflow = _stack.Count < parameters.Count + (opcode == OpCodes.Callvirt ? 1 : 0);
            var popped = new List<StackValue>(parameters.Count);
            for (var i = 0; i < parameters.Count; i++)
                popped.Add(TryPop());
            if (opcode == OpCodes.Callvirt)
                TryPop();
            popped.Reverse();

            if (underflow)
                _lostPrecision = true;
            var reason = _sawBranch || _lostPrecision
                ? HarmonyPatchReasons.UnrecognizedManualShape
                : HarmonyPatchReasons.NonConstantArguments;

            var original = popped.Count > 0 ? popped[0] : StackValue.Unknown;
            var target = original is MethodReferenceValue reference
                ? reference.ArgumentTypes is null
                    ? reference.Type + "::" + reference.Method
                    : reference.Type + "::" + reference.Method + "(" + string.Join(",", reference.ArgumentTypes) + ")"
                : null;

            for (var i = 1; i < popped.Count && i - 1 < KindByPosition.Length; i++)
            {
                if (popped[i] == StackValue.Null)
                    continue;
                // A constant target resolves even when the patch method does not; the edge
                // then hangs on the registering method, so the target is still checked.
                var patch = popped[i] as PatchMethodValue;
                _facts.Add(new ManagedPatchFact(
                    KindByPosition[i - 1],
                    target,
                    target is null ? reason : null,
                    RelationshipEvidence.RecoveredIL,
                    patch?.Type,
                    patch?.Method,
                    patch?.ArgumentTypes));
            }
        }

        private IReadOnlyList<string>? ResolveTypeArray(StackValue value)
        {
            if (value == StackValue.Null)
                return null;
            if (!_arrayTable.TryGet(value, out var tracked) ||
                tracked.Poisoned || tracked.Elements.Count != tracked.Length)
                return null;
            var names = new List<string>(tracked.Elements.Count);
            foreach (var (_, element) in tracked.Elements.OrderBy(element => element.Index ?? long.MaxValue))
            {
                if (element is not TypeValue type)
                    return null;
                names.Add(type.TypeName);
            }

            return names;
        }

        private StackValue TryPeek()
        {
            if (_stack.Count == 0)
            {
                _lostPrecision = true;
                return StackValue.Unknown;
            }

            return _stack[^1];
        }

        private StackValue TryPop()
        {
            if (_stack.Count == 0)
            {
                _lostPrecision = true;
                return StackValue.Unknown;
            }

            var value = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return value;
        }

        private void Push(StackValue value) => _stack.Add(value);

        private void Clear()
        {
            _stack.Clear();
            foreach (var slot in _locals.Keys.Where(slot => !_pinnedSlots.Contains(slot)).ToArray())
                _locals.Remove(slot);
            _epoch++;
        }

        private StackValue StaticFieldValue(string? detail)
        {
            if (detail is not null &&
                SymbolNames.TrySplitMember(detail, out var type, out var tail) &&
                string.Equals(type, "System.Type", StringComparison.Ordinal) &&
                string.Equals(SymbolNames.SimpleName("X::" + tail), "EmptyTypes", StringComparison.Ordinal))
            {
                return _arrayTable.Add(new TrackedArray(0, this, _epoch));
            }

            return StackValue.Unknown;
        }
    }

    private static bool TryReflectionKind(string name, out ManagedReflectionKind kind)
    {
        kind = name switch
        {
            "Method" => ManagedReflectionKind.Method,
            "Field" => ManagedReflectionKind.Field,
            "Property" => ManagedReflectionKind.Property,
            "PropertyGetter" => ManagedReflectionKind.PropertyGetter,
            "PropertySetter" => ManagedReflectionKind.PropertySetter,
            "Constructor" => ManagedReflectionKind.Constructor,
            _ => default
        };
        return name is "Method" or "Field" or "Property" or "PropertyGetter" or "PropertySetter" or "Constructor";
    }

    private static StackValue TokenValue(string? detail)
    {
        if (detail is null || detail.StartsWith("unresolved:", StringComparison.Ordinal))
            return StackValue.Unknown;
        return new HandleValue(detail);
    }

    private static bool TryLoadConstant(CapturedInstruction instruction, out StackValue constant)
    {
        constant = StackValue.Unknown;
        var opcode = instruction.Opcode;
        if (opcode == OpCodes.Ldc_I4_M1) constant = new IntValue(-1);
        else if (opcode == OpCodes.Ldc_I4_0) constant = new IntValue(0);
        else if (opcode == OpCodes.Ldc_I4_1) constant = new IntValue(1);
        else if (opcode == OpCodes.Ldc_I4_2) constant = new IntValue(2);
        else if (opcode == OpCodes.Ldc_I4_3) constant = new IntValue(3);
        else if (opcode == OpCodes.Ldc_I4_4) constant = new IntValue(4);
        else if (opcode == OpCodes.Ldc_I4_5) constant = new IntValue(5);
        else if (opcode == OpCodes.Ldc_I4_6) constant = new IntValue(6);
        else if (opcode == OpCodes.Ldc_I4_7) constant = new IntValue(7);
        else if (opcode == OpCodes.Ldc_I4_8) constant = new IntValue(8);
        else if (opcode == OpCodes.Ldc_I4_S || opcode == OpCodes.Ldc_I4) constant = new IntValue(instruction.Number);
        else if (opcode == OpCodes.Ldc_I8) constant = new IntValue(instruction.Number);
        else if (opcode == OpCodes.Ldc_R4 || opcode == OpCodes.Ldc_R8) constant = StackValue.Unknown;
        else return false;
        return true;
    }

    private static IReadOnlySet<int> PinnedLocalSlots(IReadOnlyList<CapturedInstruction> instructions)
    {
        var stores = new Dictionary<int, int>();
        var addressed = new HashSet<int>();
        foreach (var instruction in instructions)
        {
            if (TryLocalSlot(instruction.Opcode, instruction, load: false, out var slot))
                stores[slot] = stores.GetValueOrDefault(slot) + 1;
            else if (instruction.Opcode == OpCodes.Ldloca || instruction.Opcode == OpCodes.Ldloca_S)
                addressed.Add((int)instruction.Number);
        }

        return stores.Where(entry => entry.Value == 1 && !addressed.Contains(entry.Key))
            .Select(entry => entry.Key)
            .ToHashSet();
    }

    private static bool TryLocalSlot(OpCode opcode, CapturedInstruction instruction, bool load, out int slot)
    {
        slot = 0;
        if (load)
        {
            if (opcode == OpCodes.Ldloc_0) slot = 0;
            else if (opcode == OpCodes.Ldloc_1) slot = 1;
            else if (opcode == OpCodes.Ldloc_2) slot = 2;
            else if (opcode == OpCodes.Ldloc_3) slot = 3;
            else if (opcode == OpCodes.Ldloc_S || opcode == OpCodes.Ldloc) slot = (int)instruction.Number;
            else return false;
            return true;
        }

        if (opcode == OpCodes.Stloc_0) slot = 0;
        else if (opcode == OpCodes.Stloc_1) slot = 1;
        else if (opcode == OpCodes.Stloc_2) slot = 2;
        else if (opcode == OpCodes.Stloc_3) slot = 3;
        else if (opcode == OpCodes.Stloc_S || opcode == OpCodes.Stloc) slot = (int)instruction.Number;
        else return false;
        return true;
    }

    private static bool TryArgumentSlot(OpCode opcode, CapturedInstruction instruction, out int slot)
    {
        slot = 0;
        if (opcode == OpCodes.Ldarg_0) slot = 0;
        else if (opcode == OpCodes.Ldarg_1) slot = 1;
        else if (opcode == OpCodes.Ldarg_2) slot = 2;
        else if (opcode == OpCodes.Ldarg_3) slot = 3;
        else if (opcode == OpCodes.Ldarg_S || opcode == OpCodes.Ldarg) slot = (int)instruction.Number;
        else return false;
        return true;
    }

    // A parameter the helper overwrites or takes the address of no longer holds the
    // caller's value, so it reads as unknown everywhere in the inlined body.
    private static IReadOnlySet<int> WrittenArgumentSlots(IReadOnlyList<CapturedInstruction> instructions) =>
        instructions
            .Where(instruction => instruction.Opcode == OpCodes.Starg || instruction.Opcode == OpCodes.Starg_S ||
                instruction.Opcode == OpCodes.Ldarga || instruction.Opcode == OpCodes.Ldarga_S)
            .Select(instruction => (int)instruction.Number)
            .ToHashSet();

    private static bool IsLoadArgument(OpCode opcode) =>
        opcode == OpCodes.Ldarg_0 || opcode == OpCodes.Ldarg_1 || opcode == OpCodes.Ldarg_2 || opcode == OpCodes.Ldarg_3 ||
        opcode == OpCodes.Ldarg_S || opcode == OpCodes.Ldarg || opcode == OpCodes.Ldarga_S || opcode == OpCodes.Ldarga ||
        opcode == OpCodes.Ldloca_S || opcode == OpCodes.Ldloca;

    private static bool IsPrefix(OpCode opcode) =>
        opcode == OpCodes.Tailcall || opcode == OpCodes.Volatile || opcode == OpCodes.Unaligned ||
        opcode == OpCodes.Constrained || opcode == OpCodes.Readonly;

    private static bool IsBranch(OpCode opcode) =>
        opcode.OperandType == OperandType.InlineBrTarget ||
        opcode.OperandType == OperandType.ShortInlineBrTarget ||
        opcode.OperandType == OperandType.InlineSwitch;

    private static bool IsPassThrough(OpCode opcode) =>
        opcode == OpCodes.Box || opcode == OpCodes.Unbox || opcode == OpCodes.Unbox_Any ||
        opcode == OpCodes.Castclass || opcode == OpCodes.Isinst;

    private static bool TryStackEffect(OpCode opcode, out int pop, out bool push)
    {
        pop = 0;
        push = false;
        if (opcode == OpCodes.Break)
            return true;
        if (opcode == OpCodes.Sizeof || opcode == OpCodes.Arglist || opcode == OpCodes.Localloc)
        {
            pop = opcode == OpCodes.Localloc ? 1 : 0;
            push = true;
            return true;
        }
        if (opcode == OpCodes.Starg || opcode == OpCodes.Starg_S || opcode == OpCodes.Initobj ||
            opcode == OpCodes.Mkrefany || opcode == OpCodes.Stobj)
        {
            pop = opcode == OpCodes.Mkrefany ? 1 : opcode == OpCodes.Stobj ? 2 : 1;
            push = opcode == OpCodes.Mkrefany;
            return true;
        }
        if (opcode == OpCodes.Cpobj || opcode == OpCodes.Cpblk || opcode == OpCodes.Initblk)
        {
            pop = opcode == OpCodes.Cpobj ? 2 : 3;
            return true;
        }
        if (opcode == OpCodes.Ldlen || opcode == OpCodes.Ldind_I1 || opcode == OpCodes.Ldind_U1 ||
            opcode == OpCodes.Ldind_I2 || opcode == OpCodes.Ldind_U2 || opcode == OpCodes.Ldind_I4 ||
            opcode == OpCodes.Ldind_U4 || opcode == OpCodes.Ldind_I8 || opcode == OpCodes.Ldind_I ||
            opcode == OpCodes.Ldind_R4 || opcode == OpCodes.Ldind_R8 || opcode == OpCodes.Ldind_Ref ||
            opcode == OpCodes.Ldobj || opcode == OpCodes.Refanyval || opcode == OpCodes.Refanytype ||
            opcode == OpCodes.Neg || opcode == OpCodes.Not || opcode == OpCodes.Ckfinite ||
            opcode == OpCodes.Conv_I1 || opcode == OpCodes.Conv_I2 ||
            opcode == OpCodes.Conv_I4 || opcode == OpCodes.Conv_I8 || opcode == OpCodes.Conv_R4 ||
            opcode == OpCodes.Conv_R8 || opcode == OpCodes.Conv_U4 || opcode == OpCodes.Conv_U8 ||
            opcode == OpCodes.Conv_R_Un || opcode == OpCodes.Conv_Ovf_I1 || opcode == OpCodes.Conv_Ovf_U1 ||
            opcode == OpCodes.Conv_Ovf_I2 || opcode == OpCodes.Conv_Ovf_U2 || opcode == OpCodes.Conv_Ovf_I4 ||
            opcode == OpCodes.Conv_Ovf_U4 || opcode == OpCodes.Conv_Ovf_I8 || opcode == OpCodes.Conv_Ovf_U8 ||
            opcode == OpCodes.Conv_Ovf_I1_Un || opcode == OpCodes.Conv_Ovf_U1_Un || opcode == OpCodes.Conv_Ovf_I2_Un ||
            opcode == OpCodes.Conv_Ovf_U2_Un || opcode == OpCodes.Conv_Ovf_I4_Un || opcode == OpCodes.Conv_Ovf_U4_Un ||
            opcode == OpCodes.Conv_Ovf_I8_Un || opcode == OpCodes.Conv_Ovf_U8_Un || opcode == OpCodes.Conv_U ||
            opcode == OpCodes.Conv_I || opcode == OpCodes.Conv_Ovf_I || opcode == OpCodes.Conv_Ovf_U ||
            opcode == OpCodes.Conv_Ovf_I_Un || opcode == OpCodes.Conv_Ovf_U_Un)
        {
            pop = 1;
            push = true;
            return true;
        }

        if (opcode == OpCodes.Add || opcode == OpCodes.Sub || opcode == OpCodes.Mul ||
            opcode == OpCodes.Div || opcode == OpCodes.Div_Un || opcode == OpCodes.Rem ||
            opcode == OpCodes.Rem_Un || opcode == OpCodes.And || opcode == OpCodes.Or ||
            opcode == OpCodes.Xor || opcode == OpCodes.Shl || opcode == OpCodes.Shr ||
            opcode == OpCodes.Shr_Un || opcode == OpCodes.Ceq || opcode == OpCodes.Cgt ||
            opcode == OpCodes.Cgt_Un || opcode == OpCodes.Clt || opcode == OpCodes.Clt_Un ||
            opcode == OpCodes.Add_Ovf || opcode == OpCodes.Add_Ovf_Un || opcode == OpCodes.Mul_Ovf ||
            opcode == OpCodes.Mul_Ovf_Un || opcode == OpCodes.Sub_Ovf || opcode == OpCodes.Sub_Ovf_Un ||
            opcode == OpCodes.Ldelem_I1 || opcode == OpCodes.Ldelem_U1 || opcode == OpCodes.Ldelem_I2 ||
            opcode == OpCodes.Ldelem_U2 || opcode == OpCodes.Ldelem_I4 || opcode == OpCodes.Ldelem_U4 ||
            opcode == OpCodes.Ldelem_I8 || opcode == OpCodes.Ldelem_I || opcode == OpCodes.Ldelem_R4 ||
            opcode == OpCodes.Ldelem_R8 || opcode == OpCodes.Ldelem_Ref || opcode == OpCodes.Ldelema)
        {
            pop = 2;
            push = true;
            return true;
        }

        if (opcode == OpCodes.Stind_I1 || opcode == OpCodes.Stind_I2 || opcode == OpCodes.Stind_I4 ||
            opcode == OpCodes.Stind_I8 || opcode == OpCodes.Stind_I || opcode == OpCodes.Stind_R4 ||
            opcode == OpCodes.Stind_R8 || opcode == OpCodes.Stind_Ref)
        {
            pop = 2;
            return true;
        }

        return false;
    }

    private static bool TryParseCallIdentity(
        string? detail,
        out string owner,
        out string name,
        out IReadOnlyList<string> parameters,
        out bool returnsValue)
    {
        owner = string.Empty;
        name = string.Empty;
        parameters = [];
        returnsValue = true;
        if (detail is null || detail.StartsWith("unresolved:", StringComparison.Ordinal))
            return false;
        if (!SymbolNames.TrySplitMember(detail, out owner, out var tail))
            return false;
        var open = tail.IndexOf('(');
        var close = tail.LastIndexOf(')');
        if (open < 1 || close < open || tail.Contains("method ", StringComparison.Ordinal))
            return false;
        name = tail[..open];
        var list = tail[(open + 1)..close];
        parameters = list.Length == 0 ? [] : SplitTopLevel(list);
        if (close + 2 < tail.Length && tail[close + 1] == ':')
            returnsValue = !tail[(close + 2)..].Equals("System.Void", StringComparison.Ordinal);
        return name.Length > 0;
    }

    private static IReadOnlyList<string> SplitTopLevel(string list)
    {
        var parts = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (var c in list)
        {
            if (c is '<' or '(' or '[') depth++;
            if (c is '>' or ')' or ']') depth--;
            if (c == ',' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString());
        return parts;
    }
}
