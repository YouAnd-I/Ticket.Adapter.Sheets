using System.Globalization;
using System.Text.Json.Nodes;

namespace Ticket.Adapter.Sheets;

public sealed record DashboardCounts(string Label, int Count);

public sealed record DashboardRecent(
    string TicketId, string Status, string? Priority, double AtSerial, string? Title);

public sealed record DashboardHeader(int RowIndex, int StartColumn, int EndColumn);

public sealed record DashboardModel(
    string SyncedAtUtc,
    IReadOnlyList<DashboardCounts> Status,
    IReadOnlyList<DashboardCounts> Priority,
    IReadOnlyList<DashboardRecent> Recent,
    int StatusRowsStart,
    int PriorityRowsStart,
    int RecentHeaderRowIndex)
{
    public const string Tab = "Dashboard";
    public const string StatusChartTitle = "Tickets by status";
    public const string PriorityChartTitle = "Tickets by priority";

    public IReadOnlyList<string> ManagedChartTitles => [StatusChartTitle, PriorityChartTitle];

    public IReadOnlyList<DashboardHeader> SectionHeaders =>
    [
        new(3, 0, 2),
        new(3, 3, 5),
        new(RecentHeaderRowIndex, 0, 5),
    ];

    public JsonArray Matrix()
    {
        var height = RecentHeaderRowIndex + 1 + Math.Max(Recent.Count, 1) + 1;
        var grid = new JsonArray();
        for (var row = 0; row < height; row++)
        {
            var cells = new JsonNode?[5];
            if (row == 0)
                cells[0] = "NeonDB — IT Tickets";
            else if (row == 1)
                cells[0] = $"synced {SyncedAtUtc}";
            else if (row == 3)
            {
                cells[0] = "Status";
                cells[1] = "Count";
                cells[3] = "Priority";
                cells[4] = "Count";
            }
            else if (row > 3 && row - 4 < Math.Max(Status.Count, Priority.Count))
            {
                if (row - 4 < Status.Count)
                {
                    cells[0] = Status[row - 4].Label;
                    cells[1] = Status[row - 4].Count;
                }
                if (row - 4 < Priority.Count)
                {
                    cells[3] = Priority[row - 4].Label;
                    cells[4] = Priority[row - 4].Count;
                }
            }
            else if (row == RecentHeaderRowIndex)
            {
                cells[0] = "Ticket";
                cells[1] = "Status";
                cells[2] = "Priority";
                cells[3] = "At (UTC)";
                cells[4] = "Title";
            }
            else if (row > RecentHeaderRowIndex && row - RecentHeaderRowIndex - 1 < Recent.Count)
            {
                var recent = Recent[row - RecentHeaderRowIndex - 1];
                cells[0] = recent.TicketId;
                cells[1] = recent.Status;
                cells[2] = recent.Priority;
                cells[3] = recent.AtSerial;
                cells[4] = recent.Title;
            }
            grid.Add(new JsonArray(cells));
        }
        return grid;
    }
}

public static class SheetsDashboard
{
    private static readonly string[] KnownStatuses = ["open", "planned", "complete", "reopened", "unsolved"];

    public static DashboardModel Build(IReadOnlyList<NeonTable> dump)
    {
        var tickets = dump.FirstOrDefault(table => table.Name == "ticket");
        var events = dump.FirstOrDefault(table => table.Name == "ticket_status_event");
        var priorities = dump.FirstOrDefault(table => table.Name == "priority");

        var latest = LatestStatusPerTicket(events);
        var statusOrder = KnownStatuses
            .Concat(latest.Values.Except(KnownStatuses).OrderBy(status => status, StringComparer.Ordinal))
            .Distinct()
            .ToArray();
        var statusCounts = statusOrder
            .Select(status => new DashboardCounts(status, latest.Values.Count(value => value == status)))
            .ToArray();

        var priorityCodes = priorities is null
            ? []
            : Column(priorities, "priority_code");
        var ticketPriority = tickets is null
            ? []
            : IndexBy(tickets, "ticket_id", row => RowValue(tickets, row, "priority_code"));
        var codes = priorityCodes.OfType<string>().ToArray();
        if (codes.Length == 0)
            codes = ticketPriority.Values.OfType<string>()
                .Distinct().OrderBy(code => code, StringComparer.Ordinal).ToArray();
        var priorityCounts = codes
            .Select(code => new DashboardCounts(
                code,
                tickets is null ? 0 : CountBy(tickets, "priority_code", code)))
            .ToArray();

        var ticketTitle = tickets is null
            ? []
            : IndexBy(tickets, "ticket_id", row => RowValue(tickets, row, "title"));
        var recent = RecentActivity(events, latest, ticketPriority, ticketTitle);

        return new DashboardModel(
            DateTimeOffset.UtcNow.ToString("u"),
            statusCounts,
            priorityCounts,
            recent,
            StatusRowsStart: 4,
            PriorityRowsStart: 4,
            RecentHeaderRowIndex: 3 + Math.Max(statusCounts.Length, priorityCounts.Length) + 3);
    }

    private static Dictionary<string, string> LatestStatusPerTicket(NeonTable? events)
    {
        var latest = new Dictionary<string, string>();
        if (events is null) return latest;
        var newest = new Dictionary<string, double>();
        foreach (var row in events.Rows)
        {
            var ticket = RowValue(events, row, "ticket_id");
            if (ticket is null) continue;
            var status = RowValue(events, row, "status_code") ?? "";
            var serial = SerialOf(RowValue(events, row, "occurred_at_utc"));
            if (!newest.TryGetValue(ticket, out var best) || serial >= best)
            {
                newest[ticket] = serial;
                latest[ticket] = status;
            }
        }
        return latest;
    }

    private static List<DashboardRecent> RecentActivity(
        NeonTable? events,
        Dictionary<string, string> latest,
        Dictionary<string, string?> ticketPriority,
        Dictionary<string, string?> ticketTitle)
    {
        if (events is null) return [];
        return events.Rows
            .Select(row => (
                Ticket: RowValue(events, row, "ticket_id"),
                Status: RowValue(events, row, "status_code") ?? "",
                Serial: SerialOf(RowValue(events, row, "occurred_at_utc"))))
            .Where(entry => entry.Ticket is not null)
            .OrderByDescending(entry => entry.Serial)
            .Take(10)
            .Select(entry => new DashboardRecent(
                entry.Ticket!,
                entry.Status,
                ticketPriority.TryGetValue(entry.Ticket!, out var priority) ? priority : null,
                entry.Serial,
                ticketTitle.TryGetValue(entry.Ticket!, out var title) ? title : null))
            .ToList();
    }

    private static string? RowValue(NeonTable table, JsonArray row, string column)
    {
        var index = Array.IndexOf(table.Columns, column);
        return index < 0 ? null : row[index]?.ToString();
    }

    private static string?[] Column(NeonTable table, string column)
    {
        var index = Array.IndexOf(table.Columns, column);
        if (index < 0) return [];
        return table.Rows.Select(row => row[index]?.ToString()).ToArray();
    }

    private static int CountBy(NeonTable table, string column, string value)
    {
        var index = Array.IndexOf(table.Columns, column);
        if (index < 0) return 0;
        return table.Rows.Count(row => row[index]?.ToString() == value);
    }

    private static Dictionary<string, TValue> IndexBy<TValue>(
        NeonTable table, string keyColumn, Func<JsonArray, TValue?> select) where TValue : class
    {
        var keyIndex = Array.IndexOf(table.Columns, keyColumn);
        if (keyIndex < 0) return [];
        var index = new Dictionary<string, TValue>();
        foreach (var row in table.Rows)
        {
            var key = row[keyIndex]?.ToString();
            if (key is null) continue;
            if (select(row) is { } value)
                index[key] = value;
        }
        return index;
    }

    private static double SerialOf(string? text)
    {
        if (text is not null && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
            return SheetPlanBuilder.Serial(moment);
        return 0;
    }
}
