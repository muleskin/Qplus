using Qplus.Core.Data;
using Qplus.Core.Models;

namespace Qplus.App.ViewModels;

/// <summary>Builds the lazily-loaded object-explorer tree for a connection.</summary>
public static class ExplorerBuilder
{
    public static ExplorerNode BuildServerNode(ConnectionInfo info)
    {
        var engine = DbEngines.For(info);
        var label = $"{info.Name}  ({engine.DisplayName})";

        var server = new ExplorerNode(label, SchemaNodeKind.Server, LoadSchemas) { Connection = info };
        return server;
    }

    private static async Task<IEnumerable<ExplorerNode>> LoadSchemas(ExplorerNode parent)
    {
        var info = parent.Connection!;
        var engine = DbEngines.For(info);
        await using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
        await conn.OpenAsync();
        var schemas = await engine.GetSchemasAsync(conn, CancellationToken.None);

        return schemas.Select(s =>
        {
            var schemaName = s.Schema;
            var node = new ExplorerNode(s.Name, SchemaNodeKind.SchemaFolder, LoadSchemaFolders)
            {
                Connection = info,
                Schema = schemaName,
            };
            return node;
        });
    }

    private static Task<IEnumerable<ExplorerNode>> LoadSchemaFolders(ExplorerNode parent)
    {
        var info = parent.Connection!;
        var schema = parent.Schema;

        var tables = new ExplorerNode("Tables", SchemaNodeKind.TablesFolder, LoadTables)
        { Connection = info, Schema = schema };
        var views = new ExplorerNode("Views", SchemaNodeKind.ViewsFolder, LoadViews)
        { Connection = info, Schema = schema };

        return Task.FromResult<IEnumerable<ExplorerNode>>(new[] { tables, views });
    }

    private static async Task<IEnumerable<ExplorerNode>> LoadTables(ExplorerNode parent)
    {
        var info = parent.Connection!;
        var engine = DbEngines.For(info);
        await using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
        await conn.OpenAsync();
        var items = await engine.GetTablesAsync(conn, parent.Schema, CancellationToken.None);
        return items.Select(t => MakeObjectNode(info, t, SchemaNodeKind.Table));
    }

    private static async Task<IEnumerable<ExplorerNode>> LoadViews(ExplorerNode parent)
    {
        var info = parent.Connection!;
        var engine = DbEngines.For(info);
        await using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
        await conn.OpenAsync();
        var items = await engine.GetViewsAsync(conn, parent.Schema, CancellationToken.None);
        return items.Select(v => MakeObjectNode(info, v, SchemaNodeKind.View));
    }

    private static ExplorerNode MakeObjectNode(ConnectionInfo info, SchemaNode obj, SchemaNodeKind kind)
    {
        var node = new ExplorerNode(obj.Name, kind, LoadColumns)
        {
            Connection = info,
            Schema = obj.Schema,
            ObjectName = obj.Name,
        };
        return node;
    }

    private static async Task<IEnumerable<ExplorerNode>> LoadColumns(ExplorerNode parent)
    {
        var info = parent.Connection!;
        var engine = DbEngines.For(info);
        await using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
        await conn.OpenAsync();
        var cols = await engine.GetColumnsAsync(conn, parent.Schema, parent.ObjectName, CancellationToken.None);
        return cols.Select(c => new ExplorerNode(c.Name, SchemaNodeKind.Column, detail: c.Detail)
        {
            Connection = info,
            Schema = parent.Schema,
        });
    }
}
