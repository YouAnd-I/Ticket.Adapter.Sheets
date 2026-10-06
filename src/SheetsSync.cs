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
        var tabs = await sheets.FetchTabsAsync(id, ct).ConfigureAwait(false);

        var missing = tables.Select(table => table.Name)
            .Append(DashboardModel.Tab)
            .Except(tabs.Select(tab => tab.Title))
            .ToArray();
        if (missing.Length > 0)
        {
            await sheets.AddTabsAsync(id, missing, ct).ConfigureAwait(false);
            tabs = await sheets.FetchTabsAsync(id, ct).ConfigureAwait(false);
        }

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
}
