using System.Data;

namespace Qplus.Core.Data;

/// <summary>
/// Human-readable rendering of cell values that do not stringify usefully on their own —
/// chiefly binary columns (varbinary, BLOB, RAW, LONG RAW, IMAGE, rowversion), whose default
/// <see cref="object.ToString"/> is the unhelpful literal "System.Byte[]". Grid, text and CSV
/// output all route binary values through here so they agree.
/// </summary>
public static class CellFormat
{
    /// <summary>
    /// Short, grid-friendly text for a value. Null becomes an empty string; a binary value
    /// becomes a size summary such as "[BLOB 1.2 KB]"; anything else is its own ToString().
    /// </summary>
    public static string Display(object? value)
    {
        if (value is null or DBNull) return "";
        if (value is byte[] bytes) return $"[BLOB {HumanSize(bytes.Length)}]";
        return value.ToString() ?? "";
    }

    /// <summary>
    /// A spaced hex preview of a binary value, suitable for a tooltip so the bytes remain
    /// inspectable without dumping the whole blob. Returns null for non-binary values.
    /// </summary>
    public static string? HexPreview(object? value, int maxBytes = 64)
    {
        if (value is not byte[] bytes) return null;
        if (bytes.Length == 0) return "(empty — 0 bytes)";

        var shown = Math.Min(maxBytes, bytes.Length);
        var hex = Convert.ToHexString(bytes, 0, shown);
        var spaced = string.Join(" ",
            Enumerable.Range(0, shown).Select(i => hex.Substring(i * 2, 2)));
        var tail = bytes.Length > shown
            ? $" … ({HumanSize(bytes.Length)} total)"
            : $"  ({HumanSize(bytes.Length)})";
        return spaced + tail;
    }

    /// <summary>Renders a byte count as B / KB / MB / GB with one decimal place.</summary>
    public static string HumanSize(long bytes)
    {
        const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
        return bytes < KB ? $"{bytes} B"
             : bytes < MB ? $"{bytes / (double)KB:0.#} KB"
             : bytes < GB ? $"{bytes / (double)MB:0.#} MB"
             : $"{bytes / (double)GB:0.#} GB";
    }
}
