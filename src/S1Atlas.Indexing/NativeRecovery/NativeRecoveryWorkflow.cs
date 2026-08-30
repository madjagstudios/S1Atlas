using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.NativeRecovery;

public sealed class NativeRecoveryWorkflow
{
    private const int MinimumTraversalEdges = 1;
    private const int MaximumTraversalEdges = 500;
    private const int MaximumSummaryLength = 512;
    private const string InputChangedMessage =
        "Current build, index, or GameAssembly.dll identity does not match the recovery request.";
    private const string UnsupportedMessage = "No native body recovery provider is configured.";

    private readonly INativeBodyRecoveryProvider? _provider;
    private readonly TimeProvider _timeProvider;

    public NativeRecoveryWorkflow(
        INativeBodyRecoveryProvider? provider,
        TimeProvider? timeProvider = null)
    {
        _provider = provider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<NativeRecoveryRecord> RecoverAsync(
        NativeRecoveryRequest request,
        NativeRecoveryExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var canonicalRequest = ValidateAndCanonicalize(request);
        Validate(executionContext);

        if (!InputMatches(canonicalRequest, executionContext))
        {
            return CreateRecord(
                canonicalRequest,
                executionContext,
                NativeRecoveryStatus.InputChanged,
                mappingEvidence: [],
                edges: [],
                fieldAccesses: [],
                isComplete: false,
                InputChangedMessage);
        }

        if (_provider is null)
        {
            return CreateRecord(
                canonicalRequest,
                executionContext,
                NativeRecoveryStatus.Unsupported,
                mappingEvidence: [],
                edges: [],
                fieldAccesses: [],
                isComplete: false,
                UnsupportedMessage);
        }

        NativeRecoveryRecord providerRecord;
        try
        {
            providerRecord = await _provider.RecoverAsync(canonicalRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CreateRecord(
                canonicalRequest,
                executionContext,
                NativeRecoveryStatus.Failed,
                mappingEvidence: [],
                edges: [],
                fieldAccesses: [],
                isComplete: false,
                "Native recovery provider failed.");
        }

        return NormalizeProviderRecord(canonicalRequest, executionContext, providerRecord);
    }

    private NativeRecoveryRecord NormalizeProviderRecord(
        NativeRecoveryRequest request,
        NativeRecoveryExecutionContext executionContext,
        NativeRecoveryRecord providerRecord)
    {
        if (providerRecord is null)
        {
            return InvalidProviderRecord(request, executionContext, "The provider returned no record.");
        }

        try
        {
            var providerRequest = ValidateAndCanonicalize(providerRecord.Request);
            if (!RequestsMatch(request, providerRequest))
            {
                return CreateRecord(
                    request,
                    executionContext,
                    NativeRecoveryStatus.InputChanged,
                    mappingEvidence: [],
                    edges: [],
                    fieldAccesses: [],
                    isComplete: false,
                    "The provider returned evidence for different recovery inputs.");
            }

            if (!ToolMatches(providerRecord, executionContext))
            {
                return InvalidProviderRecord(
                    request,
                    executionContext,
                    "The provider tool identity did not match the configured tool identity.");
            }

            if (!Enum.IsDefined(providerRecord.Status))
            {
                return InvalidProviderRecord(request, executionContext, "The provider returned an unknown recovery status.");
            }

            var mappingEvidence = CanonicalizeText(providerRecord.MappingEvidence, nameof(providerRecord.MappingEvidence));
            var fieldAccesses = CanonicalizeText(providerRecord.FieldAccesses, nameof(providerRecord.FieldAccesses));
            var allEdges = CanonicalizeEdges(providerRecord.Edges);
            var duplicateEdgeId = allEdges
                .GroupBy(edge => edge.EdgeId, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicateEdgeId is not null)
            {
                return InvalidProviderRecord(
                    request,
                    executionContext,
                    $"The provider returned duplicate native edge ID '{duplicateEdgeId}'.");
            }

            var truncated = allEdges.Count > request.MaxTraversalEdges;
            var edges = allEdges.Take(request.MaxTraversalEdges).ToArray();
            var isRecovered = providerRecord.Status == NativeRecoveryStatus.Recovered;
            if (!isRecovered)
            {
                edges = [];
                fieldAccesses = [];
                if (providerRecord.Status is not (NativeRecoveryStatus.NoBody or NativeRecoveryStatus.AmbiguousMapping))
                    mappingEvidence = [];
            }

            if (isRecovered && (mappingEvidence.Count == 0 || (edges.Length == 0 && fieldAccesses.Count == 0)))
                return InvalidProviderRecord(request, executionContext, "Native recovery did not return sufficient evidence.");

            var isComplete = isRecovered &&
                providerRecord.IsComplete &&
                !truncated &&
                edges.All(edge => edge.IsComplete);

            var failureMessage = isRecovered
                ? null
                : SanitizeFailureMessage(providerRecord.FailureMessage, providerRecord.Status);

            return CreateRecord(
                request,
                executionContext,
                providerRecord.Status,
                mappingEvidence,
                edges,
                fieldAccesses,
                isComplete,
                failureMessage);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return InvalidProviderRecord(request, executionContext, "The provider returned invalid evidence.");
        }
    }

    private NativeRecoveryRecord InvalidProviderRecord(
        NativeRecoveryRequest request,
        NativeRecoveryExecutionContext executionContext,
        string _) =>
        CreateRecord(
            request,
            executionContext,
            NativeRecoveryStatus.Failed,
            mappingEvidence: [],
            edges: [],
            fieldAccesses: [],
            isComplete: false,
            "Native recovery provider returned invalid evidence.");

    private NativeRecoveryRecord CreateRecord(
        NativeRecoveryRequest request,
        NativeRecoveryExecutionContext executionContext,
        NativeRecoveryStatus status,
        IReadOnlyList<string> mappingEvidence,
        IReadOnlyList<NativeEvidenceEdge> edges,
        IReadOnlyList<string> fieldAccesses,
        bool isComplete,
        string? failureMessage)
    {
        var outputSha256 = NativeRecoveryIntegrity.ComputeOutputSha256(
            status,
            mappingEvidence,
            edges,
            fieldAccesses,
            isComplete,
            failureMessage);
        var recoveryId = NativeRecoveryIntegrity.ComputeRecoveryId(
            request,
            executionContext.ToolName,
            executionContext.ToolVersion,
            executionContext.ToolSha256,
            outputSha256);
        return new NativeRecoveryRecord(
            recoveryId,
            request,
            executionContext.ToolName,
            executionContext.ToolVersion,
            executionContext.ToolSha256,
            status,
            mappingEvidence,
            edges,
            fieldAccesses,
            isComplete,
            outputSha256,
            _timeProvider.GetUtcNow(),
            failureMessage);
    }

    private static NativeRecoveryRequest ValidateAndCanonicalize(NativeRecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireText(request.BuildId, nameof(request.BuildId));
        RequireText(request.IndexId, nameof(request.IndexId));
        RequireSha256(request.GameAssemblySha256, nameof(request.GameAssemblySha256));
        ArgumentNullException.ThrowIfNull(request.SymbolIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            request.MaxTraversalEdges,
            MinimumTraversalEdges,
            nameof(request.MaxTraversalEdges));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            request.MaxTraversalEdges,
            MaximumTraversalEdges,
            nameof(request.MaxTraversalEdges));

        if (request.SymbolIds.Count == 0)
            throw new ArgumentException("At least one native symbol ID must be selected.", nameof(request.SymbolIds));

        var symbolIds = request.SymbolIds
            .Select(symbolId => RequireText(symbolId, nameof(request.SymbolIds)))
            .OrderBy(symbolId => symbolId, StringComparer.Ordinal)
            .ToArray();
        if (symbolIds.Distinct(StringComparer.Ordinal).Count() != symbolIds.Length)
            throw new ArgumentException("Native symbol IDs must be unique.", nameof(request.SymbolIds));

        return request with { SymbolIds = symbolIds };
    }

    private static void Validate(NativeRecoveryExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        RequireText(executionContext.CurrentBuildId, nameof(executionContext.CurrentBuildId));
        RequireText(executionContext.CurrentIndexId, nameof(executionContext.CurrentIndexId));
        RequireSha256(executionContext.CurrentGameAssemblySha256, nameof(executionContext.CurrentGameAssemblySha256));
        RequireText(executionContext.ToolName, nameof(executionContext.ToolName));
        RequireText(executionContext.ToolVersion, nameof(executionContext.ToolVersion));
        RequireSha256(executionContext.ToolSha256, nameof(executionContext.ToolSha256));
    }

    private static IReadOnlyList<string> CanonicalizeText(
        IReadOnlyList<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return values
            .Select(value => SanitizeSummary(value, parameterName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<NativeEvidenceEdge> CanonicalizeEdges(IReadOnlyList<NativeEvidenceEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        return edges
            .Select(CanonicalizeEdge)
            .Select(edge => edge with { EdgeId = CreateCanonicalEdgeId(edge) })
            .OrderBy(edge => edge.SourceMethodPointer, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetMethodPointer, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetText, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind, StringComparer.Ordinal)
            .ThenBy(edge => edge.Evidence, StringComparer.Ordinal)
            .ThenBy(edge => edge.IsComplete)
            .ThenBy(edge => edge.EdgeId, StringComparer.Ordinal)
            .ToArray();
    }

    private static NativeEvidenceEdge CanonicalizeEdge(NativeEvidenceEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var source = SanitizeSummary(edge.SourceMethodPointer, nameof(edge.SourceMethodPointer));
        var target = edge.TargetMethodPointer is null
            ? null
            : SanitizeSummary(edge.TargetMethodPointer, nameof(edge.TargetMethodPointer));
        var targetText = edge.TargetText is null
            ? null
            : SanitizeSummary(edge.TargetText, nameof(edge.TargetText));
        var kind = SanitizeSummary(edge.Kind, nameof(edge.Kind));
        var evidence = SanitizeSummary(edge.Evidence, nameof(edge.Evidence));
        var isDirect = kind.Equals("DirectCall", StringComparison.Ordinal) && target is not null;
        if (isDirect)
        {
            return edge with
            {
                SourceMethodPointer = source,
                TargetMethodPointer = target,
                TargetText = targetText,
                Kind = "DirectCall",
                Evidence = evidence
            };
        }

        var unknownEvidence = evidence.StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase)
            ? evidence
            : "UNKNOWN: " + evidence;
        return edge with
        {
            SourceMethodPointer = source,
            TargetMethodPointer = null,
            TargetText = targetText,
            Kind = "UNKNOWN",
            Evidence = unknownEvidence,
            IsComplete = false
        };
    }

    private static string SanitizeFailureMessage(string? failureMessage, NativeRecoveryStatus status)
    {
        if (string.IsNullOrWhiteSpace(failureMessage))
        {
            return status switch
            {
                NativeRecoveryStatus.NoBody => "The selected native method has no recoverable body.",
                NativeRecoveryStatus.AmbiguousMapping => "The selected symbol has ambiguous native mapping.",
                NativeRecoveryStatus.InputChanged => InputChangedMessage,
                NativeRecoveryStatus.Unsupported => UnsupportedMessage,
                NativeRecoveryStatus.Failed => "Native recovery failed.",
                _ => "Native recovery did not complete."
            };
        }

        return SanitizeSummary(failureMessage, nameof(failureMessage));
    }

    private static string SanitizeSummary(string value, string parameterName)
    {
        RequireText(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > MaximumSummaryLength ||
            trimmed.Contains('\0') ||
            trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.Contains('\\') ||
            trimmed.Contains('/') ||
            trimmed.Contains(".bin", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("disassembly", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{parameterName} must be a bounded evidence summary.");
        }

        return trimmed;
    }

    private static string CreateCanonicalEdgeId(NativeEvidenceEdge edge)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "s1atlas-native-edge-v1");
        Append(hash, edge.SourceMethodPointer);
        AppendOptional(hash, edge.TargetMethodPointer);
        AppendOptional(hash, edge.TargetText);
        Append(hash, edge.Kind);
        Append(hash, edge.Evidence);
        Append(hash, edge.IsComplete);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool InputMatches(
        NativeRecoveryRequest request,
        NativeRecoveryExecutionContext executionContext) =>
        string.Equals(request.BuildId, executionContext.CurrentBuildId, StringComparison.Ordinal) &&
        string.Equals(request.IndexId, executionContext.CurrentIndexId, StringComparison.Ordinal) &&
        string.Equals(
            request.GameAssemblySha256,
            executionContext.CurrentGameAssemblySha256,
            StringComparison.Ordinal);

    private static bool RequestsMatch(NativeRecoveryRequest left, NativeRecoveryRequest right) =>
        string.Equals(left.BuildId, right.BuildId, StringComparison.Ordinal) &&
        string.Equals(left.IndexId, right.IndexId, StringComparison.Ordinal) &&
        string.Equals(left.GameAssemblySha256, right.GameAssemblySha256, StringComparison.Ordinal) &&
        left.MaxTraversalEdges == right.MaxTraversalEdges &&
        left.SymbolIds.SequenceEqual(right.SymbolIds, StringComparer.Ordinal);

    private static bool ToolMatches(
        NativeRecoveryRecord providerRecord,
        NativeRecoveryExecutionContext executionContext) =>
        string.Equals(providerRecord.ToolName, executionContext.ToolName, StringComparison.Ordinal) &&
        string.Equals(providerRecord.ToolVersion, executionContext.ToolVersion, StringComparison.Ordinal) &&
        string.Equals(providerRecord.ToolSha256, executionContext.ToolSha256, StringComparison.Ordinal);

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

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static void RequireSha256(string value, string parameterName)
    {
        RequireText(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The value must be a 64-character lower-case SHA-256 digest.", parameterName);
    }
}
