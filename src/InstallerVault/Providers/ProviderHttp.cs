using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InstallerVault.Providers;

public sealed class ProviderException(string message) : Exception(message);
public sealed record MetadataResponse(string Body, string? ETag, DateTimeOffset? LastModified, string? Link);

/// <summary>Bounded metadata cache. A failed refresh never pretends cached metadata is current.</summary>
public sealed class ProviderHttp(ISafeHttp safeHttp, VaultOptions options, TimeSpan? cacheLifetime = null)
{
    private sealed record Entry(MetadataResponse Value, DateTimeOffset At);
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly TimeSpan lifetime = cacheLifetime ?? TimeSpan.FromMinutes(5);
    private DateTimeOffset githubBlockedUntil;
    public bool HasGitHubToken => !string.IsNullOrWhiteSpace(options.GitHubToken);

    public async Task<MetadataResponse> GetAsync(string url, CancellationToken ct, int maxBytes = 8 * 1024 * 1024)
    {
        await gate.WaitAsync(ct);
        try
        {
            cache.TryGetValue(url, out var previous);
            if (previous is not null && DateTimeOffset.UtcNow - previous.At < lifetime) return previous.Value;
            using var request = Request(HttpMethod.Get,url);
            if (previous?.Value.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match",etag);
            if (previous?.Value.LastModified is { } modified) request.Headers.IfModifiedSince = modified;
            using var response = await safeHttp.SendAsync(request,ct);
            CheckStatus(response, request.RequestUri!);
            if (response.StatusCode == HttpStatusCode.NotModified && previous is not null)
            {
                cache[url] = previous with { At=DateTimeOffset.UtcNow };
                return previous.Value;
            }
            if (response.Content.Headers.ContentLength > maxBytes) throw new ProviderException("Provider metadata exceeds the supported size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[32768]; int read;
            while ((read = await stream.ReadAsync(buffer,ct)) != 0)
            {
                if (memory.Length + read > maxBytes) throw new ProviderException("Provider metadata exceeds the supported size limit.");
                await memory.WriteAsync(buffer.AsMemory(0,read),ct);
            }
            var value = new MetadataResponse(Encoding.UTF8.GetString(memory.ToArray()),response.Headers.ETag?.ToString(),response.Content.Headers.LastModified,Header(response,"Link"));
            // Keep at most 128 entries / approximately 96 MiB of UTF-16 metadata.
            while (cache.Count >= 128 || cache.Values.Sum(x => (long)x.Value.Body.Length * 2) + value.Body.Length * 2 > 96L * 1024 * 1024)
            {
                if (cache.Count == 0) break;
                cache.Remove(cache.MinBy(x=>x.Value.At).Key);
            }
            cache[url] = new(value,DateTimeOffset.UtcNow);
            return value;
        }
        finally { gate.Release(); }
    }

    public async Task<string> ProbeIdentityAsync(string url,CancellationToken ct)
    {
        using var request = Request(HttpMethod.Head,url);
        using var response = await safeHttp.SendAsync(request,ct);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented) return "unknown";
        CheckStatus(response,request.RequestUri!);
        var validators = response.Headers.ETag?.ToString() ?? response.Content.Headers.LastModified?.ToString("O");
        return validators is null ? "unknown" : "validators:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(validators))).ToLowerInvariant();
    }

    private HttpRequestMessage Request(HttpMethod method,string url)
    {
        if (!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new ProviderException("Sources must be HTTPS URLs without embedded credentials.");
        var request = new HttpRequestMessage(method,uri);
        request.Headers.UserAgent.ParseAdd("InstallerVault/1.0");
        if (uri.Host.Equals("api.github.com",StringComparison.OrdinalIgnoreCase))
        {
            if (DateTimeOffset.UtcNow < githubBlockedUntil) throw new ProviderException($"GitHub rate limit reached. Retry after {githubBlockedUntil:O}.");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version","2026-03-10");
            if (HasGitHubToken) request.Headers.Authorization = new("Bearer",options.GitHubToken);
        }
        return request;
    }

    private void CheckStatus(HttpResponseMessage response,Uri uri)
    {
        if (response.StatusCode == HttpStatusCode.NotModified || response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden && (Header(response,"X-RateLimit-Remaining")=="0" || response.Headers.RetryAfter is not null))
        {
            var until = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            if (long.TryParse(Header(response,"X-RateLimit-Reset"),out var unix) && unix > 0 && unix < 253402300799) until = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (uri.Host == "api.github.com") githubBlockedUntil = until;
            throw new ProviderException($"Provider rate limit reached. Retry after {until:O}; configure a GitHub token to increase GitHub limits.");
        }
        throw new ProviderException($"Provider {uri.Host} returned HTTP {(int)response.StatusCode}. Check the source identifier and provider access.");
    }
    private static string? Header(HttpResponseMessage response,string name) => response.Headers.TryGetValues(name,out var values) ? values.FirstOrDefault() : null;
    public async Task<JsonElement> JsonAsync(string url,CancellationToken ct,int maxBytes=8*1024*1024) => JsonDocument.Parse((await GetAsync(url,ct,maxBytes)).Body).RootElement.Clone();
}
