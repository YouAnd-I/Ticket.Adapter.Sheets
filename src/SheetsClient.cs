using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ticket.Adapter.Sheets;

public sealed class SheetsClient(HttpClient http, SheetsOptions options)
{
    private const int ChunkRows = 5000;

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;
    private string? _spreadsheetId;

    public async Task<string> ResolveSpreadsheetIdAsync(CancellationToken ct = default)
    {
        if (_spreadsheetId is not null) return _spreadsheetId;
        if (!string.IsNullOrWhiteSpace(options.SpreadsheetId)) return options.SpreadsheetId;
        using var json = await SendAsync(HttpMethod.Post, "https://sheets.googleapis.com/v4/spreadsheets",
            """{"properties":{"title":"NeonDB sync"}}""", ct).ConfigureAwait(false);
        var id = json.RootElement.GetProperty("spreadsheetId").GetString()!;
        Console.WriteLine($"[sheets] created spreadsheet https://docs.google.com/spreadsheets/d/{id} — set Google__SpreadsheetId to keep it");
        _spreadsheetId = id;
        return id;
    }

    public async Task<List<string>> TabTitlesAsync(string id, CancellationToken ct = default)
    {
        using var json = await SendAsync(HttpMethod.Get,
            $"https://sheets.googleapis.com/v4/spreadsheets/{id}?fields=sheets%2Fproperties%2Ftitle",
            null, ct).ConfigureAwait(false);
        return json.RootElement.GetProperty("sheets").EnumerateArray()
            .Select(sheet => sheet.GetProperty("properties").GetProperty("title").GetString()!)
            .ToList();
    }

    public async Task AddTabsAsync(string id, IEnumerable<string> titles, CancellationToken ct = default)
    {
        var requests = titles.Select(title => (object)new { addSheet = new { properties = new { title } } }).ToArray();
        using var json = await SendAsync(HttpMethod.Post,
            $"https://sheets.googleapis.com/v4/spreadsheets/{id}:batchUpdate",
            JsonSerializer.Serialize(new { requests }), ct).ConfigureAwait(false);
    }

    public async Task ReplaceTabAsync(string id, NeonTable table, CancellationToken ct = default)
    {
        using (await SendAsync(HttpMethod.Post,
            $"https://sheets.googleapis.com/v4/spreadsheets/{id}/values/{Range($"{table.Name}!A:ZZ")}:clear",
            "{}", ct).ConfigureAwait(false)) { }

        var offset = 0;
        foreach (var chunk in table.Rows.Chunk(ChunkRows))
        {
            var values = new JsonArray();
            if (offset == 0)
                values.Add(new JsonArray(table.Columns.Select(column => (JsonNode?)column).ToArray()));
            foreach (var row in chunk)
                values.Add(row.DeepClone());

            using var json = await SendAsync(HttpMethod.Put,
                $"https://sheets.googleapis.com/v4/spreadsheets/{id}/values/{Range($"{table.Name}!A{offset + 1}")}?valueInputOption=RAW",
                JsonSerializer.Serialize(new { values }), ct).ConfigureAwait(false);
            offset += chunk.Length;
        }
    }

    private static string Range(string range) => Uri.EscapeDataString(range);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct).ConfigureAwait(false));
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Google Sheets HTTP {(int)response.StatusCode}: {raw}");
        return JsonDocument.Parse(raw);
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpires)
            return _accessToken;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["refresh_token"] = options.RefreshToken,
            ["grant_type"] = "refresh_token",
        };
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Google token HTTP {(int)response.StatusCode}: {raw}");
        using var json = JsonDocument.Parse(raw);
        _accessToken = json.RootElement.GetProperty("access_token").GetString()!;
        _accessTokenExpires = DateTimeOffset.UtcNow
            .AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32() - 60);
        return _accessToken;
    }
}
