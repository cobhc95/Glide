using System.Text;

namespace Glide.Imaging;

public enum VideoTrackKind { Video, Audio }

/// <summary>
/// A codec Glide can name in its "can't play this video" message.
/// </summary>
/// <param name="Id">Stable id (h264, vp9, opus...; unrecognised tags are "video:&lt;tag&gt;" / "audio:&lt;tag&gt;").</param>
/// <param name="Name">What the user reads: "VP9", "HEVC (H.265)", "Opus".</param>
/// <param name="Subtype">The Media Foundation input subtype a Windows decoder for it registers; empty when there is none.</param>
/// <param name="NeedsDecoder">False for uncompressed audio/video, which Media Foundation reads without a decoder.</param>
public sealed record VideoCodec(string Id, VideoTrackKind Kind, string Name, Guid Subtype, bool NeedsDecoder = true);

/// <summary>
/// The codecs Glide recognises in video containers, with their Media Foundation subtypes. Glide ships no decoders:
/// these subtypes are only used to ask Windows whether it has one (<see cref="NativeVideoPlayer.HasDecoder"/>).
/// </summary>
public static class VideoCodecCatalog
{
    // ---------------------------------------------------------------- video
    public static readonly VideoCodec H264 = Video("h264", "H.264 (AVC)", Fcc("H264"));
    public static readonly VideoCodec Hevc = Video("hevc", "HEVC (H.265)", Fcc("HEVC"));
    public static readonly VideoCodec Vvc = Video("vvc", "VVC (H.266)", Guid.Empty);
    public static readonly VideoCodec Av1 = Video("av1", "AV1", Fcc("AV01"));
    public static readonly VideoCodec Vp8 = Video("vp8", "VP8", Fcc("VP80"));
    public static readonly VideoCodec Vp9 = Video("vp9", "VP9", Fcc("VP90"));
    public static readonly VideoCodec Mpeg4Part2 = Video("mpeg4", "MPEG-4 Part 2", Fcc("MP4V"));
    public static readonly VideoCodec MsMpeg4V3 = Video("msmpeg4v3", "Microsoft MPEG-4 v3", Fcc("MP43"));
    public static readonly VideoCodec MsMpeg4V2 = Video("msmpeg4v2", "Microsoft MPEG-4 v2", Fcc("MP42"));
    public static readonly VideoCodec MsMpeg4V1 = Video("msmpeg4v1", "Microsoft MPEG-4 v1", Fcc("MPG4"));
    public static readonly VideoCodec Mpeg2 = Video("mpeg2", "MPEG-2", new Guid(unchecked((int)0xE06D8026), unchecked((short)0xDB46), 0x11CF, 0xB4, 0xD1, 0x00, 0x80, 0x5F, 0x6C, 0xBB, 0xEA));
    public static readonly VideoCodec Mpeg1 = Video("mpeg1", "MPEG-1", Fcc("MPG1"));
    public static readonly VideoCodec H263 = Video("h263", "H.263", Fcc("H263"));
    public static readonly VideoCodec MotionJpeg = Video("mjpeg", "Motion JPEG", Fcc("MJPG"));
    public static readonly VideoCodec Theora = Video("theora", "Theora", Fcc("theo"));
    public static readonly VideoCodec Wmv7 = Video("wmv1", "Windows Media Video 7", Fcc("WMV1"));
    public static readonly VideoCodec Wmv8 = Video("wmv2", "Windows Media Video 8", Fcc("WMV2"));
    public static readonly VideoCodec Wmv9 = Video("wmv3", "Windows Media Video 9", Fcc("WMV3"));
    public static readonly VideoCodec Vc1 = Video("vc1", "VC-1", Fcc("WVC1"));
    public static readonly VideoCodec Dv = Video("dv", "DV", Fcc("dvsd"));
    public static readonly VideoCodec UncompressedVideo = new("rawvideo", VideoTrackKind.Video, "uncompressed video", Guid.Empty, NeedsDecoder: false);

    /// <summary>Apple ProRes. Windows has no built-in decoder; third-party ones register the profile's own FourCC.</summary>
    public static VideoCodec ProRes(string fourCc) => Video("prores", "Apple ProRes", Fcc(fourCc));

    // ---------------------------------------------------------------- audio
    public static readonly VideoCodec Aac = Audio("aac", "AAC", Wave(0x1610));
    public static readonly VideoCodec Mp3 = Audio("mp3", "MP3", Wave(0x0055));
    public static readonly VideoCodec MpegAudio = Audio("mp2", "MPEG audio (MP2)", Wave(0x0050));
    public static readonly VideoCodec Ac3 = Audio("ac3", "Dolby Digital (AC-3)", new Guid(unchecked((int)0xE06D802C), unchecked((short)0xDB46), 0x11CF, 0xB4, 0xD1, 0x00, 0x80, 0x5F, 0x6C, 0xBB, 0xEA));
    public static readonly VideoCodec Eac3 = Audio("eac3", "Dolby Digital Plus (E-AC-3)", new Guid(unchecked((int)0xA7FB87AF), 0x2D02, 0x42FB, 0xA4, 0xD4, 0x05, 0xCD, 0x93, 0x84, 0x3B, 0xDD));
    public static readonly VideoCodec Ac4 = Audio("ac4", "Dolby AC-4", Guid.Empty);
    public static readonly VideoCodec TrueHd = Audio("truehd", "Dolby TrueHD", Guid.Empty);
    public static readonly VideoCodec Dts = Audio("dts", "DTS", Wave(0x0008));
    public static readonly VideoCodec Opus = Audio("opus", "Opus", Wave(0x704F));
    public static readonly VideoCodec Vorbis = Audio("vorbis", "Vorbis", new Guid(unchecked((int)0x8D2FD10B), 0x5841, 0x4A6B, 0x89, 0x05, 0x58, 0x8F, 0xEC, 0x1A, 0xDE, 0xD9));
    public static readonly VideoCodec Flac = Audio("flac", "FLAC", Wave(0xF1AC));
    public static readonly VideoCodec Alac = Audio("alac", "Apple Lossless (ALAC)", Wave(0x6C61));
    public static readonly VideoCodec AmrNb = Audio("amrnb", "AMR-NB", Wave(0x7361));
    public static readonly VideoCodec AmrWb = Audio("amrwb", "AMR-WB", Wave(0x7362));
    public static readonly VideoCodec Wma = Audio("wma", "Windows Media Audio", Wave(0x0161));
    public static readonly VideoCodec WmaPro = Audio("wmapro", "Windows Media Audio Pro", Wave(0x0162));
    public static readonly VideoCodec WmaLossless = Audio("wmalossless", "Windows Media Audio Lossless", Wave(0x0163));
    public static readonly VideoCodec Speex = Audio("speex", "Speex", Guid.Empty);
    public static readonly VideoCodec Pcm = new("pcm", VideoTrackKind.Audio, "PCM", Guid.Empty, NeedsDecoder: false);

    /// <summary>A video FourCC as AVI, ASF and Matroska's VFW mode store it (BITMAPINFOHEADER.biCompression).</summary>
    public static VideoCodec FromVideoFourCc(string fourCc)
    {
        switch (fourCc.ToUpperInvariant())
        {
            case "H264": case "X264": case "AVC1": case "DAVC": case "VSSH": case "AVC3": return H264;
            case "HEVC": case "H265": case "X265": case "HVC1": case "HEV1": return Hevc;
            case "AV01": return Av1;
            case "VP80": return Vp8;
            case "VP90": return Vp9;
            case "XVID": case "DIVX": case "DX50": case "FMP4": case "MP4V": case "M4S2": case "3IV2": case "XVIX": return Mpeg4Part2;
            case "MP43": case "DIV3": case "DIV4": case "DIV5": case "DIV6": case "AP41": case "COL1": return MsMpeg4V3;
            case "MP42": case "DIV2": return MsMpeg4V2;
            case "MPG4": case "MP41": return MsMpeg4V1;
            case "MJPG": case "AVRN": case "DMB1": case "AVDJ": case "JPEG": return MotionJpeg;
            case "WMV1": return Wmv7;
            case "WMV2": return Wmv8;
            case "WMV3": case "WMVP": return Wmv9;
            case "WVC1": case "WMVA": case "WVP2": return Vc1;
            case "MPG1": case "MPEG": case "PIM1": return Mpeg1;
            case "MPG2": case "MP2V": case "MMES": case "EM2V": return Mpeg2;
            case "DVSD": case "DV25": case "DV50": case "DVHD": case "DVSL": case "CDVC": case "CDVH": return Dv;
            case "H263": case "S263": case "U263": return H263;
            case "THEO": return Theora;
            case "APCN": case "APCH": case "APCS": case "APCO": case "AP4H": case "AP4X": return ProRes(fourCc);
            case "\0\0\0\0": case "RAW ": case "DIB ": case "RGB ": return UncompressedVideo;
        }
        return UnknownVideo(fourCc);
    }

    /// <summary>An audio format tag (WAVEFORMATEX.wFormatTag) as AVI, ASF and Matroska's ACM mode store it.</summary>
    public static VideoCodec FromWaveFormatTag(int tag) => tag switch
    {
        0x0001 or 0x0003 => Pcm,
        0x0055 => Mp3,
        0x0050 => MpegAudio,
        0x00FF or 0x1610 or 0x706D or 0x4143 or 0xA106 => Aac,
        0x2000 => Ac3,
        0x2001 or 0x0008 => Dts,
        0x0160 or 0x0161 => Wma,
        0x0162 => WmaPro,
        0x0163 => WmaLossless,
        0x704F => Opus,
        0xF1AC => Flac,
        0x6C61 => Alac,
        0x7361 => AmrNb,
        0x7362 => AmrWb,
        0x674F or 0x6750 or 0x6751 or 0x676F or 0x6770 or 0x6771 => Vorbis,
        0xA109 => Speex,
        _ => new VideoCodec($"audio:0x{tag:X4}", VideoTrackKind.Audio, $"audio format 0x{tag:X4}", tag is > 0 and <= 0xFFFF ? Wave(tag) : Guid.Empty),
    };

    /// <summary>A video FourCC Glide has no name for: named by the tag itself, probed as the FourCC subtype.</summary>
    public static VideoCodec UnknownVideo(string tag)
    {
        var printable = Printable(tag);
        return new VideoCodec("video:" + printable, VideoTrackKind.Video, $"'{printable}'", tag.Length == 4 ? Fcc(tag) : Guid.Empty);
    }

    /// <summary>A codec named only by its container identifier (a Matroska CodecID, an MP4 sample entry...).</summary>
    public static VideoCodec Unnamed(VideoTrackKind kind, string tag)
    {
        var printable = Printable(tag);
        return new VideoCodec((kind == VideoTrackKind.Video ? "video:" : "audio:") + printable, kind, $"'{printable}'", Guid.Empty);
    }

    /// <summary>Media Foundation subtype of a FourCC (FOURCC-0000-0010-8000-00AA00389B71), the form MFVideoFormat_* use.</summary>
    public static Guid Fcc(string fourCc)
    {
        Span<byte> b = stackalloc byte[4];
        b.Fill((byte)' ');
        for (var i = 0; i < Math.Min(4, fourCc.Length); i++) b[i] = (byte)fourCc[i];
        var data1 = b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24;
        return new Guid(data1, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    }

    /// <summary>Media Foundation subtype of a WAVE format tag, the form MFAudioFormat_* use.</summary>
    public static Guid Wave(int tag) => new(tag, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    internal static string Printable(string tag)
    {
        var text = new StringBuilder(Math.Min(tag.Length, 48));
        foreach (var c in tag.AsSpan(0, Math.Min(tag.Length, 48)))
            text.Append(c is >= ' ' and <= '~' ? c : '?');
        return text.ToString().TrimEnd();
    }

    private static VideoCodec Video(string id, string name, Guid subtype) => new(id, VideoTrackKind.Video, name, subtype);
    private static VideoCodec Audio(string id, string name, Guid subtype) => new(id, VideoTrackKind.Audio, name, subtype);
}
