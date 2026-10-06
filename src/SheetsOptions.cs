namespace Ticket.Adapter.Sheets;

public sealed record SheetsOptions(
    string ConnectionString,
    string ClientId,
    string ClientSecret,
    string RefreshToken,
    string SpreadsheetId,
    TimeSpan Interval)
{
    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromMinutes(10);

    public static SheetsOptions? FromEnvironment()
    {
        var connectionString = Environment.GetEnvironmentVariable("Postgres__ConnectionString");
        var clientId = Environment.GetEnvironmentVariable("Google__ClientId");
        var clientSecret = Environment.GetEnvironmentVariable("Google__ClientSecret");
        var refreshToken = Environment.GetEnvironmentVariable("Google__RefreshToken");
        if (string.IsNullOrWhiteSpace(connectionString) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(refreshToken))
            return null;

        var spreadsheetId = Environment.GetEnvironmentVariable("Google__SpreadsheetId") ?? "";
        var minutes = Environment.GetEnvironmentVariable("Google__SyncIntervalMinutes");
        var interval = double.TryParse(minutes, out var parsed) && parsed > 0
            ? TimeSpan.FromMinutes(parsed)
            : DefaultInterval;
        return new SheetsOptions(connectionString, clientId!, clientSecret!, refreshToken!, spreadsheetId, interval);
    }
}
