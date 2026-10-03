using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using S1Atlas.Core;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Decompilation;

internal sealed record CapturedInstruction(OpCode Opcode, string? Detail, long Number, int Offset);

/// <summary>
/// Recognizes constant manual Harmony patches in one method body:
/// harmony.Patch(AccessTools.Method(...), prefix/postfix/transpiler/finalizer:
/// new HarmonyMethod(...), ...). A straight-line abstract interpreter tracks constant
/// types, strings, and Type arrays; anything else stays unresolved with a reason and
/// is never guessed. Branch instructions and branch-target merge points clear the
/// tracked state, so values from one arm can never leak into the merged flow.
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
        IReadOnlySet<int> branchTargets)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(branchTargets);

        var state = new InterpreterState(branchTargets);
        foreach (var instruction in instructions)
            state.Step(instruction);
        return state.Facts;
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

    private sealed class TrackedArray
    {
        public List<(long? Index, StackValue Value)> Elements { get; } = [];

        public bool Poisoned { get; set; }
    }

    private sealed class InterpreterState
    {
        private readonly List<StackValue> _stack = [];
        private readonly Dictionary<int, StackValue> _locals = [];
        private readonly Dictionary<int, TrackedArray> _arrays = [];
        private readonly List<ManagedPatchFact> _facts = [];
        private readonly IReadOnlySet<int> _mergeOffsets;
        private int _nextArrayId;
        private bool _sawBranch;
        private bool _lostPrecision;

        public InterpreterState(IReadOnlySet<int> mergeOffsets)
        {
            _mergeOffsets = mergeOffsets;
        }

        public IReadOnlyList<ManagedPatchFact> Facts => _facts;

        public void Step(CapturedInstruction instruction)
        {
            // Values must not flow across a branch merge: whichever arm the linear walk
            // happens to visit last would otherwise win, recording one arm as certain.
            if (_mergeOffsets.Contains(instruction.Offset))
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
                _locals[storeSlot] = _sawBranch ? StackValue.Unknown : TryPop();
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
                TryPop();
                if (instruction.Detail is null || instruction.Detail.StartsWith("unresolved:", StringComparison.Ordinal))
                {
                    _lostPrecision = true;
                    Push(StackValue.Unknown);
                    return;
                }

                var id = _nextArrayId++;
                _arrays[id] = new TrackedArray();
                Push(new ArrayReference(id));
                return;
            }
            if (opcode == OpCodes.Stelem_Ref)
            {
                var value = TryPop();
                var index = TryPop();
                var array = TryPop();
                if (array is ArrayReference reference && _arrays.TryGetValue(reference.Id, out var tracked))
                {
                    if (index is IntValue at)
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
                string.Equals(name, "Method", StringComparison.Ordinal))
            {
                Push(AccessToolsResult(parameters));
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

            // Newobj carries only the constructor arguments; the instance does not exist yet,
            // so unlike Call/.ctor and Callvirt there is no receiver to pop.
            var pop = parameters.Count + (opcode == OpCodes.Callvirt ||
                (opcode == OpCodes.Call && string.Equals(name, ".ctor", StringComparison.Ordinal)) ? 1 : 0);
            for (var i = 0; i < pop; i++)
                TryPop();
            if (opcode == OpCodes.Newobj || returnsValue)
                Push(StackValue.Unknown);
        }

        private StackValue AccessToolsResult(IReadOnlyList<string> parameters)
        {
            if (parameters.Count == 2)
            {
                var name = TryPop();
                var type = TryPop();
                if (type is TypeValue target && name is StringValue method)
                    return new MethodReferenceValue(target.TypeName, method.Text, null);
                return StackValue.Unknown;
            }

            if (parameters.Count == 3)
            {
                var types = TryPop();
                var name = TryPop();
                var type = TryPop();
                if (type is not TypeValue target || name is not StringValue method)
                    return StackValue.Unknown;
                // Only an explicit null widens to an unparameterized target; an
                // unreadable array stays unknown so it can never resolve by luck.
                if (types == StackValue.Null)
                    return new MethodReferenceValue(target.TypeName, method.Text, null);
                var argumentTypes = ResolveTypeArray(types);
                return argumentTypes is null
                    ? StackValue.Unknown
                    : new MethodReferenceValue(target.TypeName, method.Text, argumentTypes);
            }

            for (var i = 0; i < parameters.Count; i++)
                TryPop();
            _lostPrecision = true;
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
                if (popped[i] is not PatchMethodValue patch)
                {
                    _facts.Add(new ManagedPatchFact(KindByPosition[i - 1], target, reason, RelationshipEvidence.RecoveredIL));
                    continue;
                }

                if (target is not null && patch.Type is not null && patch.Method is not null)
                {
                    _facts.Add(new ManagedPatchFact(
                        KindByPosition[i - 1],
                        target,
                        null,
                        RelationshipEvidence.RecoveredIL,
                        patch.Type,
                        patch.Method,
                        patch.ArgumentTypes));
                    continue;
                }

                _facts.Add(new ManagedPatchFact(
                    KindByPosition[i - 1],
                    target,
                    reason,
                    RelationshipEvidence.RecoveredIL,
                    patch.Type,
                    patch.Method,
                    patch.ArgumentTypes));
            }
        }

        private IReadOnlyList<string>? ResolveTypeArray(StackValue value)
        {
            if (value == StackValue.Null)
                return null;
            if (value is not ArrayReference reference || !_arrays.TryGetValue(reference.Id, out var tracked) || tracked.Poisoned)
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
            _locals.Clear();
        }

        private StackValue StaticFieldValue(string? detail)
        {
            if (detail is not null &&
                SymbolNames.TrySplitMember(detail, out var type, out var tail) &&
                string.Equals(type, "System.Type", StringComparison.Ordinal) &&
                string.Equals(SymbolNames.SimpleName("X::" + tail), "EmptyTypes", StringComparison.Ordinal))
            {
                var id = _nextArrayId++;
                _arrays[id] = new TrackedArray();
                return new ArrayReference(id);
            }

            return StackValue.Unknown;
        }
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
