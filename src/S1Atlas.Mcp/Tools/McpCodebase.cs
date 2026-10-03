namespace S1Atlas.Mcp.Tools;

// Codebase selector for the shared code tools. The members carry the wire
// spellings exactly (scheduleI | s1api | s1mapi): the MCP SDK emits enum
// member names verbatim into input schemas and ignores EnumMember, so any
// other member names would advertise the wrong values. ReferenceMod is
// deliberately absent; reference routing stays on scope/collection.
public enum McpCodebase
{
    scheduleI,
    s1api,
    s1mapi
}
