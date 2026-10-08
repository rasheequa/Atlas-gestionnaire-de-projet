using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Connexion à Google Drive, Dropbox et OneDrive (OAuth 2.0 + PKCE, redirection locale).
/// Les jetons sont chiffrés avec DPAPI (compte Windows courant) et ne quittent jamais le PC.
/// Seul le fichier atlas-sync.json est lu/écrit, dans le dossier d'application du fournisseur.
/// </summary>
sealed class CloudService
{
    private const string FileName = "atlas-sync.json";

    private sealed record Provider(
        string Id, string Name, string AuthUrl, string TokenUrl, string Scope,
        string RedirectHost, string ClientId, string? ClientSecret);

    private sealed class Tokens
    {
        public string Access { get; set; } = "";
        public string Refresh { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
    }

    private sealed record Pending(string Provider, string Verifier, DateTime Created);

    private sealed class CloudException(string message, int status = 400) : Exception(message)
    {
        public int Status { get; } = status;
    }

    private readonly Dictionary<string, Provider> providers = new();
    private readonly Dictionary<string, Pending> pending = new();
    private readonly string tokenPath;
    private readonly string syncPath;
    private readonly int port;
    private readonly HttpClient http;
    private readonly object gate = new();

    public CloudService(string dataDir, int port)
    {
        this.port = port;
        syncPath = Path.Combine(dataDir, FileName);
        tokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas", "cloud-tokens.dat");
        var handler = new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas");
        LoadConfig(Path.Combine(dataDir, "cloud-config.json"));
    }

    // Identifiants d'application (publics) : valeurs intégrées ci-dessous, surchargeables dans Documents\Atlas\cloud-config.json
    private const string DefaultGoogleClientId = "";
    private const string DefaultGoogleClientSecret = "";
    private const string DefaultDropboxAppKey = "";
    private const string DefaultMicrosoftClientId = "";

    private void LoadConfig(string path)
    {
        string google = DefaultGoogleClientId, googleSecret = DefaultGoogleClientSecret,
               dropbox = DefaultDropboxAppKey, microsoft = DefaultMicrosoftClientId;
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                string Read(string provider, string key, string fallback) =>
                    root.TryGetProperty(provider, out var p) && p.TryGetProperty(key, out var v) &&
                    v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                        ? v.GetString()! : fallback;
                google = Read("google", "clientId", google);
                googleSecret = Read("google", "clientSecret", googleSecret);
                dropbox = Read("dropbox", "clientId", dropbox);
                microsoft = Read("microsoft", "clientId", microsoft);
            }
            catch (JsonException) { }
        }

        providers["google"] = new Provider("google", "Google Drive",
            "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
            "https://www.googleapis.com/auth/drive.appdata", "127.0.0.1", google,
            string.IsNullOrEmpty(googleSecret) ? null : googleSecret);
        providers["dropbox"] = new Provider("dropbox", "Dropbox",
            "https://www.dropbox.com/oauth2/authorize", "https://api.dropboxapi.com/oauth2/token",
            "files.content.read files.content.write", "127.0.0.1", dropbox, null);
        providers["microsoft"] = new Provider("microsoft", "OneDrive",
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            "Files.ReadWrite.AppFolder offline_access", "localhost", microsoft, null);
    }

    public async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        try
        {
            if (path == "/__cloud/callback")
            {
                await HandleCallbackAsync(context);
                return;
            }

            if (context.Request.HttpMethod == "POST" && !IsSameOrigin(context))
                throw new CloudException("Requête refusée.", 403);

            switch (path)
            {
                case "/__cloud/status" when context.Request.HttpMethod == "GET":
                    await WriteJsonAsync(context, 200, StatusJson());
                    break;
                case "/__cloud/connect" when context.Request.HttpMethod == "POST":
                    await WriteJsonAsync(context, 200, JsonSerializer.Serialize(new { ok = true, url = StartAuth(await ReadProviderAsync(context)) }));
                    break;
                case "/__cloud/disconnect" when context.Request.HttpMethod == "POST":
                    Disconnect(await ReadProviderAsync(context));
                    await WriteJsonAsync(context, 200, "{\"ok\":true}");
                    break;
                case "/__cloud/upload" when context.Request.HttpMethod == "POST":
                    await UploadAsync(await ReadProviderAsync(context));
                    await WriteJsonAsync(context, 200, "{\"ok\":true}");
                    break;
                case "/__cloud/download" when context.Request.HttpMethod == "POST":
                    await WriteJsonAsync(context, 200, await DownloadAsync(await ReadProviderAsync(context)));
                    break;
                default:
                    context.Response.StatusCode = 404;
                    break;
            }
        }
        catch (CloudException error)
        {
            await WriteJsonAsync(context, error.Status, JsonSerializer.Serialize(new { error = error.Message }));
        }
        catch (HttpRequestException)
        {
            await WriteJsonAsync(context, 502, JsonSerializer.Serialize(new { error = "Connexion au service cloud impossible (réseau ou proxy)." }));
        }
        catch (TaskCanceledException)
        {
            await WriteJsonAsync(context, 504, JsonSerializer.Serialize(new { error = "Le service cloud ne répond pas." }));
        }
    }

    private bool IsSameOrigin(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];
        return !string.IsNullOrEmpty(origin) &&
               (origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
                origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Provider> ReadProviderAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        var id = doc.RootElement.TryGetProperty("provider", out var p) ? p.GetString() : null;
        if (id is null || !providers.TryGetValue(id, out var provider))
            throw new CloudException("Fournisseur inconnu.");
        return provider;
    }

    private string StatusJson()
    {
        var tokens = LoadTokens();
        var result = new Dictionary<string, object>();
        foreach (var provider in providers.Values)
        {
            result[provider.Id] = new
            {
                configured = !string.IsNullOrEmpty(provider.ClientId),
                connected = tokens.ContainsKey(provider.Id)
            };
        }
        return JsonSerializer.Serialize(result);
    }

    // ---------- OAuth ----------

    private string RedirectUri(Provider provider) => $"http://{provider.RedirectHost}:{port}/__cloud/callback";

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private string StartAuth(Provider provider)
    {
        if (string.IsNullOrEmpty(provider.ClientId))
            throw new CloudException($"{provider.Name} n'est pas configuré : identifiant d'application manquant (voir le README).");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        lock (gate)
        {
            foreach (var old in pending.Where(entry => DateTime.UtcNow - entry.Value.Created > TimeSpan.FromMinutes(10)).Select(entry => entry.Key).ToList())
                pending.Remove(old);
            pending[state] = new Pending(provider.Id, verifier, DateTime.UtcNow);
        }

        var query = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri(provider),
            ["scope"] = provider.Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        if (provider.Id == "google") { query["access_type"] = "offline"; query["prompt"] = "consent"; }
        if (provider.Id == "dropbox") query["token_access_type"] = "offline";

        var url = provider.AuthUrl + "?" + string.Join("&", query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return url;
    }

    private async Task HandleCallbackAsync(HttpListenerContext context)
    {
        var query = context.Request.QueryString;
        var state = query["state"];
        var code = query["code"];
        Pending? flow;
        lock (gate)
        {
            flow = state is not null && pending.Remove(state, out var found) ? found : null;
        }

        string message;
        if (flow is null || string.IsNullOrEmpty(code))
        {
            message = query["error"] is { Length: > 0 } error
                ? $"Connexion refusée ({WebUtility.HtmlEncode(error)})."
                : "Demande de connexion invalide ou expirée.";
        }
        else
        {
            try
            {
                var provider = providers[flow.Provider];
                var tokens = await ExchangeAsync(provider, new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri(provider),
                    ["code_verifier"] = flow.Verifier
                });
                SaveToken(provider.Id, tokens);
                message = $"{provider.Name} est connecté. Vous pouvez fermer cet onglet et revenir à Atlas.";
            }
            catch (Exception error) when (error is CloudException or HttpRequestException)
            {
                message = "La connexion a échoué : " + WebUtility.HtmlEncode(error.Message);
            }
        }

        var html = $"<!doctype html><meta charset=\"utf-8\"><title>Atlas</title><body style=\"font-family:Segoe UI,sans-serif;background:#0D0F11;color:#F1ECDF;display:grid;place-items:center;height:100vh;margin:0\"><p>{message}</p></body>";
        context.Response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private async Task<Tokens> ExchangeAsync(Provider provider, Dictionary<string, string> form, string? existingRefresh = null)
    {
        form["client_id"] = provider.ClientId;
        if (provider.ClientSecret is not null) form["client_secret"] = provider.ClientSecret;
        using var response = await http.PostAsync(provider.TokenUrl, new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new CloudException(ExtractError(body) ?? $"Échec de l'authentification (HTTP {(int)response.StatusCode}).", 401);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? "";
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        return new Tokens
        {
            Access = access,
            Refresh = refresh ?? existingRefresh ?? "",
            ExpiresAt = DateTime.UtcNow.AddSeconds(seconds - 60)
        };
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_description", out var description)) return description.GetString();
            if (root.TryGetProperty("error", out var error))
                return error.ValueKind == JsonValueKind.String ? error.GetString() : error.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException) { }
        return null;
    }

    // ---------- Jetons (DPAPI) ----------

    private Dictionary<string, Tokens> LoadTokens()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(tokenPath)) return new();
                var clear = ProtectedData.Unprotect(File.ReadAllBytes(tokenPath), null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Dictionary<string, Tokens>>(clear) ?? new();
            }
            catch (Exception error) when (error is CryptographicException or JsonException or IOException)
            {
                return new();
            }
        }
    }

    private void SaveTokens(Dictionary<string, Tokens> tokens)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
            var protectedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(tokenPath, protectedBytes);
        }
    }

    private void SaveToken(string provider, Tokens tokens)
    {
        var all = LoadTokens();
        all[provider] = tokens;
        SaveTokens(all);
    }

    private void Disconnect(Provider provider)
    {
        var all = LoadTokens();
        if (all.Remove(provider.Id)) SaveTokens(all);
    }

    private async Task<string> AccessTokenAsync(Provider provider)
    {
        var all = LoadTokens();
        if (!all.TryGetValue(provider.Id, out var tokens))
            throw new CloudException($"Connectez d'abord {provider.Name}.", 401);
        if (tokens.ExpiresAt > DateTime.UtcNow) return tokens.Access;
        if (string.IsNullOrEmpty(tokens.Refresh))
        {
            Disconnect(provider);
            throw new CloudException($"La session {provider.Name} a expiré : reconnectez-vous.", 401);
        }

        try
        {
            var refreshed = await ExchangeAsync(provider, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.Refresh
            }, tokens.Refresh);
            SaveToken(provider.Id, refreshed);
            return refreshed.Access;
        }
        catch (CloudException)
        {
            Disconnect(provider);
            throw new CloudException($"La session {provider.Name} a expiré : reconnectez-vous.", 401);
        }
    }

    // ---------- Fichier atlas-sync.json ----------

    private async Task UploadAsync(Provider provider)
    {
        if (!File.Exists(syncPath)) throw new CloudException("Aucune sauvegarde locale à envoyer. Utilisez d'abord « Forcer la sauvegarde ».");
        var content = await File.ReadAllBytesAsync(syncPath);
        var token = await AccessTokenAsync(provider);

        switch (provider.Id)
        {
            case "dropbox": await UploadDropboxAsync(token, content); break;
            case "google": await UploadGoogleAsync(token, content); break;
            default: await UploadMicrosoftAsync(token, content); break;
        }
    }

    private async Task<string> DownloadAsync(Provider provider)
    {
        var token = await AccessTokenAsync(provider);
        var content = provider.Id switch
        {
            "dropbox" => await DownloadDropboxAsync(token),
            "google" => await DownloadGoogleAsync(token),
            _ => await DownloadMicrosoftAsync(token)
        };

        try { using var _ = JsonDocument.Parse(content); }
        catch (JsonException) { throw new CloudException("Le fichier trouvé dans le cloud n'est pas une sauvegarde Atlas valide."); }
        return content;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task EnsureOkAsync(HttpResponseMessage response, string notFoundMessage)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.NotFound || (int)response.StatusCode == 409 && body.Contains("not_found"))
            throw new CloudException(notFoundMessage, 404);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new CloudException("Autorisation refusée : reconnectez-vous.", 401);
        throw new CloudException(ExtractError(body) ?? $"Erreur du service cloud (HTTP {(int)response.StatusCode}).", 502);
    }

    private const string NotFoundMessage = "Aucune sauvegarde Atlas trouvée dans ce cloud. Enregistrez-en une d'abord.";

    // Dropbox : dossier d'application, chemin /atlas-sync.json
    private async Task UploadDropboxAsync(string token, byte[] content)
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = Request(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload", token, body);
        request.Headers.Add("Dropbox-API-Arg", "{\"path\":\"/" + FileName + "\",\"mode\":\"overwrite\",\"mute\":true}");
        using var response = await http.SendAsync(request);
        await EnsureOkAsync(response, NotFoundMessage);
    }

    private async Task<string> DownloadDropboxAsync(string token)
    {
        using var request = Request(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download", token);
        request.Headers.Add("Dropbox-API-Arg", "{\"path\":\"/" + FileName + "\"}");
        using var response = await http.SendAsync(request);
        await EnsureOkAsync(response, NotFoundMessage);
        return await response.Content.ReadAsStringAsync();
    }

    // Google Drive : dossier caché de l'application (appDataFolder)
    private async Task<string?> FindGoogleFileAsync(string token)
    {
        var url = "https://www.googleapis.com/drive/v3/files?spaces=appDataFolder&fields=files(id)&q=" +
                  Uri.EscapeDataString($"name='{FileName}' and trashed=false");
        using var response = await http.SendAsync(Request(HttpMethod.Get, url, token));
        await EnsureOkAsync(response, NotFoundMessage);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var files = doc.RootElement.GetProperty("files");
        return files.GetArrayLength() > 0 ? files[0].GetProperty("id").GetString() : null;
    }

    private async Task UploadGoogleAsync(string token, byte[] content)
    {
        var id = await FindGoogleFileAsync(token);
        if (id is null)
        {
            var metadata = JsonSerializer.Serialize(new { name = FileName, parents = new[] { "appDataFolder" } });
            using var create = Request(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id", token,
                new StringContent(metadata, Encoding.UTF8, "application/json"));
            using var created = await http.SendAsync(create);
            await EnsureOkAsync(created, NotFoundMessage);
            using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            id = doc.RootElement.GetProperty("id").GetString();
        }

        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var update = Request(HttpMethod.Patch, $"https://www.googleapis.com/upload/drive/v3/files/{id}?uploadType=media", token, body);
        using var response = await http.SendAsync(update);
        await EnsureOkAsync(response, NotFoundMessage);
    }

    private async Task<string> DownloadGoogleAsync(string token)
    {
        var id = await FindGoogleFileAsync(token) ?? throw new CloudException(NotFoundMessage, 404);
        using var response = await http.SendAsync(Request(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{id}?alt=media", token));
        await EnsureOkAsync(response, NotFoundMessage);
        return await response.Content.ReadAsStringAsync();
    }

    // OneDrive : dossier d'application (Applications\Atlas)
    private const string GraphFile = "https://graph.microsoft.com/v1.0/me/drive/special/approot:/" + FileName + ":";

    private async Task UploadMicrosoftAsync(string token, byte[] content)
    {
        if (content.Length <= 4 * 1024 * 1024)
        {
            using var body = new ByteArrayContent(content);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.SendAsync(Request(HttpMethod.Put, GraphFile + "/content", token, body));
            await EnsureOkAsync(response, NotFoundMessage);
            return;
        }

        using var session = Request(HttpMethod.Post, GraphFile + "/createUploadSession", token,
            new StringContent("{\"item\":{\"@microsoft.graph.conflictBehavior\":\"replace\"}}", Encoding.UTF8, "application/json"));
        using var sessionResponse = await http.SendAsync(session);
        await EnsureOkAsync(sessionResponse, NotFoundMessage);
        using var doc = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        var uploadUrl = doc.RootElement.GetProperty("uploadUrl").GetString()!;

        const int chunk = 320 * 1024 * 12;
        for (var offset = 0; offset < content.Length; offset += chunk)
        {
            var length = Math.Min(chunk, content.Length - offset);
            using var part = new ByteArrayContent(content, offset, length);
            part.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, content.Length);
            part.Headers.ContentLength = length;
            using var response = await http.PutAsync(uploadUrl, part);
            await EnsureOkAsync(response, NotFoundMessage);
        }
    }

    private async Task<string> DownloadMicrosoftAsync(string token)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, GraphFile + "/content", token));
        await EnsureOkAsync(response, NotFoundMessage);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        var bytes = Encoding.UTF8.GetBytes(json);
        await context.Response.OutputStream.WriteAsync(bytes);
    }
}
