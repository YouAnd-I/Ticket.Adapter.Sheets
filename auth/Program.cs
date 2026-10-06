using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

if (args.Length is 1 && File.Exists(args[0]))
{
    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
    var installed = doc.RootElement.GetProperty("installed");
    await RunAsync(installed.GetProperty("client_id").GetString()!,
        installed.GetProperty("client_secret").GetString()!);
}
else if (args.Length is 2)
{
    await RunAsync(args[0], args[1]);
}
else
{
    Console.WriteLine("usage: dotnet run -- <path-to-client_secret.json>  |  <client-id> <client-secret>");
    Console.WriteLine("grants https://www.googleapis.com/auth/spreadsheets and prints a refresh token");
}

static async Task RunAsync(string clientId, string clientSecret)
{
    var port = FreePort();
    var redirect = $"http://localhost:{port}";
    var consent = "https://accounts.google.com/o/oauth2/v2/auth" +
                  $"?client_id={Uri.EscapeDataString(clientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                  "&response_type=code" +
                  "&scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fspreadsheets" +
                  "&access_type=offline&prompt=consent";

    var listener = new HttpListener();
    listener.Prefixes.Add($"{redirect}/");
    listener.Start();
    Console.WriteLine($"open in a browser and approve:{Environment.NewLine}{consent}");
    TryOpenBrowser(consent);

    var context = await listener.GetContextAsync();
    var code = context.Request.QueryString["code"];
    var error = context.Request.QueryString["error"];
    await WriteDonePageAsync(context);
    listener.Stop();

    if (code is null)
    {
        Console.WriteLine($"consent failed: {error}");
        return;
    }

    using var http = new HttpClient();
    using var response = await http.PostAsync("https://oauth2.googleapis.com/token",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
        }), default);
    var raw = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"token exchange failed HTTP {(int)response.StatusCode}: {raw}");
        return;
    }
    using var json = JsonDocument.Parse(raw);
    Console.WriteLine($"refresh token: {json.RootElement.GetProperty("refresh_token").GetString()}");
}

static async Task WriteDonePageAsync(HttpListenerContext context)
{
    var body = Encoding.UTF8.GetBytes(
        "<html><body style=\"font-family:sans-serif;padding:2rem\">Token received — this helper printed it in the terminal. Close this tab.</body></html>");
    context.Response.ContentType = "text/html";
    context.Response.ContentLength64 = body.Length;
    await context.Response.OutputStream.WriteAsync(body);
    context.Response.Close();
}

static int FreePort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

static void TryOpenBrowser(string url)
{
    try
    {
        using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch
    {
    }
}
