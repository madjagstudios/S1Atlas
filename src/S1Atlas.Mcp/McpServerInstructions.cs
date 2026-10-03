namespace S1Atlas.Mcp;

/// <summary>
/// Single home of the MCP server instructions surfaced through initialize.
/// </summary>
internal static class McpServerInstructions
{
    public const string Text = """
        S1Atlas is a read-only evidence source for Schedule I internals. Every tool returns a JSON envelope with a status: resolved (the answer is in data), ambiguous (pick from candidates), not_found, invalid, or unavailable. Failure statuses arrive with isError=true; the envelope text still carries the full detail including the error code. Setup errors such as no_current_build or no_completed_index carry a hint field with the exact CLI fix command; run that command, or run s1atlas doctor to see the full readiness checklist.

        Evidence loop: search_symbols to locate candidates, then get_type, get_method, or get_source to resolve the exact symbol and inspect its decompiled span, then find_callers, find_callees, find_references, find_call_sites, find_field_references, find_related_types, find_overrides, find_overriders, or find_derived_types to trace relationships. The shared code tools take a codebase of scheduleI, s1api, or s1mapi with an optional channel; get_scene resolves scenes and prefabs by name.

        Symbol selectors: a symbol ID (symbolId), a canonical key like ScheduleI:Installed:Type:Namespace.Name, an exact signature such as System.Void Demo.Widget::Run(), an exact qualified name such as Demo.Widget, or a name fragment. Fragments may return ambiguous with a candidates list; re-call with one candidate's symbolId instead of guessing.

        Provenance: label a claim FACT when it is directly returned from indexed metadata, source, or a resolved relationship, and DERIVED when it is computed from returned facts. Cite the resolved build, extraction, and index IDs from the envelope.

        Scope: buildId and sceneSnapshotId may be omitted to use the current build and snapshot; an explicit ID is never silently replaced. compare_symbol requires two explicit build IDs.

        Paging: paged tools return nextCursor when more rows exist. Pass it as cursor with otherwise identical arguments for the next page; changing arguments or re-indexing invalidates the cursor.

        Static results do not prove runtime behavior: recovered-IL relationships, call sites, field references, and source hints describe indexed code only. Verify behavior in-game.
        """;
}
