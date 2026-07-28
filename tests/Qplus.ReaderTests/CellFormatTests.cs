using System.Data;
using Qplus.Core.Data;

namespace Qplus.ReaderTests;

/// <summary>
/// Tests for <see cref="CellFormat"/> — the shared renderer that keeps binary columns from
/// showing up as the literal "System.Byte[]" in the grid, the text view and CSV export.
/// </summary>
public static class CellFormatTests
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    public static int Run()
    {
        _failures = 0;
        Console.WriteLine();
        Console.WriteLine("--- cell formatting (binary columns) ---");

        // The bug: a byte[] must never render as its type name anywhere.
        var blob = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG magic
        var shown = CellFormat.Display(blob);
        Check("binary is not rendered as the type name",
            !shown.Contains("Byte[]") && !shown.Contains("System."), shown);
        Check("binary shows a BLOB size summary",
            shown.Contains("BLOB") && shown.Contains("8 B"), shown);

        // Nulls and ordinary values are untouched.
        Check("null renders as empty", CellFormat.Display(null) == "");
        Check("DBNull renders as empty", CellFormat.Display(DBNull.Value) == "");
        Check("string passes through", CellFormat.Display("hello") == "hello");
        Check("number passes through", CellFormat.Display(42) == "42");

        // Size formatting crosses each unit boundary.
        Check("bytes", CellFormat.HumanSize(512) == "512 B", CellFormat.HumanSize(512));
        Check("kilobytes", CellFormat.HumanSize(2048) == "2 KB", CellFormat.HumanSize(2048));
        Check("fractional KB", CellFormat.HumanSize(1536) == "1.5 KB", CellFormat.HumanSize(1536));
        Check("megabytes", CellFormat.HumanSize(3 * 1024 * 1024) == "3 MB",
            CellFormat.HumanSize(3 * 1024 * 1024));
        Check("gigabytes", CellFormat.HumanSize(5L * 1024 * 1024 * 1024) == "5 GB",
            CellFormat.HumanSize(5L * 1024 * 1024 * 1024));

        // Hex preview is inspectable, non-binary yields nothing, and long blobs are elided.
        var hex = CellFormat.HexPreview(blob);
        Check("hex preview shows the leading bytes",
            hex is not null && hex.StartsWith("89 50 4E 47"), hex ?? "null");
        Check("hex preview is null for non-binary", CellFormat.HexPreview("not bytes") is null);
        Check("empty blob is described, not blank",
            CellFormat.HexPreview(Array.Empty<byte>())?.Contains("empty") == true);

        var big = new byte[500];
        var bigHex = CellFormat.HexPreview(big, maxBytes: 16);
        Check("long blob preview is truncated with a total",
            bigHex is not null && bigHex.Contains("…") && bigHex.Contains("500 B"), bigHex ?? "null");

        // End to end: a DataTable with a byte[] column, the way ADO.NET hands back varbinary.
        var dt = new DataTable();
        dt.Columns.Add("id", typeof(int));
        dt.Columns.Add("photo", typeof(byte[]));
        dt.Rows.Add(1, blob);
        Check("DataTable byte[] column formats cleanly",
            CellFormat.Display(dt.Rows[0]["photo"]).Contains("BLOB"),
            CellFormat.Display(dt.Rows[0]["photo"]));

        // Content sniffing for the blob viewer's header and Save default extension.
        var png = BinarySniff.Identify(blob);   // blob above is the PNG signature
        Check("PNG detected as an image", png is { IsImage: true, Extension: "png" }, png.Label);
        Check("JPEG detected", BinarySniff.Identify(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }).Extension == "jpg");
        Check("PDF detected, not an image",
            BinarySniff.Identify(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }) is { IsImage: false, Extension: "pdf" });
        Check("ZIP/Office detected",
            BinarySniff.Identify(new byte[] { 0x50, 0x4B, 0x03, 0x04 }).Extension == "zip");
        Check("unknown bytes fall back to Binary data",
            BinarySniff.Identify(new byte[] { 0x01, 0x02, 0x03 }) == BinarySniff.Unknown);
        Check("too-short input does not throw",
            BinarySniff.Identify(new byte[] { 0x89 }) == BinarySniff.Unknown);

        return _failures;
    }
}
