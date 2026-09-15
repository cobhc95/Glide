using System.Net;
using System.Net.Sockets;
using System.Security;

namespace Glide.Core.Security;

/// <summary>
/// Hardened HTTP/HTTPS image fetcher providing robust Server-Side Request Forgery (SSRF)
/// defenses, strict URL scheme validation, DNS rebinding mitigation at the socket layer,
/// and bounded stream download caps.
/// </summary>
public static class SecureImageFetcher
{
    public const long DefaultMaxDownloadBytes = 100L * 1024L * 1024L; // 100 MB max download size
    public const int MaxRedirectHops = 5;

    private static readonly Lazy<HttpClient> SafeClient = new(CreateSafeHttpClient);

    public static HttpClient HttpClient => SafeClient.Value;

    /// <summary>
    /// Validates that a candidate URL uses only approved HTTP/HTTPS schemes, is absolute,
    /// and contains no user-info credentials or disallowed authority components.
    /// </summary>
    public static bool ValidateUrl(string? url, out Uri? validatedUri, out string failureReason)
    {
        validatedUri = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            failureReason = "URL is empty.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            failureReason = "URL is not a valid absolute URI.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            failureReason = $"Disallowed URI scheme '{uri.Scheme}'. Only HTTP and HTTPS are permitted.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            failureReason = "URLs containing userinfo credentials are not permitted.";
            return false;
        }

        if (uri.HostNameType == UriHostNameType.Unknown || string.IsNullOrWhiteSpace(uri.Host))
        {
            failureReason = "URL contains an invalid or empty host.";
            return false;
        }

        validatedUri = uri;
        failureReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Checks whether an IP address belongs to loopback, RFC 1918 private subnets,
    /// link-local/cloud metadata, CGNAT, multicast, or broadcast ranges.
    /// </summary>
    public static bool IsBlockedIpAddress(IPAddress ip)
    {
        // Unmap IPv4-mapped IPv6 addresses (e.g. ::ffff:127.0.0.1 -> 127.0.0.1)
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            var b0 = bytes[0];
            var b1 = bytes[1];

            // 0.0.0.0/8 (Current network)
            if (b0 == 0) return true;

            // 10.0.0.0/8 (RFC 1918 Private)
            if (b0 == 10) return true;

            // 127.0.0.0/8 (Loopback)
            if (b0 == 127) return true;

            // 100.64.0.0/10 (Shared Address Space / CGNAT)
            if (b0 == 100 && (b1 >= 64 && b1 <= 127)) return true;

            // 169.254.0.0/16 (Link-Local / AWS/Azure/GCP Cloud Metadata 169.254.169.254)
            if (b0 == 169 && b1 == 254) return true;

            // 172.16.0.0/12 (RFC 1918 Private: 172.16.0.0 – 172.31.255.255)
            if (b0 == 172 && (b1 >= 16 && b1 <= 31)) return true;

            // 192.0.0.0/24 (IETF Protocol Assignments)
            if (b0 == 192 && b1 == 0 && bytes[2] == 0) return true;

            // 192.168.0.0/16 (RFC 1918 Private)
            if (b0 == 192 && b1 == 168) return true;

            // 198.18.0.0/15 (Network Benchmark Tests)
            if (b0 == 198 && (b1 == 18 || b1 == 19)) return true;

            // 224.0.0.0/4 (Multicast: 224.0.0.0 - 239.255.255.255)
            if (b0 >= 224 && b0 <= 239) return true;

            // 240.0.0.0/4 (Reserved / Future Use)
            if (b0 >= 240) return true;

            // 255.255.255.255 (Broadcast)
            if (b0 == 255 && b1 == 255 && bytes[2] == 255 && bytes[3] == 255) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any))
                return true;

            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;

            var bytes = ip.GetAddressBytes();
            // fc00::/7 (Unique Local Address - RFC 4193)
            if ((bytes[0] & 0xFE) == 0xFC)
                return true;

            // fe80::/10 (Link-Local Unicast)
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a hardened HttpClient utilizing SocketsHttpHandler with a custom ConnectCallback.
    /// The ConnectCallback resolves DNS and validates the destination IP before initiating the socket,
    /// defeating Time-Of-Check to Time-Of-Use (TOCTOU) DNS rebinding attacks.
    /// </summary>
    public static HttpClient CreateSafeHttpClient() => CreateSafeHttpClient(TimeSpan.FromSeconds(20));

    /// <summary>
    /// Creates a hardened HttpClient utilizing SocketsHttpHandler with a custom ConnectCallback and specified timeout.
    /// </summary>
    public static HttpClient CreateSafeHttpClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // Manual redirect handling ensures redirect targets are validated
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
                if (entry.AddressList.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);

                // Check all resolved addresses; if any point to private/loopback, reject
                foreach (var address in entry.AddressList)
                {
                    if (IsBlockedIpAddress(address))
                    {
                        throw new SecurityException(
                            $"SSRF Protection: Connection to host '{context.DnsEndPoint.Host}' with resolved address '{address}' is blocked.");
                    }
                }

                // Connect to the first valid IP
                var targetIp = entry.AddressList[0];
                var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(targetIp, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout
        };
    }

    /// <summary>
    /// Securely downloads an image from a remote HTTP(S) URL into memory, enforcing SSRF validation,
    /// redirect policy checks, and maximum byte size limits.
    /// </summary>
    public static async Task<byte[]> FetchImageBytesAsync(
        string url,
        long maxDownloadBytes = DefaultMaxDownloadBytes,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateUrl(url, out var currentUri, out var failureReason))
            throw new ArgumentException($"Invalid URL: {failureReason}", nameof(url));

        var client = HttpClient;
        var hops = 0;

        while (hops < MaxRedirectHops)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Add("User-Agent", "GlideImageViewer/3.5 (SecurityHardened)");
            request.Headers.Add("Accept", "image/*,*/*;q=0.8");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            // Handle manual redirect verification
            if (response.StatusCode is HttpStatusCode.MovedPermanently or
                HttpStatusCode.Found or
                HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or
                (HttpStatusCode)308)
            {
                var location = response.Headers.Location;
                if (location is null)
                    throw new InvalidDataException("Redirect response missing Location header.");

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri!, location);
                if (!ValidateUrl(nextUri.ToString(), out currentUri, out failureReason))
                    throw new SecurityException($"SSRF Protection: Disallowed redirect target URL '{nextUri}': {failureReason}");

                hops++;
                continue;
            }

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > maxDownloadBytes)
            {
                throw new InvalidDataException(
                    $"Remote resource size ({declaredLength} bytes) exceeds the maximum allowed limit of {maxDownloadBytes} bytes.");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            long totalRead = 0;

            while (true)
            {
                var read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;

                totalRead += read;
                if (totalRead > maxDownloadBytes)
                {
                    throw new InvalidDataException(
                        $"Download exceeded the maximum allowed size limit of {maxDownloadBytes} bytes.");
                }

                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }

        throw new InvalidDataException($"Exceeded maximum redirect limit of {MaxRedirectHops} hops.");
    }
}
