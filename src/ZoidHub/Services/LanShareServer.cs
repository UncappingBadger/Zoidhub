using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZoidHub.Models;

namespace ZoidHub.Services;

/// <summary>Minimal, hand-rolled GET-only HTTPS/1.1 server so another device on the same LAN (a
/// phone/tablet) can view the map in a plain browser - "LAN Mode" in MainWindow. Deliberately not
/// System.Net.HttpListener: binding that to anything other than localhost normally requires
/// either admin rights or a one-time `netsh http add urlacl` reservation (it's backed by
/// http.sys, which enforces this), which would be a bad first-run experience for a feature meant
/// to be a simple checkbox. TcpListener has no such restriction.
///
/// HTTPS via a self-signed certificate (see GetOrCreateCertificate) - there's no way around a
/// browser's "not private" warning for this, since a private LAN IP with no public domain name
/// can never get a certificate a browser trusts out of the box (public CAs like Let's Encrypt
/// won't issue for private IPs at all). The certificate is generated once and reused across
/// restarts specifically so that's a one-time click-through per device, not a fresh warning every
/// single session.
///
/// Serves three things, all same-origin (no CORS needed since the remote browser loads
/// everything from this one server): the static WebMap files at "/", the active map's rendered
/// tiles at "/tiles/..." (mirrors the "/base/..." shape TILES_HOST already uses inside WebView2 -
/// see MainWindow.RemapTileHost), and a read-only "/api/markers" JSON endpoint. No write path for
/// markers at all - editing only ever happens from the PC's own WebView2 instance.</summary>
public class LanShareServer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _webMapDir;
    private readonly Func<string?> _getTileDir;
    private readonly Func<List<Marker>> _getMarkers;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private X509Certificate2? _certificate;

    public int Port { get; }

    public LanShareServer(string webMapDir, Func<string?> getTileDir, Func<List<Marker>> getMarkers, int port = 41414)
    {
        _webMapDir = webMapDir;
        _getTileDir = getTileDir;
        _getMarkers = getMarkers;
        Port = port;
    }

    public void Start()
    {
        _certificate = GetOrCreateCertificate();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        AppLogger.Log($"LanShareServer: listening on port {Port} (HTTPS, self-signed cert expires {_certificate.NotAfter:yyyy-MM-dd}).");
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* already stopped */ }
        _listener = null;
        _certificate?.Dispose();
        _certificate = null;
        AppLogger.Log("LanShareServer: stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception)
            {
                break; // listener was Stop()'d
            }
            _ = HandleClientAsync(client, ct);
        }
    }

    // One connection, one response, then close - no keep-alive/pipelining support. Simpler and
    // more robust for a hand-rolled server than trying to correctly handle persistent
    // connections, at the cost of a new TCP connection per asset - a non-issue for a handful of
    // devices on a home LAN loading a map view.
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            try
            {
                using var networkStream = client.GetStream();
                using var stream = new SslStream(networkStream, leaveInnerStreamOpen: false);
                // SslProtocols.None lets the OS/.NET negotiate the best protocol both sides
                // support, rather than pinning to a specific TLS version by hand - the current
                // recommended approach rather than a maintenance liability as TLS versions age.
                await stream.AuthenticateAsServerAsync(_certificate!, clientCertificateRequired: false,
                    enabledSslProtocols: SslProtocols.None, checkCertificateRevocation: false);

                var requestLine = await ReadLineAsync(stream, ct);
                if (string.IsNullOrEmpty(requestLine)) return;

                // Only "If-Modified-Since" is actually used (for cache revalidation - see
                // RouteAsync); everything else is parsed just enough to consume the request
                // correctly, not acted on.
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = await ReadLineAsync(stream, ct)))
                {
                    var colonIndex = headerLine.IndexOf(':');
                    if (colonIndex <= 0) continue;
                    headers[headerLine[..colonIndex].Trim()] = headerLine[(colonIndex + 1)..].Trim();
                }

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || parts[0] != "GET")
                {
                    await WriteResponseAsync(stream, 405, "Method Not Allowed", "text/plain",
                        Encoding.UTF8.GetBytes("Only GET is supported"), ct);
                    return;
                }

                var rawPath = parts[1];
                var path = Uri.UnescapeDataString(rawPath.Split('?')[0]);
                await RouteAsync(stream, path, headers, ct);
            }
            catch (Exception)
            {
                // Client disconnected mid-request, malformed input, etc. - nothing actionable,
                // and logging every one of these would be noise (browsers routinely open and
                // abandon speculative connections).
            }
        }
    }

    // "Public" cache lifetime for static files (WebMap assets + rendered tiles) before a browser
    // will even bother asking again - paired with Last-Modified/If-Modified-Since revalidation
    // below rather than relied on alone, specifically so a Re-render Map (which genuinely changes
    // tile file contents on disk, mtime and all) doesn't leave browsers showing stale cached tiles
    // for a full day. A revalidation request is cheap (a 304 with no body) even within this
    // window, so there's no real downside to a long max-age here.
    private const int StaticCacheMaxAgeSeconds = 86400;

    private async Task RouteAsync(Stream stream, string path, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (path == "/api/markers")
        {
            // Never cached - this is what the remote view's 60s poll relies on actually being
            // fresh each time, not a stale cached response from an earlier request.
            var json = JsonSerializer.Serialize(_getMarkers(), JsonOptions);
            await WriteResponseAsync(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes(json), ct,
                cacheControl: "no-store");
            return;
        }

        string rootDir;
        string relativePath;
        if (path.StartsWith("/tiles/", StringComparison.Ordinal))
        {
            var tileDir = _getTileDir();
            if (tileDir == null)
            {
                await WriteResponseAsync(stream, 404, "Not Found", "text/plain",
                    Encoding.UTF8.GetBytes("Map not rendered yet"), ct);
                return;
            }
            rootDir = tileDir;
            relativePath = path.Substring("/tiles/".Length);
        }
        else
        {
            rootDir = _webMapDir;
            relativePath = path.TrimStart('/');
            if (relativePath.Length == 0) relativePath = "index.html";
        }

        // Path-traversal guard: the resolved path must stay strictly inside rootDir, regardless
        // of ".."/encoded segments in the request - this is real, untrusted network input now
        // (anything on the LAN can send arbitrary paths), not a locally-trusted call.
        var fullPath = Path.GetFullPath(Path.Combine(rootDir, relativePath));
        var rootFull = Path.GetFullPath(rootDir) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            await WriteResponseAsync(stream, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("Not found"), ct);
            return;
        }

        // HTTP dates only carry 1-second resolution, so truncate the file's own mtime to whole
        // seconds before comparing - otherwise sub-second differences would make it look "newer"
        // than the client's cached copy on every single request and the 304 path would never hit.
        var lastModified = new DateTime(File.GetLastWriteTimeUtc(fullPath).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        if (headers.TryGetValue("If-Modified-Since", out var ifModifiedSinceRaw)
            && DateTime.TryParse(ifModifiedSinceRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ifModifiedSince)
            && lastModified <= ifModifiedSince)
        {
            await WriteResponseAsync(stream, 304, "Not Modified", ContentTypeFor(fullPath), Array.Empty<byte>(), ct,
                cacheControl: $"public, max-age={StaticCacheMaxAgeSeconds}", lastModified: lastModified);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        await WriteResponseAsync(stream, 200, "OK", ContentTypeFor(fullPath), bytes, ct,
            cacheControl: $"public, max-age={StaticCacheMaxAgeSeconds}", lastModified: lastModified);
    }

    private static async Task WriteResponseAsync(Stream stream, int statusCode, string statusText,
        string contentType, byte[] body, CancellationToken ct, string? cacheControl = null, DateTime? lastModified = null)
    {
        var header = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n" +
                     (cacheControl != null ? $"Cache-Control: {cacheControl}\r\n" : "") +
                     (lastModified != null ? $"Last-Modified: {lastModified.Value.ToString("R", CultureInfo.InvariantCulture)}\r\n" : "") +
                     "\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct);
        await stream.WriteAsync(body, ct);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return sb.Length == 0 ? null : sb.ToString(); // connection closed
            var c = (char)buffer[0];
            if (c == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return sb.ToString();
            }
            sb.Append(c);
        }
    }

    /// <summary>Loads the persisted self-signed certificate if one already exists and is still
    /// comfortably valid, otherwise generates a fresh one and persists it - reused across
    /// restarts specifically so a browser's "I trust this" decision (if someone chooses to
    /// permanently trust it rather than click through the warning each time) survives too, not
    /// just this one session. Includes the current LAN IP as a Subject Alternative Name; if the
    /// IP ever changes (no longer static, moved networks, etc.) the stale cert simply gets
    /// regenerated automatically next launch rather than silently kept.</summary>
    private static X509Certificate2 GetOrCreateCertificate()
    {
        var certPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZoidHub", "lanshare-cert.pfx");
        var currentIp = FindLanIPv4Address();

        if (File.Exists(certPath))
        {
            try
            {
                var existing = new X509Certificate2(certPath, (string?)null, X509KeyStorageFlags.Exportable);
                var stillMatchesIp = currentIp == null
                    || existing.GetNameInfo(X509NameType.DnsName, false) == currentIp
                    || GetCertificateSanIPs(existing).Contains(currentIp);
                if (existing.NotAfter > DateTime.Now.AddDays(30) && stillMatchesIp) return existing;
                existing.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"LanShareServer: couldn't load existing cert, regenerating: {ex.Message}");
            }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=ZoidHub LAN Share", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        if (currentIp != null && IPAddress.TryParse(currentIp, out var parsedIp)) sanBuilder.AddIpAddress(parsedIp);
        req.CertificateExtensions.Add(sanBuilder.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        // 10-year validity - this is a private, self-signed cert with no CA chain to worry about
        // rotating; the goal is "don't make the user re-approve a new one every so often", not
        // short-lived-cert hygiene that matters for publicly-trusted certificates.
        using var generated = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
        var exportable = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);

        Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);
        File.WriteAllBytes(certPath, exportable.Export(X509ContentType.Pfx));
        AppLogger.Log($"LanShareServer: generated new self-signed certificate (IP: {currentIp ?? "unknown"}, expires {exportable.NotAfter:yyyy-MM-dd}).");
        return exportable;
    }

    private static IEnumerable<string> GetCertificateSanIPs(X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue; // Subject Alternative Name OID
            var formatted = ext.Format(false);
            foreach (var part in formatted.Split(new[] { ", ", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = part.IndexOf('=');
                if (idx > 0) yield return part[(idx + 1)..].Trim();
            }
        }
    }

    /// <summary>Best-effort pick of "the" LAN-facing IPv4 address to show the user for
    /// "connect from another device". Prefers an interface that's actually Up and not a
    /// loopback/virtual adapter, then narrows to a private-range address (192.168.x.x, 10.x.x.x,
    /// 172.16-31.x.x) as a heuristic for "a real home-network adapter" over stray virtual
    /// adapters (Hyper-V, VPN, etc.) that can otherwise show up first and aren't reachable from a
    /// phone on the same WiFi. Returns null if nothing plausible is found (e.g. no network at
    /// all) - the caller shows a "couldn't determine your IP" message rather than a wrong address.</summary>
    public static string? FindLanIPv4Address()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .ToList();

        return candidates.FirstOrDefault(IsPrivateRange)?.ToString()
            ?? candidates.FirstOrDefault()?.ToString();
    }

    private static bool IsPrivateRange(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 192 && b[1] == 168
            || b[0] == 10
            || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".dzi" => "application/xml",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };
}
