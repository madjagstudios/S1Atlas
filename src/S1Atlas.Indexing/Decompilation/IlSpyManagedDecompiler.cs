using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using UniversalAssemblyResolver = ICSharpCode.Decompiler.Metadata.UniversalAssemblyResolver;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Decompilation;

public sealed class IlSpyManagedDecompiler : IManagedDecompiler
{
    private static readonly OpCode[] OneByteOpCodes = CreateOneByteOpCodes();
    private static readonly OpCode[] TwoByteOpCodes = CreateTwoByteOpCodes();
    private static readonly BodyRecoveryClassifier BodyClassifier = new();

    public Task<ManagedDecompilation> DecompileAsync(
        string assemblyPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The managed assembly was not found.", fullPath);
        }

        var resolver = new UniversalAssemblyResolver(
            fullPath,
            throwOnError: false,
            targetFramework: ".NETCoreApp,Version=8.0",
            runtimePack: "Microsoft.NETCore.App",
            PEStreamOptions.Default,
            MetadataReaderOptions.Default);
        var source = new CSharpDecompiler(fullPath, resolver, new DecompilerSettings())
            .DecompileWholeModuleAsString();
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = File.OpenRead(fullPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException("The input is not a managed assembly.");
        }

        var metadata = peReader.GetMetadataReader();
        var types = metadata.TypeDefinitions
            .Select(handle => ReadType(metadata, peReader, handle))
            .Where(type => !string.Equals(type.Name, "<Module>", StringComparison.Ordinal))
            .ToArray();

        return Task.FromResult(new ManagedDecompilation(fullPath, source, types));
    }

    private static ManagedTypeFacts ReadType(
        MetadataReader metadata,
        PEReader peReader,
        TypeDefinitionHandle typeHandle)
    {
        var definition = metadata.GetTypeDefinition(typeHandle);
        var name = metadata.GetString(definition.Name);
        var @namespace = metadata.GetString(definition.Namespace);
        var fullName = GetTypeName(metadata, typeHandle);
        var baseType = definition.BaseType.IsNil ? null : GetTypeName(metadata, definition.BaseType);
        var interfaces = definition.GetInterfaceImplementations()
            .Select(handle => GetTypeName(metadata, metadata.GetInterfaceImplementation(handle).Interface))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var members = new List<ManagedMemberFacts>();
        var typeProvider = new MetadataTypeNameProvider();
        foreach (var fieldHandle in definition.GetFields())
        {
            var field = metadata.GetFieldDefinition(fieldHandle);
            var valueType = field.DecodeSignature(typeProvider, null);
            members.Add(new ManagedMemberFacts(
                metadata.GetString(field.Name),
                ManagedMemberKind.Field,
                CanonicalSignatureRenderer.RenderType(valueType) + " " + metadata.GetString(field.Name),
                false,
                [],
                ValueType: valueType,
                IsPublic: IsPublic(field.Attributes)));
        }

        foreach (var propertyHandle in definition.GetProperties())
        {
            var property = metadata.GetPropertyDefinition(propertyHandle);
            var signature = property.DecodeSignature(typeProvider, null);
            var propertyName = metadata.GetString(property.Name);
            var parameterTypes = signature.ParameterTypes.ToArray();
            members.Add(new ManagedMemberFacts(
                propertyName,
                ManagedMemberKind.Property,
                CanonicalPropertySignature(propertyName, signature.ReturnType, parameterTypes),
                false,
                [],
                ParameterTypes: parameterTypes,
                ValueType: signature.ReturnType,
                IsPublic: IsPublic(metadata, property.GetAccessors().Getter, property.GetAccessors().Setter)));
        }

        foreach (var eventHandle in definition.GetEvents())
        {
            var @event = metadata.GetEventDefinition(eventHandle);
            var valueType = GetTypeName(metadata, @event.Type);
            var eventName = metadata.GetString(@event.Name);
            members.Add(new ManagedMemberFacts(
                eventName,
                ManagedMemberKind.Event,
                CanonicalSignatureRenderer.RenderType(valueType) + " " + eventName,
                false,
                [],
                ValueType: valueType,
                IsPublic: IsPublic(metadata, @event.GetAccessors().Adder, @event.GetAccessors().Remover, @event.GetAccessors().Raiser)));
        }

        foreach (var methodHandle in definition.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            var methodName = metadata.GetString(method.Name);
            var kind = methodName switch
            {
                ".ctor" or ".cctor" => ManagedMemberKind.Constructor,
                _ => ManagedMemberKind.Method
            };
            var methodSignature = method.DecodeSignature(typeProvider, null);
            var parameterTypes = methodSignature.ParameterTypes.ToArray();
            var genericParameterCount = methodSignature.GenericParameterCount;
            var signature = CanonicalSignatureRenderer.RenderMethod(
                fullName,
                methodName,
                methodSignature.ReturnType,
                parameterTypes,
                genericParameterCount);
            var hasBody = method.RelativeVirtualAddress != 0;
            var bodyAnalysis = hasBody
                ? ReadBodyAnalysis(metadata, peReader, method.RelativeVirtualAddress)
                : BodyAnalysis.Empty;
            var bodyFacts = new ManagedMethodBodyFacts(
                hasBody,
                IsNoBodyByDesign(method),
                bodyAnalysis.IlByteCount,
                bodyAnalysis.InstructionCount,
                bodyAnalysis.References.Count,
                bodyAnalysis.MatchesVerifiedStubPattern,
                bodyAnalysis.MatchesInteropWrapperPattern);
            var bodyRecoveryStatus = BodyClassifier.Classify(bodyFacts);

            members.Add(new ManagedMemberFacts(
                methodName,
                kind,
                signature,
                hasBody,
                bodyAnalysis.References,
                parameterTypes,
                methodSignature.ReturnType,
                GenericParameterCount: genericParameterCount,
                BodyFacts: bodyFacts,
                BodyRecoveryStatus: bodyRecoveryStatus,
                IsPublic: IsPublic(method.Attributes)));
        }

        return new ManagedTypeFacts(fullName, @namespace, name, baseType, interfaces, members);
    }

    private static bool IsNoBodyByDesign(MethodDefinition method) =>
        (method.Attributes & MethodAttributes.Abstract) != 0 ||
        (method.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
        (method.ImplAttributes & MethodImplAttributes.Runtime) != 0 ||
        (method.ImplAttributes & MethodImplAttributes.InternalCall) != 0;

    private static bool IsPublic(FieldAttributes attributes) =>
        (attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;

    private static bool IsPublic(MethodAttributes attributes) =>
        (attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;

    private static bool IsPublic(
        MetadataReader metadata,
        params MethodDefinitionHandle[] accessors) =>
        accessors.Any(accessor =>
            !accessor.IsNil && IsPublic(metadata.GetMethodDefinition(accessor).Attributes));

    private static string CanonicalPropertySignature(string name, string valueType, IReadOnlyList<string> parameterTypes) =>
        parameterTypes.Count == 0
            ? CanonicalSignatureRenderer.RenderType(valueType) + " " + name
            : CanonicalSignatureRenderer.RenderType(valueType) + " " + name + "(" + string.Join(",", parameterTypes.Select(CanonicalSignatureRenderer.RenderType)) + ")";

    private static BodyAnalysis ReadBodyAnalysis(
        MetadataReader metadata,
        PEReader peReader,
        int relativeVirtualAddress)
    {
        var body = peReader.GetMethodBody(relativeVirtualAddress);
        var il = body.GetILBytes() ?? [];
        var references = new List<ManagedReferenceFact>();
        var opcodes = new List<OpCode>();
        var typeProvider = new MetadataTypeNameProvider();
        var offset = 0;

        while (offset < il.Length)
        {
            var opcode = ReadOpCode(il, ref offset);
            opcodes.Add(opcode);
            var operandOffset = offset;
            switch (opcode.OperandType)
            {
                case OperandType.InlineMethod:
                    {
                        var token = BitConverter.ToInt32(il, offset);
                        offset += 4;
                        references.Add(new ManagedReferenceFact(
                            opcode == OpCodes.Newobj ? ManagedReferenceKind.Constructs : ManagedReferenceKind.Calls,
                            GetMemberIdentity(metadata, token, typeProvider)));
                        break;
                    }
                case OperandType.InlineField:
                    {
                        var token = BitConverter.ToInt32(il, offset);
                        offset += 4;
                        references.Add(new ManagedReferenceFact(
                            opcode is { } op && (op == OpCodes.Stfld || op == OpCodes.Stsfld)
                                ? ManagedReferenceKind.WritesField
                                : ManagedReferenceKind.ReadsField,
                            GetMemberIdentity(metadata, token, typeProvider)));
                        break;
                    }
                default:
                    offset = operandOffset + OperandSize(opcode.OperandType, il, operandOffset);
                    break;
            }
        }

        return new BodyAnalysis(
            il.Length,
            opcodes.Count,
            references,
            MatchesVerifiedThrowStub(opcodes, references),
            references.Any(reference =>
                reference.Kind == ManagedReferenceKind.Calls &&
                IsInteropRuntimeInvokeTarget(reference.Target)));
    }

    private static bool IsInteropRuntimeInvokeTarget(string target)
    {
        const string marker = "::il2cpp_runtime_invoke";
        var start = target.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        var end = start + marker.Length;
        return end == target.Length || target[end] is '(' or '_';
    }

    private static bool MatchesVerifiedThrowStub(
        IReadOnlyList<OpCode> opcodes,
        IReadOnlyList<ManagedReferenceFact> references)
    {
        if (opcodes.Count < 2 || opcodes[^1] != OpCodes.Throw)
            return false;

        if (opcodes.Count == 2 && opcodes[0] == OpCodes.Ldnull)
            return true;

        var exceptionConstructors = references.Count(reference =>
            reference.Kind == ManagedReferenceKind.Constructs &&
            reference.Target.Contains("Exception::.ctor", StringComparison.Ordinal));
        if (exceptionConstructors != 1 || opcodes.Count(opcode => opcode == OpCodes.Newobj) != 1)
            return false;

        return opcodes.All(opcode =>
            opcode == OpCodes.Nop ||
            opcode == OpCodes.Ldstr ||
            opcode == OpCodes.Ldnull ||
            opcode == OpCodes.Ldc_I4 ||
            opcode == OpCodes.Ldc_I4_S ||
            opcode == OpCodes.Ldc_I4_M1 ||
            opcode == OpCodes.Ldc_I4_0 ||
            opcode == OpCodes.Ldc_I4_1 ||
            opcode == OpCodes.Ldc_I4_2 ||
            opcode == OpCodes.Ldc_I4_3 ||
            opcode == OpCodes.Ldc_I4_4 ||
            opcode == OpCodes.Ldc_I4_5 ||
            opcode == OpCodes.Ldc_I4_6 ||
            opcode == OpCodes.Ldc_I4_7 ||
            opcode == OpCodes.Ldc_I4_8 ||
            opcode == OpCodes.Newobj ||
            opcode == OpCodes.Throw);
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        var first = il[offset++];
        if (first != 0xFE)
        {
            return OneByteOpCodes[first];
        }

        return TwoByteOpCodes[il[offset++]];
    }

    private static int OperandSize(OperandType operandType, byte[] il, int offset) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineI or OperandType.ShortInlineBrTarget or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.ShortInlineR => 4,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
            OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
            OperandType.InlineTok or OperandType.InlineType => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, offset) * 4),
        _ => throw new InvalidDataException($"Unsupported IL operand type '{operandType}'.")
    };

    private static string GetMemberIdentity(MetadataReader metadata, int token, MetadataTypeNameProvider typeProvider)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => GetMethodIdentity(metadata, (MethodDefinitionHandle)handle, typeProvider),
                HandleKind.FieldDefinition => GetFieldIdentity(metadata, (FieldDefinitionHandle)handle, typeProvider),
                HandleKind.MemberReference => GetMemberReferenceIdentity(metadata, (MemberReferenceHandle)handle, typeProvider),
                HandleKind.MethodSpecification => GetMemberIdentity(metadata, MetadataTokens.GetToken(metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method), typeProvider),
                _ => $"0x{token:X8}"
            };
        }
        catch (ArgumentException)
        {
            return $"0x{token:X8}";
        }
    }

    private static string GetMethodIdentity(MetadataReader metadata, MethodDefinitionHandle handle, MetadataTypeNameProvider typeProvider)
    {
        var method = metadata.GetMethodDefinition(handle);
        var signature = method.DecodeSignature(typeProvider, null);
        return CanonicalSignatureRenderer.RenderMethod(
            GetTypeName(metadata, method.GetDeclaringType()),
            metadata.GetString(method.Name),
            signature.ReturnType,
            signature.ParameterTypes,
            signature.GenericParameterCount);
    }

    private static string GetFieldIdentity(MetadataReader metadata, FieldDefinitionHandle handle, MetadataTypeNameProvider typeProvider)
    {
        var field = metadata.GetFieldDefinition(handle);
        var name = metadata.GetString(field.Name);
        return GetTypeName(metadata, field.GetDeclaringType()) + "::" + CanonicalSignatureRenderer.RenderType(field.DecodeSignature(typeProvider, null)) + " " + name;
    }

    private static string GetMemberReferenceIdentity(MetadataReader metadata, MemberReferenceHandle handle, MetadataTypeNameProvider typeProvider)
    {
        var member = metadata.GetMemberReference(handle);
        var containingType = GetTypeName(metadata, member.Parent);
        var name = metadata.GetString(member.Name);
        if (member.GetKind() == MemberReferenceKind.Method)
        {
            var signature = member.DecodeMethodSignature(typeProvider, null);
            return CanonicalSignatureRenderer.RenderMethod(containingType, name, signature.ReturnType, signature.ParameterTypes, signature.GenericParameterCount);
        }

        return containingType + "::" + CanonicalSignatureRenderer.RenderType(member.DecodeFieldSignature(typeProvider, null)) + " " + name;
    }

    private sealed class MetadataTypeNameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) =>
            elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

        public string GetByReferenceType(string elementType) => "ref " + elementType;

        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            "delegate*<" + string.Join(",", signature.ParameterTypes.Prepend(signature.ReturnType)) + ">";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            _ => typeCode.ToString()
        };

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => GetTypeName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => GetTypeName(reader, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private static string GetTypeName(MetadataReader metadata, EntityHandle handle) =>
        handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeName(metadata, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => GetTypeName(metadata, (TypeReferenceHandle)handle),
            _ => $"0x{MetadataTokens.GetToken(handle):X8}"
        };

    private static string GetTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var definition = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(definition.Name);
        if (!definition.GetDeclaringType().IsNil)
            return GetTypeName(metadata, definition.GetDeclaringType()) + "+" + name;
        var @namespace = metadata.GetString(definition.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
    }

    private static string GetTypeName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var reference = metadata.GetTypeReference(handle);
        var name = metadata.GetString(reference.Name);
        if (reference.ResolutionScope.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition)
            return GetTypeName(metadata, (EntityHandle)reference.ResolutionScope) + "+" + name;
        var @namespace = metadata.GetString(reference.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
    }

    private static OpCode[] CreateOneByteOpCodes()
    {
        var result = new OpCode[0x100];
        foreach (var field in typeof(OpCodes).GetFields())
        {
            if (field.GetValue(null) is OpCode opcode && opcode.Size == 1)
            {
                result[opcode.Value & 0xFF] = opcode;
            }
        }

        return result;
    }

    private static OpCode[] CreateTwoByteOpCodes()
    {
        var result = new OpCode[0x100];
        foreach (var field in typeof(OpCodes).GetFields())
        {
            if (field.GetValue(null) is OpCode opcode && opcode.Size == 2)
            {
                result[opcode.Value & 0xFF] = opcode;
            }
        }

        return result;
    }

    private sealed record BodyAnalysis(
        int IlByteCount,
        int InstructionCount,
        IReadOnlyList<ManagedReferenceFact> References,
        bool MatchesVerifiedStubPattern,
        bool MatchesInteropWrapperPattern)
    {
        public static BodyAnalysis Empty { get; } = new(0, 0, [], false, false);
    }
}
