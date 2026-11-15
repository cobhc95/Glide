using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Glide.App.Updates;
using Glide.Core.Updates;
using static Glide.Core.Tests.UpdateTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// The HTTP side of the update check, against a fake <see cref="HttpMessageHandler"/> (no socket is ever opened): exactly what is
/// requested, what is refused (size, content type, redirects) and how failures map to statuses.
/// </summary>
public sealed class GitHubUpdateFeedTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<(string Name, string Value)> Headers { get; } = new();
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (var h in request.Headers) Headers.Add((h.Key, string.Join(",", h.Value)));
            return _respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(string body, string contentType = "application/json; charset=utf-8", HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private static (GitHubUpdateFeed Feed, FakeHandler Handler, Func<int> Created) Make(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var handler = new FakeHandler(respond);
        var created = 0;
        Func<HttpMessageHandler> factory = () => { created++; return new NonDisposingHandler(handler); };
        var feed = timeout is { } t ? new GitHubUpdateFeed(factory, t) : new GitHubUpdateFeed(factory);
        return (feed, handler, () => created);
    }

    /// <summary>HttpClient disposes its handler; the test keeps inspecting the inner one.</summary>
    private sealed class NonDisposingHandler : DelegatingHandler
    {
        public NonDisposingHandler(HttpMessageHandler inner) : base(inner) { }
        protected override void Dispose(bool disposing) { /* keep the shared fake alive for assertions */ }
    }

    [Fact]
    public async Task One_plain_GET_with_a_generic_user_agent_and_nothing_else()
    {
        var (feed, handler, created) = Make((_, _) => Task.FromResult(Json(ReleaseJson())));
        var response = await feed.GetLatestReleaseAsync(CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal(UpdateFeedStatus.Ok, response.Status);
        Assert.Contains("\"tag_name\"", response.Body);
        Assert.Equal(1, created());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/cobhc95/Glide/releases/latest", request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Content);
        Assert.Equal("Glide-UpdateCheck", string.Join(" ", request.Headers.UserAgent.Select(p => p.ToString())));
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.Single().MediaType);
        // No authentication, cookies, referrer, machine name or any other header: just these two.
        var names = handler.Headers.Select(h => h.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Accept", "User-Agent" }, names);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Null(request.Headers.Referrer);
    }

    [Fact]
    public async Task Json_flavours_are_accepted_and_other_content_types_are_not()
    {
        foreach (var good in new[] { "application/json", "application/json; charset=utf-8", "application/vnd.github+json", "application/vnd.github.v3+json" })
        {
            var (feed, _, _) = Make((_, _) => Task.FromResult(Json(ReleaseJson(), good)));
            Assert.Equal(UpdateFeedStatus.Ok, (await feed.GetLatestReleaseAsync(CancellationToken.None)).Status);
        }
        foreach (var bad in new[] { "text/html", "text/plain", "application/octet-stream", "application/xml", "image/png" })
        {
            var (feed, _, _) = Make((_, _) => Task.FromResult(Json(ReleaseJson(), bad)));
            var r = await feed.GetLatestReleaseAsync(CancellationToken.None);
            Assert.Equal(UpdateFeedStatus.BadContentType, r.Status);
            Assert.Null(r.Body);
        }
        var (noType, _, _) = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ReleaseJson())) }));
        Assert.Equal(UpdateFeedStatus.BadContentType, (await noType.GetLatestReleaseAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_declared_oversized_answer_is_refused_without_reading_it()
    {
        var content = new ByteArrayContent(new byte[UpdateReleaseParser.MaxResponseBytes + 1]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        var (feed, _, _) = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var r = await feed.GetLatestReleaseAsync(CancellationToken.None);
        Assert.Equal(UpdateFeedStatus.TooLarge, r.Status);
        Assert.Null(r.Body);
    }

    private sealed class StreamingContent : HttpContent
    {
        private readonly long _bytes;
        public StreamingContent(long bytes) { _bytes = bytes; Headers.ContentType = MediaTypeHeaderValue.Parse("application/json"); }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[8192];
            Array.Fill(chunk, (byte)'x');
            for (long sent = 0; sent < _bytes; sent += chunk.Length) await stream.WriteAsync(chunk);
        }
        protected override bool TryComputeLength(out long length) { length = -1; return false; }   // no Content-Length: a chunked answer
    }

    [Fact]
    public async Task An_oversized_answer_without_a_length_is_cut_off_at_the_limit()
    {
        var (feed, _, _) = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamingContent(50L * 1024 * 1024) }));
        var r = await feed.GetLatestReleaseAsync(CancellationToken.None);
        Assert.Equal(UpdateFeedStatus.TooLarge, r.Status);
    }

    [Fact]
    public async Task An_answer_exactly_at_the_limit_is_still_read()
    {
        var body = ReleaseJson();
        var padded = body.Substring(0, body.Length - 1) + ",\"pad\":\"" + new string('x', UpdateReleaseParser.MaxResponseBytes - body.Length - 10) + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(padded) <= UpdateReleaseParser.MaxResponseBytes);
        var (feed, _, _) = Make((_, _) => Task.FromResult(Json(padded)));
        var r = await feed.GetLatestReleaseAsync(CancellationToken.None);
        Assert.Equal(UpdateFeedStatus.Ok, r.Status);
        Assert.True(UpdateReleaseParser.Parse(r.Body).Ok);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]          // GitHub's rate limit
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task Anything_but_200_is_an_http_error(HttpStatusCode status)
    {
        var (feed, _, _) = Make((_, _) => Task.FromResult(Json("{}", status: status)));
        var r = await feed.GetLatestReleaseAsync(CancellationToken.None);
        Assert.Equal(UpdateFeedStatus.HttpError, r.Status);
        Assert.Equal((int)status, r.HttpStatus);
        Assert.Null(r.Body);
    }

    [Fact]
    public async Task Redirects_to_github_hosts_over_https_are_followed_and_others_are_not()
    {
        // api.github.com -> github.com (https): fine.
        var calls = 0;
        var (feed, handler, _) = Make((req, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? Redirect("https://github.com/repositories/1/releases/latest", HttpStatusCode.MovedPermanently) : Json(ReleaseJson()));
        });
        Assert.Equal(UpdateFeedStatus.Ok, (await feed.GetLatestReleaseAsync(CancellationToken.None)).Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("github.com", handler.Requests[1].RequestUri!.Host);

        foreach (var target in new[]
                 {
                     "http://api.github.com/repos/cobhc95/Glide/releases/latest",     // downgrade to http
                     "https://evil.com/x",
                     "https://api.github.com.evil.com/x",
                     "https://user@api.github.com/x",
                     "ftp://api.github.com/x",
                     "file:///C:/x",
                 })
        {
            var (blocked, h, _) = Make((_, _) => Task.FromResult(Redirect(target, HttpStatusCode.TemporaryRedirect)));
            var r = await blocked.GetLatestReleaseAsync(CancellationToken.None);
            Assert.Equal(UpdateFeedStatus.BlockedRedirect, r.Status);
            Assert.Single(h.Requests);                      // the hostile address is never requested
        }
    }

    [Fact]
    public async Task A_relative_redirect_stays_on_the_same_host_and_a_redirect_loop_gives_up()
    {
        var calls = 0;
        var (feed, handler, _) = Make((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? Redirect("/repos/cobhc95/Glide/releases/latest?x=1", HttpStatusCode.SeeOther) : Json(ReleaseJson()));
        });
        Assert.Equal(UpdateFeedStatus.Ok, (await feed.GetLatestReleaseAsync(CancellationToken.None)).Status);
        Assert.Equal("https://api.github.com/repos/cobhc95/Glide/releases/latest?x=1", handler.Requests[1].RequestUri!.AbsoluteUri);

        var (loop, loopHandler, _) = Make((_, _) => Task.FromResult(Redirect("https://api.github.com/loop")));
        Assert.Equal(UpdateFeedStatus.BlockedRedirect, (await loop.GetLatestReleaseAsync(CancellationToken.None)).Status);
        Assert.Equal(GitHubUpdateFeed.MaxRedirects + 1, loopHandler.Requests.Count);

        var (noLocation, _, _) = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)));
        Assert.Equal(UpdateFeedStatus.BlockedRedirect, (await noLocation.GetLatestReleaseAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Transport_failures_are_network_errors()
    {
        var (feed, _, _) = Make((_, _) => throw new HttpRequestException("no such host"));
        Assert.Equal(UpdateFeedStatus.NetworkError, (await feed.GetLatestReleaseAsync(CancellationToken.None)).Status);
        var (feed2, _, _) = Make((_, _) => throw new InvalidOperationException("anything"));
        Assert.Equal(UpdateFeedStatus.NetworkError, (await feed2.GetLatestReleaseAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_stalled_request_times_out()
    {
        var (feed, _, _) = Make(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); }, timeout: TimeSpan.FromMilliseconds(150));
        var started = DateTime.UtcNow;
        var r = await feed.GetLatestReleaseAsync(CancellationToken.None);
        Assert.Equal(UpdateFeedStatus.Timeout, r.Status);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(10), GitHubUpdateFeed.RequestTimeout);
    }

    [Fact]
    public async Task A_stalled_body_times_out_too()
    {
        var (feed, _, _) = Make((_, _) =>
        {
            var content = new StallingContent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }, timeout: TimeSpan.FromMilliseconds(150));
        Assert.Equal(UpdateFeedStatus.Timeout, (await feed.GetLatestReleaseAsync(CancellationToken.None)).Status);
    }

    private sealed class StallingContent : HttpContent
    {
        public StallingContent() => Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new HangingStream());
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_reported_as_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        var (feed, _, _) = Make(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); });
        var pending = feed.GetLatestReleaseAsync(cts.Token);
        cts.Cancel();
        Assert.Equal(UpdateFeedStatus.NetworkError, (await pending).Status);
    }

    [Fact]
    public async Task Every_request_gets_a_fresh_handler_so_no_connection_or_cookie_state_is_kept()
    {
        var (feed, _, created) = Make((_, _) => Task.FromResult(Json(ReleaseJson())));
        await feed.GetLatestReleaseAsync(CancellationToken.None);
        await feed.GetLatestReleaseAsync(CancellationToken.None);
        Assert.Equal(2, created());
    }

    [Fact]
    public async Task The_whole_chain_feed_checker_service_works_on_the_fake_http_layer()
    {
        var (feed, _, _) = Make((_, _) => Task.FromResult(Json(ReleaseJson("v5.0.2"))));
        var checker = new UpdateChecker(feed, V("5.0.1"), new SemaphoreSlim(1, 1));
        var outcome = await checker.CheckAsync();
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
        Assert.Equal("v5.0.2", outcome.Release!.Tag);
    }
}
