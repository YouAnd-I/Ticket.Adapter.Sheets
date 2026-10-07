using Npgsql;

namespace Ticket.Adapter.Sheets;

public static class SheetsSync
{
    public static async Task<int> SyncAsync(
        NpgsqlDataSource db, SheetsClient sheets, IReverseSync? reverse = null,
        CancellationToken ct = default)
    {
        var id = await sheets.ResolveSpreadsheetIdAsync(ct).ConfigureAwait(false);
        var tabs = await EnsureTabsAsync(id, sheets,
            await sheets.FetchTabsAsync(id, ct).ConfigureAwait(false),
            await TableNamesAsync(db, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await ApplyReverseAsync(id, sheets, tabs, reverse, ct).ConfigureAwait(false);

        var tables = await NeonDump.ReadAllAsync(db, ct).ConfigureAwait(false);
        return await PushAsync(tables, id, sheets, tabs, ct).ConfigureAwait(false);
    }

    public static async Task<int> SyncAsync(
        IReadOnlyList<NeonTable> tables, SheetsClient sheets, IReverseSync? reverse = null,
        CancellationToken ct = default)
    {
        var id = await sheets.ResolveSpreadsheetIdAsync(ct).ConfigureAwait(false);
        var tabs = await EnsureTabsAsync(id, sheets,
            await sheets.FetchTabsAsync(id, ct).ConfigureAwait(false),
            tables.Select(table => table.Name).Append(DashboardModel.Tab), ct).ConfigureAwait(false);
        await ApplyReverseAsync(id, sheets, tabs, reverse, ct).ConfigureAwait(false);
        return await PushAsync(tables, id, sheets, tabs, ct).ConfigureAwait(false);
    }

    private static async Task<int> PushAsync(
        IReadOnlyList<NeonTable> tables, string id, SheetsClient sheets,
        IReadOnlyList<SheetTabState> tabs, CancellationToken ct)
    {
        var rows = 0;
        foreach (var table in tables)
        {
            var plan = SheetPlanBuilder.Build(table, tables);
            await sheets.WriteValuesAsync(id, plan, ct).ConfigureAwait(false);
            var state = tabs.First(tab => tab.Title == table.Name);
            await sheets.ApplyRequestsAsync(id, SheetsPresentation.TableRequests(state.SheetId, plan, state), ct)
                .ConfigureAwait(false);
            rows += plan.Rows.Count;
        }

        var dashboard = SheetsDashboard.Build(tables);
        var dashboardState = tabs.First(tab => tab.Title == DashboardModel.Tab);
        await sheets.WriteMatrixAsync(id, DashboardModel.Tab, dashboard.Matrix(), ct).ConfigureAwait(false);
        await sheets.ApplyRequestsAsync(id,
            SheetsPresentation.DashboardRequests(dashboardState.SheetId, dashboard, dashboardState), ct)
            .ConfigureAwait(false);
        return rows;
    }

    private static async Task<IEnumerable<string>> TableNamesAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        var names = new List<string>();
        await using (var cmd = db.CreateCommand("""
            select table_name from information_schema.tables
            where table_schema = 'public' and table_type = 'BASE TABLE'
            """))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<IReadOnlyList<SheetTabState>> EnsureTabsAsync(
        string id, SheetsClient sheets, IReadOnlyList<SheetTabState> tabs,
        IEnumerable<string> wanted, CancellationToken ct)
    {
        var missing = wanted.Except(tabs.Select(tab => tab.Title)).ToArray();
        if (missing.Length == 0) return tabs;
        await sheets.AddTabsAsync(id, missing, ct).ConfigureAwait(false);
        return await sheets.FetchTabsAsync(id, ct).ConfigureAwait(false);
    }

    private static async Task ApplyReverseAsync(
        string id, SheetsClient sheets, IReadOnlyList<SheetTabState> tabs,
        IReverseSync? reverse, CancellationToken ct)
    {
        if (reverse is null) return;
        foreach (var tab in reverse.Tabs)
        {
            if (tabs.All(state => state.Title != tab)) continue;
            var rows = await sheets.ReadTabAsync(id, tab, ct).ConfigureAwait(false);
            await reverse.ApplyAsync(tab, rows, ct).ConfigureAwait(false);
        }
    }
}
