namespace S1Atlas.Core.Tools;

public interface IToolDefinitionProvider
{
    IReadOnlyList<ResolvedToolDefinition> GetAll();

    ResolvedToolDefinition GetRequired(string toolId, string platform);
}
