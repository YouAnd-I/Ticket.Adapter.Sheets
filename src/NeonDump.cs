using System.Text.Json.Nodes;
using Npgsql;

namespace Ticket.Adapter.Sheets;

public sealed record NeonTable(string Name, string[] Columns, List<JsonArray> Rows);

public static class NeonDump
{
    public static async Task<List<NeonTable>> ReadAllAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        var names = new List<string>();
        await using (var cmd = db.CreateCommand("""
            select table_name from information_schema.tables
            where table_schema = 'public' and table_type = 'BASE TABLE'
            order by table_name
            """))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                names.Add(reader.GetString(0));

        var tables = new List<NeonTable>(names.Count);
        foreach (var name in names)
            tables.Add(await ReadTableAsync(db, name, ct).ConfigureAwait(false));
        return tables;
    }

    private static async Task<NeonTable> ReadTableAsync(
        NpgsqlDataSource db, string name, CancellationToken ct)
    {
        var columns = new List<string>();
        await using (var cmd = db.CreateCommand("""
            select column_name from information_schema.columns
            where table_schema = 'public' and table_name = @table
            order by ordinal_position
            """))
        {
            cmd.Parameters.AddWithValue("table", name);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                columns.Add(reader.GetString(0));
        }

        var rows = new List<JsonArray>();
        await using (var cmd = db.CreateCommand(
            $"select * from public.\"{name.Replace("\"", "\"\"")}\""))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new JsonArray();
                for (var i = 0; i < reader.FieldCount; i++)
                    row.Add(await reader.IsDBNullAsync(i, ct).ConfigureAwait(false)
                        ? Cell(null)
                        : Cell(reader.GetValue(i)));
                rows.Add(row);
            }

        return new NeonTable(name, [.. columns], rows);
    }

    internal static JsonNode? Cell(object? value) => value switch
    {
        null or DBNull => JsonValue.Create(""),
        bool flag => JsonValue.Create(flag),
        long number => number is > 9007199254740991L or < -9007199254740991L
            ? JsonValue.Create(number.ToString())
            : JsonValue.Create(number),
        int number => JsonValue.Create(number),
        short number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        DateTime moment => JsonValue.Create(moment.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss")),
        DateTimeOffset moment => JsonValue.Create(moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss")),
        Guid guid => JsonValue.Create(guid.ToString()),
        string text => JsonValue.Create(text),
        var other => JsonValue.Create(other.ToString() ?? ""),
    };
}
