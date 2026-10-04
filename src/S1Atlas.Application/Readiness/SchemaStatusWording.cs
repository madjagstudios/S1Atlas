using S1Atlas.Core.Storage;

namespace S1Atlas.Application.Readiness;

// Single home of the atlas-schema status wording. Doctor, serve, and the MCP
// server all describe statuses through here so the message and fix command
// cannot drift apart. A hint is an exact runnable command, or null when no
// command fixes the status.
public static class SchemaStatusWording
{
    public static (string Message, string? Hint) Describe(AtlasSchemaStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Kind switch
        {
            AtlasSchemaStatusKind.NotCreated => (
                "No atlas database yet; the first scan creates it.",
                ReadinessFixCommands.Scan),
            AtlasSchemaStatusKind.Current => (
                "Atlas database schema is current.",
                null),
            AtlasSchemaStatusKind.Behind => (
                $"Atlas database schema v{status.AppliedVersion} is older than v{status.ExpectedVersion}.",
                ReadinessFixCommands.Status),
            AtlasSchemaStatusKind.Ahead => (
                $"Upgrade S1Atlas to a build that understands atlas schema v{status.AppliedVersion} (this build expects v{status.ExpectedVersion}).",
                null),
            AtlasSchemaStatusKind.Unreadable => (
                "The atlas database could not be read (another s1atlas command may be using it). Try again.",
                ReadinessFixCommands.Doctor),
            _ => (
                "Back up and remove the unrecognized atlas database, then run 's1atlas scan'.",
                null)
        };
    }

    public static string StartupLine(AtlasSchemaStatus status)
    {
        var (message, hint) = Describe(status);
        return hint is null ? message : $"{message} Run '{hint}' to fix.";
    }

    // Null when requests proceed: a current schema, or no database at all
    // (each host keeps its own missing-store behaviour for that case).
    public static SchemaBlock? BlockFor(AtlasSchemaStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Kind switch
        {
            AtlasSchemaStatusKind.Behind => Block(
                status,
                "AtlasSchemaBehind"),
            AtlasSchemaStatusKind.Ahead => Block(
                status,
                "AtlasSchemaAhead"),
            AtlasSchemaStatusKind.Unrecognized => Block(
                status,
                "AtlasSchemaUnrecognized"),
            AtlasSchemaStatusKind.Unreadable => Block(
                status,
                "AtlasSchemaUnreadable"),
            _ => null
        };
    }

    private static SchemaBlock Block(AtlasSchemaStatus status, string code)
    {
        var (message, hint) = Describe(status);
        return new SchemaBlock(status.Kind, code, message, hint);
    }
}

public sealed record SchemaBlock(
    AtlasSchemaStatusKind Kind,
    string Code,
    string Message,
    string? Hint);
