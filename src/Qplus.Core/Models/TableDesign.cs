namespace Qplus.Core.Models;

/// <summary>One column in the visual table designer.</summary>
public sealed class ColumnDesign
{
    public string Name { get; set; } = "";

    /// <summary>Base type, e.g. "varchar", "int", "NUMBER". Length/precision is appended from <see cref="Length"/>.</summary>
    public string DataType { get; set; } = "";

    /// <summary>Optional length / precision text, e.g. "50" or "10,2". Empty = no parentheses.</summary>
    public string Length { get; set; } = "";

    public bool Nullable { get; set; } = true;
    public bool PrimaryKey { get; set; }

    /// <summary>Optional DEFAULT expression (raw SQL).</summary>
    public string Default { get; set; } = "";

    /// <summary>Type text as it should appear in DDL, e.g. "varchar(50)" or "NUMBER(10,2)".</summary>
    public string TypeText => string.IsNullOrWhiteSpace(Length) ? DataType : $"{DataType}({Length})";
}

/// <summary>A table being created or extended in the designer.</summary>
public sealed class TableDesign
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ColumnDesign> Columns { get; set; } = new();
}
