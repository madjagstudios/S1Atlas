using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal static class ManagedToolInstanceFactory
{
    public static ToolInstance Create(
        ResolvedToolDefinition definition,
        ManagedToolInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(installation);

        var executablePath = ToolPathPolicy.ResolveContainedRelativePath(
            installation.RootPath,
            definition.Definition.Package.ExecutableRelativePath);
        var toolInstanceId = ToolInstanceId.Create(
            definition.Definition.ToolId,
            installation.ExecutableSha256,
            definition.Definition.Platform,
            ToolTrustLevel.ManagedPinned);
        return new ToolInstance(
            toolInstanceId,
            definition.Definition.ToolId,
            definition.Definition.Version,
            definition.Definition.Platform,
            ToolTrustLevel.ManagedPinned,
            definition.DefinitionDigest,
            installation.PackageSha256,
            installation.ExecutableSha256,
            executablePath,
            installation.InstalledAtUtc,
            installation.LastVerifiedAtUtc,
            ToolInstallationStatus.Verified);
    }
}
