using System.Text.RegularExpressions;

namespace S1Atlas.NativeRecovery;

/// <summary>
/// Provides utilities for formatting native evidence strings in a safe, slash-free format.
/// All outputs are guaranteed to pass <see cref="IsSummarySafe"/>.
/// </summary>
public static class NativeNameNormalizer
{
    private const int MaximumSummaryLength = 512;

    /// <summary>
    /// Formats a virtual address as lowercase hexadecimal with a 0x prefix and no separators.
    /// </summary>
    /// <param name="virtualAddress">The virtual address to format.</param>
    /// <returns>A lowercase hex string like "0x1806a7c20".</returns>
    public static string Pointer(ulong virtualAddress) => $"0x{virtualAddress:x}";

    /// <summary>
    /// Formats a managed method name by joining the type and method name with a dot,
    /// and replacing IL2CPP nested-type separators (/ and +) with dots.
    /// </summary>
    /// <param name="declaringTypeFullName">The full name of the declaring type, possibly with / or + separators.</param>
    /// <param name="methodName">The method name.</param>
    /// <returns>A slash-free managed name like "ScheduleOne.Economy.Customer.Nested.M".</returns>
    public static string ManagedName(string declaringTypeFullName, string methodName)
    {
        // Replace IL2CPP nested-type separators with dots
        var normalizedType = declaringTypeFullName
            .Replace('/', '.')
            .Replace('+', '.');

        // Collapse any doubled dots that resulted from replacement
        normalizedType = Regex.Replace(normalizedType, @"\.+", ".");

        return $"{normalizedType}.{methodName}";
    }

    /// <summary>
    /// Formats a field access as "this.fieldName @ 0x&lt;offset&gt;" or "field @ 0x&lt;offset&gt;"
    /// if the field name is null or whitespace-only.
    /// </summary>
    /// <param name="fieldName">The field name, or null if unknown.</param>
    /// <param name="offset">The field offset in bytes.</param>
    /// <returns>A formatted field access string.</returns>
    public static string FieldAccess(string? fieldName, ulong offset)
    {
        var offsetHex = $"0x{offset:x}";

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return $"field @ {offsetHex}";
        }

        return $"this.{fieldName} @ {offsetHex}";
    }

    /// <summary>
    /// Determines whether a summary string is safe to emit as native evidence.
    /// A string is unsafe if it is null, blank, longer than 512 characters after trimming,
    /// or contains any of: null character, "://", backslash, forward slash, ".bin" (case-insensitive),
    /// or "disassembly" (case-insensitive).
    /// </summary>
    /// <param name="value">The summary string to check.</param>
    /// <returns>True if the string is safe to emit, false otherwise.</returns>
    public static bool IsSummarySafe(string? value)
    {
        if (value == null)
        {
            return false;
        }

        var trimmed = value.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (trimmed.Length > MaximumSummaryLength)
        {
            return false;
        }

        if (trimmed.Contains('\0'))
        {
            return false;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Contains('\\'))
        {
            return false;
        }

        if (trimmed.Contains('/'))
        {
            return false;
        }

        if (trimmed.Contains(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.Contains("disassembly", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
