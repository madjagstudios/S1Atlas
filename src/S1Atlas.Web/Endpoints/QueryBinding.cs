using S1Atlas.Core.Indexing;

namespace S1Atlas.Web.Endpoints;

public sealed class ServeInvalidQueryException : Exception
{
    public ServeInvalidQueryException(string message)
        : base(message)
    {
    }
}

public sealed class ServeScopeMissingException : Exception
{
    public ServeScopeMissingException(string message)
        : base(message)
    {
    }
}

internal sealed record SearchArgs(string Query, SymbolKind? Kind, CodebaseKind Codebase, int Page);

internal static class QueryBinding
{
    internal const int SearchPageSize = 20;
    internal const int DefaultRelationshipLimit = 50;
    internal const int MaxLimit = 500;

    internal static SearchArgs BindSearch(Microsoft.AspNetCore.Http.IQueryCollection query) =>
        new(
            query["q"].ToString() ?? string.Empty,
            BindKind(query["kind"].ToString()),
            BindCodebase(query["codebase"].ToString()),
            BindPage(query["page"].ToString()));

    internal static CodebaseKind BindCodebase(string? value) =>
        (string.IsNullOrEmpty(value) ? "schedule-i" : value).ToLowerInvariant() switch
        {
            "schedule-i" or "schedulei" => CodebaseKind.ScheduleI,
            "s1api" => CodebaseKind.S1Api,
            "s1mapi" => CodebaseKind.S1MApi,
            _ => throw new ServeInvalidQueryException(
                $"Unknown codebase '{value}'. Use schedule-i, s1api, or s1mapi.")
        };

    internal static SymbolKind? BindKind(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (Enum.TryParse<SymbolKind>(value, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
        {
            return kind;
        }

        throw new ServeInvalidQueryException(
            $"Unknown kind '{value}'. Use type, constructor, method, field, property, or event.");
    }

    internal static int BindPage(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        if (int.TryParse(value, out var page) && page >= 0)
        {
            return page;
        }

        throw new ServeInvalidQueryException($"Invalid page '{value}'. Use 0 or a positive integer.");
    }

    internal static int BindLimit(string? value, int fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (int.TryParse(value, out var limit) && limit >= 1 && limit <= MaxLimit)
        {
            return limit;
        }

        throw new ServeInvalidQueryException($"Invalid limit '{value}'. Use 1 to {MaxLimit}.");
    }
}
