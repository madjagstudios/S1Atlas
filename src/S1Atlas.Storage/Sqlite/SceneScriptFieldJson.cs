using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Core.Scenes;

namespace S1Atlas.Storage.Sqlite;

// Stores a field set's leaves as one JSON array per owner; the list is written and read whole.
internal static class SceneScriptFieldJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(IReadOnlyList<SceneScriptField> fields) =>
        JsonSerializer.Serialize(fields, Options);

    public static IReadOnlyList<SceneScriptField> Deserialize(string json) =>
        JsonSerializer.Deserialize<SceneScriptField[]>(json, Options)
        ?? throw new InvalidDataException("A stored script field set is not a JSON array.");
}
