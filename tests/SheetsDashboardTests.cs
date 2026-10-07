using System.Text.Json.Nodes;
using Ticket.Adapter.Sheets;
using Xunit;

namespace Ticket.Adapter.Sheets.Tests;

public class SheetsDashboardTests
{
    private static JsonArray Row(params object?[] cells) =>
        new(cells.Select(NeonDump.Cell).ToArray());

    private static string At(DateTimeOffset moment) => moment.ToString("yyyy-MM-dd HH:mm:ss");

    private static List<NeonTable> Dump()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new NeonTable("priority", ["priority_code"], [Row("urgent"), Row("no-rush"), Row("report")]),
            new NeonTable("it_staff", ["user_id", "display_name", "handles", "active"],
                [Row(100, "Alice", "wifi, VPN", true)]),
            new NeonTable("discord_user", ["user_id", "username"],
                [Row(100, "alice99"), Row(200, "bob")]),
            new NeonTable("ticket",
                ["ticket_id", "priority_code", "title", "assignee_user_id", "created_at_utc"],
            [
                Row("abc", "urgent", "printer", 100, "2026-01-01 00:00:00"),
                Row("def", "no-rush", "mouse", null, At(now.AddDays(-2))),
                Row("ghi", "urgent", "screen", 200, At(now.AddHours(-2))),
            ]),
            new NeonTable("ticket_status_event", ["ticket_id", "status_code", "occurred_at_utc"],
            [
                Row("abc", "open", "2026-01-01 00:00:00"),
                Row("abc", "planned", "2026-01-02 00:00:00"),
                Row("abc", "complete", "2026-01-03 00:00:00"),
                Row("def", "open", "2026-01-04 00:00:00"),
                Row("ghi", "planned", "2026-01-05 00:00:00"),
            ]),
        ];
    }

    [Fact]
    public void Build_CountsOpenStalePlannedAndComplete()
    {
        var dashboard = SheetsDashboard.Build(Dump());

        Assert.Equal(2, dashboard.OpenTotal);
        Assert.Equal(1, dashboard.OpenStale);
        Assert.Equal(1, dashboard.Planned);
        Assert.Equal(1, dashboard.Complete);
    }

    [Fact]
    public void Build_OpenTickets_AreOldestFirst_WithStaffNamesAndAge()
    {
        var dashboard = SheetsDashboard.Build(Dump());

        var open = dashboard.OpenTickets;
        Assert.Equal(2, open.Count);
        Assert.Equal("def", open[0].TicketId);
        Assert.Equal("open", open[0].Status);
        Assert.Null(open[0].Assignee);
        Assert.True(open[0].AgeDays >= 1.9);
        Assert.Equal("ghi", open[1].TicketId);
        Assert.Equal("planned", open[1].Status);
        Assert.Equal("bob", open[1].Assignee);
        Assert.True(open[1].AgeDays < 1);
    }

    [Fact]
    public void Build_Solvers_CountCompletedTicketsPerAssignee()
    {
        var dashboard = SheetsDashboard.Build(Dump());

        var solver = Assert.Single(dashboard.Solvers);
        Assert.Equal("Alice", solver.Staff);
        Assert.Equal(1, solver.Solved);
    }

    [Fact]
    public void Build_LatestStatusWins_PerTicket()
    {
        var dashboard = SheetsDashboard.Build(Dump());

        Assert.Equal(
        [
            new DashboardCounts("open", 1),
            new DashboardCounts("planned", 1),
            new DashboardCounts("complete", 1),
            new DashboardCounts("reopened", 0),
            new DashboardCounts("unsolved", 0),
        ], dashboard.Status);
        Assert.Equal(
        [
            new DashboardCounts("urgent", 2),
            new DashboardCounts("no-rush", 1),
            new DashboardCounts("report", 0),
        ], dashboard.Priority);
    }

    [Fact]
    public void Build_RecentActivity_IsNewestFirst_WithTicketContext()
    {
        var dashboard = SheetsDashboard.Build(Dump());

        Assert.Equal(5, dashboard.Recent.Count);
        Assert.Equal("ghi", dashboard.Recent[0].TicketId);
        Assert.Equal("planned", dashboard.Recent[0].Status);
        Assert.Equal("urgent", dashboard.Recent[0].Priority);
        Assert.Equal("screen", dashboard.Recent[0].Title);
        Assert.Equal("complete", dashboard.Recent[2].Status);
        Assert.Equal("printer", dashboard.Recent[2].Title);
    }

    [Fact]
    public void Matrix_DrawsCardsOpenListSolversBlocksAndRecent()
    {
        var dashboard = SheetsDashboard.Build(Dump());
        var matrix = dashboard.Matrix();

        Assert.Equal("NeonDB — IT Tickets", matrix[0]![0]!.GetValue<string>());
        Assert.Equal("Open tickets", matrix[3]![0]!.GetValue<string>());
        Assert.Equal("Unsolved > 1 day", matrix[3]![2]!.GetValue<string>());
        Assert.Equal(2, matrix[4]![0]!.GetValue<int>());
        Assert.Equal(1, matrix[4]![2]!.GetValue<int>());
        Assert.Equal(1, matrix[4]![4]!.GetValue<int>());
        Assert.Equal(1, matrix[4]![6]!.GetValue<int>());

        Assert.Equal(6, dashboard.OpenHeaderRowIndex);
        Assert.Equal("Open tickets — oldest first", matrix[6]![0]!.GetValue<string>());
        Assert.Equal("Ticket", matrix[7]![0]!.GetValue<string>());
        Assert.Equal("Age (days)", matrix[7]![6]!.GetValue<string>());
        Assert.Equal("def", matrix[8]![0]!.GetValue<string>());
        Assert.Equal("open", matrix[8]![3]!.GetValue<string>());
        Assert.True(matrix[8]![6]!.GetValue<double>() >= 1.9);
        Assert.Equal("ghi", matrix[9]![0]!.GetValue<string>());
        Assert.Equal("bob", matrix[9]![4]!.GetValue<string>());

        var solved = dashboard.SolvedHeaderRowIndex;
        Assert.Equal(11, solved);
        Assert.Equal("Solved by staff", matrix[solved]![0]!.GetValue<string>());
        Assert.Equal("Status", matrix[solved]![3]!.GetValue<string>());
        Assert.Equal("Priority", matrix[solved]![6]!.GetValue<string>());
        Assert.Equal("Staff", matrix[solved + 1]![0]!.GetValue<string>());
        Assert.Equal("Alice", matrix[solved + 2]![0]!.GetValue<string>());
        Assert.Equal(1, matrix[solved + 2]![1]!.GetValue<int>());
        Assert.Equal("open", matrix[solved + 2]![3]!.GetValue<string>());
        Assert.Equal(1, matrix[solved + 2]![4]!.GetValue<int>());

        var recent = dashboard.RecentHeaderRowIndex;
        Assert.True(recent > solved + 5);
        Assert.Equal("Recent activity", matrix[recent]![0]!.GetValue<string>());
        Assert.Equal("ghi", matrix[recent + 2]![0]!.GetValue<string>());
        Assert.True(matrix[recent + 2]![3]!.GetValue<double>() > 40000);
    }

    [Fact]
    public void Build_SurvivesEmptyDump()
    {
        var dashboard = SheetsDashboard.Build([]);

        Assert.Equal(0, dashboard.OpenTotal);
        Assert.Equal(0, dashboard.OpenStale);
        Assert.Equal(0, dashboard.Planned);
        Assert.Equal(0, dashboard.Complete);
        Assert.Empty(dashboard.OpenTickets);
        Assert.Empty(dashboard.Solvers);
        Assert.Equal(
        [
            new DashboardCounts("open", 0),
            new DashboardCounts("planned", 0),
            new DashboardCounts("complete", 0),
            new DashboardCounts("reopened", 0),
            new DashboardCounts("unsolved", 0),
        ], dashboard.Status);
        Assert.Empty(dashboard.Priority);
        Assert.Empty(dashboard.Recent);
        Assert.Equal("NeonDB — IT Tickets", dashboard.Matrix()[0]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Sync_WritesValuesAppliesPresentation_AndBuildsDashboard()
    {
        var stub = new StubSequence();
        var client = stub.Client;
        var dump = new List<NeonTable>
        {
            new("priority", ["priority_code"], [Row("urgent")]),
            new("ticket", ["ticket_id", "priority_code", "status_code"],
            [
                Row("abc", "urgent", "open"),
            ]),
            new NeonTable("ticket_status_event", ["ticket_id", "status_code", "occurred_at_utc"],
            [
                Row("abc", "open", "2026-01-01 00:00:00"),
            ]),
        };
        stub.Replies(
            Json("""{"access_token":"tok","expires_in":3600,"token_type":"Bearer"}"""),
            Json("""{"sheets":[{"properties":{"sheetId":1,"title":"priority","gridProperties":{"rowCount":1000}}},{"properties":{"sheetId":2,"title":"ticket","gridProperties":{"rowCount":1000}}},{"properties":{"sheetId":3,"title":"ticket_status_event","gridProperties":{"rowCount":1000}}},{"properties":{"sheetId":4,"title":"Dashboard","gridProperties":{"rowCount":1000}}}]}"""),
            Json("""{"spreadsheetId":"s","clearedRange":"priority!A:ZZ"}"""),
            Json("""{"spreadsheetId":"s","updatedRange":"priority!A1"}"""),
            Json("""{"spreadsheetId":"s","replies":[{}]}"""),
            Json("""{"spreadsheetId":"s","clearedRange":"ticket!A:ZZ"}"""),
            Json("""{"spreadsheetId":"s","updatedRange":"ticket!A1"}"""),
            Json("""{"spreadsheetId":"s","replies":[{}]}"""),
            Json("""{"spreadsheetId":"s","clearedRange":"ticket_status_event!A:ZZ"}"""),
            Json("""{"spreadsheetId":"s","updatedRange":"ticket_status_event!A1"}"""),
            Json("""{"spreadsheetId":"s","replies":[{}]}"""),
            Json("""{"spreadsheetId":"s","clearedRange":"Dashboard!A:ZZ"}"""),
            Json("""{"spreadsheetId":"s","updatedRange":"Dashboard!A1"}"""),
            Json("""{"spreadsheetId":"s","replies":[{}]}"""));

        var rows = await SheetsSync.SyncAsync(dump, client);

        Assert.Equal(3, rows);
        var batches = stub.Sent
            .Where(sent => sent.Request!.RequestUri!.AbsolutePath.EndsWith(":batchUpdate"))
            .Select(sent => JsonNode.Parse(sent.Body!)!)
            .ToList();
        Assert.Equal(4, batches.Count);
        var ticketBatch = batches[1];
        var kinds = ticketBatch["requests"]!.AsArray()
            .Select(request => request!.AsObject().First().Key).ToList();
        Assert.Contains("addBanding", kinds);
        Assert.Contains("setBasicFilter", kinds);
        Assert.Contains("addChart", batches[3]["requests"]!.AsArray()
            .Select(request => request!.AsObject().First().Key));
    }

    private sealed class StubSequence
    {
        private readonly Queue<HttpResponseMessage> _replies = [];

        public List<(HttpRequestMessage? Request, string? Body)> Sent { get; } = [];

        public SheetsClient Client { get; }

        public StubSequence()
        {
            var handler = new SequenceHandler(this);
            Client = new SheetsClient(new HttpClient(handler), new SheetsOptions(
                "Host=x", "cid", "csecret", "rtoken", "sheet1", TimeSpan.FromMinutes(10)));
        }

        public void Replies(params HttpResponseMessage[] replies)
        {
            foreach (var reply in replies)
                _replies.Enqueue(reply);
        }

        private sealed class SequenceHandler(StubSequence owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string? body = null;
                if (request.Content is not null)
                    body = await request.Content.ReadAsStringAsync(cancellationToken);
                owner.Sent.Add((request, body));
                if (owner._replies.Count == 0) throw new InvalidOperationException("no stub reply queued");
                return owner._replies.Dequeue();
            }
        }
    }

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingReverse(params string[] tabs) : IReverseSync
    {
        public IReadOnlyCollection<string> Tabs { get; } = tabs;
        public List<(string Tab, int RowCount)> Applied { get; } = [];

        public Task ApplyAsync(string tab, IReadOnlyList<IReadOnlyList<string?>> rows,
            CancellationToken ct = default)
        {
            Applied.Add((tab, rows.Count));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Sync_ReverseAppliesConfigTabs_BeforeWritingValues()
    {
        var stub = new StubSequence();
        var client = stub.Client;
        var reverse = new RecordingReverse("priority", "not_a_tab");
        var dump = new List<NeonTable>
        {
            new("ticket", ["ticket_id"], [Row("abc")]),
            new("priority", ["priority_code", "description"], []),
        };
        stub.Replies(
            Json("""{"access_token":"tok","expires_in":3600,"token_type":"Bearer"}"""),
            Json("""{"sheets":[{"properties":{"sheetId":1,"title":"ticket","gridProperties":{"rowCount":1000}}},{"properties":{"sheetId":2,"title":"priority","gridProperties":{"rowCount":1000}}},{"properties":{"sheetId":3,"title":"Dashboard","gridProperties":{"rowCount":1000}}}]}"""),
            Json("""{"range":"priority!A1:ZZ","values":[["Priority Code","Description"],["urgent","ping me now"]]}"""),
            Json("""{"spreadsheetId":"s","clearedRange":"ticket!A:ZZ"}"""),
            Json("""{"spreadsheetId":"s","updatedRange":"ticket!A1"}"""),
            Json("""{"spreadsheetId":"s","replies":[{}]}"""),
            Json("""{"spreadsheetId":"s","clearedRange":"priority!A:ZZ"}"""),
            Json("""{"spreadsheetId":"s","updatedRange":"priority!A1"}"""),
            Json("""{"spreadsheetId":"s","replies":[{}]}"""),
            Json("""{"spreadsheetId":"s","clearedRange":"Dashboard!A:ZZ"}"""),
            Json("""{"spreadsheetId":"s","updatedRange":"Dashboard!A1"}"""),
            Json("""{"spreadsheetId":"s","replies":[{}]}"""));

        await SheetsSync.SyncAsync(dump, client, reverse);

        var applied = Assert.Single(reverse.Applied);
        Assert.Equal(("priority", 2), applied);
        var readAt = stub.Sent.FindIndex(sent =>
            sent.Request!.RequestUri!.AbsolutePath.Contains("/values/priority"));
        var writeAt = stub.Sent.FindIndex(sent =>
            sent.Request!.RequestUri!.AbsolutePath.Contains("/values/ticket%21A1"));
        Assert.True(readAt >= 0 && writeAt > readAt);
    }
}
