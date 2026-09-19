namespace S1Atlas.Extraction.Scene;

/// <summary>
/// The repository tool pin that supplies Unity class databases to the scene parser.
/// </summary>
public static class UnityClassDatabasePin
{
    /// <summary>Tool ID of the pinned class package definition under <c>config/tools</c>.</summary>
    public const string ToolId = "unity-classdata";
}

/// <summary>
/// Identity of the pinned Unity class database package that supplies type trees for
/// SerializedFile containers whose own type trees were stripped at build time.
/// </summary>
public sealed record UnityClassDatabaseDescriptor(
    string PackageId,
    string PackageVersion,
    string PackageSha256)
{
    public string PackageId { get; init; } = Require(PackageId, nameof(PackageId));
    public string PackageVersion { get; init; } = Require(PackageVersion, nameof(PackageVersion));
    public string PackageSha256 { get; init; } = RequireSha256(PackageSha256, nameof(PackageSha256));

    private static string Require(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    private static string RequireSha256(string value, string name)
    {
        Require(value, name);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The value must be a lower-case SHA-256 digest.", name);
        return value;
    }
}

public enum ParsedTypeTreeSourceKind
{
    /// <summary>No type-tree source was available; supported objects stayed undecoded stubs.</summary>
    Unavailable,
    /// <summary>The SerializedFile embeds its own type tree.</summary>
    Embedded,
    /// <summary>The type tree came from the pinned class database package.</summary>
    ClassDatabase
}

/// <summary>
/// Which type-tree source decoded a container's supported objects. For a class database the
/// resolved Unity version is the newest dump in the package that is not newer than the
/// container's Unity version; <see cref="ExactVersionMatch"/> is false when the package holds
/// no dump for the container's exact version and a nearest earlier one was used.
/// </summary>
public sealed record ParsedTypeTreeSource(
    ParsedTypeTreeSourceKind Kind,
    UnityClassDatabaseDescriptor? ClassDatabase = null,
    string? ResolvedUnityVersion = null,
    bool ExactVersionMatch = true)
{
    public static ParsedTypeTreeSource Embedded { get; } = new(ParsedTypeTreeSourceKind.Embedded);
    public static ParsedTypeTreeSource Unavailable { get; } = new(ParsedTypeTreeSourceKind.Unavailable);

    public static ParsedTypeTreeSource FromClassDatabase(
        UnityClassDatabaseDescriptor descriptor,
        string resolvedUnityVersion,
        bool exactVersionMatch)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedUnityVersion);
        return new ParsedTypeTreeSource(
            ParsedTypeTreeSourceKind.ClassDatabase,
            descriptor,
            resolvedUnityVersion,
            exactVersionMatch);
    }

    /// <summary>A bounded, path-free label suitable for snapshot provenance and CLI/MCP output.</summary>
    public string Label(string containerUnityVersion) => Kind switch
    {
        ParsedTypeTreeSourceKind.Embedded => "embedded",
        ParsedTypeTreeSourceKind.ClassDatabase =>
            $"class-database {ClassDatabase!.PackageId} {ClassDatabase.PackageVersion} sha256:{ClassDatabase.PackageSha256}" +
            (ExactVersionMatch
                ? $" ({ResolvedUnityVersion} exact)"
                : $" ({ResolvedUnityVersion} layouts for {containerUnityVersion}; nearest earlier dump)"),
        _ => "unavailable"
    };
}
