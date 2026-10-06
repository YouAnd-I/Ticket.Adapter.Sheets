using System.Text.Json;

namespace Ticket.Adapter.Sheets;

public sealed record SheetGridRange(
    int? StartRowIndex = null,
    int? EndRowIndex = null,
    int? StartColumnIndex = null,
    int? EndColumnIndex = null);

public sealed record SheetProtectedRange(long Id, string? Description, SheetGridRange Range);

public sealed record SheetChart(long ChartId, string? Title);

public sealed record SheetTabState(
    int SheetId,
    string Title,
    int RowCount,
    IReadOnlyList<long> BandedRangeIds,
    IReadOnlyList<IReadOnlyList<SheetGridRange>> ConditionalFormats,
    IReadOnlyList<SheetProtectedRange> ProtectedRanges,
    IReadOnlyList<SheetChart> Charts);

public static class SheetStateParser
{
    public static IReadOnlyList<SheetTabState> Parse(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("sheets", out var sheets))
            return [];

        var tabs = new List<SheetTabState>();
        foreach (var sheet in sheets.EnumerateArray())
        {
            var properties = sheet.GetProperty("properties");
            var grid = properties.TryGetProperty("gridProperties", out var gridProperties)
                ? gridProperties : default;
            tabs.Add(new SheetTabState(
                properties.GetProperty("sheetId").GetInt32(),
                properties.GetProperty("title").GetString()!,
                grid.ValueKind is JsonValueKind.Object &&
                    grid.TryGetProperty("rowCount", out var rowCount)
                    ? rowCount.GetInt32()
                    : 1000,
                Ids(sheet, "bandedRanges", "bandedRangeId"),
                ConditionalFormats(sheet),
                ProtectedRanges(sheet),
                Charts(sheet)));
        }
        return tabs;
    }

    private static IReadOnlyList<long> Ids(JsonElement sheet, string collection, string idField)
    {
        if (!sheet.TryGetProperty(collection, out var items)) return [];
        return items.EnumerateArray()
            .Where(item => item.TryGetProperty(idField, out _))
            .Select(item => item.GetProperty(idField).GetInt64())
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<SheetGridRange>> ConditionalFormats(JsonElement sheet)
    {
        if (!sheet.TryGetProperty("conditionalFormats", out var items)) return [];
        return items.EnumerateArray()
            .Select(item => item.GetProperty("ranges").EnumerateArray().Select(Range).ToArray())
            .ToArray();
    }

    private static IReadOnlyList<SheetProtectedRange> ProtectedRanges(JsonElement sheet)
    {
        if (!sheet.TryGetProperty("protectedRanges", out var items)) return [];
        return items.EnumerateArray()
            .Where(item => item.TryGetProperty("protectedRangeId", out _))
            .Select(item => new SheetProtectedRange(
                item.GetProperty("protectedRangeId").GetInt64(),
                item.TryGetProperty("description", out var description)
                    ? description.GetString()
                    : null,
                item.TryGetProperty("range", out var range) ? Range(range) : new SheetGridRange()))
            .ToArray();
    }

    private static IReadOnlyList<SheetChart> Charts(JsonElement sheet)
    {
        if (!sheet.TryGetProperty("charts", out var items)) return [];
        return items.EnumerateArray()
            .Select(item => new SheetChart(
                item.GetProperty("chartId").GetInt64(),
                item.TryGetProperty("spec", out var spec) &&
                    spec.TryGetProperty("title", out var title)
                    ? title.GetString()
                    : null))
            .ToArray();
    }

    private static SheetGridRange Range(JsonElement range) => new(
        Optional(range, "startRowIndex"),
        Optional(range, "endRowIndex"),
        Optional(range, "startColumnIndex"),
        Optional(range, "endColumnIndex"));

    private static int? Optional(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value.GetInt32() : null;
}
