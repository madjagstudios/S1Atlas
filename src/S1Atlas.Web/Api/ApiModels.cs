using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Web.Api;

public sealed record ServeIndexStatus(
    CodebaseKind Codebase,
    CodeChannel Channel,
    string IndexId,
    int SymbolCount);

public sealed record ServeStatusResult(
    string? ResolvedBuildId,
    InstalledBuildAuthorityStatus AuthorityStatus,
    string? Message,
    IReadOnlyList<ServeIndexStatus> Indexes);
