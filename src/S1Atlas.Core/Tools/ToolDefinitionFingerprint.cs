using S1Atlas.Core.Identity;

namespace S1Atlas.Core.Tools;

public static class ToolDefinitionFingerprint
{
    public static string Create(ToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        using var writer = new CanonicalHashWriter("tool-definition", 1);
        writer.AppendInt32(definition.SchemaVersion);
        writer.AppendString(definition.ToolId);
        writer.AppendString(definition.DisplayName);
        writer.AppendString(definition.Version);
        writer.AppendString(definition.Platform);

        var package = definition.Package;
        writer.AppendString(package.Kind.ToString());
        writer.AppendNullableString(package.ArchiveFormat?.ToString());
        writer.AppendString(package.SourceUri.AbsoluteUri);
        writer.AppendString(package.ReleaseUri.AbsoluteUri);
        writer.AppendString(package.AssetName);
        writer.AppendInt64(package.ExpectedSize);
        writer.AppendString(package.Sha256);
        writer.AppendString(package.ExecutableRelativePath);
        writer.AppendInt64(package.Limits.MaximumDownloadBytes);
        writer.AppendInt64(package.Limits.MaximumExpandedBytes);
        writer.AppendInt32(package.Limits.MaximumFileCount);

        writer.AppendString(definition.License.SpdxIdentifier);
        writer.AppendString(definition.License.SourceUri.AbsoluteUri);

        writer.AppendInt32(definition.Probes.Count);
        foreach (var probe in definition.Probes)
        {
            writer.AppendString(probe.ProbeId);

            writer.AppendInt32(probe.Arguments.Count);
            foreach (var argument in probe.Arguments)
            {
                writer.AppendString(argument);
            }

            writer.AppendInt32(probe.AcceptedExitCodes.Count);
            foreach (var exitCode in probe.AcceptedExitCodes)
            {
                writer.AppendInt32(exitCode);
            }

            writer.AppendInt64(
                probe.Timeout.Ticks / TimeSpan.TicksPerMillisecond);

            writer.AppendInt32(probe.RequiredOutputSubstrings.Count);
            foreach (var requiredOutput in probe.RequiredOutputSubstrings)
            {
                writer.AppendString(requiredOutput);
            }
        }

        return writer.Complete();
    }
}
