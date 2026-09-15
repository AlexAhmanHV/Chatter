using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Chatter.Shared.Models;

namespace Chatter.Server.Services;

// Fetches title/description/image metadata for a URL someone pasted into a message, so the
// client can render a rich preview card instead of a bare link (see ChatHub.GetLinkPreview).
// Registered as a typed HttpClient (Program.cs: AddHttpClient<LinkPreviewFetcher>()) so tests can
// just construct one with a plain HttpClient instead of going through IHttpClientFactory.
public class LinkPreviewFetcher
{
    private readonly HttpClient _http;

    // Process-lifetime cache, not persisted - a stale preview after a page changes is a cosmetic
    // problem, not a correctness one, and this app has no background job infrastructure to
    // refresh it proactively anyway.
    private static readonly ConcurrentDictionary<string, (LinkPreviewDto? Preview, DateTime FetchedAtUtc)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    // How much of the response body we'll actually read into memory - title/description/og:image
    // are always in <head>, so a page's full body (which could be megabytes) is never needed.
    private const int MaxHtmlChars = 200_000;

    public LinkPreviewFetcher(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ChatterLinkPreviewBot/1.0 (+https://github.com/AlexAhmanHV/Chatter)");
    }

    public async Task<LinkPreviewDto?> GetOrFetchAsync(string url)
    {
        if (_cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.FetchedAtUtc < CacheTtl)
            return cached.Preview;

        var preview = await FetchAsync(url);
        _cache[url] = (preview, DateTime.UtcNow);
        return preview;
    }

    private async Task<LinkPreviewDto?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.IsDefaultPort) return null; // narrows the attack surface - see IsPublicAddress below

        // Best-effort SSRF guard: refuse to fetch a URL whose host resolves to a
        // loopback/private/link-local address (which would include cloud metadata endpoints like
        // 169.254.169.254) - without this, a user could make the server fetch its own internal
        // network on their behalf. Not exhaustive (e.g. it can't stop a DNS answer changing
        // between this check and the actual connection, a.k.a. DNS rebinding), but it blocks the
        // straightforward case.
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.Host); }
        catch { return null; }
        if (addresses.Length == 0 || !addresses.All(IsPublicAddress)) return null;

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        }
        catch
        {
            return null;
        }

        if (!resp.IsSuccessStatusCode) return null;
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        if (contentType is null || !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return null;

        string html;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            var buffer = new char[MaxHtmlChars];
            var read = await reader.ReadBlockAsync(buffer, 0, MaxHtmlChars);
            html = new string(buffer, 0, read);
        }
        catch
        {
            return null;
        }

        var title = ExtractMetaContent(html, "og:title") ?? ExtractTitleTag(html);
        var description = ExtractMetaContent(html, "og:description") ?? ExtractMetaContent(html, "description");
        var imageUrl = ExtractMetaContent(html, "og:image");

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(imageUrl))
            return null;

        if (!string.IsNullOrWhiteSpace(imageUrl) && Uri.TryCreate(uri, imageUrl, out var absoluteImage))
            imageUrl = absoluteImage.ToString();

        return new LinkPreviewDto(uri.ToString(), Truncate(title, 200), Truncate(description, 300), imageUrl);
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return false;               // 0.0.0.0/8
            if (b[0] == 10) return false;               // 10.0.0.0/8
            if (b[0] == 127) return false;              // 127.0.0.0/8
            if (b[0] == 169 && b[1] == 254) return false; // 169.254.0.0/16 (incl. cloud metadata)
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return false; // 192.168.0.0/16
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 - unique local
            return true;
        }

        return false;
    }

    public static string? ExtractMetaContent(string html, string property)
    {
        var m = Regex.Match(html,
            $@"<meta[^>]+(?:property|name)\s*=\s*[""']{Regex.Escape(property)}[""'][^>]+content\s*=\s*[""']([^""']*)[""']",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(html,
                $@"<meta[^>]+content\s*=\s*[""']([^""']*)[""'][^>]+(?:property|name)\s*=\s*[""']{Regex.Escape(property)}[""']",
                RegexOptions.IgnoreCase);

        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
    }

    public static string? ExtractTitleTag(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
    }

    private static string? Truncate(string? s, int maxLength) =>
        string.IsNullOrEmpty(s) || s.Length <= maxLength ? s : s[..maxLength] + "…";
}
