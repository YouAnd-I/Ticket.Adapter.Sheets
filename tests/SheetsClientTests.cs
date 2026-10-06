using System.Net;
using System.Text;
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

    private static JsonArray Row(params object?[] cells) =>
        new(cells.Select(NeonDump.Cell).ToArray());

    [Fact]
    public async Task ReplaceTab_ClearsThenWritesHeaderAndRows()
    {
        var stub = new StubHttp(
            Json(Token),
            Json("""{"spreadsheetId":"sheet1","clearedRange":"ticket!A:ZZ"}"""),
            Json("""{"spreadsheetId":"sheet1","updatedRange":"ticket!A1"}"""));
        var client = new SheetsClient(new HttpClient(stub), Options());
        var table = new NeonTable("ticket", ["ticket_id", "title"],
            [Row("abc", "printer"), Row("def", null)]);

        await client.ReplaceTabAsync("sheet1", table);

        Assert.Equal(3, stub.Sent.Count);
        Assert.Equal("https://oauth2.googleapis.com/token", stub.Sent[0].Request.RequestUri!.ToString());
        Assert.Contains("grant_type=refresh_token", stub.Sent[0].Body);
        Assert.Contains("refresh_token=rtoken", stub.Sent[0].Body);
        Assert.Equal(HttpMethod.Post, stub.Sent[1].Request.Method);
        Assert.EndsWith("/values/ticket%21A%3AZZ:clear", stub.Sent[1].Request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, stub.Sent[2].Request.Method);
        Assert.EndsWith("/values/ticket%21A1?valueInputOption=RAW", stub.Sent[2].Request.RequestUri!.ToString());
        var body = JsonNode.Parse(stub.Sent[2].Body!)!.AsObject();
        Assert.Equal(3, body["values"]!.AsArray().Count);
        Assert.Equal("ticket_id", body["values"]![0]![0]!.GetValue<string>());
        Assert.Equal("", body["values"]![2]![1]!.GetValue<string>());
    }

    [Fact]
    public async Task AccessToken_IsCachedAcrossCalls()
    {
        var stub = new StubHttp(
            Json(Token),
            Json("""{"spreadsheetId":"sheet1","clearedRange":"t!A:ZZ"}"""),
            Json("""{"spreadsheetId":"sheet1","updatedRange":"t!A1"}"""),
            Json("""{"spreadsheetId":"sheet1","clearedRange":"t!A:ZZ"}"""),
            Json("""{"spreadsheetId":"sheet1","updatedRange":"t!A1"}"""));
        var client = new SheetsClient(new HttpClient(stub), Options());
        var table = new NeonTable("t", ["a"], [Row(1)]);

        await client.ReplaceTabAsync("sheet1", table);
        await client.ReplaceTabAsync("sheet1", table);

        Assert.Equal(5, stub.Sent.Count);
        Assert.Single(stub.Sent, sent => sent.Request.RequestUri!.Host == "oauth2.googleapis.com");
    }

    [Fact]
    public async Task AddTabs_SendsOneAddSheetRequestPerTitle()
    {
        var stub = new StubHttp(
            Json(Token),
            Json("""{"spreadsheetId":"sheet1","replies":[{}]}"""));
        var client = new SheetsClient(new HttpClient(stub), Options());

        await client.AddTabsAsync("sheet1", ["ticket", "ticket_note"]);

        var body = JsonNode.Parse(stub.Sent[1].Body!)!;
        var titles = body["requests"]!.AsArray()
            .Select(request => request!["addSheet"]!["properties"]!["title"]!.GetValue<string>())
            .ToArray();
        Assert.Equal(["ticket", "ticket_note"], titles);
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
        Assert.EndsWith("/v4/spreadsheets", stub.Sent[1].Request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.Sent[1].Request.Method);

        Assert.Equal("created-42", await client.ResolveSpreadsheetIdAsync());
        Assert.Equal(2, stub.Sent.Count);
    }

    [Fact]
    public async Task Sync_CreatesMissingTabsThenRewritesEveryTable()
    {
        var stub = new StubHttp(
            Json(Token),
            Json("""{"sheets":[{"properties":{"title":"discord_user"}}]}"""),
            Json("""{"spreadsheetId":"sheet1","replies":[{},{}]}"""),
            Json("""{"spreadsheetId":"sheet1","clearedRange":"discord_user!A:ZZ"}"""),
            Json("""{"spreadsheetId":"sheet1","updatedRange":"discord_user!A1"}"""),
            Json("""{"spreadsheetId":"sheet1","clearedRange":"ticket!A:ZZ"}"""),
            Json("""{"spreadsheetId":"sheet1","updatedRange":"ticket!A1"}"""));
        var client = new SheetsClient(new HttpClient(stub), Options());
        var tables = new List<NeonTable>
        {
            new("discord_user", ["user_id"], [Row(42L)]),
            new("ticket", ["ticket_id"], [Row("abc"), Row("def")]),
        };

        var rows = await SheetsSync.SyncAsync(tables, client);

        Assert.Equal(3, rows);
        var addedTitles = JsonNode.Parse(stub.Sent[2].Body!)!["requests"]!.AsArray()
            .Select(request => request!["addSheet"]!["properties"]!["title"]!.GetValue<string>());
        Assert.Equal(["ticket"], addedTitles);
        Assert.Equal(2, stub.Sent.Count(sent =>
            sent.Request.Method == HttpMethod.Post && sent.Request.RequestUri!.AbsolutePath.EndsWith(":clear")));
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
        Assert.Equal("2026-01-02 03:04:05",
            NeonDump.Cell(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))!.GetValue<string>());
        Assert.Equal("0f8fad5b-d930-4b64-9c8f-2c1f2a3b4c5d",
            NeonDump.Cell(new Guid("0f8fad5b-d930-4b64-9c8f-2c1f2a3b4c5d"))!.GetValue<string>());
    }
}
