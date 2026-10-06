using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ticket.Adapter.Sheets;

public sealed class SheetsSyncService(SheetsOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var db = NpgsqlDataSource.Create(options.ConnectionString);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var sheets = new SheetsClient(http, options);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rows = await SheetsSync.SyncAsync(db, sheets, stoppingToken).ConfigureAwait(false);
                Console.WriteLine($"[sheets] synced {rows} rows");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sheets] sync failed: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
