using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace S1Atlas.Extraction.Scene;

/// <summary>
/// Decides whether a reconstructed script assembly can supply MonoBehaviour layouts. IL2CPP
/// reconstructions drop custom attributes unless Cpp2IL's attribute processors ran; without
/// <c>[SerializeField]</c> a layout silently omits private serialized fields and every later
/// field misaligns, so layouts are trusted only when at least one such attribute survived.
/// Reads metadata only; the assembly is never loaded or executed.
/// </summary>
public static class ScriptLayoutProbe
{
    public const string ScriptAssemblyFileName = "Assembly-CSharp.dll";

    public static bool HasRestoredSerializationAttributes(string managedAssembliesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedAssembliesPath);
        var path = Path.Combine(managedAssembliesPath, ScriptAssemblyFileName);
        if (!File.Exists(path))
            return false;

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            return false;
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.CustomAttributes)
        {
            if (IsSerializeField(metadata, metadata.GetCustomAttribute(handle).Constructor))
                return true;
        }

        return false;
    }

    private static bool IsSerializeField(MetadataReader metadata, EntityHandle constructor)
    {
        EntityHandle type = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };
        return type.Kind switch
        {
            HandleKind.TypeReference => IsSerializeField(
                metadata,
                metadata.GetTypeReference((TypeReferenceHandle)type).Namespace,
                metadata.GetTypeReference((TypeReferenceHandle)type).Name),
            HandleKind.TypeDefinition => IsSerializeField(
                metadata,
                metadata.GetTypeDefinition((TypeDefinitionHandle)type).Namespace,
                metadata.GetTypeDefinition((TypeDefinitionHandle)type).Name),
            _ => false
        };
    }

    private static bool IsSerializeField(MetadataReader metadata, StringHandle @namespace, StringHandle name) =>
        metadata.StringComparer.Equals(@namespace, "UnityEngine") &&
        metadata.StringComparer.Equals(name, "SerializeField");
}
