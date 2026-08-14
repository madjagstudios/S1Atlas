using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Decompilation;

public sealed class IlSpyManagedDecompiler : IManagedDecompiler
{
    private static readonly OpCode[] OneByteOpCodes = CreateOneByteOpCodes();
    private static readonly OpCode[] TwoByteOpCodes = CreateTwoByteOpCodes();

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

        var source = new CSharpDecompiler(fullPath, new DecompilerSettings())
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
        var fullName = string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
        var baseType = definition.BaseType.IsNil ? null : GetTypeName(metadata, definition.BaseType);
        var interfaces = definition.GetInterfaceImplementations()
            .Select(handle => GetTypeName(metadata, metadata.GetInterfaceImplementation(handle).Interface))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var members = new List<ManagedMemberFacts>();
        foreach (var fieldHandle in definition.GetFields())
        {
            var field = metadata.GetFieldDefinition(fieldHandle);
            members.Add(new ManagedMemberFacts(
                metadata.GetString(field.Name),
                ManagedMemberKind.Field,
                metadata.GetString(field.Name),
                false,
                []));
        }

        foreach (var propertyHandle in definition.GetProperties())
        {
            var property = metadata.GetPropertyDefinition(propertyHandle);
            members.Add(new ManagedMemberFacts(
                metadata.GetString(property.Name),
                ManagedMemberKind.Property,
                metadata.GetString(property.Name),
                false,
                []));
        }

        foreach (var eventHandle in definition.GetEvents())
        {
            var @event = metadata.GetEventDefinition(eventHandle);
            members.Add(new ManagedMemberFacts(
                metadata.GetString(@event.Name),
                ManagedMemberKind.Event,
                metadata.GetString(@event.Name),
                false,
                []));
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
            var genericParameterCount = method.GetGenericParameters().Count;
            var parameterCount = method.GetParameters()
                .Count(parameter => metadata.GetParameter(parameter).SequenceNumber != 0);
            var signature = methodName +
                (genericParameterCount == 0 ? string.Empty : $"<{new string('T', genericParameterCount)}>") +
                $"({parameterCount})";
            var hasBody = method.RelativeVirtualAddress != 0;
            var references = hasBody
                ? ReadReferences(metadata, peReader, method.RelativeVirtualAddress)
                : [];
            members.Add(new ManagedMemberFacts(methodName, kind, signature, hasBody, references));
        }

        return new ManagedTypeFacts(fullName, @namespace, name, baseType, interfaces, members);
    }

    private static IReadOnlyList<ManagedReferenceFact> ReadReferences(
        MetadataReader metadata,
        PEReader peReader,
        int relativeVirtualAddress)
    {
        var body = peReader.GetMethodBody(relativeVirtualAddress);
        var il = body.GetILBytes() ?? [];
        var references = new List<ManagedReferenceFact>();
        var offset = 0;

        while (offset < il.Length)
        {
            var opcode = ReadOpCode(il, ref offset);
            var operandOffset = offset;
            switch (opcode.OperandType)
            {
                case OperandType.InlineMethod:
                    {
                        var token = BitConverter.ToInt32(il, offset);
                        offset += 4;
                        references.Add(new ManagedReferenceFact(
                            opcode == OpCodes.Newobj ? ManagedReferenceKind.Constructs : ManagedReferenceKind.Calls,
                            GetMemberName(metadata, token)));
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
                            GetMemberName(metadata, token)));
                        break;
                    }
                default:
                    offset = operandOffset + OperandSize(opcode.OperandType, il, operandOffset);
                    break;
            }
        }

        return references;
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
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
            OperandType.InlineI8 or OperandType.InlineMethod or OperandType.InlineSig or
            OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType =>
            operandType == OperandType.InlineI8 ? 8 : 4,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, offset) * 4),
        _ => throw new InvalidDataException($"Unsupported IL operand type '{operandType}'.")
    };

    private static string GetMemberName(MetadataReader metadata, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => metadata.GetString(metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
                HandleKind.FieldDefinition => metadata.GetString(metadata.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
                HandleKind.MemberReference => metadata.GetString(metadata.GetMemberReference((MemberReferenceHandle)handle).Name),
                _ => $"0x{token:X8}"
            };
        }
        catch (ArgumentException)
        {
            return $"0x{token:X8}";
        }
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
        var @namespace = metadata.GetString(definition.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
    }

    private static string GetTypeName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var reference = metadata.GetTypeReference(handle);
        var name = metadata.GetString(reference.Name);
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
}
