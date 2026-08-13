using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Core.Identity;

internal sealed class CanonicalHashWriter : IDisposable
{
    private readonly IncrementalHash _hash =
        IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private bool _completed;
    private bool _disposed;

    public CanonicalHashWriter(string identityKind, int identityVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityKind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(identityVersion);

        AppendString(identityKind);
        AppendInt32(identityVersion);
    }

    public void AppendString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureWritable();
        AppendNonNullScalar(value);
    }

    public void AppendNullableString(string? value)
    {
        EnsureWritable();

        if (value is null)
        {
            Span<byte> marker = stackalloc byte[1];
            marker[0] = 0;
            _hash.AppendData(marker);
            return;
        }

        AppendNonNullScalar(value);
    }

    public void AppendInt32(int value) =>
        AppendString(value.ToString(CultureInfo.InvariantCulture));

    public void AppendInt64(long value) =>
        AppendString(value.ToString(CultureInfo.InvariantCulture));

    public void AppendBoolean(bool value) =>
        AppendString(value ? "1" : "0");

    public string Complete()
    {
        EnsureWritable();
        _completed = true;

        return Convert
            .ToHexString(_hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _hash.Dispose();
        _disposed = true;
    }

    private void AppendNonNullScalar(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> prefix = stackalloc byte[1 + sizeof(int)];
        prefix[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(prefix[1..], bytes.Length);
        _hash.AppendData(prefix);
        _hash.AppendData(bytes);
    }

    private void EnsureWritable()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CanonicalHashWriter));
        }

        if (_completed)
        {
            throw new InvalidOperationException(
                "The canonical identity hash has already been completed.");
        }
    }
}
