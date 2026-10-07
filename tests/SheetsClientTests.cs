using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ticket.Adapter.Sheets;
using Xunit;

namespace Ticket.Adapter.Sheets.Tests;

public class SheetsClientTests
{
    private sealed class StubHttp(params HttpResponseMessage[] replies) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Sent { get; } = [];
        private int _call;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((request, request.Content is null
                ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return replies[Math.Min(_call++, replies.Length - 1)];
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private const string Token = """
        {"access_token":"tok","expires_in":3600,"token_type":"Bearer"}
        """;

    private static SheetsOptions Options(string spreadsheetId = "sheet1") =>
        new("Host=x", "cid", "csecret", "rtoken", spreadsheetId, TimeSpan.FromMinutes(10));

    [Fact]
    public async Task WriteValues_ClearsThenWritesDisplayHeadersAndRows()
    {
        var stub = new StubHttp(
            Json(Token),
            Json("""{"spreadsheetId":"sheet1","clearedRange":"ticket!A:ZZ"}"""),
            Json("""{"spreadsheetId":"sheet1","updatedRange":"ticket!A1"}"""));
        var client = new SheetsClient(new HttpClient(stub), Options());
        var plan = SheetPlanBuilder.Build(
            new NeonTable("ticket", ["ticket_id", "created_at_utc"],
            [
                new JsonArray("abc", "2026-01-02 03:04:05"),
            ]),
            []);

        await client.WriteValuesAsync("sheet1", plan);

        Assert.Equal(3, stub.Sent.Count);
        Assert.Equal("https://oauth2.googleapis.com/token", stub.Sent[0].Request.RequestUri!.ToString());
        Assert.Contains("grant_type=refresh_token", stub.Sent[0].Body);
        Assert.EndsWith("/values/ticket%21A%3AZZ:clear", stub.Sent[1].Request.RequestUri!.ToString());
        Assert.EndsWith("/values/ticket%21A1?valueInputOption=RAW", stub.Sent[2].Request.RequestUri!.ToString());
        var body = JsonNode.Parse(stub.Sent[2].Body!)!;
        Assert.Equal("Ticket Id", body["values"]![0]![0]!.GetValue<string>());
        Assert.Equal("abc", body["values"]![1]![0]!.GetValue<string>());
        var serial = body["values"]![1]![1]!.GetValue<double>();
        Assert.Equal(
            SheetPlanBuilder.Serial(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
            serial, 10);
    }

    [Fact]
    public async Task AccessToken_IsCachedAcrossCalls()
    {
        var stub = new StubHttp(
            Json(Token),
            Json("""{"sheets":[]}"""),
            Json("""{"spreadsheetId":"sheet1","replies":[{}]}"""),
            Json("""{"spreadsheetId":"sheet1","replies":[{}]}"""));
        var client = new SheetsClient(new HttpClient(stub), Options());

        await client.FetchTabsAsync("sheet1");
        await client.ApplyRequestsAsync("sheet1", [new { updateSheetProperties = new { } }]);
        await client.ApplyRequestsAsync("sheet1", [new { updateSheetProperties = new { } }]);

        Assert.Equal(4, stub.Sent.Count);
        Assert.Single(stub.Sent, sent => sent.Request.RequestUri!.Host == "oauth2.googleapis.com");
    }

    [Fact]
    public async Task FetchTabs_ParsesBandingFormatsProtectionAndCharts()
    {
        var stub = new StubHttp(
            Json(Token),
            Json("""
                {"sheets":[
                  {"properties":{"sheetId":7,"title":"ticket","gridProperties":{"rowCount":1000}},
                   "bandedRanges":[{"bandedRangeId":91}],
                   "conditionalFormats":[{"ranges":[{"startRowIndex":1,"endRowIndex":5}]}],
                   "protectedRanges":[{"protectedRangeId":12,"description":"neon sync ticket.ticket_id",
                                       "range":{"startRowIndex":1,"endColumnIndex":1}}],
                   "charts":[]},
                  {"properties":{"sheetId":9,"title":"Dashboard","gridProperties":{"rowCount":1000}},
                   "charts":[{"chartId":44,"spec":{"title":"Tickets by status"}}]}
                ]}
                """));
        var client = new SheetsClient(new HttpClient(stub), Options());

        var tabs = await client.FetchTabsAsync("sheet1");

        var ticket = tabs.Single(tab => tab.Title == "ticket");
        Assert.Equal(7, ticket.SheetId);
        Assert.Equal(1000, ticket.RowCount);
        Assert.Equal([91L], ticket.BandedRangeIds);
        Assert.Equal(1, ticket.ConditionalFormats.Count);
        Assert.Equal(1, ticket.ConditionalFormats[0][0].StartRowIndex);
        Assert.Equal("neon sync ticket.ticket_id", ticket.ProtectedRanges[0].Description);
        var dashboard = tabs.Single(tab => tab.Title == "Dashboard");
        Assert.Equal("Tickets by status", dashboard.Charts[0].Title);
    }

    [Fact]
    public async Task ResolveSpreadsheetId_CreatesOne_WhenConfiguredIdIsEmpty()
    {
        var stub = new StubHttp(
            Json(Token),
            Json("""{"spreadsheetId":"created-42","properties":{"title":"NeonDB sync"}}"""));
        var client = new SheetsClient(new HttpClient(stub), Options(""));

        var id = await client.ResolveSpreadsheetIdAsync();

        Assert.Equal("created-42", id);
        Assert.Equal("created-42", await client.ResolveSpreadsheetIdAsync());
        Assert.Equal(2, stub.Sent.Count);
    }

    [Fact]
    public async Task ReadTab_TrimsTrailingEmptyAndPhantomCheckboxRows()
    {
        var stub = new StubHttp(
            Json(Token),
            Json("""{"values":[["User Id","Active"],["100","TRUE"],["","FALSE"],["","FALSE"]]}"""));
        var client = new SheetsClient(new HttpClient(stub), Options("sheet1"));

        var rows = await client.ReadTabAsync("sheet1", "it_staff");

        Assert.Equal(2, rows.Count);
        Assert.Equal("100", rows[1][0]);
    }

    [Fact]
    public void Cell_RendersValuesForSheets()
    {
        Assert.Equal("", NeonDump.Cell(null)!.GetValue<string>());
        Assert.Equal("", NeonDump.Cell(DBNull.Value)!.GetValue<string>());
        Assert.Equal(42L, NeonDump.Cell(42L)!.GetValue<long>());
        Assert.True(NeonDump.Cell(true)!.GetValue<bool>());
        Assert.Equal("x", NeonDump.Cell("x")!.GetValue<string>());
        Assert.Equal("2026-01-02 03:04:05",
            NeonDump.Cell(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero))!.GetValue<string>());
        Assert.Equal("0f8fad5b-d930-4b64-9c8f-2c1f2a3b4c5d",
            NeonDump.Cell(new Guid("0f8fad5b-d930-4b64-9c8f-2c1f2a3b4c5d"))!.GetValue<string>());
    }
}
