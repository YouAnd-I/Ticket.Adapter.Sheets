using System.Globalization;
using System.Text.Json.Nodes;

namespace Ticket.Adapter.Sheets;

public enum ColumnKind
{
    Identifier,
    DateTime,
    Boolean,
    LongText,
    Number,
    Text,
}

public sealed record ColumnPlan(
    string Name,
    string Header,
    ColumnKind Kind,
    int Width,
    IReadOnlyList<string>? Dropdown = null)
{
    public bool Wrap => Kind is ColumnKind.LongText;

    public string? NumberFormat => Kind switch
    {
        ColumnKind.Identifier => "@",
        ColumnKind.DateTime => "yyyy-mm-dd hh:mm:ss",
        _ => null,
    };
}

public sealed record SheetPlan(string Tab, ColumnPlan[] Columns, List<JsonArray> Rows)
{
    public string[] Headers => Columns.Select(column => column.Header).ToArray();
}

public static class SheetPlanBuilder
{
    private const int MinWidth = 76;
    private const int WidthCap = 240;
    private const int LongTextWidthCap = 320;
    private const int SampleRows = 200;

    internal static readonly DateTime SheetEpoch = new(1899, 12, 30);

    internal static double Serial(DateTimeOffset moment) =>
        (moment.UtcTicks - SheetEpoch.Ticks) / (double)TimeSpan.TicksPerDay;

    public static SheetPlan Build(NeonTable table, IReadOnlyList<NeonTable> dump)
    {
        var dropdowns = DropdownValues(dump);
        var columns = new ColumnPlan[table.Columns.Length];
        for (var i = 0; i < table.Columns.Length; i++)
        {
            var name = table.Columns[i];
            var kind = KindOf(name, table.Rows, i);
            columns[i] = new ColumnPlan(
                name,
                DisplayHeader(name),
                kind,
                WidthOf(kind, name, table.Rows, i),
                dropdowns.TryGetValue(name, out var values) ? values : null);
        }

        var rows = new List<JsonArray>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var adjusted = new JsonArray();
            for (var i = 0; i < row.Count; i++)
                adjusted.Add(AdjustCell(row[i], columns[i].Kind));
            rows.Add(adjusted);
        }
        return new SheetPlan(table.Name, columns, rows);
    }

    internal static string DisplayHeader(string name) => string.Join(' ', name.Split('_')
        .Select(part => char.ToUpper(part[0], CultureInfo.InvariantCulture) + part[1..]));

    private static ColumnKind KindOf(string name, List<JsonArray> rows, int index)
    {
        if (name.EndsWith("_at_utc", StringComparison.OrdinalIgnoreCase)) return ColumnKind.DateTime;
        if (name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)) return ColumnKind.Identifier;

        var sawBoolean = false;
        var sawNumber = false;
        var longest = 0;
        foreach (var row in rows.Take(SampleRows))
        {
            if (row[index] is not JsonValue value) continue;
            if (value.TryGetValue<bool>(out _)) sawBoolean = true;
            else if (value.TryGetValue<long>(out _) || value.TryGetValue<double>(out _)) sawNumber = true;
            longest = Math.Max(longest, value.ToString()!.Length);
        }
        if (sawBoolean) return ColumnKind.Boolean;
        if (longest > 50) return ColumnKind.LongText;
        if (sawNumber) return ColumnKind.Number;
        return ColumnKind.Text;
    }

    private static int WidthOf(ColumnKind kind, string name, List<JsonArray> rows, int index)
    {
        var cap = kind is ColumnKind.LongText ? LongTextWidthCap : WidthCap;
        var longest = DisplayHeader(name).Length;
        foreach (var row in rows.Take(SampleRows))
            longest = Math.Max(longest, row[index]?.ToString()?.Length ?? 0);
        if (kind is ColumnKind.DateTime) longest = Math.Max(longest, 19);
        return Math.Max(MinWidth, Math.Min(cap, longest * 7 + 22));
    }

    private static JsonNode? AdjustCell(JsonNode? cell, ColumnKind kind)
    {
        if (cell is not JsonValue value) return cell?.DeepClone();
        if (kind is ColumnKind.Identifier && !value.TryGetValue<string>(out _))
            return JsonValue.Create(value.ToString()!);
        if (kind is ColumnKind.DateTime &&
            value.TryGetValue<string>(out var text) &&
            DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var moment))
            return JsonValue.Create(Serial(moment));
        return value.DeepClone();
    }

    private static Dictionary<string, IReadOnlyList<string>> DropdownValues(IReadOnlyList<NeonTable> dump) =>
        new Dictionary<string, string[]>
        {
            ["priority_code"] = Codes(dump, "priority", "priority_code"),
            ["status_code"] = Codes(dump, "ticket_status", "status_code"),
        }
        .Where(pair => pair.Value.Length > 0)
        .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);

    private static string[] Codes(IReadOnlyList<NeonTable> dump, string table, string column)
    {
        var source = dump.FirstOrDefault(t => t.Name == table);
        if (source is null) return [];
        var index = Array.IndexOf(source.Columns, column);
        if (index < 0) return [];
        return source.Rows
            .Select(row => row[index]?.ToString())
            .OfType<string>()
            .Where(code => code.Length > 0)
            .Distinct()
            .ToArray();
    }
}
