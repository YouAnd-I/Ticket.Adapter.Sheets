using Npgsql;

namespace Ticket.Adapter.Sheets;

public static class SheetsSync
{
    public static async Task<int> SyncAsync(
        NpgsqlDataSource db, SheetsClient sheets, CancellationToken ct = default) =>
        await SyncAsync(await NeonDump.ReadAllAsync(db, ct).ConfigureAwait(false), sheets, ct)
            .ConfigureAwait(false);

    public static async Task<int> SyncAsync(
        IReadOnlyList<NeonTable> tables, SheetsClient sheets, CancellationToken ct = default)
    {
        var id = await sheets.ResolveSpreadsheetIdAsync(ct).ConfigureAwait(false);
        var existing = await sheets.TabTitlesAsync(id, ct).ConfigureAwait(false);
        var missing = tables.Select(table => table.Name).Where(name => !existing.Contains(name)).ToArray();
        if (missing.Length > 0)
            await sheets.AddTabsAsync(id, missing, ct).ConfigureAwait(false);

        var rows = 0;
        foreach (var table in tables)
        {
            await sheets.ReplaceTabAsync(id, table, ct).ConfigureAwait(false);
            rows += table.Rows.Count;
        }
        return rows;
    }
}
