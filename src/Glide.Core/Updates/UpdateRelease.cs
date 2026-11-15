using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glide.Core.Updates;

/// <summary>The few fields of GitHub's "latest release" answer that Glide reads.</summary>
public sealed record UpdateRelease(
    string Tag,
    UpdateVersion Version,
    string? HtmlUrl,
    DateTimeOffset? PublishedAt,
    bool IsDraft,
    bool IsPreRelease)
{
    /// <summary>A published, non-pre-release version: the only kind that can be offered as an update.</summary>
    public bool IsStable => !IsDraft && !IsPreRelease && !Version.IsPreRelease;
}

public enum UpdateParseStatus { Ok, Empty, TooLarge, BadJson, MissingTag, BadVersion }

public readonly record struct UpdateParseResult(UpdateParseStatus Status, UpdateRelease? Release)
{
    public bool Ok => Status == UpdateParseStatus.Ok;
}

/// <summary>Wire shape of GitHub's release JSON. Source-generated (CoreJsonContext), so it works in the NativeAOT build.</summary>
internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
}

public static class UpdateReleaseParser
{
    /// <summary>The answer is never larger than a few KB; anything above this is refused unread.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    public static UpdateParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(UpdateParseStatus.Empty, null);
        // The size limit is in bytes; a char is at least one byte, so a longer string is certainly too large.
        if (json.Length > MaxResponseBytes) return new(UpdateParseStatus.TooLarge, null);
        GitHubReleaseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, CoreJsonContext.Default.GitHubReleaseDto);
        }
        catch (JsonException)
        {
            return new(UpdateParseStatus.BadJson, null);
        }
        if (dto is null) return new(UpdateParseStatus.BadJson, null);
        if (string.IsNullOrWhiteSpace(dto.TagName)) return new(UpdateParseStatus.MissingTag, null);
        if (!UpdateVersion.TryParse(dto.TagName, out var version)) return new(UpdateParseStatus.BadVersion, null);
        DateTimeOffset? published = null;
        if (!string.IsNullOrWhiteSpace(dto.PublishedAt) &&
            DateTimeOffset.TryParse(dto.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            published = when;
        return new(UpdateParseStatus.Ok, new UpdateRelease(dto.TagName.Trim(), version, dto.HtmlUrl, published, dto.Draft, dto.Prerelease));
    }
}
