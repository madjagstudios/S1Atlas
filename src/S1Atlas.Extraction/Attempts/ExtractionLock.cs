using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Extraction.Inputs;

namespace S1Atlas.Extraction.Attempts;

internal sealed record ExtractionLockState(
    int SchemaVersion,
    string AttemptId,
    int OwnerProcessId,
    int? ChildProcessId,
    DateTimeOffset StartedAtUtc);

internal sealed class ExtractionAlreadyActiveException : InvalidOperationException
{
    public ExtractionAlreadyActiveException(string attemptId)
        : base($"Extraction attempt '{attemptId}' is already active.")
    {
        AttemptId = attemptId;
    }

    public string AttemptId { get; }
}

internal sealed class ExtractionLock
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _dataRoot;
    private readonly string _lockPath;
    private readonly int _ownerProcessId;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, FileAttributes> _getFileAttributes;

    public ExtractionLock(
        string dataRoot,
        int ownerProcessId,
        Func<int, bool> isProcessAlive,
        TimeProvider? timeProvider = null,
        Func<string, FileAttributes>? getFileAttributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerProcessId);
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _lockPath = Path.Combine(_dataRoot, "extraction.lock");
        _ownerProcessId = ownerProcessId;
        _isProcessAlive = isProcessAlive;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _getFileAttributes = getFileAttributes ?? File.GetAttributes;
    }

    public async Task<ExtractionLockLease> AcquireAsync(
        string attemptId,
        CancellationToken cancellationToken)
    {
        if (!OwnedAttemptPaths.IsLowerGuidN(attemptId))
        {
            throw new ArgumentException(
                "The attempt ID must be a lower-case GUID N value.", nameof(attemptId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        RequireSafeRootAndPath();
        var state = new ExtractionLockState(
            1, attemptId, _ownerProcessId, null,
            _timeProvider.GetUtcNow().ToUniversalTime());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var creationStream = new FileStream(
                    _lockPath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                try
                {
                    await WriteStateAsync(creationStream, state, cancellationToken);
                    await creationStream.DisposeAsync();
                    var retainedHandle = OpenRetainedHandle(_lockPath);
                    return new ExtractionLockLease(
                        _dataRoot, _lockPath, state, retainedHandle);
                }
                catch
                {
                    await creationStream.DisposeAsync();
                    DeleteExactLockBestEffort();
                    throw;
                }
            }
            catch (IOException) when (File.Exists(_lockPath))
            {
                var existing = await ReadAsync(cancellationToken) ??
                    throw new InvalidDataException("The extraction lock disappeared while inspected.");
                if (IsAlive(existing.OwnerProcessId) ||
                    existing.ChildProcessId is int child && IsAlive(child))
                {
                    throw new ExtractionAlreadyActiveException(existing.AttemptId);
                }

                if (attempt != 0)
                {
                    throw new IOException("The stale extraction lock could not be reclaimed.");
                }

                await DeleteStaleAsync(existing, cancellationToken);
            }
        }

        throw new IOException("The extraction lock could not be acquired.");
    }

    public async Task<ExtractionLockState?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireSafeRootAndPath();
        if (!File.Exists(_lockPath))
        {
            return null;
        }

        if (!PathSafety.TryObserveRegularFile(_dataRoot, _lockPath, out _))
        {
            throw new InvalidDataException("The extraction lock is not a regular owned file.");
        }

        try
        {
            var json = await File.ReadAllTextAsync(
                _lockPath, Utf8NoBom, cancellationToken);
            using var parsed = JsonDocument.Parse(json);
            var actualProperties = parsed.RootElement.ValueKind == JsonValueKind.Object
                ? parsed.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
                : [];
            var expectedProperties = new HashSet<string>(
                ["schemaVersion", "attemptId", "ownerProcessId", "childProcessId", "startedAtUtc"],
                StringComparer.Ordinal);
            if (actualProperties.Length != expectedProperties.Count ||
                !actualProperties.ToHashSet(StringComparer.Ordinal).SetEquals(expectedProperties))
            {
                throw new InvalidDataException("The extraction lock document is malformed.");
            }

            var document = JsonSerializer.Deserialize<LockDocument>(json, JsonOptions);
            if (document?.SchemaVersion != 1 ||
                !OwnedAttemptPaths.IsLowerGuidN(document.AttemptId) ||
                document.OwnerProcessId is null or <= 0 ||
                document.ChildProcessId is <= 0 ||
                document.StartedAtUtc is null)
            {
                throw new InvalidDataException("The extraction lock document is malformed.");
            }

            return new ExtractionLockState(
                1, document.AttemptId!, document.OwnerProcessId.Value,
                document.ChildProcessId,
                document.StartedAtUtc.Value.ToUniversalTime());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The extraction lock document is malformed.", exception);
        }
    }

    internal bool IsAlive(int processId)
    {
        try
        {
            return _isProcessAlive(processId);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Process liveness for PID {processId} could not be proven.", exception);
        }
    }

    internal async Task DeleteStaleAsync(
        ExtractionLockState expected,
        CancellationToken cancellationToken)
    {
        var current = await ReadAsync(cancellationToken);
        if (current != expected)
        {
            throw new InvalidOperationException(
                "The extraction lock changed while stale ownership was verified.");
        }

        if (IsAlive(current.OwnerProcessId) ||
            current.ChildProcessId is int child && IsAlive(child))
        {
            throw new ExtractionAlreadyActiveException(current.AttemptId);
        }

        File.Delete(_lockPath);
    }

    private void RequireSafeRootAndPath()
    {
        if (!PathSafety.IsNormalDirectory(_dataRoot))
        {
            throw new InvalidOperationException("The Atlas data root is unsafe.");
        }

        OwnedAttemptPaths.EnsureSafeExistingPath(
            _dataRoot,
            _lockPath,
            allowFinalFile: true,
            getFileAttributes: _getFileAttributes);
    }

    private void DeleteExactLockBestEffort()
    {
        try
        {
            File.Delete(_lockPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task WriteStateAsync(
        FileStream stream,
        ExtractionLockState state,
        CancellationToken cancellationToken)
    {
        var document = new LockDocument
        {
            SchemaVersion = 1,
            AttemptId = state.AttemptId,
            OwnerProcessId = state.OwnerProcessId,
            ChildProcessId = state.ChildProcessId,
            StartedAtUtc = state.StartedAtUtc
        };
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(document, JsonOptions));
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    internal static FileStream OpenRetainedHandle(string lockPath) => new(
        lockPath, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);

    private sealed class LockDocument
    {
        public int? SchemaVersion { get; init; }
        public string? AttemptId { get; init; }
        public int? OwnerProcessId { get; init; }
        public int? ChildProcessId { get; init; }
        public DateTimeOffset? StartedAtUtc { get; init; }
    }
}

internal sealed class ExtractionLockLease : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _dataRoot;
    private readonly string _lockPath;
    private ExtractionLockState _state;
    private FileStream? _handle;
    private bool _finished;

    internal ExtractionLockLease(
        string dataRoot,
        string lockPath,
        ExtractionLockState state,
        FileStream handle)
    {
        _dataRoot = dataRoot;
        _lockPath = lockPath;
        _state = state;
        _handle = handle;
    }

    public async Task UpdateChildProcessIdAsync(
        int childProcessId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childProcessId);
        ThrowIfFinished();
        cancellationToken.ThrowIfCancellationRequested();
        await VerifyCurrentOwnershipAsync(cancellationToken);
        var updated = _state with { ChildProcessId = childProcessId };
        var temporary = $"{_lockPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            OwnedAttemptPaths.EnsureSafeExistingPath(
                _dataRoot, temporary, allowFinalFile: true);
            var document = new
            {
                schemaVersion = 1,
                attemptId = updated.AttemptId,
                ownerProcessId = updated.OwnerProcessId,
                childProcessId = updated.ChildProcessId,
                startedAtUtc = updated.StartedAtUtc
            };
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(document, JsonOptions));
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await VerifyCurrentOwnershipAsync(cancellationToken);
            OwnedAttemptPaths.EnsureSafeExistingPath(
                _dataRoot, _lockPath, allowFinalFile: true);
            await DisposeHandleAsync();
            File.Move(temporary, _lockPath, overwrite: true);
            _handle = ExtractionLock.OpenRetainedHandle(_lockPath);
            _state = updated;
        }
        finally
        {
            DeleteOwnedTemporaryBestEffort(temporary);
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var manager = new ExtractionLock(
            _dataRoot, _state.OwnerProcessId, _ => false, TimeProvider.System);
        var current = await manager.ReadAsync(cancellationToken);
        if (current is null || current.AttemptId != _state.AttemptId ||
            current.OwnerProcessId != _state.OwnerProcessId)
        {
            _finished = true;
            await DisposeHandleAsync();
            throw new InvalidOperationException(
                "The extraction lock ownership changed; evidence was preserved.");
        }

        await DisposeHandleAsync();
        var verified = await manager.ReadAsync(cancellationToken);
        if (verified is null || verified.AttemptId != _state.AttemptId ||
            verified.OwnerProcessId != _state.OwnerProcessId)
        {
            _finished = true;
            throw new InvalidOperationException(
                "The extraction lock ownership changed; evidence was preserved.");
        }

        File.Delete(_lockPath);
        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished)
        {
            await ReleaseAsync(CancellationToken.None);
        }
    }

    private void ThrowIfFinished()
    {
        if (_finished)
        {
            throw new ObjectDisposedException(nameof(ExtractionLockLease));
        }
    }

    private async Task VerifyCurrentOwnershipAsync(CancellationToken cancellationToken)
    {
        var manager = new ExtractionLock(
            _dataRoot, _state.OwnerProcessId, _ => false, TimeProvider.System);
        var current = await manager.ReadAsync(cancellationToken);
        if (current is not null && current.AttemptId == _state.AttemptId &&
            current.OwnerProcessId == _state.OwnerProcessId)
        {
            return;
        }

        _finished = true;
        await DisposeHandleAsync();
        throw new InvalidOperationException(
            "The extraction lock ownership changed; evidence was preserved.");
    }

    private async Task DisposeHandleAsync()
    {
        if (_handle is not null)
        {
            await _handle.DisposeAsync();
            _handle = null;
        }
    }

    private void DeleteOwnedTemporaryBestEffort(string temporary)
    {
        try
        {
            if (Path.GetDirectoryName(temporary) == _dataRoot &&
                Path.GetFileName(temporary).StartsWith("extraction.lock.", StringComparison.Ordinal) &&
                Path.GetFileName(temporary).EndsWith(".tmp", StringComparison.Ordinal))
            {
                File.Delete(temporary);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
