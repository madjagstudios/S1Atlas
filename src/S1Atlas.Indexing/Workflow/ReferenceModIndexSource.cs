using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.ReferenceMods;

namespace S1Atlas.Indexing.Workflow;

public sealed class ReferenceModIndexSource
{
    public const int MaximumDocumentBytes = 1_048_576;
    private readonly IManagedDecompiler _decompiler;

    public ReferenceModIndexSource(IManagedDecompiler decompiler)
    {
        _decompiler = decompiler ?? throw new ArgumentNullException(nameof(decompiler));
    }

    public Task<ManagedDecompilation> ReadModAssemblyAsync(
        ReferenceModInputFile input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Kind != ReferenceModInputKind.ManagedAssembly)
            throw new ArgumentException("Only managed-assembly inputs can be decompiled.", nameof(input));
        return _decompiler.DecompileAsync(input.FullPath, cancellationToken);
    }

    public async Task<IndexReferenceDocumentRecord> ReadDocumentAsync(
        ReferenceModInputFileHash input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Kind == ReferenceModInputKind.ManagedAssembly)
            throw new ArgumentException("Managed assemblies are not text documents.", nameof(input));
        if (input.ByteCount > MaximumDocumentBytes)
            throw new InvalidDataException($"Reference document '{input.RelativePath}' exceeds the {MaximumDocumentBytes}-byte local evidence limit.");

        var bytes = await File.ReadAllBytesAsync(input.FullPath, cancellationToken);
        if (bytes.LongLength > MaximumDocumentBytes)
            throw new InvalidDataException($"Reference document '{input.RelativePath}' exceeds the {MaximumDocumentBytes}-byte local evidence limit.");
        return new IndexReferenceDocumentRecord(
            input.ModId,
            input.RelativePath,
            input.DeclaredDocumentKind ?? "Document",
            input.Sha256,
            bytes.LongLength,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes));
    }
}
