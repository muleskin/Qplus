using System.Data;

namespace Qplus.Core.Models;

/// <summary>The full outcome of running a batch: zero or more grids plus messages.</summary>
public sealed class QueryExecutionResult
{
    public List<DataTable> Grids { get; } = new();
    public List<string> Messages { get; } = new();
    public int TotalRowsAffected { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool HasError { get; set; }
    public string? ErrorText { get; set; }
}
