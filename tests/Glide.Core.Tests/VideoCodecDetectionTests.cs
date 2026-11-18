using System.Buffers.Binary;
using System.Text;
using Glide.Core.Reset;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.4: Glide plays videos only with the codecs installed in Windows and, when it can't, names the missing codec.
/// Every container here is a SYNTHETIC minimal file built in the test (headers only, no media data, nothing downloaded).
/// The Media Foundation decoder probe is mocked, so the expected message never depends on this PC's codecs.
/// </summary>
[Trait("Area", "Video")]
public sealed class VideoCodecDetectionTests
{
    // ============================================================================ MP4 / MOV ========

    [Theory]
    [InlineData("avc1", "h264")]
    [InlineData("avc3", "h264")]
    [InlineData("hvc1", "hevc")]
    [InlineData("hev1", "hevc")]
    [InlineData("av01", "av1")]
    [InlineData("vp09", "vp9")]
    [InlineData("vp08", "vp8")]
    [InlineData("ap4h", "prores")]
    [InlineData("apcn", "prores")]
    [InlineData("s263", "h263")]
    [InlineData("xyz1", "video:xyz1")]
    public void Mp4_video_sample_entries_name_their_codec(string format, string expected)
    {
        var info = Probe(Mp4.File(Mp4.VideoTrack(format, 1920, 1080)));
        Assert.Equal(VideoProbeStatus.Ok, info.Status);
        Assert.Equal(VideoContainerKind.Mp4, info.Container);
        var track = Assert.Single(info.Tracks);
        Assert.Equal((VideoTrackKind.Video, expected, 1920, 1080), (track.Kind, track.Codec.Id, track.Width, track.Height));
    }

    [Theory]
    [InlineData("Opus", -1, "opus")]
    [InlineData("fLaC", -1, "flac")]
    [InlineData("ac-3", -1, "ac3")]
    [InlineData("ec-3", -1, "eac3")]
    [InlineData("alac", -1, "alac")]
    [InlineData("samr", -1, "amrnb")]
    [InlineData("sowt", -1, "pcm")]
    [InlineData("mp4a", 0x40, "aac")]
    [InlineData("mp4a", 0x6B, "mp3")]
    [InlineData("mp4a", 0xA5, "ac3")]
    [InlineData("mp4a", 0xAD, "opus")]
    public void Mp4_audio_sample_entries_name_their_codec(string format, int objectType, string expected)
    {
        var info = Probe(Mp4.File(Mp4.VideoTrack("avc1", 640, 360), Mp4.AudioTrack(format, objectType)));
        Assert.Equal(["h264", expected], info.Tracks.Select(x => x.Codec.Id).ToArray());
        Assert.Equal(VideoTrackKind.Audio, info.Tracks[1].Kind);
    }

    [Theory]
    [InlineData(0x20, "mpeg4")]
    [InlineData(0x61, "mpeg2")]
    [InlineData(0x6A, "mpeg1")]
    public void Mp4v_entries_are_told_apart_by_their_object_type(int objectType, string expected)
    {
        var info = Probe(Mp4.File(Mp4.VideoTrack("mp4v", 720, 576, Mp4.Esds(objectType))));
        Assert.Equal(expected, Assert.Single(info.Tracks).Codec.Id);
    }

    [Fact]
    public void QuickTime_movies_with_esds_inside_wave_and_encrypted_entries_are_read()
    {
        var wave = Mp4.Box("wave", Mp4.Box("frma", "mp4a"u8.ToArray()), Mp4.Esds(0x6B));
        var sinf = Mp4.Box("sinf", Mp4.Box("frma", "hvc1"u8.ToArray()));
        var info = Probe(Mp4.File("qt  ", Mp4.VideoTrack("encv", 3840, 2160, sinf), Mp4.AudioTrack("mp4a", -1, 1, wave)));
        Assert.Equal(VideoContainerKind.QuickTime, info.Container);
        Assert.Equal(["hevc", "mp3"], info.Tracks.Select(x => x.Codec.Id).ToArray());
    }

    [Fact]
    public void An_mp4_without_its_movie_box_is_reported_damaged()
    {
        var file = Concat(Mp4.Box("ftyp", "isom\0\0\0\0isom"u8.ToArray()), Mp4.Box("mdat", new byte[64]));
        var info = Probe(file);
        Assert.Equal((VideoContainerKind.Mp4, VideoProbeStatus.Damaged), (info.Container, info.Status));
    }

    // ============================================================================ Matroska / WebM ========

    [Theory]
    [InlineData("V_VP8", "vp8")]
    [InlineData("V_VP9", "vp9")]
    [InlineData("V_AV1", "av1")]
    [InlineData("V_MPEGH/ISO/HEVC", "hevc")]
    [InlineData("V_MPEG4/ISO/AVC", "h264")]
    [InlineData("V_MPEG4/ISO/ASP", "mpeg4")]
    [InlineData("V_MPEG2", "mpeg2")]
    [InlineData("V_THEORA", "theora")]
    [InlineData("V_FFV1", "video:V_FFV1")]
    public void Matroska_video_codec_ids_are_named(string codecId, string expected)
    {
        var info = Probe(Mkv.File("matroska", Mkv.Track(1, codecId, 1280, 720)));
        Assert.Equal((VideoContainerKind.Matroska, VideoProbeStatus.Ok), (info.Container, info.Status));
        var track = Assert.Single(info.Tracks);
        Assert.Equal((expected, 1280, 720), (track.Codec.Id, track.Width, track.Height));
    }

    [Theory]
    [InlineData("A_OPUS", "opus")]
    [InlineData("A_VORBIS", "vorbis")]
    [InlineData("A_FLAC", "flac")]
    [InlineData("A_AC3", "ac3")]
    [InlineData("A_EAC3", "eac3")]
    [InlineData("A_DTS", "dts")]
    [InlineData("A_DTS/EXPRESS", "dts")]
    [InlineData("A_TRUEHD", "truehd")]
    [InlineData("A_AAC/MPEG4/LC", "aac")]
    [InlineData("A_MPEG/L3", "mp3")]
    [InlineData("A_PCM/INT/LIT", "pcm")]
    public void Matroska_audio_codec_ids_are_named(string codecId, string expected)
    {
        var info = Probe(Mkv.File("webm", Mkv.Track(1, "V_VP9", 640, 360), Mkv.Track(2, codecId)));
        Assert.Equal(VideoContainerKind.WebM, info.Container);
        Assert.Equal(["vp9", expected], info.Tracks.Select(x => x.Codec.Id).ToArray());
    }

    [Fact]
    public void Matroska_vfw_and_acm_tracks_use_their_fourcc_and_format_tag()
    {
        var bitmapInfo = new byte[40];
        "XVID"u8.CopyTo(bitmapInfo.AsSpan(16));
        var waveFormat = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(waveFormat, 0x2000);
        var info = Probe(Mkv.File("matroska", Mkv.Track(1, "V_MS/VFW/FOURCC", 0, 0, bitmapInfo), Mkv.Track(2, "A_MS/ACM", 0, 0, waveFormat)));
        Assert.Equal(["mpeg4", "ac3"], info.Tracks.Select(x => x.Codec.Id).ToArray());
    }

    [Fact]
    public void Matroska_tracks_after_the_first_cluster_are_found_through_the_seek_head()
    {
        var info = Probe(Mkv.File("webm", tracksAfterCluster: true, Mkv.Track(1, "V_AV1", 3840, 2160), Mkv.Track(2, "A_OPUS")));
        Assert.Equal(["av1", "opus"], info.Tracks.Select(x => x.Codec.Id).ToArray());
    }

    // ============================================================================ AVI / ASF ========

    [Theory]
    [InlineData("H264", "h264")]
    [InlineData("XVID", "mpeg4")]
    [InlineData("DIV3", "msmpeg4v3")]
    [InlineData("MJPG", "mjpeg")]
    [InlineData("HEVC", "hevc")]
    [InlineData("dvsd", "dv")]
    [InlineData("QQQQ", "video:QQQQ")]
    public void Avi_streams_are_named_from_their_fourcc(string fourCc, string expected)
    {
        var info = Probe(Avi.File(fourCc, 0x2000));
        Assert.Equal((VideoContainerKind.Avi, VideoProbeStatus.Ok), (info.Container, info.Status));
        Assert.Equal([expected, "ac3"], info.Tracks.Select(x => x.Codec.Id).ToArray());
        Assert.Equal((640, 480), (info.Tracks[0].Width, info.Tracks[0].Height));
    }

    [Fact]
    public void Avi_extensible_audio_uses_the_subformat_tag()
    {
        var info = Probe(Avi.File("H264", 0xFFFE, extensibleTag: 0x0055));
        Assert.Equal("mp3", info.Tracks[1].Codec.Id);
    }

    [Theory]
    [InlineData("WMV3", 0x0161, "wmv3", "wma")]
    [InlineData("WVC1", 0x0162, "vc1", "wmapro")]
    [InlineData("WMV2", 0x0163, "wmv2", "wmalossless")]
    public void Asf_stream_properties_name_video_and_audio(string fourCc, int audioTag, string video, string audio)
    {
        var info = Probe(Asf.File(fourCc, audioTag));
        Assert.Equal((VideoContainerKind.Asf, VideoProbeStatus.Ok), (info.Container, info.Status));
        Assert.Equal([video, audio], info.Tracks.Select(x => x.Codec.Id).ToArray());
        Assert.Equal((1280, 720), (info.Tracks[0].Width, info.Tracks[0].Height));
    }

    // ============================================================================ MPEG-TS / PS / ES ========

    [Fact]
    public void Transport_stream_types_and_descriptors_are_named()
    {
        var streams = new (byte Type, byte[] Descriptors)[]
        {
            (0x24, []), (0x81, []), (0x06, Ts.Registration("Opus")), (0x06, [0x7A, 0x00]), (0x90, []), (0x1B, []),
        };
        var info = Probe(Ts.File(188, streams));
        Assert.Equal((VideoContainerKind.MpegTransportStream, VideoProbeStatus.Ok), (info.Container, info.Status));
        Assert.Equal(["hevc", "ac3", "opus", "eac3", "h264"], info.Tracks.Select(x => x.Codec.Id).ToArray());
    }

    [Fact]
    public void Blu_ray_m2ts_with_timestamp_prefixes_is_read()
    {
        var info = Probe(Ts.File(192, [(0x02, []), (0xEA, []), (0x83, []), (0x82, []), (0x80, [])]));
        Assert.Equal(["mpeg2", "vc1", "truehd", "dts", "pcm"], info.Tracks.Select(x => x.Codec.Id).ToArray());
    }

    [Fact]
    public void Program_stream_names_mpeg2_video_mpeg_audio_and_private_ac3()
    {
        var info = Probe(Ps.File());
        Assert.Equal((VideoContainerKind.MpegProgramStream, VideoProbeStatus.Ok), (info.Container, info.Status));
        Assert.Equal(["mpeg2", "mp2", "ac3"], info.Tracks.Select(x => x.Codec.Id).ToArray());
        Assert.Equal((720, 576), (info.Tracks[0].Width, info.Tracks[0].Height));
    }

    [Theory]
    [InlineData(true, "mpeg2")]
    [InlineData(false, "mpeg1")]
    public void Elementary_mpeg_video_is_mpeg2_only_with_a_sequence_extension(bool extension, string expected)
    {
        var file = new List<byte> { 0, 0, 1, 0xB3, 0x2D, 0x02, 0x40, 0x33, 0xFF, 0xFF, 0xE0, 0x18 };
        if (extension) file.AddRange(new byte[] { 0, 0, 1, 0xB5, 0x14, 0x8A, 0, 1, 0, 0 });
        file.AddRange(new byte[64]);
        var info = Probe([.. file]);
        Assert.Equal(VideoContainerKind.MpegVideoStream, info.Container);
        var track = Assert.Single(info.Tracks);
        Assert.Equal((expected, 720, 576), (track.Codec.Id, track.Width, track.Height));
    }

    // ============================================================================ Ogg ========

    [Fact]
    public void Ogg_beginning_pages_name_theora_vorbis_and_opus()
    {
        var theora = new byte[42];
        theora[0] = 0x80;
        "theora"u8.CopyTo(theora.AsSpan(1));
        theora[7] = 3; theora[8] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(theora.AsSpan(10), 40); // 640 / 16
        BinaryPrimitives.WriteUInt16BigEndian(theora.AsSpan(12), 30); // 480 / 16
        var vorbis = Concat([0x01], "vorbis"u8.ToArray(), new byte[23]);
        var opus = Concat("OpusHead"u8.ToArray(), new byte[11]);
        var file = Concat(Ogg.Page(theora, bos: true, serial: 1), Ogg.Page(vorbis, bos: true, serial: 2), Ogg.Page(opus, bos: true, serial: 3),
            Ogg.Page(new byte[30], bos: false, serial: 1));
        var info = Probe(file);
        Assert.Equal((VideoContainerKind.Ogg, VideoProbeStatus.Ok), (info.Container, info.Status));
        Assert.Equal(["theora", "vorbis", "opus"], info.Tracks.Select(x => x.Codec.Id).ToArray());
        Assert.Equal((640, 480), (info.Tracks[0].Width, info.Tracks[0].Height));
    }

    // ============================================================================ safety ========

    [Fact]
    public void Unknown_bytes_are_unrecognised_and_a_missing_file_is_unreadable()
    {
        Assert.Equal(VideoProbeStatus.Unrecognised, Probe(Enumerable.Repeat((byte)0x5A, 4096).ToArray()).Status);
        Assert.Equal(VideoProbeStatus.Unrecognised, Probe([1, 2]).Status);
        Assert.Equal(VideoProbeStatus.Unreadable, VideoContainerProbe.Probe(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".mp4")).Status);
    }

    [Fact]
    public void Truncated_and_corrupted_files_never_throw()
    {
        var samples = new[]
        {
            Mp4.File(Mp4.VideoTrack("vp09", 1920, 1080), Mp4.AudioTrack("mp4a", 0x40)),
            Mkv.File("webm", Mkv.Track(1, "V_VP9", 640, 360), Mkv.Track(2, "A_OPUS")),
            Avi.File("H264", 0x0055), Asf.File("WMV3", 0x0161), Ts.File(188, [(0x24, [])]), Ps.File(),
        };
        var random = new Random(504);
        foreach (var sample in samples)
        {
            for (var length = 0; length < sample.Length; length += Math.Max(1, sample.Length / 97))
                _ = Probe(sample[..length]);
            for (var round = 0; round < 200; round++)
            {
                var copy = (byte[])sample.Clone();
                for (var flips = 0; flips < 4; flips++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
                _ = Probe(copy);
            }
        }
    }

    [Fact]
    public void Huge_declared_sizes_and_deep_nesting_stay_bounded()
    {
        // A box claiming 4 GB, a 64-bit box claiming 2^62 bytes and moov nested far beyond the depth the probe walks.
        var claim = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, (byte)'m', (byte)'o', (byte)'o', (byte)'v' };
        var nested = Mp4.Box("trak", Array.Empty<byte>());
        for (var i = 0; i < 200; i++) nested = Mp4.Box("mdia", nested);
        var large = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(large, 1);
        "moov"u8.CopyTo(large.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(large.AsSpan(8), 1UL << 62);
        foreach (var file in new[] { Concat(claim, new byte[100]), Concat(Mp4.Box("ftyp", "isom"u8.ToArray()), Mp4.Box("moov", nested)), Concat(large, new byte[64]) })
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var info = Probe(file);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(VideoProbeStatus.Damaged, info.Status);
        }
    }

    [Fact]
    public void Probing_never_blocks_deleting_the_video()
    {
        var path = Path.Combine(Path.GetTempPath(), $"glide-probe-{Guid.NewGuid():N}.webm");
        File.WriteAllBytes(path, Mkv.File("webm", Mkv.Track(1, "V_VP9", 640, 360)));
        try
        {
            Assert.Equal("vp9", Assert.Single(VideoContainerProbe.Probe(path).Tracks).Codec.Id);
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ============================================================================ the message ========

    [Fact]
    public void Missing_video_codec_is_named_exactly()
    {
        var diagnosis = Diagnose(Mkv.File("webm", Mkv.Track(1, "V_VP9", 640, 360), Mkv.Track(2, "A_OPUS")), missing: ["vp9"]);
        Assert.Equal(VideoDiagnosisKind.MissingCodecs, diagnosis.Kind);
        Assert.Equal("This video uses the VP9 codec, which isn't installed on this PC.", diagnosis.Message);
        Assert.Equal(["vp9"], diagnosis.MissingCodecs.Select(x => x.Id).ToArray());
    }

    [Theory]
    [InlineData("hvc1", "This video uses the HEVC (H.265) codec, which isn't installed on this PC.")]
    [InlineData("av01", "This video uses the AV1 codec, which isn't installed on this PC.")]
    [InlineData("apcn", "This video uses the Apple ProRes codec, which isn't installed on this PC.")]
    public void Each_missing_video_codec_has_its_plain_name(string format, string message)
    {
        var codec = Probe(Mp4.File(Mp4.VideoTrack(format, 1920, 1080))).Tracks[0].Codec.Id;
        Assert.Equal(message, Diagnose(Mp4.File(Mp4.VideoTrack(format, 1920, 1080)), missing: [codec]).Message);
    }

    [Fact]
    public void Missing_audio_only_names_the_audio_codec()
    {
        var diagnosis = Diagnose(Mkv.File("webm", Mkv.Track(1, "V_VP9", 640, 360), Mkv.Track(2, "A_OPUS")), missing: ["opus"]);
        Assert.Equal("This video's audio uses the Opus codec, which isn't installed on this PC.", diagnosis.Message);
    }

    [Fact]
    public void Missing_video_and_audio_are_both_named()
    {
        var diagnosis = Diagnose(Mkv.File("matroska", Mkv.Track(1, "V_AV1", 640, 360), Mkv.Track(2, "A_OPUS")), missing: ["av1", "opus"]);
        Assert.Equal("This video uses the AV1 video codec and the Opus audio codec, which aren't installed on this PC.", diagnosis.Message);
        Assert.Equal(["av1", "opus"], diagnosis.MissingCodecs.Select(x => x.Id).ToArray());
    }

    [Fact]
    public void Only_codecs_windows_lacks_are_named_and_uncompressed_audio_is_never_probed()
    {
        var asked = new List<Guid>();
        var info = Probe(Ts.File(188, [(0x24, []), (0x81, []), (0x80, [])])); // HEVC, AC-3 (no LPCM: 188-byte packets)
        var diagnosis = VideoPlaybackDiagnoser.Diagnose(info, (subtype, audio) =>
        {
            asked.Add(subtype);
            return subtype != VideoCodecCatalog.Ac3.Subtype;
        });
        Assert.Equal("This video's audio uses the Dolby Digital (AC-3) codec, which isn't installed on this PC.", diagnosis.Message);
        Assert.Equal([VideoCodecCatalog.Hevc.Subtype, VideoCodecCatalog.Ac3.Subtype], asked);

        var pcm = Probe(Avi.File("H264", 0x0001));
        var pcmDiagnosis = VideoPlaybackDiagnoser.Diagnose(pcm, (subtype, audio) => audio ? throw new InvalidOperationException("PCM needs no decoder") : true);
        Assert.Equal(VideoDiagnosisKind.DecodersPresent, pcmDiagnosis.Kind);
    }

    [Fact]
    public void An_unknown_probe_result_counts_as_missing()
    {
        var diagnosis = VideoPlaybackDiagnoser.Diagnose(Probe(Mp4.File(Mp4.VideoTrack("vp09", 640, 360))), (_, _) => null);
        Assert.Equal("This video uses the VP9 codec, which isn't installed on this PC.", diagnosis.Message);
    }

    [Fact]
    public void When_every_decoder_exists_the_message_says_so_and_hints_at_oversized_frames()
    {
        var diagnosis = Diagnose(Mp4.File(Mp4.VideoTrack("avc1", 7680, 4320), Mp4.AudioTrack("mp4a", 0x40)), missing: []);
        Assert.Equal(VideoDiagnosisKind.DecodersPresent, diagnosis.Kind);
        Assert.Equal("Windows has the codecs this video uses (H.264 (AVC) video and AAC audio), but couldn't play it. The file may be damaged. "
                     + "Its picture size (7680 × 4320) may also be larger than this PC's video decoder supports.", diagnosis.Message);
        Assert.DoesNotContain("picture size", Diagnose(Mp4.File(Mp4.VideoTrack("avc1", 1920, 1080)), missing: []).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_damaged_and_audio_only_files_say_so()
    {
        Assert.Equal("This file isn't in a video format Glide recognises. It may be damaged, or it may not be a video.",
            Diagnose(Enumerable.Repeat((byte)0x33, 512).ToArray(), missing: []).Message);
        Assert.Equal("This MP4 file appears to be damaged or incomplete: Glide couldn't find its video track.",
            Diagnose(Concat(Mp4.Box("ftyp", "isom"u8.ToArray()), Mp4.Box("mdat", new byte[8])), missing: []).Message);
        var audioOnly = Diagnose(Mkv.File("webm", Mkv.Track(2, "A_OPUS")), missing: ["opus"]);
        Assert.Equal((VideoDiagnosisKind.NoVideoTrack, "This file has no video track, only Opus audio."), (audioOnly.Kind, audioOnly.Message));
    }

    [Fact]
    public void Messages_never_offer_a_download_link_or_store()
    {
        var files = new[]
        {
            Mkv.File("webm", Mkv.Track(1, "V_VP9", 640, 360), Mkv.Track(2, "A_OPUS")), Mp4.File(Mp4.VideoTrack("hvc1", 640, 360)),
            Mp4.File(Mp4.VideoTrack("avc1", 640, 360)), new byte[64], Asf.File("WMV3", 0x0161),
        };
        foreach (var file in files)
        foreach (var missing in new string[][] { [], ["vp9", "opus", "hevc", "wmv3", "wma"] })
        {
            var message = Diagnose(file, missing).Message;
            foreach (var forbidden in new[] { "http", "www.", "Store", "download", "extension", "FFmpeg" })
                Assert.DoesNotContain(forbidden, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ============================================================================ helpers ========

    private static VideoContainerInfo Probe(byte[] file)
    {
        using var stream = new MemoryStream(file, writable: false);
        return VideoContainerProbe.Probe(stream);
    }

    /// <summary>The mocked Media Foundation probe: every codec has a decoder except <paramref name="missing"/> (by id).</summary>
    private static VideoPlaybackDiagnosis Diagnose(byte[] file, string[] missing)
    {
        var info = Probe(file);
        var missingSubtypes = info.Tracks.Where(x => missing.Contains(x.Codec.Id)).Select(x => x.Codec.Subtype).ToHashSet();
        return VideoPlaybackDiagnoser.Diagnose(info, (subtype, _) => !missingSubtypes.Contains(subtype));
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

    private static class Mp4
    {
        public static byte[] Box(string type, params byte[][] payload)
        {
            var body = Concat(payload);
            var box = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.Latin1.GetBytes(type).CopyTo(box, 4);
            body.CopyTo(box, 8);
            return box;
        }

        public static byte[] File(params byte[][] tracks) => File("isom", tracks);

        public static byte[] File(string brand, params byte[][] tracks) =>
            Concat(Box("ftyp", Encoding.Latin1.GetBytes(brand), new byte[4], Encoding.Latin1.GetBytes(brand)),
                Box("moov", Box("mvhd", new byte[100]), Concat(tracks)), Box("mdat", new byte[32]));

        public static byte[] VideoTrack(string format, int width, int height, params byte[][] children)
        {
            var body = new byte[78];
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(24), (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(26), (ushort)height);
            return Track("vide", Box(format, body, Concat(children)));
        }

        public static byte[] AudioTrack(string format, int objectType, int version = 0, params byte[][] children)
        {
            var body = new byte[version == 1 ? 44 : 28];
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), (ushort)version);
            var extra = objectType >= 0 ? Esds(objectType) : [];
            return Track("soun", Box(format, body, extra, Concat(children)));
        }

        public static byte[] Esds(int objectType)
        {
            // ES_Descriptor(ES_ID, flags) > DecoderConfigDescriptor(objectTypeIndication, ...).
            byte[] decoderConfig = [0x04, 13, (byte)objectType, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
            return Box("esds", [0, 0, 0, 0], [0x03, (byte)(3 + decoderConfig.Length), 0, 1, 0], decoderConfig);
        }

        private static byte[] Track(string handler, byte[] sampleEntry)
        {
            var hdlr = Box("hdlr", new byte[8], Encoding.Latin1.GetBytes(handler), new byte[13]);
            var stsd = Box("stsd", [0, 0, 0, 0, 0, 0, 0, 1], sampleEntry);
            return Box("trak", Box("tkhd", new byte[84]), Box("mdia", Box("mdhd", new byte[24]), hdlr, Box("minf", Box("stbl", stsd, Box("stts", new byte[8])))));
        }
    }

    private static class Mkv
    {
        public static byte[] Element(uint id, params byte[][] payload)
        {
            var body = Concat(payload);
            var idBytes = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
            var element = new byte[idBytes + 8 + body.Length];
            for (var i = 0; i < idBytes; i++) element[i] = (byte)(id >> (8 * (idBytes - 1 - i)));
            element[idBytes] = 0x01; // 8-byte size
            for (var i = 0; i < 7; i++) element[idBytes + 1 + i] = (byte)((ulong)body.Length >> (8 * (6 - i)));
            body.CopyTo(element, idBytes + 8);
            return element;
        }

        private static byte[] UInt(uint id, ulong value) => Element(id, [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
        private static byte[] Text(uint id, string value) => Element(id, Encoding.ASCII.GetBytes(value));

        public static byte[] Track(int type, string codecId, int width = 0, int height = 0, byte[]? codecPrivate = null)
        {
            var fields = new List<byte[]> { UInt(0xD7, 1), UInt(0x83, (ulong)type), Text(0x86, codecId) };
            if (codecPrivate is not null) fields.Add(Element(0x63A2, codecPrivate));
            if (width > 0) fields.Add(Element(0xE0, UInt(0xB0, (ulong)width), UInt(0xBA, (ulong)height)));
            return Element(0xAE, [.. fields]);
        }

        public static byte[] File(string docType, params byte[][] tracks) => File(docType, false, tracks);

        public static byte[] File(string docType, bool tracksAfterCluster, params byte[][] tracks)
        {
            var header = Element(0x1A45DFA3, UInt(0x4286, 1), Text(0x4282, docType));
            var info = Element(0x1549A966, UInt(0x2AD7B1, 1000000));
            var tracksElement = Element(0x1654AE6B, tracks);
            var cluster = Element(0x1F43B675, UInt(0xE7, 0), Element(0xA3, new byte[16]));
            byte[] body;
            if (!tracksAfterCluster) body = Concat(info, tracksElement, cluster);
            else
            {
                // SeekHead (fixed size) > Seek(SeekID = Tracks, SeekPosition = offset from the segment's data start).
                var seekHeadLength = Element(0x114D9B74, Element(0x4DBB, Element(0x53AB, [0x16, 0x54, 0xAE, 0x6B]), UInt(0x53AC, 0))).Length;
                var position = (ulong)(seekHeadLength + info.Length + cluster.Length);
                var seekHead = Element(0x114D9B74, Element(0x4DBB, Element(0x53AB, [0x16, 0x54, 0xAE, 0x6B]), UInt(0x53AC, position)));
                body = Concat(seekHead, info, cluster, tracksElement);
            }
            // Unknown-size segment, as live/streamed WebM writes it.
            var segment = Concat([0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], body);
            return Concat(header, segment);
        }
    }

    private static class Avi
    {
        private static byte[] Chunk(string id, params byte[][] payload)
        {
            var body = Concat(payload);
            var chunk = new byte[8 + body.Length + (body.Length & 1)];
            Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)body.Length);
            body.CopyTo(chunk, 8);
            return chunk;
        }

        private static byte[] List(string type, params byte[][] chunks) => Chunk("LIST", Encoding.ASCII.GetBytes(type), Concat(chunks));

        public static byte[] File(string fourCc, int audioTag, int extensibleTag = 0)
        {
            var videoHeader = new byte[56];
            "vids"u8.CopyTo(videoHeader);
            Encoding.ASCII.GetBytes(fourCc).CopyTo(videoHeader, 4);
            var bitmapInfo = new byte[40];
            BinaryPrimitives.WriteInt32LittleEndian(bitmapInfo, 40);
            BinaryPrimitives.WriteInt32LittleEndian(bitmapInfo.AsSpan(4), 640);
            BinaryPrimitives.WriteInt32LittleEndian(bitmapInfo.AsSpan(8), -480);
            Encoding.ASCII.GetBytes(fourCc).CopyTo(bitmapInfo, 16);
            var audioHeader = new byte[56];
            "auds"u8.CopyTo(audioHeader);
            var waveFormat = new byte[extensibleTag != 0 ? 40 : 18];
            BinaryPrimitives.WriteUInt16LittleEndian(waveFormat, (ushort)audioTag);
            if (extensibleTag != 0) BinaryPrimitives.WriteUInt16LittleEndian(waveFormat.AsSpan(24), (ushort)extensibleTag);
            var hdrl = List("hdrl", Chunk("avih", new byte[56]),
                List("strl", Chunk("strh", videoHeader), Chunk("strf", bitmapInfo)),
                List("strl", Chunk("strh", audioHeader), Chunk("strf", waveFormat)));
            var movi = List("movi", Chunk("00dc", new byte[10]));
            var riffBody = Concat("AVI "u8.ToArray(), hdrl, movi);
            return Concat("RIFF"u8.ToArray(), BitConverter.GetBytes(riffBody.Length), riffBody);
        }
    }

    private static class Asf
    {
        private static byte[] Object(Guid id, params byte[][] payload)
        {
            var body = Concat(payload);
            var bytes = new byte[24 + body.Length];
            id.TryWriteBytes(bytes);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), (ulong)bytes.Length);
            body.CopyTo(bytes, 24);
            return bytes;
        }

        private static byte[] Stream(Guid type, byte[] specific)
        {
            var fixedPart = new byte[54];
            type.TryWriteBytes(fixedPart);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(40), (uint)specific.Length);
            return Object(new Guid("B7DC0791-A9B7-11CF-8EE6-00C00C205365"), fixedPart, specific);
        }

        public static byte[] File(string fourCc, int audioTag)
        {
            var video = new byte[11 + 40];
            BinaryPrimitives.WriteInt32LittleEndian(video, 1280);
            BinaryPrimitives.WriteInt32LittleEndian(video.AsSpan(4), 720);
            BinaryPrimitives.WriteUInt16LittleEndian(video.AsSpan(9), 40);
            Encoding.ASCII.GetBytes(fourCc).CopyTo(video, 11 + 16);
            var audio = new byte[18];
            BinaryPrimitives.WriteUInt16LittleEndian(audio, (ushort)audioTag);
            var streams = Concat(Stream(new Guid("BC19EFC0-5B4D-11CF-A8FD-00805F5C442B"), video),
                Stream(new Guid("F8699E40-5B4D-11CF-A8FD-00805F5C442B"), audio));
            var header = Object(new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C"), BitConverter.GetBytes(2), [1, 2], streams);
            var data = Object(new Guid("75B22636-668E-11CF-A6D9-00AA0062CE6C"), new byte[26]);
            return Concat(header, data);
        }
    }

    private static class Ts
    {
        public static byte[] Registration(string format) => Concat([0x05, 4], Encoding.ASCII.GetBytes(format));

        public static byte[] File(int packetSize, (byte Type, byte[] Descriptors)[] streams)
        {
            byte[] pat = [0x00, 0xB0, 13, 0, 1, 0xC1, 0, 0, 0, 1, 0xE1, 0x00, 0, 0, 0, 0];
            var entries = new List<byte>();
            var pid = 0x101;
            foreach (var (type, descriptors) in streams)
            {
                entries.AddRange([type, (byte)(0xE0 | pid >> 8), (byte)pid, (byte)(0xF0 | descriptors.Length >> 8), (byte)descriptors.Length]);
                entries.AddRange(descriptors);
                pid++;
            }
            var pmtBody = Concat([0, 1, 0xC1, 0, 0, 0xE1, 0x01, 0xF0, 0], [.. entries], [0, 0, 0, 0]);
            var pmt = Concat([0x02, (byte)(0xB0 | pmtBody.Length >> 8), (byte)pmtBody.Length], pmtBody);
            var packets = new List<byte[]> { Packet(0, pat, packetSize), Packet(0x100, pmt, packetSize) };
            for (var i = 0; i < 6; i++) packets.Add(Packet(0x1FFF, [], packetSize));
            return Concat([.. packets]);
        }

        private static byte[] Packet(int pid, byte[] section, int packetSize)
        {
            var packet = Enumerable.Repeat((byte)0xFF, 188).ToArray();
            packet[0] = 0x47;
            packet[1] = (byte)((section.Length > 0 ? 0x40 : 0) | pid >> 8);
            packet[2] = (byte)pid;
            packet[3] = 0x10;
            if (section.Length > 0)
            {
                packet[4] = 0; // pointer field
                section.CopyTo(packet, 5);
            }
            return packetSize == 192 ? Concat(new byte[4], packet) : packet;
        }
    }

    private static class Ps
    {
        public static byte[] File()
        {
            byte[] pack = [0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 0x01, 0x89, 0xC3, 0xF8];
            var sequence = new byte[] { 0, 0, 1, 0xB3, 0x2D, 0x02, 0x40, 0x33, 0xFF, 0xFF, 0xE0, 0x18 };
            var video = Pes(0xE0, Concat([0x80, 0x80, 0x05, 0x21, 0, 1, 0, 1], sequence, new byte[20]));
            var audio = Pes(0xC0, Concat([0x80, 0x80, 0x05, 0x21, 0, 1, 0, 1], [0xFF, 0xFD], new byte[20]));
            var ac3 = Pes(0xBD, Concat([0x80, 0x80, 0x05, 0x21, 0, 1, 0, 1], [0x80, 1, 0, 1], new byte[20]));
            return Concat(pack, video, pack, audio, pack, ac3, [0, 0, 1, 0xB9]);
        }

        private static byte[] Pes(byte id, byte[] payload) =>
            Concat([0, 0, 1, id, (byte)(payload.Length >> 8), (byte)payload.Length], payload);
    }

    private static class Ogg
    {
        public static byte[] Page(byte[] packet, bool bos, int serial)
        {
            var header = new byte[27];
            "OggS"u8.CopyTo(header);
            header[5] = (byte)(bos ? 0x02 : 0x00);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), serial);
            var segments = new List<byte>();
            var remaining = packet.Length;
            do { segments.Add((byte)Math.Min(255, remaining)); remaining -= 255; } while (remaining >= 0);
            header[26] = (byte)segments.Count;
            return Concat(header, [.. segments], packet);
        }
    }
}

/// <summary>The old FFmpeg decoder pack is gone from code, build scripts and the native bridge (5.0.4).</summary>
[Trait("Area", "Video")]
public sealed class NoBundledVideoDecoderTests
{
    // The only source allowed to name it: the cleanup that deletes what older versions downloaded.
    private static readonly string[] Allowed = [Path.Combine("src", "Glide.Core", "Reset", "LegacyVideoDecoderCleanup.cs")];

    [Fact]
    public void No_source_or_build_script_mentions_ffmpeg()
    {
        var root = SourceText.RepoRoot;
        var files = new List<string>();
        foreach (var folder in new[] { "src", Path.Combine("native", "Glide.Native"), "tools", "installer" })
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory)) continue;
            files.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(Path.GetRelativePath(root, f)))
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".cs" or ".axaml" or ".csproj" or ".props" or ".targets" or ".json"
                    or ".cpp" or ".h" or ".txt" or ".ps1" or ".py" or ".cmd" or ".bat" or ".iss" or ".md"));
        }
        files.AddRange(new[] { "build.cmd", "Directory.Build.props", "Glide.sln", ".gitignore" }.Select(x => Path.Combine(root, x)).Where(File.Exists));

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file);
            if (Allowed.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            // libheif's own optional FFmpeg plugin is switched OFF when the image codec provider is built.
            var text = File.ReadAllText(file).Replace("-DWITH_FFMPEG_DECODER=OFF", string.Empty, StringComparison.Ordinal);
            if (text.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) offenders.Add(relative);
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_ffmpeg_sources_scripts_and_notices_are_deleted()
    {
        var root = SourceText.RepoRoot;
        foreach (var path in new[]
                 {
                     Path.Combine("native", "Glide.Native", "GlideVideoFfmpeg.cpp"), Path.Combine("src", "Glide.Imaging", "VideoDecoderPack.cs"),
                     Path.Combine("tools", "fetch-ffmpeg.ps1"), Path.Combine("tools", "package-ffmpeg-pack.ps1"),
                     Path.Combine("native", "third_party", "FFMPEG-NOTICE.txt"),
                 })
            Assert.False(File.Exists(Path.Combine(root, path)), path);
        foreach (var path in new[] { Path.Combine("native", "third_party", "ffmpeg"), Path.Combine("native", "third_party", "ffmpeg-licenses") })
            Assert.False(Directory.Exists(Path.Combine(root, path)), path);
    }

    private static bool IsBuildOutput(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj" or "build" or ".artifacts" or "out");
    }
}

/// <summary>Leftovers of the decoder older versions downloaded are removed on startup idle and by Reset's caches item.</summary>
[Trait("Area", "Video")]
public sealed class LegacyVideoDecoderCleanupTests
{
    [Fact]
    public void Removes_only_the_old_decoder_folders()
    {
        using var s = new ResetScratch();
        s.Write(s.Local, @"ffmpeg-9.0.2\avcodec-63.dll");
        s.Write(s.Local, @"ffmpeg-9.0.2\.glide-verified");
        s.Write(s.Local, @"ffmpeg-download-0123abcd\pack.zip");
        var keepFile = s.Write(s.Local, "ffmpeg-notes.txt"); // a file, not a decoder folder
        var keepDiagnostics = s.Write(s.Local, @"Startup Diagnostics\startup.log");
        var keepSettings = s.Write(s.Local, "glide.settings.json");

        Assert.Equal(2, LegacyVideoDecoderCleanup.RemoveLeftovers(s.Local));

        Assert.Empty(Directory.EnumerateDirectories(s.Local, "ffmpeg-*"));
        foreach (var kept in new[] { keepFile, keepDiagnostics, keepSettings }) Assert.True(File.Exists(kept), kept);
        Assert.Equal(0, LegacyVideoDecoderCleanup.RemoveLeftovers(s.Local)); // nothing left: one listing, no work
    }

    [Fact]
    public void A_missing_folder_or_locked_file_never_throws()
    {
        Assert.Equal(0, LegacyVideoDecoderCleanup.RemoveLeftovers(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid())));
        using var s = new ResetScratch();
        var locked = s.Write(s.Local, @"ffmpeg-9.0.2\avcodec-63.dll");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(0, LegacyVideoDecoderCleanup.RemoveLeftovers(s.Local));
        Assert.Equal(1, LegacyVideoDecoderCleanup.RemoveLeftovers(s.Local)); // retried on a later start
    }

    [Fact]
    public void Startup_schedules_the_cleanup_off_the_cold_path()
    {
        var startup = SourceText.Read("src/Glide.App/MainWindow.Startup.cs");
        Assert.Contains("ScheduleLegacyVideoDecoderCleanup();", startup, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(30)", startup, StringComparison.Ordinal);
        Assert.Contains("LegacyVideoDecoderCleanup.RemoveLeftovers", startup, StringComparison.Ordinal);
    }
}
