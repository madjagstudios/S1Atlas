namespace S1Atlas.Extraction.Processes;

internal sealed record ProcessRequest(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> EnvironmentOverrides,
    string StandardOutputPath,
    string StandardErrorPath,
    long MaximumRetainedStandardOutputBytes,
    long MaximumRetainedStandardErrorBytes,
    TimeSpan Timeout);
