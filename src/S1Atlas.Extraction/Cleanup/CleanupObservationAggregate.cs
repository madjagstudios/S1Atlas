using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Extraction.Cleanup;

/// <summary>
/// Computes the canonical aggregate observation digest a candidate carries. Both the
/// planner (at plan time) and the apply service (at preflight) hash the per-owned-path
/// observation digests in the exact owned-path order, so apply can prove a candidate is
/// byte-for-byte unchanged before deleting it.
/// </summary>
internal static class CleanupObservationAggregate
{
    public static string Digest(IEnumerable<string> perPathObservationDigests)
    {
        var builder = new StringBuilder();
        foreach (var digest in perPathObservationDigests)
        {
            builder.Append(digest);
            builder.Append('\n');
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}
