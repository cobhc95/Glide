namespace Glide.Core.Updates;

public enum UpdateFeedStatus
{
    Ok,
    /// <summary>No connection, DNS or TLS failure, or any other transport error.</summary>
    NetworkError,
    Timeout,
    /// <summary>GitHub answered with something other than 200 (rate limit, 404, 5xx...).</summary>
    HttpError,
    TooLarge,
    BadContentType,
    /// <summary>A redirect to somewhere other than https on api.github.com / github.com (never followed).</summary>
    BlockedRedirect,
    /// <summary>No feed is configured (tests, designer): nothing was requested.</summary>
    Unavailable
}

/// <summary>The raw answer of one feed request. <see cref="Body"/> is set only when <see cref="Status"/> is <see cref="UpdateFeedStatus.Ok"/>.</summary>
public sealed record UpdateFeedResponse(UpdateFeedStatus Status, string? Body = null, int HttpStatus = 0)
{
    public bool Ok => Status == UpdateFeedStatus.Ok && Body is not null;
}

/// <summary>
/// Where the update checker gets "the latest release" from. The only real implementation is
/// <c>Glide.App.Updates.GitHubUpdateFeed</c>; tests use fakes, so no test ever touches the network.
/// </summary>
public interface IUpdateFeed
{
    Task<UpdateFeedResponse> GetLatestReleaseAsync(CancellationToken cancellationToken);
}
