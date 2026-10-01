using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Display;
using S1Atlas.Core.Environment;
using S1Atlas.Web.Api;
using S1Atlas.Web.Envelopes;
using S1Atlas.Web.Queries;
using S1Atlas.Web.Rendering;

namespace S1Atlas.Web.Endpoints;

internal static class EnvironmentEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapMethods("/environment", ["GET", "HEAD"], EnvironmentPageAsync);
        app.MapMethods("/api/environment", ["GET", "HEAD"], ApiEnvironmentAsync);
    }

    private static async Task<IResult> EnvironmentPageAsync(
        ServeQueries queries,
        CancellationToken ct)
    {
        try
        {
            var authority = await queries.ResolveAuthorityAsync(ct);
            if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
            {
                return ServeHttp.Html(Html.Layout(
                    "Environment",
                    $"<h1>Environment</h1><p>{Html.Escape(Html.AuthorityMessage(authority))}</p>"));
            }

            var snapshot = await queries.GetCurrentSnapshotAsync(ct);
            if (snapshot is null)
            {
                return ServeHttp.Html(Html.Layout(
                    "Environment",
                    "<h1>Environment</h1><p>No atlas has been scanned yet. Scan a game installation, then reload this page.</p>"));
            }

            return ServeHttp.Html(EnvironmentView.Render(snapshot));
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Html(Html.Layout(
                "Environment",
                "<h1>Environment</h1><p>No Atlas data store was found.</p>"));
        }
    }

    private static async Task<IResult> ApiEnvironmentAsync(
        ServeQueries queries,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await queries.GetCurrentSnapshotAsync(ct);
            if (snapshot is null)
            {
                return ServeHttp.Envelope(ToolEnvelope<ServeEnvironmentResult>.Unavailable(
                    new ToolError("NoCurrentBuild", "No current environment snapshot is available.")));
            }

            var authority = await queries.ResolveAuthorityAsync(ct, snapshot.Build.BuildId);
            if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
            {
                return ServeHttp.Envelope(AuthorityEnvelope.From<ServeEnvironmentResult>(authority));
            }

            var data = new ServeEnvironmentResult(
                snapshot.Build.BuildId,
                snapshot.Installation.ExecutableVersion,
                snapshot.Installation.SteamAppId,
                snapshot.Installation.SteamBuildId,
                EnvironmentView.RecordedNotShown,
                PortalPathDisplay.ToDisplayPath(
                    snapshot.Installation.GameAssemblyPath, snapshot.Installation.InstallationRoot),
                PortalPathDisplay.ToDisplayPath(
                    snapshot.Installation.GlobalMetadataPath, snapshot.Installation.InstallationRoot),
                snapshot.Dependencies
                    .OrderBy(dependency => dependency.Kind)
                    .ThenBy(dependency => dependency.Version, StringComparer.Ordinal)
                    .ThenBy(dependency => dependency.Path, StringComparer.Ordinal)
                    .Select(dependency => new ServeEnvironmentDependency(
                        dependency.Kind,
                        dependency.Version,
                        PortalPathDisplay.ToDisplayPath(dependency.Path, snapshot.Installation.InstallationRoot),
                        dependency.IsInstalled,
                        dependency.BinarySha256))
                    .ToArray());
            return ServeHttp.Envelope(ToolEnvelope<ServeEnvironmentResult>.Resolved(
                ServeEnvelopes.BuildFrom(authority),
                data,
                new ProvenanceEntry(
                    ProvenanceClassification.Fact,
                    "current-environment-snapshot",
                    snapshot.Build.BuildId,
                    authority.ExtractionId,
                    authority.IndexId)));
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Envelope(ServeEnvelopes.StoreMissing<ServeEnvironmentResult>());
        }
    }
}
