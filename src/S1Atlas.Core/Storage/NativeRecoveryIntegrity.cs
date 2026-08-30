using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Core.Storage;

public static class NativeRecoveryIntegrity
{
    public static string ComputeOutputSha256(
        NativeRecoveryStatus status,
        IReadOnlyList<string> mappingEvidence,
        IReadOnlyList<NativeEvidenceEdge> edges,
        IReadOnlyList<string> fieldAccesses,
        bool isComplete,
        string? failureMessage)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "s1atlas-native-recovery-output-v1");
        Append(hash, status.ToString());
        Append(hash, mappingEvidence.Count);
        foreach (var evidence in mappingEvidence)
            Append(hash, evidence);
        Append(hash, edges.Count);
        foreach (var edge in edges)
        {
            Append(hash, edge.EdgeId);
            Append(hash, edge.SourceMethodPointer);
            AppendOptional(hash, edge.TargetMethodPointer);
            AppendOptional(hash, edge.TargetText);
            Append(hash, edge.Kind);
            Append(hash, edge.Evidence);
            Append(hash, edge.IsComplete);
        }
        Append(hash, fieldAccesses.Count);
        foreach (var fieldAccess in fieldAccesses)
            Append(hash, fieldAccess);
        Append(hash, isComplete);
        AppendOptional(hash, failureMessage);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeRecoveryId(
        NativeRecoveryRequest request,
        string toolName,
        string toolVersion,
        string toolSha256,
        string outputSha256)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "s1atlas-native-recovery-record-v1");
        Append(hash, request.BuildId);
        Append(hash, request.IndexId);
        Append(hash, request.GameAssemblySha256);
        Append(hash, request.SymbolIds.Count);
        foreach (var symbolId in request.SymbolIds)
            Append(hash, symbolId);
        Append(hash, request.MaxTraversalEdges);
        Append(hash, toolName);
        Append(hash, toolVersion);
        Append(hash, toolSha256);
        Append(hash, outputSha256);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendOptional(IncrementalHash hash, string? value)
    {
        Append(hash, value is not null);
        if (value is not null)
            Append(hash, value);
    }

    private static void Append(IncrementalHash hash, bool value) =>
        Append(hash, value ? "true" : "false");

    private static void Append(IncrementalHash hash, int value) =>
        Append(hash, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
