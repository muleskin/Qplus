using System.Text;
using System.Text.RegularExpressions;

namespace Qplus.Core.Completion;

public enum SqlCompletionKind
{
    None,
    Tables,
    Columns,
}

/// <summary>A table/view referenced in a FROM or JOIN clause, with its optional alias.</summary>
public sealed record TableRef(string Schema, string Name, string? Alias)
{
    /// <summary>True if <paramref name="token"/> names this reference (alias first, then table name).</summary>
    public bool Matches(string token) =>
        (!string.IsNullOrEmpty(Alias) && string.Equals(Alias, token, StringComparison.OrdinalIgnoreCase))
        || string.Equals(Name, token, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the caret position is asking for.</summary>
public sealed record SqlCompletionContext(
    SqlCompletionKind Kind,
    string Prefix,
    int PrefixStart,
    string? Qualifier,
    IReadOnlyList<TableRef> Tables)
{
    public static readonly SqlCompletionContext None =
        new(SqlCompletionKind.None, "", 0, null, Array.Empty<TableRef>());
}

/// <summary>
/// Heuristic (non-validating) SQL analyzer good enough to drive editor completion:
/// works out whether the caret wants table names or column names, and which tables are in scope.
/// </summary>
public static class SqlContextAnalyzer
{
    private static readonly HashSet<string> TableKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "FROM", "JOIN", "INTO", "UPDATE", "TABLE" };

    private static readonly HashSet<string> ColumnKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "WHERE", "AND", "OR", "ON", "SELECT", "SET", "HAVING", "BY", "NOT", "LIKE", "IN", "having" };

    /// <summary>Words that must never be treated as a table alias.</summary>
    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "JOIN", "ON", "GROUP",
        "ORDER", "HAVING", "UNION", "SELECT", "SET", "AND", "OR", "AS", "WITH", "OPTION",
    };

    private static readonly Regex FromJoinRegex = new(
        @"\b(?:FROM|JOIN)\s+(?<tbl>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#]*))*)" +
        @"(?:\s+(?:AS\s+)?(?<alias>\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#]*))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static SqlCompletionContext Analyze(string text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return SqlCompletionContext.None;
        caret = Math.Clamp(caret, 0, text.Length);

        // Blank out comments and string literals so they can't confuse keyword scanning.
        // Same length as the original, so all offsets stay valid.
        var safe = Blank(text);

        // Limit analysis to the statement containing the caret.
        var (stmtStart, stmtEnd) = StatementBounds(safe, caret);

        // The partial identifier immediately before the caret.
        var prefixStart = caret;
        while (prefixStart > stmtStart && IsIdentChar(text[prefixStart - 1])) prefixStart--;
        var prefix = text[prefixStart..caret];

        // An alias/schema qualifier, e.g. the "t" in "t.na|".
        string? qualifier = null;
        var q = prefixStart;
        if (q > stmtStart && text[q - 1] == '.')
        {
            var qEnd = q - 1;
            var qStart = qEnd;
            while (qStart > stmtStart && IsIdentChar(text[qStart - 1])) qStart--;
            if (qEnd > qStart) qualifier = Unquote(text[qStart..qEnd]);
        }

        var tables = ExtractTables(safe, text, stmtStart, stmtEnd);

        // The nearest preceding keyword decides what we're completing. A qualifier only
        // narrows it: in a table context it's a schema, in a column context it's an alias.
        var keyword = NearestKeywordBefore(safe, stmtStart, prefixStart);
        var kind =
            keyword is null ? SqlCompletionKind.None :
            TableKeywords.Contains(keyword) ? SqlCompletionKind.Tables :
            ColumnKeywords.Contains(keyword) ? SqlCompletionKind.Columns :
            SqlCompletionKind.None;

        // "alias." with no recognisable keyword still clearly wants that table's columns.
        if (kind == SqlCompletionKind.None && qualifier is not null && tables.Any(t => t.Matches(qualifier)))
            kind = SqlCompletionKind.Columns;

        return new SqlCompletionContext(kind, prefix, prefixStart, qualifier, tables);
    }

    /// <summary>Tables/views named in FROM or JOIN clauses of the statement.</summary>
    private static List<TableRef> ExtractTables(string safe, string original, int start, int end)
    {
        var list = new List<TableRef>();
        var segment = safe[start..end];

        foreach (Match m in FromJoinRegex.Matches(segment))
        {
            // Read the real (unblanked) text so bracketed names survive.
            var tblText = original.Substring(start + m.Groups["tbl"].Index, m.Groups["tbl"].Length);
            var parts = SplitQualified(tblText);
            if (parts.Count == 0) continue;

            var name = parts[^1];
            var schema = parts.Count > 1 ? parts[^2] : "";

            string? alias = null;
            if (m.Groups["alias"].Success)
            {
                var a = Unquote(original.Substring(start + m.Groups["alias"].Index, m.Groups["alias"].Length));
                if (!NotAliases.Contains(a)) alias = a;
            }

            list.Add(new TableRef(schema, name, alias));
        }
        return list;
    }

    private static List<string> SplitQualified(string text)
    {
        var parts = new List<string>();
        foreach (var raw in text.Split('.'))
        {
            var p = Unquote(raw.Trim());
            if (p.Length > 0) parts.Add(p);
        }
        return parts;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2)
        {
            if (s[0] == '[' && s[^1] == ']') return s[1..^1];
            if (s[0] == '"' && s[^1] == '"') return s[1..^1];
        }
        return s;
    }

    /// <summary>Last word-token before <paramref name="before"/> that we recognise as a context keyword.</summary>
    private static string? NearestKeywordBefore(string safe, int start, int before)
    {
        var i = before;
        while (i > start)
        {
            // Skip non-identifier characters (whitespace, commas, parens, operators).
            while (i > start && !IsIdentChar(safe[i - 1])) i--;
            if (i <= start) break;

            var wordEnd = i;
            while (i > start && IsIdentChar(safe[i - 1])) i--;
            var word = safe[i..wordEnd];

            if (TableKeywords.Contains(word) || ColumnKeywords.Contains(word))
                return word.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>Bounds of the statement containing the caret (split on ';' and standalone GO).</summary>
    private static (int start, int end) StatementBounds(string safe, int caret)
    {
        var start = 0;
        for (var i = caret - 1; i >= 0; i--)
        {
            if (safe[i] == ';') { start = i + 1; break; }
        }

        var end = safe.Length;
        for (var i = caret; i < safe.Length; i++)
        {
            if (safe[i] == ';') { end = i; break; }
        }

        // Honour a standalone GO batch separator between start and caret.
        var goMatch = Regex.Matches(safe[start..caret], @"^[ \t]*GO[ \t]*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (goMatch.Count > 0) start += goMatch[^1].Index + goMatch[^1].Length;

        return (Math.Min(start, caret), Math.Max(end, caret));
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

    /// <summary>Replaces comments and string literals with spaces, preserving length/offsets.</summary>
    private static string Blank(string text)
    {
        var sb = new StringBuilder(text);
        var i = 0;
        while (i < sb.Length)
        {
            // line comment
            if (i + 1 < sb.Length && sb[i] == '-' && sb[i + 1] == '-')
            {
                while (i < sb.Length && sb[i] != '\n') { sb[i] = ' '; i++; }
                continue;
            }
            // block comment
            if (i + 1 < sb.Length && sb[i] == '/' && sb[i + 1] == '*')
            {
                while (i < sb.Length && !(i + 1 < sb.Length && sb[i] == '*' && sb[i + 1] == '/'))
                { sb[i] = ' '; i++; }
                if (i < sb.Length) { sb[i] = ' '; i++; }
                if (i < sb.Length) { sb[i] = ' '; i++; }
                continue;
            }
            // single-quoted string
            if (sb[i] == '\'')
            {
                sb[i] = ' '; i++;
                while (i < sb.Length)
                {
                    if (sb[i] == '\'')
                    {
                        sb[i] = ' '; i++;
                        if (i < sb.Length && sb[i] == '\'') { sb[i] = ' '; i++; continue; } // escaped ''
                        break;
                    }
                    if (sb[i] != '\n') sb[i] = ' ';
                    i++;
                }
                continue;
            }
            i++;
        }
        return sb.ToString();
    }
}
