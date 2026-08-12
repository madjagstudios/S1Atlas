namespace S1Atlas.Extraction.Steam;

internal sealed record SteamAppManifest(
    string AppId,
    string InstallDirectory,
    string BuildId);
