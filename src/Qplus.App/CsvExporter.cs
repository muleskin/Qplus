using System.Data;
using System.IO;
using System.Text;

namespace Qplus.App;

/// <summary>Writes a <see cref="DataTable"/> to RFC-4180-style CSV.</summary>
public static class CsvExporter
{
    public static void Write(DataTable table, string path)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(true));
        WriteTo(table, writer);
    }

    public static string ToString(DataTable table)
    {
        using var writer = new StringWriter();
        WriteTo(table, writer);
        return writer.ToString();
    }

    private static void WriteTo(DataTable table, TextWriter writer)
    {
        writer.WriteLine(string.Join(",",
            table.Columns.Cast<DataColumn>().Select(c => Escape(c.ColumnName))));

        foreach (DataRow row in table.Rows)
        {
            writer.WriteLine(string.Join(",",
                row.ItemArray.Select(v => Escape(v is null or DBNull ? "" : v.ToString() ?? ""))));
        }
    }

    private static string Escape(string field)
    {
        if (field.Contains('"') || field.Contains(',') || field.Contains('\n') || field.Contains('\r'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }
}
