using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Composition;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Web.Tests;

// Acceptance measurement for the AT-88 serve targets: cold start under 3 s
// and search p95 under 200 ms on the full local game build. Opt-in only
// (S1ATLAS_RUN_LOCAL_GAME_TESTS=1); CI skips. Migrates the copy, never the
// live atlas. Reports sizes and timings only — no symbol names or queries.
public sealed class ServePerformanceTests
{
    private readonly ITestOutputHelper _output;

    public ServePerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Trait("Category", "LocalGameRequired")]
    [Fact]
    public async Task ServeMeetsStartAndSearchTargetsOnLocalGameBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.SkipUnless(
            LiveAtlasCopy.IsExplicitlyEnabled,
            $"Opt-in only: set {LiveAtlasCopy.EnableVariable}=1 to run. Skipping: NEEDS_CONTEXT.");
        var liveRoot = LiveAtlasCopy.ResolveLiveDataRoot();
        Assert.SkipUnless(
            File.Exists(Path.Combine(liveRoot, "atlas.db")),
            "No local atlas database exists. Skipping: NEEDS_CONTEXT.");

        await using var copy = await LiveAtlasCopy.CreateAsync(liveRoot, cancellationToken);
        Assert.SkipUnless(
            copy is not null,
            "The temp drive cannot hold a copy of the live atlas. Skipping: NEEDS_CONTEXT.");
        LiveAtlasCopy.RequireTestTempRoot(copy!.DataRoot);

        var sizeBeforeBytes = new FileInfo(copy.DatabasePath).Length;
        var migrationWatch = Stopwatch.StartNew();
        var repository = new SqliteAtlasRepository(
            copy.DatabasePath,
            Path.Combine(copy.DataRoot, "backups"));
        await repository.InitializeAsync(cancellationToken);
        migrationWatch.Stop();
        var sizeAfterBytes = new FileInfo(copy.DatabasePath).Length;
        _output.WriteLine($"PERF: migration_ms={migrationWatch.ElapsedMilliseconds} " +
            $"db_before_bytes={sizeBeforeBytes} db_after_bytes={sizeAfterBytes}");

        var services = ReadOnlyAtlasComposition.BuildReadOnlyServices(copy.DataRoot);
        var authority = await services.AuthorityResolver.ResolveAsync(null, cancellationToken);
        Assert.SkipUnless(
            authority.Status == InstalledBuildAuthorityStatus.Resolved && authority.IndexRun is not null,
            "No completed Schedule I Installed index exists locally. Skipping: NEEDS_CONTEXT.");
        var probes = new List<string>();
        foreach (var offset in new[] { 0, 1000, 2000 })
        {
            await using var probeConnection = new SqliteConnection(
                $"Data Source={copy.DatabasePath};Mode=ReadOnly;Pooling=False");
            await probeConnection.OpenAsync(cancellationToken);
            await using var probeCommand = probeConnection.CreateCommand();
            probeCommand.CommandText = """
                SELECT symbol.qualified_name
                FROM symbols AS symbol
                INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
                WHERE run.index_id = $indexId
                  AND run.status = 'Completed'
                  AND LENGTH(symbol.simple_name) >= 6
                  AND symbol.qualified_name LIKE '%.%' ESCAPE '\'
                ORDER BY symbol.rowid
                LIMIT 1 OFFSET $offset;
                """;
            probeCommand.Parameters.AddWithValue("$indexId", authority.IndexRun!.IndexId);
            probeCommand.Parameters.AddWithValue("$offset", offset);
            var name = Convert.ToString(await probeCommand.ExecuteScalarAsync(cancellationToken));
            if (!string.IsNullOrEmpty(name) && name.Length >= 12 && !name.EndsWith('.'))
                probes.Add(name);
        }

        Assert.SkipUnless(
            probes.Count == 3,
            "Too few representative qualified names found for query shapes. Skipping: NEEDS_CONTEXT.");
        var shapes = probes.SelectMany(QueryShapes).ToArray();

        var host = ServeHost.Create(new ServeOptions(copy.DataRoot, 0));
        await using (host)
        {
            var startWatch = Stopwatch.StartNew();
            await host.StartAsync(cancellationToken);
            using var client = new HttpClient { BaseAddress = host.BaseAddress };
            await WaitForFirstSuccessAsync(client, cancellationToken);
            startWatch.Stop();
            _output.WriteLine($"PERF: cold_start_ms={startWatch.ElapsedMilliseconds}");
            Assert.True(
                startWatch.ElapsedMilliseconds < 3000,
                $"Serve cold start took {startWatch.ElapsedMilliseconds} ms (target under 3000 ms).");

            // One unmeasured warmup pass so page cache and JIT settle before
            // the measured repetitions.
            foreach (var (_, query) in shapes)
            {
                using var warmup = await client.GetAsync(
                    $"/api/search?q={Uri.EscapeDataString(query)}&codebase=schedule-i",
                    cancellationToken);
                await warmup.Content.ReadAsStringAsync(cancellationToken);
            }

            var latencies = new List<double>();
            var byShape = new Dictionary<string, List<double>>();
            foreach (var (shape, query) in shapes)
            {
                for (var repetition = 0; repetition < 3; repetition++)
                {
                    var watch = Stopwatch.StartNew();
                    using var response = await client.GetAsync(
                        $"/api/search?q={Uri.EscapeDataString(query)}&codebase=schedule-i",
                        cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    watch.Stop();
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var json = JsonDocument.Parse(body);
                    Assert.True(
                        json.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32() > 0,
                        $"Query shape '{shape}' unexpectedly matched nothing.");
                    latencies.Add(watch.Elapsed.TotalMilliseconds);
                    if (!byShape.TryGetValue(shape, out var shapeLatencies))
                    {
                        shapeLatencies = [];
                        byShape[shape] = shapeLatencies;
                    }

                    shapeLatencies.Add(watch.Elapsed.TotalMilliseconds);
                }
            }

            foreach (var (shape, shapeLatencies) in byShape.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                shapeLatencies.Sort();
                _output.WriteLine($"PERF: shape={shape} n={shapeLatencies.Count} median_ms={Median(shapeLatencies):F1}");
            }

            latencies.Sort();
            var median = Median(latencies);
            var p95 = latencies[(int)Math.Ceiling(0.95 * latencies.Count) - 1];
            _output.WriteLine($"PERF: search_n={latencies.Count} median_ms={median:F1} p95_ms={p95:F1}");
            Assert.True(p95 < 200, $"Search p95 took {p95:F1} ms (target under 200 ms).");

            await ReportDiffTimingsAsync(client, services, cancellationToken);

            await host.StopAsync(cancellationToken);
        }
    }

    private async Task ReportDiffTimingsAsync(
        HttpClient client,
        AtlasReadOnlyServices services,
        CancellationToken cancellationToken)
    {
        var builds = await services.Repository.ListBuildsAsync(cancellationToken);
        var diffable = new List<string>();
        foreach (var build in builds)
        {
            if (diffable.Count == 2)
                break;
            var authority = await services.AuthorityResolver.ResolveAsync(build.BuildId, cancellationToken);
            if (authority.Status == InstalledBuildAuthorityStatus.Resolved)
                diffable.Add(build.BuildId);
        }

        if (diffable.Count < 2)
        {
            _output.WriteLine("PERF: diff=n/a (fewer than two diffable builds)");
            return;
        }

        var url = $"/api/diff?from={Uri.EscapeDataString(diffable[0])}&to={Uri.EscapeDataString(diffable[1])}&page=0";
        var first = Stopwatch.StartNew();
        using (var response = await client.GetAsync(url, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync(cancellationToken);
        }

        first.Stop();
        var cached = Stopwatch.StartNew();
        using (var response = await client.GetAsync(url, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync(cancellationToken);
        }

        cached.Stop();
        _output.WriteLine($"PERF: diff_first_ms={first.ElapsedMilliseconds} diff_cached_ms={cached.ElapsedMilliseconds}");
    }

    private static (string Shape, string Query)[] QueryShapes(string qualifiedName)
    {
        var dot = qualifiedName.LastIndexOf('.');
        var simple = qualifiedName[(dot + 1)..];
        var middle = qualifiedName.Length / 2 - 2;
        return [
            ("exact", simple),
            ("prefix", qualifiedName[..Math.Max(6, qualifiedName.Length / 2)]),
            ("substring", qualifiedName.Substring(middle, 6)),
            ("two-char", qualifiedName[..2]),
        ];
    }

    private static double Median(List<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static async Task WaitForFirstSuccessAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                // The listener may not be accepting yet; retry below.
            }

            if (deadline.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("Serve did not return 200 on / within 30 s.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
