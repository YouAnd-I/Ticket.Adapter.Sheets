using Npgsql;
using Ticket.Adapter.Sheets;
using Xunit;

namespace Ticket.Adapter.Sheets.Tests;

public class NeonDumpTests
{
    private static string? Pg => Environment.GetEnvironmentVariable("TEST_POSTGRES");

    [Fact]
    public async Task ReadAll_ReturnsEveryPublicTableWithColumnsAndRows()
    {
        if (Pg is null) return;

        await using var db = NpgsqlDataSource.Create(Pg);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var setup = db.CreateCommand($"""
            create table public.dump_alpha_{suffix} (id bigint primary key, label text not null);
            create table public.dump_beta_{suffix} (id bigint primary key, at_utc timestamptz not null);
            insert into public.dump_alpha_{suffix} values (1, 'one'), (2, 'two');
            insert into public.dump_beta_{suffix} values (1, '2026-01-02T03:04:05Z');
            """))
            await setup.ExecuteNonQueryAsync();

        try
        {
            var tables = await NeonDump.ReadAllAsync(db);
            var alpha = tables.Single(table => table.Name == $"dump_alpha_{suffix}");
            var beta = tables.Single(table => table.Name == $"dump_beta_{suffix}");

            Assert.Equal(["id", "label"], alpha.Columns);
            Assert.Equal(2, alpha.Rows.Count);
            Assert.Equal(1L, alpha.Rows[0][0]!.GetValue<long>());
            Assert.Equal("one", alpha.Rows[0][1]!.GetValue<string>());
            Assert.Equal(["id", "at_utc"], beta.Columns);
            Assert.Equal("2026-01-02 03:04:05", beta.Rows[0][1]!.GetValue<string>());
        }
        finally
        {
            await using var cleanup = db.CreateCommand(
                $"drop table public.dump_alpha_{suffix}; drop table public.dump_beta_{suffix};");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
