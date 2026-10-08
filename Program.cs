using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

const int Port = 51735;
const string UpdateUrl = "https://raw.githubusercontent.com/rasheequa/Atlas-gestionnaire-de-projet/main/Atlas.html";
var installDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
var webDir = Path.Combine(installDir, "web");
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    "Atlas");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "exported"));
Directory.CreateDirectory(webDir);

var bundledSync = Path.Combine(installDir, "atlas-sync.json");
var dataSync = Path.Combine(dataDir, "atlas-sync.json");
if (!File.Exists(dataSync) && File.Exists(bundledSync))
    File.Copy(bundledSync, dataSync);

await TryUpdateAsync(webDir, UpdateUrl);

var port = int.TryParse(Environment.GetEnvironmentVariable("ATLAS_PORT"), out var customPort) ? customPort : Port;
using var server = new AtlasServer(installDir, webDir, dataDir, port);
server.Start();

var url = $"http://127.0.0.1:{port}/Atlas.html";
var edge = Environment.GetEnvironmentVariable("ATLAS_NO_BROWSER") is null ? FindEdge() : null;
Process? browser = null;
try
{
    if (Environment.GetEnvironmentVariable("ATLAS_NO_BROWSER") is not null)
    {
        await Task.Delay(Timeout.Infinite);
    }
    else if (edge is not null)
    {
        // Profil dédié : sans lui, Edge déjà ouvert reprend la fenêtre et le serveur s'arrêterait aussitôt
        var profile = Path.Combine(installDir, "edge-profile");
        browser = Process.Start(new ProcessStartInfo
        {
            FileName = edge,
            Arguments = $"--app=\"{url}\" --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check",
            UseShellExecute = false
        });
    }
    else
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    if (browser is not null)
        await browser.WaitForExitAsync();
    else if (Environment.GetEnvironmentVariable("ATLAS_NO_BROWSER") is null)
        Console.ReadLine();
}
finally
{
    server.Stop();
    browser?.Dispose();
}

static async Task TryUpdateAsync(string webDir, string url)
{
    // Mise à jour silencieuse : en cas d'échec (hors ligne, dépôt privé), la version locale reste utilisée.
    try
    {
        using var handler = new HttpClientHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas-Updater");
        var html = await client.GetStringAsync(url);
        if (html.Length < 50_000 || !html.Contains("<title>Atlas", StringComparison.Ordinal))
            return;

        var target = Path.Combine(webDir, "Atlas.html");
        if (File.Exists(target) && File.ReadAllText(target, Encoding.UTF8) == html)
            return;

        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, html, new UTF8Encoding(false));
        File.Move(temporary, target, true);
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
    {
    }
}

static string? FindEdge()
{
    var candidates = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
    };
    return candidates.FirstOrDefault(File.Exists);
}

sealed class AtlasServer : IDisposable
{
    private readonly string installDir;
    private readonly string webDir;
    private readonly string dataDir;
    private readonly HttpListener listener = new();
    private CancellationTokenSource? cancellation;

    public AtlasServer(string installDir, string webDir, string dataDir, int port)
    {
        this.installDir = installDir;
        this.webDir = webDir;
        this.dataDir = dataDir;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        listener.Start();
        cancellation = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(cancellation.Token));
    }

    public void Stop()
    {
        cancellation?.Cancel();
        if (listener.IsListening)
            listener.Stop();
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch when (token.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context), token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod == "POST" &&
                context.Request.Url?.AbsolutePath is "/__atlas_sync" or "/__atlas_export")
            {
                var origin = context.Request.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) &&
                    !origin.Equals($"http://127.0.0.1:{context.Request.Url!.Port}", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                await HandlePostAsync(context);
                return;
            }

            if (context.Request.HttpMethod == "GET")
            {
                await HandleGetAsync(context);
                return;
            }

            context.Response.StatusCode = 405;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            context.Response.StatusCode = 400;
            await WriteTextAsync(context.Response, error.Message);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleGetAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (path == "/atlas-sync.json")
        {
            await WriteFileAsync(context.Response, Path.Combine(dataDir, "atlas-sync.json"), "application/json");
            return;
        }

        var relative = Uri.UnescapeDataString(path.TrimStart('/'));
        if (string.IsNullOrWhiteSpace(relative))
            relative = "Atlas.html";

        // La version téléchargée a priorité sur celle fournie par l'installateur
        foreach (var root in new[] { webDir, installDir })
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                continue;
            await WriteFileAsync(context.Response, fullPath, GetContentType(fullPath));
            return;
        }

        context.Response.StatusCode = 404;
    }

    private async Task HandlePostAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid backup payload.");

        var target = context.Request.Url!.AbsolutePath == "/__atlas_export"
            ? GetExportPath(document.RootElement)
            : Path.Combine(dataDir, "atlas-sync.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(
            document.RootElement, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temporary, target, true);
        context.Response.StatusCode = 204;
    }

    private string GetExportPath(JsonElement root)
    {
        if (!root.TryGetProperty("filename", out var filenameElement) ||
            filenameElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid export filename.");
        var filename = filenameElement.GetString()!;
        if (!filename.StartsWith("atlas-sauvegarde-", StringComparison.Ordinal) ||
            !filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid export filename.");
        return Path.Combine(dataDir, "exported", filename);
    }

    private static async Task WriteFileAsync(HttpListenerResponse response, string path, string contentType)
    {
        if (!File.Exists(path))
        {
            response.StatusCode = 404;
            return;
        }
        response.ContentType = contentType;
        await using var stream = File.OpenRead(path);
        await stream.CopyToAsync(response.OutputStream);
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, string text)
    {
        response.ContentType = "text/plain; charset=utf-8";
        await using var writer = new StreamWriter(response.OutputStream, Encoding.UTF8, leaveOpen: true);
        await writer.WriteAsync(text);
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream"
    };

    public void Dispose()
    {
        Stop();
        listener.Close();
        cancellation?.Dispose();
    }
}
