using System.Globalization;
using System.Text.Json.Nodes;

namespace Ticket.Adapter.Sheets;

public sealed record DashboardCounts(string Label, int Count);

public sealed record DashboardRecent(
    string TicketId, string Status, string? Priority, double AtSerial, string? Title);

public sealed record DashboardTicket(
    string TicketId, string? Title, string? Priority, string Status,
    string? Assignee, double CreatedSerial, double AgeDays);

public sealed record DashboardSolver(string Staff, int Solved);

public sealed record DashboardHeader(int RowIndex, int StartColumn, int EndColumn);

public sealed record DashboardModel(
    string SyncedAtUtc,
    int OpenTotal,
    int OpenStale,
    int Planned,
    int Complete,
    IReadOnlyList<DashboardTicket> OpenTickets,
    IReadOnlyList<DashboardSolver> Solvers,
    IReadOnlyList<DashboardCounts> Status,
    IReadOnlyList<DashboardCounts> Priority,
    IReadOnlyList<DashboardRecent> Recent,
    int OpenHeaderRowIndex,
    int SolvedHeaderRowIndex,
    int RecentHeaderRowIndex)
{
    public const string Tab = "Dashboard";
    public const int Width = 8;
    public const string StatusChartTitle = "Tickets by status";
    public const string PriorityChartTitle = "Tickets by priority";
    public const string SolvedChartTitle = "Solved by staff";

    public int CountsRowsStart => SolvedHeaderRowIndex + 2;

    public IReadOnlyList<string> ManagedChartTitles =>
        [StatusChartTitle, PriorityChartTitle, SolvedChartTitle];

    public IReadOnlyList<DashboardHeader> SectionHeaders =>
    [
        new(OpenHeaderRowIndex, 0, Width),
        new(OpenHeaderRowIndex + 1, 0, Width),
        new(SolvedHeaderRowIndex, 0, 2),
        new(SolvedHeaderRowIndex, 3, 5),
        new(SolvedHeaderRowIndex, 6, 8),
        new(SolvedHeaderRowIndex + 1, 0, 2),
        new(SolvedHeaderRowIndex + 1, 3, 5),
        new(SolvedHeaderRowIndex + 1, 6, 8),
        new(RecentHeaderRowIndex, 0, 5),
        new(RecentHeaderRowIndex + 1, 0, 5),
    ];

    public JsonArray Matrix()
    {
        var blocks = Math.Max(Math.Max(Solvers.Count, Status.Count), Priority.Count);
        var height = RecentHeaderRowIndex + 2 + Math.Max(Recent.Count, 1) + 1;
        var grid = new JsonArray();
        for (var row = 0; row < height; row++)
        {
            var cells = new JsonNode?[Width];
            if (row == 0)
                cells[0] = "NeonDB — IT Tickets";
            else if (row == 1)
                cells[0] = $"synced {SyncedAtUtc}";
            else if (row == 3)
            {
                cells[0] = "Open tickets";
                cells[2] = "Unsolved > 1 day";
                cells[4] = "Planned";
                cells[6] = "Complete";
            }
            else if (row == 4)
            {
                cells[0] = OpenTotal;
                cells[2] = OpenStale;
                cells[4] = Planned;
                cells[6] = Complete;
            }
            else if (row == OpenHeaderRowIndex)
                cells[0] = "Open tickets — oldest first";
            else if (row == OpenHeaderRowIndex + 1)
            {
                cells[0] = "Ticket";
                cells[1] = "Title";
                cells[2] = "Priority";
                cells[3] = "Status";
                cells[4] = "Assignee";
                cells[5] = "Created";
                cells[6] = "Age (days)";
            }
            else if (row > OpenHeaderRowIndex + 1 && row - OpenHeaderRowIndex - 2 < OpenTickets.Count)
            {
                var ticket = OpenTickets[row - OpenHeaderRowIndex - 2];
                cells[0] = ticket.TicketId;
                cells[1] = ticket.Title;
                cells[2] = ticket.Priority;
                cells[3] = ticket.Status;
                cells[4] = ticket.Assignee;
                cells[5] = ticket.CreatedSerial;
                cells[6] = ticket.AgeDays;
            }
            else if (row == SolvedHeaderRowIndex)
            {
                cells[0] = "Solved by staff";
                cells[3] = "Status";
                cells[6] = "Priority";
            }
            else if (row == SolvedHeaderRowIndex + 1)
            {
                cells[0] = "Staff";
                cells[1] = "Solved";
                cells[3] = "Status";
                cells[4] = "Count";
                cells[6] = "Priority";
                cells[7] = "Count";
            }
            else if (row >= SolvedHeaderRowIndex + 2 && row <= SolvedHeaderRowIndex + 1 + blocks)
            {
                var i = row - SolvedHeaderRowIndex - 2;
                if (i < Solvers.Count)
                {
                    cells[0] = Solvers[i].Staff;
                    cells[1] = Solvers[i].Solved;
                }
                if (i < Status.Count)
                {
                    cells[3] = Status[i].Label;
                    cells[4] = Status[i].Count;
                }
                if (i < Priority.Count)
                {
                    cells[6] = Priority[i].Label;
                    cells[7] = Priority[i].Count;
                }
            }
            else if (row == RecentHeaderRowIndex)
                cells[0] = "Recent activity";
            else if (row == RecentHeaderRowIndex + 1)
            {
                cells[0] = "Ticket";
                cells[1] = "Status";
                cells[2] = "Priority";
                cells[3] = "At (UTC)";
                cells[4] = "Title";
            }
            else if (row > RecentHeaderRowIndex + 1 && row - RecentHeaderRowIndex - 2 < Recent.Count)
            {
                var recent = Recent[row - RecentHeaderRowIndex - 2];
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
    private const int OpenListCap = 15;

    public static DashboardModel Build(IReadOnlyList<NeonTable> dump)
    {
        var tickets = dump.FirstOrDefault(table => table.Name == "ticket");
        var events = dump.FirstOrDefault(table => table.Name == "ticket_status_event");
        var priorities = dump.FirstOrDefault(table => table.Name == "priority");
        var staff = dump.FirstOrDefault(table => table.Name == "it_staff");
        var users = dump.FirstOrDefault(table => table.Name == "discord_user");

        var names = NamesByUser(staff, users);
        var latest = LatestStatusPerTicket(events);
        if (tickets is not null)
            foreach (var row in tickets.Rows)
            {
                var id = RowValue(tickets, row, "ticket_id");
                if (id is not null && !latest.ContainsKey(id))
                    latest[id] = "open";
            }

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

        var now = DateTimeOffset.UtcNow;
        var (cards, open) = OpenSection(tickets, latest, names, now);
        var solvers = Solvers(tickets, latest, names);

        var solvedHeader = 9 + Math.Max(open.Count, 1);
        var blocks = Math.Max(Math.Max(solvers.Count, statusCounts.Length), priorityCounts.Length);

        return new DashboardModel(
            now.ToString("u"),
            cards.Open,
            cards.Stale,
            cards.Planned,
            cards.Complete,
            open,
            solvers,
            statusCounts,
            priorityCounts,
            recent,
            OpenHeaderRowIndex: 6,
            SolvedHeaderRowIndex: solvedHeader,
            RecentHeaderRowIndex: solvedHeader + 3 + Math.Max(blocks, 1));
    }

    private sealed record Cards(int Open, int Stale, int Planned, int Complete);

    private static (Cards Cards, List<DashboardTicket> Open) OpenSection(
        NeonTable? tickets, Dictionary<string, string> latest,
        Dictionary<string, string> names, DateTimeOffset now)
    {
        int open = 0, stale = 0, planned = 0, complete = 0;
        var rows = new List<(string Id, string? Title, string? Priority, string Status,
            string? Assignee, DateTimeOffset Created)>();
        if (tickets is not null)
            foreach (var row in tickets.Rows)
            {
                var id = RowValue(tickets, row, "ticket_id");
                if (id is null) continue;
                var status = latest.TryGetValue(id, out var known) ? known : "open";
                var created = MomentOf(RowValue(tickets, row, "created_at_utc")) ?? now;
                var age = (now - created).TotalDays;
                if (IsComplete(status))
                {
                    complete++;
                    continue;
                }
                open++;
                if (string.Equals(status, "planned", StringComparison.OrdinalIgnoreCase)) planned++;
                if (age >= 1) stale++;
                rows.Add((id,
                    RowValue(tickets, row, "title"),
                    RowValue(tickets, row, "priority_code"),
                    status,
                    DisplayName(RowValue(tickets, row, "assignee_user_id"), names),
                    created));
            }

        var oldestFirst = rows
            .OrderByDescending(row => now - row.Created)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .Take(OpenListCap)
            .Select(row => new DashboardTicket(row.Id, row.Title, row.Priority, row.Status,
                row.Assignee, SheetPlanBuilder.Serial(row.Created),
                Math.Round((now - row.Created).TotalDays, 1)))
            .ToList();
        return (new Cards(open, stale, planned, complete), oldestFirst);
    }

    private static List<DashboardSolver> Solvers(
        NeonTable? tickets, Dictionary<string, string> latest, Dictionary<string, string> names)
    {
        if (tickets is null) return [];
        var counts = new Dictionary<string, int>();
        foreach (var row in tickets.Rows)
        {
            var id = RowValue(tickets, row, "ticket_id");
            if (id is null) continue;
            var status = latest.TryGetValue(id, out var known) ? known : "open";
            if (!IsComplete(status)) continue;
            var solver = DisplayName(RowValue(tickets, row, "assignee_user_id"), names) ?? "(unassigned)";
            counts[solver] = counts.GetValueOrDefault(solver) + 1;
        }
        return counts
            .Select(pair => new DashboardSolver(pair.Key, pair.Value))
            .OrderByDescending(solver => solver.Solved)
            .ThenBy(solver => solver.Staff, StringComparer.Ordinal)
            .ToList();
    }

    private static string? DisplayName(string? userId, Dictionary<string, string> names) =>
        !string.IsNullOrWhiteSpace(userId)
            ? names.TryGetValue(userId, out var name) ? name : userId
            : null;

    private static Dictionary<string, string> NamesByUser(NeonTable? staff, NeonTable? users)
    {
        var names = new Dictionary<string, string>();
        if (users is not null)
            foreach (var row in users.Rows)
            {
                var id = RowValue(users, row, "user_id");
                var name = RowValue(users, row, "username");
                if (id is not null && !string.IsNullOrWhiteSpace(name)) names[id] = name!;
            }
        if (staff is not null)
            foreach (var row in staff.Rows)
            {
                var id = RowValue(staff, row, "user_id");
                var name = RowValue(staff, row, "display_name");
                if (id is not null && !string.IsNullOrWhiteSpace(name)) names[id] = name!;
            }
        return names;
    }

    private static bool IsComplete(string status) =>
        string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);

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

    private static double SerialOf(string? text) =>
        MomentOf(text) is { } moment ? SheetPlanBuilder.Serial(moment) : 0;

    private static DateTimeOffset? MomentOf(string? text) =>
        text is not null && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
            ? moment
            : null;
}
