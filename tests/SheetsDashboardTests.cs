using System.Text.Json.Nodes;
using Ticket.Adapter.Sheets;
using Xunit;

namespace Ticket.Adapter.Sheets.Tests;

public class SheetsDashboardTests
{
    private static JsonArray Row(params object?[] cells) =>
        new(cells.Select(NeonDump.Cell).ToArray());

    private static List<NeonTable> Dump() =>
    [
        new NeonTable("priority", ["priority_code"], [Row("urgent"), Row("no-rush"), Row("report")]),
        new NeonTable("ticket", ["ticket_id", "priority_code", "title"],
        [
            Row("abc", "urgent", "printer"),
            Row("def", "no-rush", "mouse"),
            Row("ghi", "urgent", "screen"),
        ]),
        new NeonTable("ticket_status_event", ["ticket_id", "status_code", "occurred_at_utc"],
        [
            Row("abc", "open", "2026-01-01 00:00:00"),
            Row("abc", "planned", "2026-01-02 00:00:00"),
            Row("abc", "complete", "2026-01-03 00:00:00"),
            Row("def", "open", "2026-01-04 00:00:00"),
        ]),
    ];

    [Fact]
    public void Build_LatestStatusWins_PerTicket()
    {
        var dashboard = SheetsDashboard.Build(Dump());

        Assert.Equal(
        [
            new DashboardCounts("open", 1),
            new DashboardCounts("planned", 0),
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

        Assert.Equal(4, dashboard.Recent.Count);
        Assert.Equal("def", dashboard.Recent[0].TicketId);
        Assert.Equal("open", dashboard.Recent[0].Status);
        Assert.Equal("no-rush", dashboard.Recent[0].Priority);
        Assert.Equal("mouse", dashboard.Recent[0].Title);
        Assert.Equal("complete", dashboard.Recent[1].Status);
        Assert.Equal("printer", dashboard.Recent[1].Title);
    }

    [Fact]
    public void Matrix_FillsBothCountBlocksAndRecentTable()
    {
        var dashboard = SheetsDashboard.Build(Dump());
        var matrix = dashboard.Matrix();

        Assert.Equal("NeonDB — IT Tickets", matrix[0]![0]!.GetValue<string>());
        Assert.Equal("Status", matrix[3]![0]!.GetValue<string>());
        Assert.Equal("Count", matrix[3]![1]!.GetValue<string>());
        Assert.Equal("Priority", matrix[3]![3]!.GetValue<string>());

        Assert.Equal("open", matrix[4]![0]!.GetValue<string>());
        Assert.Equal(1, matrix[4]![1]!.GetValue<int>());
        Assert.Equal("urgent", matrix[4]![3]!.GetValue<string>());
        Assert.Equal(2, matrix[4]![4]!.GetValue<int>());

        var recentHeader = dashboard.RecentHeaderRowIndex;
        Assert.Equal("Ticket", matrix[recentHeader]![0]!.GetValue<string>());
        Assert.Equal("def", matrix[recentHeader + 1]![0]!.GetValue<string>());
        Assert.Equal("open", matrix[recentHeader + 1]![1]!.GetValue<string>());
        Assert.True(matrix[recentHeader + 1]![3]!.GetValue<double>() > 40000);
    }

    [Fact]
    public void Build_SurvivesEmptyDump()
    {
        var dashboard = SheetsDashboard.Build([]);

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
}
