using System.Reflection.Metadata;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Metadata;

namespace S1Atlas.Indexing.Decompilation;

/// <summary>
/// Reads decoded custom attributes for one type definition: the type itself, its fields,
/// properties, events, methods/constructors, and each method's parameters. Injected
/// analysis attributes (<see cref="AnalysisAttributeFilter"/>) are dropped at read time so
/// they can never surface as game attributes; undecodable attributes are skipped. Only
/// members and parameters carrying at least one kept attribute appear in the result.
/// Return-value (sequence 0) parameter attributes are out of scope and are not read.
/// </summary>
public static class ManagedAttributeReader
{
    public static ManagedTypeAttributeFacts ReadTypeAttributes(MetadataReader reader, TypeDefinitionHandle type)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var definition = reader.GetTypeDefinition(type);
        var fields = new Dictionary<FieldDefinitionHandle, IReadOnlyList<DecodedCustomAttribute>>();
        var properties = new Dictionary<PropertyDefinitionHandle, IReadOnlyList<DecodedCustomAttribute>>();
        var events = new Dictionary<EventDefinitionHandle, IReadOnlyList<DecodedCustomAttribute>>();
        var methods = new Dictionary<MethodDefinitionHandle, IReadOnlyList<DecodedCustomAttribute>>();
        var parameters = new Dictionary<MethodDefinitionHandle, IReadOnlyList<ManagedParameterAttributes>>();

        foreach (var handle in definition.GetFields())
        {
            if (ReadAttributes(reader, reader.GetFieldDefinition(handle).GetCustomAttributes()) is { } attributes)
                fields[handle] = attributes;
        }

        foreach (var handle in definition.GetProperties())
        {
            if (ReadAttributes(reader, reader.GetPropertyDefinition(handle).GetCustomAttributes()) is { } attributes)
                properties[handle] = attributes;
        }

        foreach (var handle in definition.GetEvents())
        {
            if (ReadAttributes(reader, reader.GetEventDefinition(handle).GetCustomAttributes()) is { } attributes)
                events[handle] = attributes;
        }

        foreach (var handle in definition.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            if (ReadAttributes(reader, method.GetCustomAttributes()) is { } attributes)
                methods[handle] = attributes;
            if (ReadParameters(reader, method) is { } methodParameters)
                parameters[handle] = methodParameters;
        }

        return new ManagedTypeAttributeFacts(
            ReadAttributes(reader, definition.GetCustomAttributes()),
            fields,
            properties,
            events,
            methods,
            parameters);
    }

    private static IReadOnlyList<ManagedParameterAttributes>? ReadParameters(MetadataReader reader, MethodDefinition method)
    {
        List<ManagedParameterAttributes>? attributed = null;
        foreach (var handle in method.GetParameters())
        {
            var parameter = reader.GetParameter(handle);
            if (parameter.SequenceNumber == 0)
                continue;
            if (ReadAttributes(reader, parameter.GetCustomAttributes()) is not { } attributes)
                continue;
            (attributed ??= []).Add(new ManagedParameterAttributes(
                parameter.SequenceNumber - 1,
                reader.GetString(parameter.Name),
                attributes));
        }

        return attributed;
    }

    private static IReadOnlyList<DecodedCustomAttribute>? ReadAttributes(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes)
    {
        List<DecodedCustomAttribute>? kept = null;
        foreach (var handle in attributes)
        {
            DecodedCustomAttribute decoded;
            try
            {
                var name = CustomAttributeDecoder.AttributeTypeName(reader, handle);
                if (name is not null && AnalysisAttributeFilter.IsAnalysisAttribute(name))
                    continue;
                decoded = CustomAttributeDecoder.Decode(reader, handle);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or BadImageFormatException)
            {
                continue;
            }

            (kept ??= []).Add(decoded);
        }

        return kept;
    }
}

/// <summary>
/// Decoded attributes for one type definition. <see cref="Type"/> is null when the type
/// carries no kept attributes; the dictionaries hold only members and parameters with at
/// least one kept attribute. Parameter indexes are zero-based
/// (<c>SequenceNumber - 1</c>).
/// </summary>
public sealed record ManagedTypeAttributeFacts(
    IReadOnlyList<DecodedCustomAttribute>? Type,
    IReadOnlyDictionary<FieldDefinitionHandle, IReadOnlyList<DecodedCustomAttribute>> Fields,
    IReadOnlyDictionary<PropertyDefinitionHandle, IReadOnlyList<DecodedCustomAttribute>> Properties,
    IReadOnlyDictionary<EventDefinitionHandle, IReadOnlyList<DecodedCustomAttribute>> Events,
    IReadOnlyDictionary<MethodDefinitionHandle, IReadOnlyList<DecodedCustomAttribute>> Methods,
    IReadOnlyDictionary<MethodDefinitionHandle, IReadOnlyList<ManagedParameterAttributes>> Parameters);
