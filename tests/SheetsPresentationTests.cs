using System.Text.Json;
using System.Text.Json.Nodes;
using Ticket.Adapter.Sheets;
using Xunit;

namespace Ticket.Adapter.Sheets.Tests;

public class SheetsPresentationTests
{
    private static JsonArray Row(params object?[] cells) =>
        new(cells.Select(NeonDump.Cell).ToArray());

    private static (NeonTable Table, List<NeonTable> Dump) TicketFixture()
    {
        var table = new NeonTable("ticket",
            ["ticket_id", "status_code", "priority_code", "classifier_offline", "description"],
            [
                Row("abc", "open", "urgent", false, "short"),
                Row("def", "complete", "no-rush", true, "short"),
            ]);
        var dump = new List<NeonTable>
        {
            new("priority", ["priority_code"], [Row("urgent"), Row("no-rush")]),
            new("ticket_status", ["status_code"], [Row("open")]),
            table,
        };
        return (table, dump);
    }

    private static SheetPlan Plan()
    {
        var (table, dump) = TicketFixture();
        return SheetPlanBuilder.Build(table, dump);
    }

    private static SheetTabState State(
        IReadOnlyList<long>? bandings = null,
        IReadOnlyList<IReadOnlyList<SheetGridRange>>? conditionalFormats = null,
        IReadOnlyList<SheetProtectedRange>? protectedRanges = null,
        IReadOnlyList<SheetChart>? charts = null) => new(
        7, "ticket", 1000,
        bandings ?? [],
        conditionalFormats ?? [],
        protectedRanges ?? [],
        charts ?? []);

    private static string RequestKind(object request)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        return doc.RootElement.EnumerateObject().First().Name;
    }

    private static JsonElement Request(object request, string kind)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        return doc.RootElement.GetProperty(kind).Clone();
    }

    [Fact]
    public void TableRequests_AreDeterministic_WithNoExistingState()
    {
        var requests = SheetsPresentation.TableRequests(7, Plan(), State());
        var serialized = requests.Select(request => JsonSerializer.Serialize(request)).ToList();

        var again = SheetsPresentation.TableRequests(7, Plan(), State());
        Assert.Equal(serialized, again.Select(request => JsonSerializer.Serialize(request)));

        Assert.Contains(requests, request => RequestKind(request) == "updateSheetProperties");
        Assert.Contains(requests, request => RequestKind(request) == "setBasicFilter");
        Assert.Contains(requests, request => RequestKind(request) == "addBanding");
        Assert.Contains(requests, request => RequestKind(request) == "repeatCell");
        Assert.Contains(requests, request => RequestKind(request) == "updateDimensionProperties");
    }

    [Fact]
    public void TableRequests_BandingFilterAndHeightsCoverEveryDataRow()
    {
        var plan = Plan();
        var requests = SheetsPresentation.TableRequests(7, plan, State());

        var banding = Request(requests.First(request => RequestKind(request) == "addBanding"), "addBanding")
            .GetProperty("bandedRange").GetProperty("range");
        Assert.Equal(0, banding.GetProperty("startRowIndex").GetInt32());
        Assert.Equal(plan.Rows.Count + 1, banding.GetProperty("endRowIndex").GetInt32());

        var filter = Request(requests.First(request => RequestKind(request) == "setBasicFilter"), "setBasicFilter")
            .GetProperty("filter").GetProperty("range");
        Assert.Equal(0, filter.GetProperty("startRowIndex").GetInt32());
        Assert.Equal(plan.Rows.Count + 1, filter.GetProperty("endRowIndex").GetInt32());

        var heights = requests.Select(request => RequestKind(request) == "updateDimensionProperties"
                ? (JsonElement?)Request(request, "updateDimensionProperties") : null)
            .OfType<JsonElement>()
            .Single(dimension => dimension.GetProperty("range").GetProperty("dimension").GetString() == "ROWS")
            .GetProperty("range");
        Assert.Equal(0, heights.GetProperty("startIndex").GetInt32());
        Assert.Equal(plan.Rows.Count + 1, heights.GetProperty("endIndex").GetInt32());
    }

    [Fact]
    public void TableRequests_DeleteStaleBandingAndIntersectingConditionalRules()
    {
        var state = State(
            bandings: [91L, 92L],
            conditionalFormats:
            [
                new List<SheetGridRange> { new(1, 5, 2, 3) },
                new List<SheetGridRange> { new(500, 900, 2, 3) },
            ]);

        var requests = SheetsPresentation.TableRequests(7, Plan(), state);

        Assert.Equal(2, requests.Count(request => RequestKind(request) == "deleteBanding"));
        var deletes = requests.Where(request => RequestKind(request) == "deleteConditionalFormatRule")
            .Select(request => Request(request, "deleteConditionalFormatRule").GetProperty("index").GetInt32())
            .ToList();
        Assert.Equal([0], deletes);
    }

    [Fact]
    public void TableRequests_AddCodeRules_Validation_AndProtections()
    {
        var requests = SheetsPresentation.TableRequests(7, Plan(), State());
        var body = JsonSerializer.Serialize(requests);

        Assert.Contains("TEXT_EQ", body);
        Assert.Contains("\"userEnteredValue\":\"open\"", body);
        Assert.Contains("\"userEnteredValue\":\"urgent\"", body);
        Assert.Contains("BOOLEAN", body);
        Assert.Contains("ONE_OF_LIST", body);
        Assert.Contains("\"warningOnly\":true", body);
        Assert.Single(requests.Where(request => RequestKind(request) == "addProtectedRange"));
        Assert.Contains("neon sync ticket.ticket_id", body);
    }

    [Fact]
    public void TableRequests_SkipProtectionThatAlreadyMatches()
    {
        var state = State(protectedRanges:
        [
            new SheetProtectedRange(12, "neon sync ticket.ticket_id",
                new SheetGridRange(1, 1000, 0, 1)),
        ]);

        var requests = SheetsPresentation.TableRequests(7, Plan(), state);

        Assert.Empty(requests.Where(request => RequestKind(request) == "addProtectedRange"));
        Assert.Empty(requests.Where(request => RequestKind(request) == "updateProtectedRange"));
    }

    [Fact]
    public void TableRequests_UpdateProtectionWhoseColumnMoved()
    {
        var state = State(protectedRanges:
        [
            new SheetProtectedRange(12, "neon sync ticket.ticket_id",
                new SheetGridRange(1, 1000, 4, 5)),
        ]);

        var requests = SheetsPresentation.TableRequests(7, Plan(), state);

        Assert.Single(requests.Where(request => RequestKind(request) == "updateProtectedRange"));
        Assert.Empty(requests.Where(request => RequestKind(request) == "addProtectedRange"));
    }

    [Fact]
    public void DashboardRequests_ManageOnlyOwnedCharts_AndMoveTabFirst()
    {
        var dashboard = SheetsDashboard.Build([
            new NeonTable("priority", ["priority_code"], [Row("urgent"), Row("no-rush"), Row("report")]),
            new NeonTable("ticket", ["ticket_id", "priority_code", "title"],
            [
                Row("abc", "urgent", "printer"),
                Row("def", "no-rush", "mouse"),
            ]),
            new NeonTable("ticket_status_event",
                ["ticket_id", "status_code", "occurred_at_utc"],
            [
                Row("abc", "open", "2026-01-01 00:00:00"),
                Row("abc", "complete", "2026-01-02 00:00:00"),
                Row("def", "open", "2026-01-03 00:00:00"),
            ]),
        ]);
        var state = new SheetTabState(9, "Dashboard", 1000, [], [], [],
        [
            new SheetChart(44, DashboardModel.StatusChartTitle),
            new SheetChart(45, "My own chart"),
        ]);

        var requests = SheetsPresentation.DashboardRequests(9, dashboard, state);
        var body = JsonSerializer.Serialize(requests);

        var deletions = requests.Where(request => RequestKind(request) == "deleteEmbeddedObject")
            .Select(request => Request(request, "deleteEmbeddedObject").GetProperty("objectId").GetInt64())
            .ToList();
        Assert.Equal([44L], deletions);
        Assert.Equal(3, requests.Count(request => RequestKind(request) == "addChart"));
        Assert.Contains("\"index\":0", body);
        Assert.Contains("pieHole", body);
    }
}
