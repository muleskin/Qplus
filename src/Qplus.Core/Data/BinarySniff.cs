namespace Qplus.Core.Data;

/// <summary>
/// Best-effort content-type detection from a binary value's leading bytes ("magic numbers").
/// Used to label a blob in the viewer and to offer a sensible default extension when saving it.
/// Detection is deliberately conservative — an unrecognised value is simply "Binary data".
/// </summary>
public static class BinarySniff
{
    /// <summary>A detected content type.</summary>
    /// <param name="Label">Human-readable name, e.g. "PNG image".</param>
    /// <param name="IsImage">True when WPF's imaging can be expected to decode it.</param>
    /// <param name="Extension">Default file extension without a dot, e.g. "png".</param>
    public sealed record Kind(string Label, bool IsImage, string Extension);

    public static readonly Kind Unknown = new("Binary data", false, "bin");

    public static Kind Identify(byte[] b)
    {
        if (Starts(b, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return new("PNG image", true, "png");
        if (Starts(b, 0xFF, 0xD8, 0xFF))                               return new("JPEG image", true, "jpg");
        if (Starts(b, 0x47, 0x49, 0x46, 0x38))                         return new("GIF image", true, "gif");
        if (Starts(b, 0x42, 0x4D))                                     return new("Bitmap image", true, "bmp");
        if (Starts(b, 0x49, 0x49, 0x2A, 0x00) ||
            Starts(b, 0x4D, 0x4D, 0x00, 0x2A))                         return new("TIFF image", true, "tiff");
        if (Starts(b, 0x00, 0x00, 0x01, 0x00))                         return new("Icon", true, "ico");
        if (Starts(b, 0x25, 0x50, 0x44, 0x46))                         return new("PDF document", false, "pdf");
        if (Starts(b, 0x50, 0x4B, 0x03, 0x04))                         return new("ZIP / Office document", false, "zip");
        if (Starts(b, 0xD0, 0xCF, 0x11, 0xE0))                         return new("Legacy Office document", false, "doc");
        if (Starts(b, 0x1F, 0x8B))                                     return new("GZIP archive", false, "gz");
        if (Starts(b, 0x25, 0x21, 0x50, 0x53))                         return new("PostScript", false, "ps");
        return Unknown;
    }

    private static bool Starts(byte[] b, params byte[] sig)
    {
        if (b.Length < sig.Length) return false;
        for (var i = 0; i < sig.Length; i++)
            if (b[i] != sig[i]) return false;
        return true;
    }
}
