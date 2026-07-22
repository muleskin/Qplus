using Qplus.Core.Completion;

namespace Qplus.ReaderTests;

/// <summary>
/// Tests for the completion context analyzer. The caret is marked with '|' in each input.
/// </summary>
public static class AnalyzerTests
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    private static SqlCompletionContext At(string markedSql)
    {
        var caret = markedSql.IndexOf('|');
        var sql = markedSql.Remove(caret, 1);
        return SqlContextAnalyzer.Analyze(sql, caret);
    }

    public static int Run()
    {
        _failures = 0;
        Console.WriteLine("--- SQL completion context analyzer ---");

        // Table context after FROM
        var c = At("SELECT * FROM |");
        Check("FROM -> Tables", c.Kind == SqlCompletionKind.Tables, c.Kind.ToString());

        // Partial table name: prefix + offset must let us replace what's typed
        c = At("SELECT * FROM cd_w|");
        Check("partial table -> Tables", c.Kind == SqlCompletionKind.Tables);
        Check("partial table prefix captured", c.Prefix == "cd_w", $"'{c.Prefix}'");
        Check("prefix start points at word start", c.PrefixStart == "SELECT * FROM ".Length,
            c.PrefixStart.ToString());

        // Column context after WHERE, with the FROM table in scope
        c = At("SELECT * FROM cd_well_source WHERE |");
        Check("WHERE -> Columns", c.Kind == SqlCompletionKind.Columns, c.Kind.ToString());
        Check("WHERE sees FROM table",
            c.Tables.Count == 1 && c.Tables[0].Name == "cd_well_source",
            string.Join(",", c.Tables.Select(t => t.Name)));

        // Alias-qualified columns
        c = At("SELECT * FROM dbo.cd_well_source w WHERE w.|");
        Check("alias qualifier -> Columns", c.Kind == SqlCompletionKind.Columns);
        Check("alias captured", c.Qualifier == "w", c.Qualifier ?? "null");
        Check("schema + alias parsed",
            c.Tables.Count == 1 && c.Tables[0].Schema == "dbo"
            && c.Tables[0].Name == "cd_well_source" && c.Tables[0].Alias == "w",
            c.Tables.Count == 1 ? $"{c.Tables[0].Schema}.{c.Tables[0].Name} as {c.Tables[0].Alias}" : "none");

        // Schema qualifier in a table context must stay Tables (not columns of a table named "dbo")
        c = At("SELECT * FROM dbo.|");
        Check("schema qualifier -> Tables", c.Kind == SqlCompletionKind.Tables, c.Kind.ToString());
        Check("schema qualifier captured", c.Qualifier == "dbo", c.Qualifier ?? "null");

        // SELECT list gets columns, with tables discovered later in the statement
        c = At("SELECT | FROM cd_well_source");
        Check("SELECT list -> Columns", c.Kind == SqlCompletionKind.Columns, c.Kind.ToString());
        Check("SELECT list sees table after caret",
            c.Tables.Count == 1 && c.Tables[0].Name == "cd_well_source");

        // JOIN: both tables in scope for ON
        c = At("SELECT * FROM a INNER JOIN b ON |");
        Check("ON -> Columns", c.Kind == SqlCompletionKind.Columns);
        Check("JOIN puts both tables in scope",
            c.Tables.Count == 2 && c.Tables.Any(t => t.Name == "a") && c.Tables.Any(t => t.Name == "b"),
            string.Join(",", c.Tables.Select(t => t.Name)));
        Check("JOIN keyword not taken as alias",
            c.Tables.All(t => !string.Equals(t.Alias, "INNER", StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(t.Alias, "ON", StringComparison.OrdinalIgnoreCase)));

        // A string literal containing FROM must not create a phantom table
        c = At("SELECT * FROM t WHERE x = 'FROM zzz' AND |");
        Check("string literal ignored", c.Kind == SqlCompletionKind.Columns);
        Check("no phantom table from string literal",
            c.Tables.Count == 1 && c.Tables[0].Name == "t",
            string.Join(",", c.Tables.Select(t => t.Name)));

        // A comment containing FROM must not create a phantom table
        c = At("SELECT * FROM t -- FROM zzz\nWHERE |");
        Check("line comment ignored",
            c.Kind == SqlCompletionKind.Columns && c.Tables.Count == 1 && c.Tables[0].Name == "t",
            string.Join(",", c.Tables.Select(t => t.Name)));

        // Statement isolation: only the current statement's tables
        c = At("SELECT * FROM t1; SELECT * FROM t2 WHERE |");
        Check("previous statement's tables excluded",
            c.Tables.Count == 1 && c.Tables[0].Name == "t2",
            string.Join(",", c.Tables.Select(t => t.Name)));

        // Bracketed identifiers
        c = At("SELECT * FROM [dbo].[cd well] x WHERE x.|");
        Check("bracketed names unquoted",
            c.Tables.Count == 1 && c.Tables[0].Schema == "dbo" && c.Tables[0].Name == "cd well",
            c.Tables.Count == 1 ? $"{c.Tables[0].Schema}.{c.Tables[0].Name}" : "none");

        // ---- trigger points -------------------------------------------------
        // Reported case: typing "where" then space produced nothing, because the popup
        // only opened on letters and by then the nearest keyword was still FROM.
        bool Trig(string marked)
        {
            var caret = marked.IndexOf('|');
            return SqlContextAnalyzer.IsAfterTriggerKeyword(marked.Remove(caret, 1), caret);
        }

        Check("space after WHERE triggers", Trig("select * from CD_WELL_SOURCE where |"));
        Check("space after FROM triggers", Trig("select * from |"));
        Check("space after JOIN triggers", Trig("select * from a join |"));
        Check("space after AND triggers", Trig("select * from t where x = 1 and |"));
        Check("space after SELECT triggers", Trig("select |"));
        Check("comma triggers", Trig("select a, |"));
        Check("mid-keyword does not trigger", !Trig("select * from t whe|"));
        Check("no whitespace does not trigger", !Trig("select * from t where|"));
        Check("after a value does not trigger", !Trig("select * from t where x = 5 |"));
        Check("after a table name does not trigger", !Trig("select * from CD_WELL_SOURCE |"));
        // A keyword inside a string literal must not itself be the trigger word. (In
        // "select 'where ' |" the trigger is the real SELECT, which is correct — so pick a
        // case where the only keyword present is the quoted one.)
        Check("keyword inside a string is not the trigger", !Trig("update t set a = 'where ' |"));

        // And the context at that trigger point must be columns, scoped to the FROM table.
        c = At("select * from CD_WELL_SOURCE where |");
        Check("WHERE trigger point yields columns",
            c.Kind == SqlCompletionKind.Columns
            && c.Tables.Count == 1 && c.Tables[0].Name == "CD_WELL_SOURCE",
            c.Kind.ToString());

        // Nothing meaningful to suggest
        c = At("|");
        Check("empty -> None", c.Kind == SqlCompletionKind.None, c.Kind.ToString());

        return _failures;
    }
}
