namespace Glide.Imaging;

public enum VideoDiagnosisKind
{
    /// <summary>One or more codecs the video needs have no decoder in Windows on this PC.</summary>
    MissingCodecs,
    /// <summary>Windows has every decoder the video needs, yet couldn't play it (damaged file, unsupported size...).</summary>
    DecodersPresent,
    /// <summary>The file has audio tracks only.</summary>
    NoVideoTrack,
    /// <summary>A recognised container without a readable track list.</summary>
    Damaged,
    /// <summary>Not a video container Glide recognises.</summary>
    UnrecognisedFormat,
    /// <summary>The file couldn't be read.</summary>
    Unreadable,
}

/// <summary>Why a video can't be played, in words the user reads (no links: finding a codec is the user's call).</summary>
public sealed record VideoPlaybackDiagnosis(VideoDiagnosisKind Kind, string Message, IReadOnlyList<VideoCodec> MissingCodecs);

/// <summary>
/// Turns a <see cref="VideoContainerInfo"/> and Windows' decoder availability into the "can't play this video" message.
/// Glide plays videos only with the codecs installed in Windows, so the message names exactly what is missing, e.g.
/// "This video uses the VP9 codec, which isn't installed on this PC."
/// </summary>
public static class VideoPlaybackDiagnoser
{
    /// <summary>
    /// Video frames beyond this side exceed what many GPU decoders accept (AMD's H.264 decoder in particular); used only
    /// to add a hint when every decoder is present.
    /// </summary>
    public const int LargeFrameSide = 4096;

    /// <summary>Probes the file and asks Windows (Media Foundation) for each codec's decoder. Never throws.</summary>
    public static VideoPlaybackDiagnosis Diagnose(string path) =>
        Diagnose(VideoContainerProbe.Probe(path), NativeVideoPlayer.HasDecoder);

    /// <param name="hasDecoder">
    /// (subtype, isAudio) → true when Windows has a decoder, false when it has none, null when unknown (treated as missing,
    /// since Windows just failed to play the file).
    /// </param>
    public static VideoPlaybackDiagnosis Diagnose(VideoContainerInfo info, Func<Guid, bool, bool?> hasDecoder)
    {
        switch (info.Status)
        {
            case VideoProbeStatus.Unreadable:
                return new(VideoDiagnosisKind.Unreadable, "Glide couldn't read this file. It may have been moved or deleted, or another program may be using it.", []);
            case VideoProbeStatus.Unrecognised:
                return new(VideoDiagnosisKind.UnrecognisedFormat, "This file isn't in a video format Glide recognises. It may be damaged, or it may not be a video.", []);
        }

        var video = Distinct(info.Tracks.Where(x => x.Kind == VideoTrackKind.Video));
        var audio = Distinct(info.Tracks.Where(x => x.Kind == VideoTrackKind.Audio));
        if (info.Status == VideoProbeStatus.Damaged || (video.Count == 0 && audio.Count == 0))
            return new(VideoDiagnosisKind.Damaged, $"This {info.ContainerName} file appears to be damaged or incomplete: Glide couldn't find its video track.", []);
        if (video.Count == 0)
            return new(VideoDiagnosisKind.NoVideoTrack, $"This file has no video track, only {Join(audio.Select(x => x.Name).Distinct(StringComparer.Ordinal).ToArray())} audio.", []);

        var missingVideo = video.Where(x => IsMissing(x, hasDecoder)).ToArray();
        var missingAudio = audio.Where(x => IsMissing(x, hasDecoder)).ToArray();
        var missing = missingVideo.Concat(missingAudio).ToArray();
        if (missing.Length > 0) return new(VideoDiagnosisKind.MissingCodecs, MissingMessage(missingVideo, missingAudio), missing);

        var uses = Join(video.Select(x => x.Name + " video").Concat(audio.Select(x => x.Name + " audio")).Distinct(StringComparer.Ordinal).ToArray());
        var message = $"Windows has the codecs this video uses ({uses}), but couldn't play it. The file may be damaged.";
        var large = info.Tracks.FirstOrDefault(x => x.Kind == VideoTrackKind.Video && (x.Width > LargeFrameSide || x.Height > LargeFrameSide));
        if (large is not null)
            message += $" Its picture size ({large.Width} × {large.Height}) may also be larger than this PC's video decoder supports.";
        return new(VideoDiagnosisKind.DecodersPresent, message, []);
    }

    internal static string MissingMessage(IReadOnlyList<VideoCodec> video, IReadOnlyList<VideoCodec> audio)
    {
        var videoNames = video.Select(x => x.Name).Distinct(StringComparer.Ordinal).ToArray();
        var audioNames = audio.Select(x => x.Name).Distinct(StringComparer.Ordinal).ToArray();
        var verb = videoNames.Length + audioNames.Length == 1 ? "which isn't" : "which aren't";
        if (audioNames.Length == 0)
            return $"This video uses the {Join(videoNames)} {Codecs(videoNames.Length)}, {verb} installed on this PC.";
        if (videoNames.Length == 0)
            return $"This video's audio uses the {Join(audioNames)} {Codecs(audioNames.Length)}, {verb} installed on this PC.";
        return $"This video uses the {Join(videoNames)} video {Codecs(videoNames.Length)} and the {Join(audioNames)} audio {Codecs(audioNames.Length)}, {verb} installed on this PC.";
    }

    private static string Codecs(int count) => count == 1 ? "codec" : "codecs";

    private static bool IsMissing(VideoCodec codec, Func<Guid, bool, bool?> hasDecoder)
    {
        if (!codec.NeedsDecoder) return false;
        if (codec.Subtype == Guid.Empty) return true; // Windows has no decoder type for it at all
        try { return hasDecoder(codec.Subtype, codec.Kind == VideoTrackKind.Audio) != true; }
        catch (Exception) { return true; }
    }

    private static List<VideoCodec> Distinct(IEnumerable<VideoTrack> tracks)
    {
        var result = new List<VideoCodec>();
        foreach (var track in tracks)
            if (!result.Any(x => x.Id == track.Codec.Id && x.Subtype == track.Codec.Subtype)) result.Add(track.Codec);
        return result;
    }

    private static string Join(string[] items) => items.Length switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Length - 1)) + " and " + items[^1],
    };
}
