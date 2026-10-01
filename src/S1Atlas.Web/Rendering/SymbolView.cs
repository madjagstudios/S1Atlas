using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Web.Queries;

namespace S1Atlas.Web.Rendering;

internal sealed record SymbolModel(
    ServeIndex Index,
    SymbolQueryResult Symbol,
    MemberListResult Members,
    SourceSnippetQueryResult? Source,
    RelationshipQuerySetResult Callers,
    RelationshipQuerySetResult Callees,
    RelationshipQuerySetResult References);

internal static class SymbolView
{
    internal static string Render(SymbolModel model)
    {
        var symbol = model.Symbol;
        var body = new StringBuilder();
        body.Append(Html.PageTitle(symbol.QualifiedName));
        body.Append(Html.CodebaseBadge(symbol.Codebase));
        body.Append($"<span class=\"badge\">{Html.Escape(Html.KindLabel(symbol.Kind))}</span>");
        body.Append($"<p class=\"fact\">FACT: {Html.Escape(symbol.Kind)} {Html.Escape(symbol.QualifiedName)} in {Html.Escape(Html.CodebaseLabel(model.Index.Codebase))} ({Html.Escape(Html.ChannelLabel(model.Index.Channel))}).</p>");
        if (!string.IsNullOrWhiteSpace(symbol.Signature))
        {
            body.Append($"<pre><code>{Html.Escape(symbol.Signature)}</code></pre>");
        }

        body.Append($"<p>Symbol ID: <code>{Html.Escape(symbol.SymbolId)}</code></p>");
        if (symbol.Kind.Equals("Type", StringComparison.Ordinal))
        {
            RenderMembers(body, model.Members);
        }

        RenderSource(body, model.Source);
        RenderRelationships(body, "Callers", "callers", model.Callers, symbol.SymbolId);
        RenderRelationships(body, "Callees", "callees", model.Callees, symbol.SymbolId);
        RenderRelationships(body, "References", "references", model.References, symbol.SymbolId);
        return Html.Layout(symbol.QualifiedName, body.ToString());
    }

    private static void RenderMembers(StringBuilder body, MemberListResult members)
    {
        body.Append("<section><h2>Members</h2>");
        body.Append($"<p class=\"fact\">FACT: {members.Members.Count} members.</p>");
        if (members.Truncated)
        {
            body.Append($"<p class=\"derived\">DERIVED: showing the first {members.Members.Count} of the true total {members.TotalCount}.</p>");
        }

        if (members.Members.Count > 0)
        {
            body.Append("<ul>");
            foreach (var member in members.Members)
            {
                body.Append("<li>");
                body.Append($"<span class=\"badge\">{Html.Escape(Html.KindLabel(member.Kind))}</span> ");
                body.Append($"<a href=\"/symbol/{Html.UrlSegment(member.SymbolId)}\">{Html.Escape(member.QualifiedName)}</a>");
                if (!string.IsNullOrWhiteSpace(member.Signature))
                {
                    body.Append($"<br><code>{Html.Escape(member.Signature)}</code>");
                }

                body.Append("</li>");
            }

            body.Append("</ul>");
        }

        body.Append("</section>");
    }

    private static void RenderSource(StringBuilder body, SourceSnippetQueryResult? source)
    {
        body.Append("<section><h2>Source</h2>");
        if (source is null)
        {
            body.Append("<p class=\"derived\">DERIVED: no displayed source span is available.</p>");
            body.Append("</section>");
            return;
        }

        var firstLine = Math.Max(1, source.Location.StartLine - source.ContextBefore);
        var lines = source.Text.Split('\n');
        var lastLine = firstLine + lines.Length - 1;
        body.Append($"<p class=\"fact\">FACT: {Html.Escape(source.RelativePath)}, lines {firstLine}&ndash;{lastLine}.</p>");
        body.Append("<pre class=\"source\"><code>");
        for (var i = 0; i < lines.Length; i++)
        {
            body.Append($"<span class=\"ln\">{firstLine + i,4} </span>{Html.Escape(lines[i].TrimEnd('\r'))}\n");
        }

        body.Append("</code></pre></section>");
    }

    private static void RenderRelationships(
        StringBuilder body,
        string title,
        string plural,
        RelationshipQuerySetResult set,
        string pageSymbolId)
    {
        var total = set.TotalCount ?? set.Relationships.Count;
        body.Append($"<section><h2>{title}</h2>");
        body.Append($"<p class=\"fact\">FACT: {total} {plural} in this index.</p>");
        body.Append($"<p class=\"derived\">DERIVED: showing {set.Relationships.Count} of the true total {total}.</p>");
        if (!string.IsNullOrWhiteSpace(set.CompletenessNotice))
        {
            body.Append($"<p class=\"fact\">FACT: {Html.Escape(set.CompletenessNotice)}</p>");
        }

        if (set.Relationships.Count > 0)
        {
            body.Append("<ul>");
            foreach (var relationship in set.Relationships)
            {
                body.Append($"<li>{OtherEnd(relationship, pageSymbolId)} &mdash; FACT: {Html.Escape(relationship.Kind)} ({Html.Escape(relationship.Evidence)}).</li>");
            }

            body.Append("</ul>");
        }

        body.Append("</section>");
    }

    private static string OtherEnd(RelationshipQueryResult relationship, string pageSymbolId)
    {
        var other = relationship.Target.SymbolId?.Equals(pageSymbolId, StringComparison.Ordinal) == true
            ? relationship.Source
            : relationship.Target;
        var label = other.QualifiedName ?? other.Signature ?? other.RawText ?? "(unresolved)";
        return string.IsNullOrEmpty(other.SymbolId)
            ? Html.Escape(label)
            : $"<a href=\"/symbol/{Html.UrlSegment(other.SymbolId)}\">{Html.Escape(label)}</a>";
    }
}
