using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.ModChecking;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Cli.Commands;

internal static class CheckModCommand
{
    public static Command Create(string dataRoot, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var path = new Argument<string>("path-to-mod.dll") { Description = "The local .NET mod assembly to inspect read-only." };
        var from = new Option<string?>("--from") { Description = "The base build ID or unique prefix; defaults to the most recent older build with a completed game index." };
        var to = new Option<string?>("--to") { Description = "The target build ID or unique prefix; defaults to the current build." };
        var json = CommandOutput.CreateJsonOption();
        var command = new Command("check-mod", CliExamples.With("Check a mod's game dependencies after an update.",
            "s1atlas check-mod Demo.Mod.dll --from abc123 --to def456 --json"));
        command.Arguments.Add(path);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(json);
        command.Validators.Add(result =>
        {
            var modPath = result.GetValue(path);
            if (modPath is not null && (string.IsNullOrWhiteSpace(modPath) || !modPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                throw new CliValidationException("check-mod", "InvalidModPath", "Provide a path to a .NET mod .dll assembly.");
            foreach (var option in new[] { from, to })
                if (result.GetResult(option) is not null && string.IsNullOrWhiteSpace(result.GetValue(option)))
                    throw new CliValidationException("check-mod", "InvalidBuild", $"{option.Name} requires a build ID or unique prefix.");
        });
        command.SetAction(result =>
        {
            var commandOutput = new CommandOutput("check-mod", result.GetValue(json), output, error);
            return CommandExecution.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var database = Path.Combine(dataRoot, "atlas.db");
                var schema = new SqliteAtlasSchemaInspector(database).GetStatusAsync(cancellationToken).GetAwaiter().GetResult();
                if (schema.Kind != AtlasSchemaStatusKind.Current)
                {
                    var (message, hint) = SchemaStatusWording.Describe(schema);
                    return commandOutput.Failure(1, "AtlasUnavailable", message, hint: hint);
                }
                var repository = new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(database));
                var preferred = new PreferredVerifiedExtractionResolver(dataRoot, repository,
                    ValidatedExtractionIntegrityVerifier.Create(new Sha256FileHasher(), repository));
                var authority = new InstalledBuildAuthorityResolver(preferred, repository, repository, repository);
                var target = authority.ResolveAsync(result.GetValue(to), cancellationToken).GetAwaiter().GetResult();
                if (target.Status != InstalledBuildAuthorityStatus.Resolved) return AuthorityError(target);
                InstalledBuildAuthority? baseline = null;
                var requestedFrom = result.GetValue(from);
                if (requestedFrom is not null)
                {
                    baseline = authority.ResolveAsync(requestedFrom, cancellationToken).GetAwaiter().GetResult();
                    if (baseline.Status != InstalledBuildAuthorityStatus.Resolved) return AuthorityError(baseline);
                }
                else
                {
                    var builds = repository.ListBuildsAsync(cancellationToken).GetAwaiter().GetResult();
                    var targetBuild = builds.Single(build => build.BuildId == target.ResolvedBuildId);
                    foreach (var build in builds.Where(build => build.FirstSeenAtUtc < targetBuild.FirstSeenAtUtc)
                                 .OrderByDescending(build => build.FirstSeenAtUtc).ThenByDescending(build => build.BuildId, StringComparer.Ordinal))
                    {
                        // Skip unindexed builds before the expensive integrity check.
                        var preference = repository.GetPreferredExtractionAsync(build.BuildId, cancellationToken).GetAwaiter().GetResult();
                        if (preference is null) continue;
                        var index = repository.GetLatestCompletedIndexBySourceIdentityAsync(
                            Core.Indexing.CodebaseKind.ScheduleI, Core.Indexing.CodeChannel.Installed, preference.ExtractionId, cancellationToken).GetAwaiter().GetResult();
                        if (index is null) continue;
                        baseline = authority.ResolveAsync(build.BuildId, cancellationToken).GetAwaiter().GetResult();
                        if (baseline.Status != InstalledBuildAuthorityStatus.Resolved) return AuthorityError(baseline);
                        break;
                    }
                }
                var check = new ModCheckService(repository, repository, new IlSpyManagedDecompiler()).CheckAsync(
                    result.GetValue(path)!, baseline?.ResolvedBuildId, target.ResolvedBuildId!, baseline?.IndexId, target.IndexId!, cancellationToken).GetAwaiter().GetResult();
                return commandOutput.Complete(check.Summary.BreakingDependencies > 0 ? 3 : 0, check, writer => WriteHuman(writer, check));

                int AuthorityError(InstalledBuildAuthority failed) => commandOutput.Failure(1, failed.Status.ToString(), failed.Message!, hint: failed.Hint);
            }, commandOutput, cancellationToken);
        });
        return command;
    }

    private static void WriteHuman(TextWriter writer, ModCheckResult result)
    {
        writer.WriteLine(result.SingleBuild
            ? $"Single-build mode: dependencies resolved against {result.ToBuildId}; no older completed game index."
            : $"Mod dependencies: {result.FromBuildId} → {result.ToBuildId}");
        foreach (var (status, count) in result.Summary.Counts)
            writer.WriteLine($"  {status}: {count} (Harmony patch targets: {result.Summary.PatchTargets[status]})");
        writer.WriteLine($"  external references not checked: {result.Summary.ExternalReferencesNotChecked}");
        if (result.Summary.BreakingPatchTargets > 0)
            writer.WriteLine($"WARNING: {result.Summary.BreakingPatchTargets} Harmony patch targets are broken; patches can fail silently at runtime.");
        writer.WriteLine();
        writer.WriteLine($"{"Status",-19} {"Symbol / signature"}");
        foreach (var row in result.Dependencies)
        {
            writer.WriteLine($"{row.Status,-19} {row.Name} [{row.Evidence}]");
            writer.WriteLine("  sources: " + string.Join(", ", row.Sources.Select(source => source.Kind +
                (source.PatchKind is null ? "" : " (" + source.PatchKind + ")") + " in " + source.Member + " [" + source.Evidence + "]")));
            if (row.BeforeSignature is not null) writer.WriteLine("  before: " + row.BeforeSignature);
            if (row.AfterSignature is not null) writer.WriteLine("  after:  " + row.AfterSignature);
            if (row.Status == "unchanged") writer.WriteLine("  bodyChanged: " + (row.BodyChanged?.ToString().ToLowerInvariant() ?? "unknown"));
            if (row.Reason is not null) writer.WriteLine("  reason: " + row.Reason);
            foreach (var candidate in row.Candidates)
                writer.WriteLine((row.Status == "moved" ? "  moved candidate: " : "  possible replacement: ") + candidate.Signature + " [DERIVED]");
        }
    }
}
