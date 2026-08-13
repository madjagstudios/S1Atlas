using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using S1Atlas.Core.Extraction;

namespace S1Atlas.Extraction.Validation;

/// <summary>
/// The result of inspecting a single candidate artifact for managed metadata.
/// <see cref="Kind"/> classifies the artifact without loading it: a ".dll" with
/// no managed metadata is <see cref="ArtifactKind.NativeLibrary"/>; a ".dll" with
/// managed metadata is <see cref="ArtifactKind.ManagedAssembly"/>; anything else,
/// including a malformed ".dll", is <see cref="ArtifactKind.Other"/>. A malformed
/// or truncated ".dll" sets <see cref="IsValid"/> to <see langword="false"/> and
/// carries a structured <see cref="FailureCode"/>/<see cref="FailureMessage"/>
/// rather than throwing.
/// </summary>
internal sealed record ManagedAssemblyInspection(
    ArtifactKind Kind,
    bool IsValid,
    string? AssemblyName,
    string? ModuleName,
    int? TypeDefinitionCount,
    int? MethodDefinitionCount,
    int? FieldDefinitionCount,
    int? PropertyDefinitionCount,
    int? EventDefinitionCount,
    string? FailureCode,
    string? FailureMessage);

/// <summary>
/// Inspects a candidate artifact's managed metadata using only
/// <see cref="PEReader"/> and <see cref="MetadataReader"/>. Never loads a
/// reconstructed assembly through <c>Assembly.Load</c>, an
/// <c>AssemblyLoadContext</c>, a reflection-only substitute, or execution: it
/// opens the file read-only, parses the PE and metadata headers, and reads exact
/// table row counts. This is the one absolute hard rule of Phase 4 validation.
/// </summary>
internal static class ManagedAssemblyInspector
{
    private const string DllExtension = ".dll";
    private const string InvalidManagedAssemblyCode = "InvalidManagedAssembly";

    private static readonly ManagedAssemblyInspection OtherInspection = new(
        ArtifactKind.Other,
        IsValid: true,
        AssemblyName: null,
        ModuleName: null,
        TypeDefinitionCount: null,
        MethodDefinitionCount: null,
        FieldDefinitionCount: null,
        PropertyDefinitionCount: null,
        EventDefinitionCount: null,
        FailureCode: null,
        FailureMessage: null);

    private static readonly ManagedAssemblyInspection NativeLibraryInspection = new(
        ArtifactKind.NativeLibrary,
        IsValid: true,
        AssemblyName: null,
        ModuleName: null,
        TypeDefinitionCount: null,
        MethodDefinitionCount: null,
        FieldDefinitionCount: null,
        PropertyDefinitionCount: null,
        EventDefinitionCount: null,
        FailureCode: null,
        FailureMessage: null);

    /// <summary>
    /// Inspects a candidate artifact already known to exist and be stable at
    /// <paramref name="fullPath"/>. <paramref name="relativePath"/> is used only
    /// to decide whether the artifact is a ".dll" candidate for managed
    /// inspection; a non-".dll" file is always classified <see cref="ArtifactKind.Other"/>
    /// without being opened.
    /// </summary>
    public static ManagedAssemblyInspection Inspect(string fullPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (!relativePath.EndsWith(DllExtension, StringComparison.OrdinalIgnoreCase))
        {
            return OtherInspection;
        }

        try
        {
            using var stream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
            {
                return NativeLibraryInspection;
            }

            var metadataReader = peReader.GetMetadataReader();

            string? assemblyName = metadataReader.IsAssembly
                ? metadataReader.GetString(metadataReader.GetAssemblyDefinition().Name)
                : null;
            var moduleName = metadataReader.GetString(metadataReader.GetModuleDefinition().Name);

            return new ManagedAssemblyInspection(
                ArtifactKind.ManagedAssembly,
                IsValid: true,
                AssemblyName: assemblyName,
                ModuleName: moduleName,
                TypeDefinitionCount: metadataReader.TypeDefinitions.Count,
                MethodDefinitionCount: metadataReader.MethodDefinitions.Count,
                FieldDefinitionCount: metadataReader.FieldDefinitions.Count,
                PropertyDefinitionCount: metadataReader.PropertyDefinitions.Count,
                EventDefinitionCount: metadataReader.EventDefinitions.Count,
                FailureCode: null,
                FailureMessage: null);
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or InvalidOperationException or IOException or
            UnauthorizedAccessException)
        {
            return new ManagedAssemblyInspection(
                ArtifactKind.Other,
                IsValid: false,
                AssemblyName: null,
                ModuleName: null,
                TypeDefinitionCount: null,
                MethodDefinitionCount: null,
                FieldDefinitionCount: null,
                PropertyDefinitionCount: null,
                EventDefinitionCount: null,
                FailureCode: InvalidManagedAssemblyCode,
                FailureMessage:
                    $"'{relativePath}' could not be inspected as a managed assembly: {exception.Message}");
        }
    }
}
