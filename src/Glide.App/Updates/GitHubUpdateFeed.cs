using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Glide.Core.Updates;

namespace Glide.App.Updates;

/// <summary>
/// The one place Glide talks to the network for updates: a single plain GET of GitHub's "latest release" JSON
/// (<see cref="UpdateUrlPolicy.LatestReleaseApiUrl"/>). Over HTTPS only, no authentication, no cookies, no personal data (just a generic
/// User-Agent and an Accept header), a 10 second timeout, a response capped at 256 KB, and redirects followed only to https on
/// api.github.com or github.com. The system proxy settings apply. Nothing is ever downloaded or installed.
/// <para>The class has no public way to point it somewhere else: tests give it a fake <see cref="HttpMessageHandler"/>
/// (no real socket), and the real handler exists only in <see cref="CreateProduction"/>, which only production start-up calls.</para>
/// </summary>
internal sealed class GitHubUpdateFeed : IUpdateFeed
{
    public const string UserAgent = "Glide-UpdateCheck";
    public const string AcceptMediaType = "application/vnd.github+json";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public const int MaxRedirects = 3;

    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly TimeSpan _timeout;

    /// <param name="handlerFactory">Creates the handler for one request (disposed with its HttpClient). It must not follow redirects itself.</param>
    public GitHubUpdateFeed(Func<HttpMessageHandler> handlerFactory) : this(handlerFactory, RequestTimeout)
    {
    }

    /// <summary>Test seam: a shorter timeout so the timeout path can be tested quickly.</summary>
    internal GitHubUpdateFeed(Func<HttpMessageHandler> handlerFactory, TimeSpan timeout)
    {
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
        _timeout = timeout;
    }

    /// <summary>The real feed. Called only by production wiring (<see cref="UpdateProduction"/>); never from a test.</summary>
    public static GitHubUpdateFeed CreateProduction() => new(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,           // redirects are checked by hand (https on github hosts only)
        UseCookies = false,                  // no cookies, ever
        UseProxy = true,                     // system proxy default
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = RequestTimeout,
        MaxResponseHeadersLength = 32,       // KB
        PooledConnectionLifetime = TimeSpan.FromSeconds(30)
    });

    public async Task<UpdateFeedResponse> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var client = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            var uri = new Uri(UpdateUrlPolicy.LatestReleaseApiUrl);
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptMediaType));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    var next = location is null ? null : location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!UpdateUrlPolicy.IsAllowedRedirect(next)) return new UpdateFeedResponse(UpdateFeedStatus.BlockedRedirect, HttpStatus: (int)response.StatusCode);
                    uri = next!;
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    return new UpdateFeedResponse(UpdateFeedStatus.HttpError, HttpStatus: (int)response.StatusCode);

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                                           mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                    return new UpdateFeedResponse(UpdateFeedStatus.BadContentType, HttpStatus: 200);
                if (response.Content.Headers.ContentLength is { } declared && declared > UpdateReleaseParser.MaxResponseBytes)
                    return new UpdateFeedResponse(UpdateFeedStatus.TooLarge, HttpStatus: 200);

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var body = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (body.Length + read > UpdateReleaseParser.MaxResponseBytes) return new UpdateFeedResponse(UpdateFeedStatus.TooLarge, HttpStatus: 200);
                    body.Write(buffer, 0, read);
                }
                return new UpdateFeedResponse(UpdateFeedStatus.Ok, Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length), 200);
            }
            return new UpdateFeedResponse(UpdateFeedStatus.BlockedRedirect);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateFeedResponse(UpdateFeedStatus.Timeout);
        }
        catch (OperationCanceledException)
        {
            return new UpdateFeedResponse(UpdateFeedStatus.NetworkError);
        }
        catch (Exception)
        {
            // DNS, TLS, proxy, offline, malformed answer: all the same to the user ("Couldn't reach GitHub"), and silent in automatic checks.
            return new UpdateFeedResponse(UpdateFeedStatus.NetworkError);
        }
    }
}
