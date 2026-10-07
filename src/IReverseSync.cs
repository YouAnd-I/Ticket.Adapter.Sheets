namespace Ticket.Adapter.Sheets;

public interface IReverseSync
{
    IReadOnlyCollection<string> Tabs { get; }

    Task ApplyAsync(string tab, IReadOnlyList<IReadOnlyList<string?>> rows, CancellationToken ct = default);
}
