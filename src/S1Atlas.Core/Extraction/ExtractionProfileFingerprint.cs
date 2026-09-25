using S1Atlas.Core.Identity;

namespace S1Atlas.Core.Extraction;

public static class ExtractionProfileFingerprint
{
    public static string Create(ExtractionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        using var writer = new CanonicalHashWriter("extraction-profile", 1);
        writer.AppendInt32(profile.SchemaVersion);
        writer.AppendString(profile.ProfileId);
        writer.AppendInt32(profile.ProfileVersion);
        writer.AppendInt32(profile.AdapterVersion);
        writer.AppendInt32(profile.ExtractionSchemaVersion);
        writer.AppendString(profile.ExecutableName);
        writer.AppendString(profile.OutputFormat);
        writer.AppendInt64(profile.Timeout.Ticks / TimeSpan.TicksPerSecond);
        writer.AppendInt64(profile.MaximumRetainedStandardOutputBytes);
        writer.AppendInt64(profile.MaximumRetainedStandardErrorBytes);
        AppendOrdered(writer, profile.AcceptedExitCodes.Order());
        AppendOrdered(writer, profile.RequiredAssemblyIdentities.Order(StringComparer.Ordinal));

        writer.AppendInt32(profile.SnapshotInputs.Count);
        foreach (var input in profile.SnapshotInputs)
        {
            writer.AppendString(input.RelativePath);
            writer.AppendString(input.Role);
        }

        AppendOrdered(writer, profile.UnityVersionSources);

        // Appended only when present so profiles without processors keep their existing digest
        // (and existing extractions keep their recipe IDs). Order is significant to Cpp2IL.
        if (profile.Cpp2IlProcessors.Count > 0)
        {
            writer.AppendString("cpp2il-processors");
            AppendOrdered(writer, profile.Cpp2IlProcessors);
        }

        return writer.Complete();
    }

    private static void AppendOrdered(CanonicalHashWriter writer, IEnumerable<int> values)
    {
        var array = values.ToArray();
        writer.AppendInt32(array.Length);
        foreach (var value in array) writer.AppendInt32(value);
    }

    private static void AppendOrdered(CanonicalHashWriter writer, IEnumerable<string> values)
    {
        var array = values.ToArray();
        writer.AppendInt32(array.Length);
        foreach (var value in array) writer.AppendString(value);
    }
}
