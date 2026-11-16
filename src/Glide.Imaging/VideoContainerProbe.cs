using System.Buffers.Binary;
using System.Text;

namespace Glide.Imaging;

public enum VideoContainerKind
{
    Unknown, Mp4, QuickTime, Matroska, WebM, Avi, Asf, MpegTransportStream, MpegProgramStream, MpegVideoStream, Ogg,
}

public enum VideoProbeStatus
{
    /// <summary>The container was read and its tracks listed.</summary>
    Ok,
    /// <summary>Not a video container Glide recognises.</summary>
    Unrecognised,
    /// <summary>A recognised container whose track list is missing or unreadable (truncated, corrupt).</summary>
    Damaged,
    /// <summary>The file could not be opened or read.</summary>
    Unreadable,
}

/// <summary>One audio or video track found in a container.</summary>
/// <param name="Tag">The container's own identifier for the codec (MP4 sample entry, Matroska CodecID, FourCC...).</param>
public sealed record VideoTrack(VideoTrackKind Kind, VideoCodec Codec, string Tag, int Width = 0, int Height = 0);

public sealed record VideoContainerInfo(VideoContainerKind Container, VideoProbeStatus Status, IReadOnlyList<VideoTrack> Tracks)
{
    public string ContainerName => Container switch
    {
        VideoContainerKind.Mp4 => "MP4",
        VideoContainerKind.QuickTime => "QuickTime (MOV)",
        VideoContainerKind.Matroska => "Matroska (MKV)",
        VideoContainerKind.WebM => "WebM",
        VideoContainerKind.Avi => "AVI",
        VideoContainerKind.Asf => "Windows Media (WMV/ASF)",
        VideoContainerKind.MpegTransportStream => "MPEG transport stream",
        VideoContainerKind.MpegProgramStream => "MPEG program stream",
        VideoContainerKind.MpegVideoStream => "MPEG video",
        VideoContainerKind.Ogg => "Ogg",
        _ => "video",
    };
}

/// <summary>
/// Reads just enough of a video container to list its tracks' codecs: MP4/MOV/3GP (sample entries), Matroska/WebM
/// (CodecID), AVI (stream formats), ASF/WMV (stream properties), MPEG transport streams (PMT stream types),
/// MPEG program streams and elementary MPEG video, and Ogg (first packets). Used only after Windows failed to play a
/// video, to say which codec is missing.
/// <para>
/// Safety: the file is opened read-only with full sharing (it never blocks deleting or moving it); every read is bounded
/// (per-box/element size caps, nesting depth, element counts and total scan length); nothing throws. A malformed file
/// yields <see cref="VideoProbeStatus.Damaged"/> or <see cref="VideoProbeStatus.Unrecognised"/>.
/// </para>
/// </summary>
public static class VideoContainerProbe
{
    private const int MaxDepth = 8;
    private const int MaxChildren = 1024;
    private const int MaxSampleDescriptionBytes = 1 << 20;
    private const int MaxHeaderBytes = 4 << 20;
    private const long MaxTransportScanBytes = 8L << 20;
    private const int MaxProgramScanBytes = 2 << 20;
    private const int MaxTracks = 64;

    public static VideoContainerInfo Probe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
            return Probe(stream);
        }
        catch (Exception)
        {
            return new VideoContainerInfo(VideoContainerKind.Unknown, VideoProbeStatus.Unreadable, []);
        }
    }

    public static VideoContainerInfo Probe(Stream stream)
    {
        var kind = VideoContainerKind.Unknown;
        var tracks = new List<VideoTrack>();
        try
        {
            if (!stream.CanSeek || !stream.CanRead) return new VideoContainerInfo(kind, VideoProbeStatus.Unreadable, []);
            var reader = new ProbeReader(stream);
            Span<byte> head = stackalloc byte[16];
            var headLength = reader.Read(0, head);
            head = head[..headLength];
            if (headLength < 4) return new VideoContainerInfo(kind, VideoProbeStatus.Unrecognised, []);

            if (IsMp4(head)) { kind = VideoContainerKind.Mp4; Mp4.Parse(reader, tracks, ref kind); }
            else if (BinaryPrimitives.ReadUInt32BigEndian(head) == 0x1A45DFA3) { kind = VideoContainerKind.Matroska; Matroska.Parse(reader, tracks, ref kind); }
            else if (headLength >= 12 && head[..4].SequenceEqual("RIFF"u8) && (head[8..12].SequenceEqual("AVI "u8) || head[8..12].SequenceEqual("AVIX"u8)))
            { kind = VideoContainerKind.Avi; Avi.Parse(reader, tracks); }
            else if (headLength >= 16 && new Guid(head[..16]) == Asf.HeaderObject) { kind = VideoContainerKind.Asf; Asf.Parse(reader, tracks); }
            else if (head[..4].SequenceEqual("OggS"u8)) { kind = VideoContainerKind.Ogg; Ogg.Parse(reader, tracks); }
            else if (head[0] == 0 && head[1] == 0 && head[2] == 1 && head[3] == 0xBA) { kind = VideoContainerKind.MpegProgramStream; MpegProgram.Parse(reader, tracks); }
            else if (head[0] == 0 && head[1] == 0 && head[2] == 1 && head[3] == 0xB3) { kind = VideoContainerKind.MpegVideoStream; MpegProgram.ParseElementaryVideo(reader, tracks); }
            else if (MpegTransport.DetectPacket(reader, out var packetSize, out var syncOffset))
            { kind = VideoContainerKind.MpegTransportStream; MpegTransport.Parse(reader, packetSize, syncOffset, tracks); }
            else return new VideoContainerInfo(kind, VideoProbeStatus.Unrecognised, []);
        }
        catch (Exception)
        {
            // Malformed input (or a read error) mid-parse: report what was found so far.
            if (kind == VideoContainerKind.Unknown) return new VideoContainerInfo(kind, VideoProbeStatus.Unrecognised, []);
        }
        var distinct = tracks.Take(MaxTracks).ToArray();
        return new VideoContainerInfo(kind, distinct.Length == 0 ? VideoProbeStatus.Damaged : VideoProbeStatus.Ok, distinct);
    }

    private static bool IsMp4(ReadOnlySpan<byte> head)
    {
        if (head.Length < 8) return false;
        var type = head.Slice(4, 4);
        return type.SequenceEqual("ftyp"u8) || type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8) || type.SequenceEqual("wide"u8)
               || type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8) || type.SequenceEqual("pnot"u8) || type.SequenceEqual("styp"u8);
    }

    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private static void Add(List<VideoTrack> tracks, VideoTrack track)
    {
        if (tracks.Count < MaxTracks) tracks.Add(track);
    }

    /// <summary>Bounded random-access reads with a small window cache (header parsing reads many tiny fields).</summary>
    internal sealed class ProbeReader
    {
        private readonly Stream _stream;
        private readonly byte[] _window = new byte[64 * 1024];
        private long _windowStart = -1;
        private int _windowLength;

        public ProbeReader(Stream stream)
        {
            _stream = stream;
            Length = stream.Length;
        }

        public long Length { get; }

        /// <summary>Reads up to <paramref name="destination"/>.Length bytes at <paramref name="position"/>; returns how many.</summary>
        public int Read(long position, Span<byte> destination)
        {
            if (position < 0 || position >= Length || destination.IsEmpty) return 0;
            var count = (int)Math.Min(destination.Length, Length - position);
            if (count > _window.Length)
            {
                _stream.Position = position;
                var done = 0;
                while (done < count)
                {
                    var read = _stream.Read(destination.Slice(done, count - done));
                    if (read <= 0) break;
                    done += read;
                }
                return done;
            }
            if (_windowStart < 0 || position < _windowStart || position + count > _windowStart + _windowLength)
            {
                _stream.Position = position;
                _windowStart = position;
                _windowLength = 0;
                var want = (int)Math.Min(_window.Length, Length - position);
                while (_windowLength < want)
                {
                    var read = _stream.Read(_window, _windowLength, want - _windowLength);
                    if (read <= 0) break;
                    _windowLength += read;
                }
                count = Math.Min(count, _windowLength);
            }
            _window.AsSpan((int)(position - _windowStart), count).CopyTo(destination);
            return count;
        }

        public bool TryRead(long position, Span<byte> destination) => Read(position, destination) == destination.Length;

        /// <summary>Reads min(<paramref name="count"/>, <paramref name="cap"/>) bytes (fewer at end of file).</summary>
        public byte[] ReadBlock(long position, long count, int cap)
        {
            var length = (int)Math.Clamp(Math.Min(count, Length - position), 0, cap);
            var buffer = new byte[length];
            var read = Read(position, buffer);
            return read == length ? buffer : buffer[..read];
        }

        public bool TryU8(long position, out byte value)
        {
            Span<byte> b = stackalloc byte[1];
            var ok = TryRead(position, b);
            value = b[0];
            return ok;
        }
    }

    // ===================================================================================== MP4 / MOV / 3GP
    private static class Mp4
    {
        public static void Parse(ProbeReader reader, List<VideoTrack> tracks, ref VideoContainerKind kind)
        {
            Span<byte> brand = stackalloc byte[4];
            foreach (var box in Boxes(reader, 0, reader.Length))
            {
                if (box.Type == "ftyp" && reader.TryRead(box.Payload, brand) && brand.SequenceEqual("qt  "u8)) kind = VideoContainerKind.QuickTime;
                else if (box.Type == "moov")
                {
                    foreach (var trak in Boxes(reader, box.Payload, box.End))
                        if (trak.Type == "trak") ParseTrack(reader, trak, tracks);
                    return;
                }
            }
        }

        private static void ParseTrack(ProbeReader reader, Box trak, List<VideoTrack> tracks)
        {
            var mdia = Find(reader, trak, "mdia");
            if (mdia is null) return;
            string? handler = null;
            if (Find(reader, mdia.Value, "hdlr") is { } hdlr)
            {
                Span<byte> type = stackalloc byte[4];
                if (reader.TryRead(hdlr.Payload + 8, type)) handler = Ascii(type);
            }
            var kind = handler switch { "vide" => VideoTrackKind.Video, "soun" => VideoTrackKind.Audio, _ => (VideoTrackKind?)null };
            if (kind is null) return;
            var stsd = Find(reader, mdia.Value, "minf", "stbl", "stsd");
            if (stsd is null) return;
            var data = reader.ReadBlock(stsd.Value.Payload, stsd.Value.End - stsd.Value.Payload, MaxSampleDescriptionBytes);
            // FullBox header (4) + entry_count (4), then the first sample entry: size (4) + format (4) + body.
            if (data.Length < 16) return;
            var entrySize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)), (uint)(data.Length - 8));
            if (entrySize < 8) return;
            var entry = data.AsSpan(8, entrySize);
            var format = Ascii(entry.Slice(4, 4));
            var body = entry[8..];
            if (kind == VideoTrackKind.Video)
            {
                int width = 0, height = 0;
                if (body.Length >= 28)
                {
                    width = BinaryPrimitives.ReadUInt16BigEndian(body[24..]);
                    height = BinaryPrimitives.ReadUInt16BigEndian(body[26..]);
                }
                var children = body.Length > 78 ? body[78..] : Span<byte>.Empty;
                if (format == "encv" && FindFrma(children) is { } original) format = original;
                Add(tracks, new VideoTrack(VideoTrackKind.Video, VideoCodecFor(format, children), format, width, height));
            }
            else
            {
                var version = body.Length >= 10 ? BinaryPrimitives.ReadUInt16BigEndian(body[8..]) : 0;
                var fixedLength = version switch { 1 => 44, 2 => 64, _ => 28 };
                var children = body.Length > fixedLength ? body[fixedLength..] : Span<byte>.Empty;
                if (format == "enca" && FindFrma(children) is { } original) format = original;
                Add(tracks, new VideoTrack(VideoTrackKind.Audio, AudioCodecFor(format, children), format));
            }
        }

        private static VideoCodec VideoCodecFor(string format, ReadOnlySpan<byte> children)
        {
            switch (format)
            {
                case "avc1": case "avc2": case "avc3": case "avc4": case "dva1": case "dvav": return VideoCodecCatalog.H264;
                case "hvc1": case "hev1": case "dvh1": case "dvhe": return VideoCodecCatalog.Hevc;
                case "vvc1": case "vvi1": return VideoCodecCatalog.Vvc;
                case "av01": case "dav1": return VideoCodecCatalog.Av1;
                case "vp08": return VideoCodecCatalog.Vp8;
                case "vp09": return VideoCodecCatalog.Vp9;
                case "s263": case "h263": case "H263": return VideoCodecCatalog.H263;
                case "mjpa": case "mjpb": case "jpeg": case "MJPG": return VideoCodecCatalog.MotionJpeg;
                case "apcn": case "apch": case "apcs": case "apco": case "ap4h": case "ap4x": return VideoCodecCatalog.ProRes(format);
                case "m2v1": case "mp2v": case "hdv1": case "hdv2": case "hdv3": case "hdv5": case "hdv6": case "hdv7": case "hdv8": case "hdv9": case "hdva": case "mx3n": case "mx3p": case "mx4n": case "mx4p": case "mx5n": case "mx5p": return VideoCodecCatalog.Mpeg2;
                case "m1v1": case "mp1v": return VideoCodecCatalog.Mpeg1;
                case "dvc ": case "dvcp": case "dvpp": case "dv5n": case "dv5p": case "dvh2": case "dvh3": case "dvh5": case "dvh6": case "dvhp": case "dvhq": return VideoCodecCatalog.Dv;
                case "raw ": case "2vuy": case "yuv2": case "v210": case "v308": case "v408": case "v410": case "yuvs": return VideoCodecCatalog.UncompressedVideo;
                case "mp4v":
                    return ObjectType(children) switch
                    {
                        >= 0x60 and <= 0x65 => VideoCodecCatalog.Mpeg2,
                        0x6A => VideoCodecCatalog.Mpeg1,
                        0x6C => VideoCodecCatalog.MotionJpeg,
                        _ => VideoCodecCatalog.Mpeg4Part2,
                    };
            }
            if (format.StartsWith("xdv", StringComparison.Ordinal)) return VideoCodecCatalog.Mpeg2; // XDCAM
            return VideoCodecCatalog.UnknownVideo(format);
        }

        private static VideoCodec AudioCodecFor(string format, ReadOnlySpan<byte> children)
        {
            switch (format)
            {
                case "mp4a":
                    return ObjectType(children) switch
                    {
                        0x69 or 0x6B => VideoCodecCatalog.Mp3,
                        0xA5 => VideoCodecCatalog.Ac3,
                        0xA6 => VideoCodecCatalog.Eac3,
                        0xA9 or 0xAA or 0xAB or 0xAC => VideoCodecCatalog.Dts,
                        0xAD => VideoCodecCatalog.Opus,
                        _ => VideoCodecCatalog.Aac,
                    };
                case "ac-3": case "sac3": return VideoCodecCatalog.Ac3;
                case "ec-3": return VideoCodecCatalog.Eac3;
                case "ac-4": return VideoCodecCatalog.Ac4;
                case "mlpa": return VideoCodecCatalog.TrueHd;
                case "dtsc": case "dtsh": case "dtsl": case "dtse": case "dtsx": case "dtsy": return VideoCodecCatalog.Dts;
                case "Opus": case "opus": return VideoCodecCatalog.Opus;
                case "fLaC": return VideoCodecCatalog.Flac;
                case "alac": return VideoCodecCatalog.Alac;
                case ".mp3": case "ms\0U": return VideoCodecCatalog.Mp3;
                case ".mp2": case ".mp1": return VideoCodecCatalog.MpegAudio;
                case "samr": return VideoCodecCatalog.AmrNb;
                case "sawb": return VideoCodecCatalog.AmrWb;
                case "lpcm": case "twos": case "sowt": case "in24": case "in32": case "fl32": case "fl64": case "raw ": case "ipcm": case "fpcm": case "NONE": case "ulaw": case "alaw":
                    return VideoCodecCatalog.Pcm;
            }
            return VideoCodecCatalog.Unnamed(VideoTrackKind.Audio, format);
        }

        /// <summary>objectTypeIndication from the esds box (also inside a QuickTime 'wave' box); -1 when absent.</summary>
        private static int ObjectType(ReadOnlySpan<byte> children)
        {
            var esds = FindInMemory(children, "esds", 0);
            if (esds.IsEmpty || esds.Length < 5) return -1;
            var p = 4; // FullBox version/flags
            for (var guard = 0; guard < 4 && p < esds.Length; guard++)
            {
                var tag = esds[p++];
                var length = 0;
                for (var i = 0; i < 4 && p < esds.Length; i++)
                {
                    var b = esds[p++];
                    length = length << 7 | (b & 0x7F);
                    if ((b & 0x80) == 0) break;
                }
                if (tag == 0x04) return p < esds.Length ? esds[p] : -1;
                if (tag != 0x03) return -1;
                if (p + 3 > esds.Length) return -1;
                var flags = esds[p + 2];
                p += 3;
                if ((flags & 0x80) != 0) p += 2;
                if ((flags & 0x40) != 0 && p < esds.Length) p += 1 + esds[p];
                if ((flags & 0x20) != 0) p += 2;
            }
            return -1;
        }

        private static string? FindFrma(ReadOnlySpan<byte> children)
        {
            var sinf = FindInMemory(children, "sinf", 0);
            var frma = sinf.IsEmpty ? sinf : FindInMemory(sinf, "frma", 0);
            return frma.Length >= 4 ? Ascii(frma[..4]) : null;
        }

        /// <summary>Payload of the first box of <paramref name="type"/> in an in-memory box list, descending into 'wave'/'sinf'.</summary>
        private static ReadOnlySpan<byte> FindInMemory(ReadOnlySpan<byte> data, string type, int depth)
        {
            var p = 0;
            for (var count = 0; p + 8 <= data.Length && count < MaxChildren; count++)
            {
                var size = (long)BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
                var name = Ascii(data.Slice(p + 4, 4));
                if (size == 0) size = data.Length - p;
                if (size < 8 || size > data.Length - p) return [];
                var payload = data.Slice(p + 8, (int)size - 8);
                if (name == type) return payload;
                if (depth < 3 && name is "wave" or "sinf")
                {
                    var nested = FindInMemory(payload, type, depth + 1);
                    if (!nested.IsEmpty) return nested;
                }
                p += (int)size;
            }
            return [];
        }

        private readonly record struct Box(string Type, long Payload, long End);

        private static Box? Find(ProbeReader reader, Box parent, params string[] path)
        {
            var current = parent;
            foreach (var name in path)
            {
                Box? next = null;
                foreach (var child in Boxes(reader, current.Payload, current.End))
                    if (child.Type == name) { next = child; break; }
                if (next is null) return null;
                current = next.Value;
            }
            return current;
        }

        private static IEnumerable<Box> Boxes(ProbeReader reader, long start, long end)
        {
            var header = new byte[16];
            var position = start;
            for (var count = 0; position + 8 <= end && count < MaxChildren; count++)
            {
                if (!reader.TryRead(position, header.AsSpan(0, 8))) yield break;
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Ascii(header.AsSpan(4, 4));
                long headerSize = 8;
                if (size == 1)
                {
                    if (!reader.TryRead(position + 8, header.AsSpan(8, 8))) yield break;
                    var large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                    size = large > long.MaxValue ? long.MaxValue : (long)large;
                    headerSize = 16;
                }
                else if (size == 0) size = end - position;
                if (size < headerSize) yield break;
                var boxEnd = size > end - position ? end : position + size; // truncated file: clamp
                yield return new Box(type, position + headerSize, boxEnd);
                position = boxEnd;
            }
        }
    }

    // ===================================================================================== Matroska / WebM
    private static class Matroska
    {
        private const uint EbmlHeader = 0x1A45DFA3, DocType = 0x4282, Segment = 0x18538067, SeekHead = 0x114D9B74, Seek = 0x4DBB,
            SeekId = 0x53AB, SeekPosition = 0x53AC, Tracks = 0x1654AE6B, TrackEntry = 0xAE, TrackType = 0x83, CodecId = 0x86,
            CodecPrivate = 0x63A2, VideoSettings = 0xE0, PixelWidth = 0xB0, PixelHeight = 0xBA, Cluster = 0x1F43B675;

        private readonly record struct Element(uint Id, long Data, long Size, bool UnknownSize)
        {
            public long End => Data + Size;
        }

        public static void Parse(ProbeReader reader, List<VideoTrack> tracks, ref VideoContainerKind kind)
        {
            long position = 0;
            if (!ReadElement(reader, ref position, reader.Length, out var header) || header.Id != EbmlHeader || header.UnknownSize) return;
            foreach (var child in Children(reader, header.Data, header.End))
                if (child.Id == DocType && Text(reader, child) == "webm") kind = VideoContainerKind.WebM;

            position = header.End;
            Element segment = default;
            var found = false;
            for (var i = 0; i < 16 && ReadElement(reader, ref position, reader.Length, out var top); i++)
            {
                if (top.Id == Segment) { segment = top; found = true; break; }
                if (top.UnknownSize) return;
                position = top.End;
            }
            if (!found) return;
            var segmentEnd = segment.UnknownSize ? reader.Length : Math.Min(segment.End, reader.Length);

            long tracksPosition = -1;
            position = segment.Data;
            for (var i = 0; i < 256 && ReadElement(reader, ref position, segmentEnd, out var element); i++)
            {
                if (element.Id == Tracks) { ParseTracks(reader, element, tracks); return; }
                if (element.Id == SeekHead) tracksPosition = SeekTo(reader, element, Tracks, segment.Data);
                if (element.Id == Cluster || element.UnknownSize) break;
                position = element.End;
            }
            if (tracksPosition >= 0 && tracksPosition < segmentEnd)
            {
                position = tracksPosition;
                if (ReadElement(reader, ref position, segmentEnd, out var element) && element.Id == Tracks) ParseTracks(reader, element, tracks);
            }
        }

        private static long SeekTo(ProbeReader reader, Element seekHead, uint target, long segmentData)
        {
            foreach (var seek in Children(reader, seekHead.Data, seekHead.End))
            {
                if (seek.Id != Seek) continue;
                ulong id = 0, offset = ulong.MaxValue;
                foreach (var field in Children(reader, seek.Data, seek.End))
                {
                    if (field.Id == SeekId) id = UInt(reader, field);
                    else if (field.Id == SeekPosition) offset = UInt(reader, field);
                }
                if (id == target && offset < (ulong)reader.Length) return segmentData + (long)offset;
            }
            return -1;
        }

        private static void ParseTracks(ProbeReader reader, Element tracksElement, List<VideoTrack> tracks)
        {
            foreach (var entry in Children(reader, tracksElement.Data, tracksElement.End))
            {
                if (entry.Id != TrackEntry) continue;
                ulong type = 0;
                string codecId = string.Empty;
                byte[] codecPrivate = [];
                int width = 0, height = 0;
                foreach (var field in Children(reader, entry.Data, entry.End))
                {
                    switch (field.Id)
                    {
                        case TrackType: type = UInt(reader, field); break;
                        case CodecId: codecId = Text(reader, field); break;
                        case CodecPrivate: codecPrivate = reader.ReadBlock(field.Data, field.Size, 64 * 1024); break;
                        case VideoSettings:
                            foreach (var video in Children(reader, field.Data, field.End))
                            {
                                if (video.Id == PixelWidth) width = (int)Math.Min(UInt(reader, video), int.MaxValue);
                                else if (video.Id == PixelHeight) height = (int)Math.Min(UInt(reader, video), int.MaxValue);
                            }
                            break;
                    }
                }
                if (type == 1) Add(tracks, new VideoTrack(VideoTrackKind.Video, VideoCodecFor(codecId, codecPrivate), codecId, width, height));
                else if (type == 2) Add(tracks, new VideoTrack(VideoTrackKind.Audio, AudioCodecFor(codecId, codecPrivate), codecId));
            }
        }

        private static VideoCodec VideoCodecFor(string codecId, byte[] codecPrivate)
        {
            switch (codecId)
            {
                case "V_VP8": return VideoCodecCatalog.Vp8;
                case "V_VP9": return VideoCodecCatalog.Vp9;
                case "V_AV1": return VideoCodecCatalog.Av1;
                case "V_MPEG4/ISO/AVC": return VideoCodecCatalog.H264;
                case "V_MPEGH/ISO/HEVC": return VideoCodecCatalog.Hevc;
                case "V_MPEGI/ISO/VVC": return VideoCodecCatalog.Vvc;
                case "V_MPEG4/ISO/SP": case "V_MPEG4/ISO/ASP": case "V_MPEG4/ISO/AP": return VideoCodecCatalog.Mpeg4Part2;
                case "V_MPEG4/MS/V3": return VideoCodecCatalog.MsMpeg4V3;
                case "V_MPEG1": return VideoCodecCatalog.Mpeg1;
                case "V_MPEG2": return VideoCodecCatalog.Mpeg2;
                case "V_THEORA": return VideoCodecCatalog.Theora;
                case "V_MJPEG": return VideoCodecCatalog.MotionJpeg;
                case "V_UNCOMPRESSED": return VideoCodecCatalog.UncompressedVideo;
                case "V_MS/VFW/FOURCC":
                    // CodecPrivate is a BITMAPINFOHEADER; biCompression is at offset 16.
                    if (codecPrivate.Length >= 20) return Avi.VideoFromCompression(codecPrivate.AsSpan(16, 4));
                    break;
                case "V_PRORES":
                    return VideoCodecCatalog.ProRes(codecPrivate.Length >= 4 ? Ascii(codecPrivate.AsSpan(0, 4)) : "apcn");
            }
            if (codecId.StartsWith("V_REAL/", StringComparison.Ordinal)) return new VideoCodec("realvideo", VideoTrackKind.Video, "RealVideo", Guid.Empty);
            return VideoCodecCatalog.Unnamed(VideoTrackKind.Video, codecId);
        }

        private static VideoCodec AudioCodecFor(string codecId, byte[] codecPrivate)
        {
            switch (codecId)
            {
                case "A_OPUS": return VideoCodecCatalog.Opus;
                case "A_VORBIS": return VideoCodecCatalog.Vorbis;
                case "A_FLAC": return VideoCodecCatalog.Flac;
                case "A_AC3": case "A_AC3/BSID9": case "A_AC3/BSID10": return VideoCodecCatalog.Ac3;
                case "A_EAC3": return VideoCodecCatalog.Eac3;
                case "A_AC4": return VideoCodecCatalog.Ac4;
                case "A_TRUEHD": case "A_MLP": return VideoCodecCatalog.TrueHd;
                case "A_MPEG/L3": return VideoCodecCatalog.Mp3;
                case "A_MPEG/L2": case "A_MPEG/L1": return VideoCodecCatalog.MpegAudio;
                case "A_ALAC": return VideoCodecCatalog.Alac;
                case "A_MS/ACM":
                    // CodecPrivate is a WAVEFORMATEX; wFormatTag is its first field.
                    if (codecPrivate.Length >= 2) return Avi.AudioFromWaveFormat(codecPrivate);
                    break;
            }
            if (codecId.StartsWith("A_AAC", StringComparison.Ordinal)) return VideoCodecCatalog.Aac;
            if (codecId.StartsWith("A_DTS", StringComparison.Ordinal)) return VideoCodecCatalog.Dts;
            if (codecId.StartsWith("A_PCM/", StringComparison.Ordinal)) return VideoCodecCatalog.Pcm;
            if (codecId.StartsWith("A_REAL/", StringComparison.Ordinal)) return new VideoCodec("realaudio", VideoTrackKind.Audio, "RealAudio", Guid.Empty);
            return VideoCodecCatalog.Unnamed(VideoTrackKind.Audio, codecId);
        }

        private static IEnumerable<Element> Children(ProbeReader reader, long start, long end)
        {
            var position = start;
            end = Math.Min(end, reader.Length);
            for (var count = 0; count < MaxChildren && position < end; count++)
            {
                if (!ReadElement(reader, ref position, end, out var element)) yield break;
                yield return element;
                if (element.UnknownSize) yield break;
                position = element.End;
            }
        }

        /// <summary>Reads an element header at <paramref name="position"/> (advanced to the element's data).</summary>
        private static bool ReadElement(ProbeReader reader, ref long position, long end, out Element element)
        {
            element = default;
            Span<byte> buffer = stackalloc byte[12];
            var available = reader.Read(position, buffer);
            if (available < 2) return false;
            var idLength = VintLength(buffer[0]);
            if (idLength is 0 or > 4 || idLength >= available) return false;
            uint id = 0;
            for (var i = 0; i < idLength; i++) id = id << 8 | buffer[i];
            var sizeLength = VintLength(buffer[idLength]);
            if (sizeLength == 0 || idLength + sizeLength > available) return false;
            var size = (long)(buffer[idLength] & (0xFF >> sizeLength));
            var allOnes = size == (0xFF >> sizeLength);
            for (var i = 1; i < sizeLength; i++)
            {
                var b = buffer[idLength + i];
                if (b != 0xFF) allOnes = false;
                size = size << 8 | b;
            }
            var data = position + idLength + sizeLength;
            if (allOnes) { element = new Element(id, data, Math.Max(0, end - data), true); position = data; return true; }
            if (size < 0 || data > end) return false;
            if (size > end - data) size = end - data; // truncated file: clamp
            element = new Element(id, data, size, false);
            position = data;
            return true;
        }

        private static int VintLength(byte first)
        {
            for (var i = 0; i < 8; i++) if ((first & (0x80 >> i)) != 0) return i + 1;
            return 0;
        }

        private static ulong UInt(ProbeReader reader, Element element)
        {
            if (element.Size is <= 0 or > 8) return 0;
            Span<byte> b = stackalloc byte[8];
            var read = reader.Read(element.Data, b[..(int)element.Size]);
            ulong value = 0;
            for (var i = 0; i < read; i++) value = value << 8 | b[i];
            return value;
        }

        private static string Text(ProbeReader reader, Element element)
        {
            var bytes = reader.ReadBlock(element.Data, element.Size, 256);
            var length = Array.IndexOf(bytes, (byte)0);
            return Ascii(bytes.AsSpan(0, length < 0 ? bytes.Length : length));
        }
    }

    // ===================================================================================== AVI
    private static class Avi
    {
        public static void Parse(ProbeReader reader, List<VideoTrack> tracks)
        {
            // RIFF(4) size(4) 'AVI '(4), then chunks; the stream list lives in LIST 'hdrl'.
            Span<byte> header = stackalloc byte[12];
            long position = 12;
            for (var i = 0; i < 64 && reader.TryRead(position, header); i++)
            {
                var id = Ascii(header[..4]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
                if (id == "LIST" && header[8..12].SequenceEqual("hdrl"u8))
                {
                    var hdrl = reader.ReadBlock(position + 12, size - 4L, MaxHeaderBytes);
                    foreach (var (chunk, data) in Chunks(hdrl))
                        if (chunk == "LIST" && data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("strl"u8)) ParseStream(data.AsSpan(4), tracks);
                    return;
                }
                if (id == "LIST" && header[8..12].SequenceEqual("movi"u8)) return;
                position += 8L + size + (size & 1);
            }
        }

        private static void ParseStream(ReadOnlySpan<byte> strl, List<VideoTrack> tracks)
        {
            string? type = null;
            var handler = string.Empty;
            foreach (var (chunk, data) in Chunks(strl.ToArray()))
            {
                if (chunk == "strh" && data.Length >= 8)
                {
                    type = Ascii(data.AsSpan(0, 4));
                    handler = Ascii(data.AsSpan(4, 4));
                }
                else if (chunk == "strf" && type == "vids" && data.Length >= 20)
                {
                    var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                    var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)));
                    var compression = data.AsSpan(16, 4);
                    var codec = VideoFromCompression(compression);
                    Add(tracks, new VideoTrack(VideoTrackKind.Video, codec, Ascii(compression).TrimEnd('\0'), width, height));
                    return;
                }
                else if (chunk == "strf" && type == "auds" && data.Length >= 2)
                {
                    var codec = AudioFromWaveFormat(data);
                    Add(tracks, new VideoTrack(VideoTrackKind.Audio, codec, $"0x{BinaryPrimitives.ReadUInt16LittleEndian(data):X4}"));
                    return;
                }
            }
            if (type == "vids" && handler.Length == 4) Add(tracks, new VideoTrack(VideoTrackKind.Video, VideoCodecCatalog.FromVideoFourCc(handler), handler));
        }

        /// <summary>BITMAPINFOHEADER.biCompression: BI_RGB/BI_BITFIELDS (0..3) are uncompressed, anything else a FourCC.</summary>
        public static VideoCodec VideoFromCompression(ReadOnlySpan<byte> compression)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(compression);
            return value <= 3 ? VideoCodecCatalog.UncompressedVideo : VideoCodecCatalog.FromVideoFourCc(Ascii(compression));
        }

        /// <summary>WAVEFORMATEX: wFormatTag, or the SubFormat GUID's tag for WAVE_FORMAT_EXTENSIBLE.</summary>
        public static VideoCodec AudioFromWaveFormat(ReadOnlySpan<byte> format)
        {
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
            if (tag == 0xFFFE && format.Length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(format[24..]);
            return VideoCodecCatalog.FromWaveFormatTag(tag);
        }

        private static IEnumerable<(string Id, byte[] Data)> Chunks(byte[] data)
        {
            var position = 0;
            for (var count = 0; count < MaxChildren && position + 8 <= data.Length; count++)
            {
                var id = Ascii(data.AsSpan(position, 4));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
                var length = (int)Math.Min(size, (uint)(data.Length - position - 8));
                yield return (id, data.AsSpan(position + 8, length).ToArray());
                position += 8 + length + (length & 1);
            }
        }
    }

    // ===================================================================================== ASF / WMV
    private static class Asf
    {
        public static readonly Guid HeaderObject = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
        private static readonly Guid StreamProperties = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
        private static readonly Guid HeaderExtension = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
        private static readonly Guid ExtendedStreamProperties = new("14E6A5CB-C672-4332-8399-A96952065B5A");
        private static readonly Guid AudioMedia = new("F8699E40-5B4D-11CF-A8FD-00805F5C442B");
        private static readonly Guid VideoMedia = new("BC19EFC0-5B4D-11CF-A8FD-00805F5C442B");

        public static void Parse(ProbeReader reader, List<VideoTrack> tracks)
        {
            Span<byte> size = stackalloc byte[8];
            if (!reader.TryRead(16, size)) return;
            var header = reader.ReadBlock(0, (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(size), long.MaxValue), MaxHeaderBytes);
            // Header object: GUID(16) size(8) object count(4) reserved(2), then objects.
            if (header.Length > 30) ParseObjects(header.AsSpan(30), tracks, 0);
        }

        private static void ParseObjects(ReadOnlySpan<byte> data, List<VideoTrack> tracks, int depth)
        {
            var position = 0;
            for (var count = 0; count < MaxChildren && position + 24 <= data.Length; count++)
            {
                var id = new Guid(data.Slice(position, 16));
                var objectSize = BinaryPrimitives.ReadUInt64LittleEndian(data[(position + 16)..]);
                if (objectSize < 24) return;
                var length = (int)Math.Min(objectSize, (ulong)(data.Length - position));
                var payload = data.Slice(position + 24, length - 24);
                if (id == StreamProperties) ParseStream(payload, tracks);
                else if (id == HeaderExtension && depth == 0 && payload.Length >= 22) ParseObjects(payload[22..], tracks, depth + 1);
                else if (id == ExtendedStreamProperties && depth <= 1) ParseExtended(payload, tracks);
                position += length;
            }
        }

        private static void ParseExtended(ReadOnlySpan<byte> data, List<VideoTrack> tracks)
        {
            // Fixed fields (64 bytes), then stream names and payload extension systems, then an optional Stream Properties Object.
            if (data.Length < 64) return;
            var names = BinaryPrimitives.ReadUInt16LittleEndian(data[60..]);
            var extensions = BinaryPrimitives.ReadUInt16LittleEndian(data[62..]);
            var p = 64;
            for (var i = 0; i < names && p + 4 <= data.Length; i++) p += 4 + BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 2)..]);
            for (var i = 0; i < extensions && p + 22 <= data.Length; i++) p += 22 + (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 18)..]), int.MaxValue / 2);
            if (p + 24 <= data.Length && new Guid(data.Slice(p, 16)) == StreamProperties) ParseObjects(data[p..], tracks, 2);
        }

        private static void ParseStream(ReadOnlySpan<byte> payload, List<VideoTrack> tracks)
        {
            // Stream type GUID(16), error correction GUID(16), time offset(8), type-specific length(4), error data length(4),
            // flags(2), reserved(4), then type-specific data.
            if (payload.Length < 54) return;
            var type = new Guid(payload[..16]);
            var specificLength = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(payload[40..]), (uint)(payload.Length - 54));
            var specific = payload.Slice(54, specificLength);
            if (type == AudioMedia && specific.Length >= 2)
            {
                Add(tracks, new VideoTrack(VideoTrackKind.Audio, Avi.AudioFromWaveFormat(specific), $"0x{BinaryPrimitives.ReadUInt16LittleEndian(specific):X4}"));
            }
            else if (type == VideoMedia && specific.Length >= 31)
            {
                // Width(4) height(4) flags(1) format size(2), then BITMAPINFOHEADER (biCompression at +16).
                var width = BinaryPrimitives.ReadInt32LittleEndian(specific);
                var height = BinaryPrimitives.ReadInt32LittleEndian(specific[4..]);
                var compression = specific.Slice(27, 4);
                Add(tracks, new VideoTrack(VideoTrackKind.Video, Avi.VideoFromCompression(compression), Ascii(compression).TrimEnd('\0'), width, height));
            }
        }
    }

    // ===================================================================================== MPEG transport stream
    private static class MpegTransport
    {
        public static bool DetectPacket(ProbeReader reader, out int packetSize, out int syncOffset)
        {
            var probe = reader.ReadBlock(0, 204 * 6, 204 * 6);
            foreach (var size in new[] { 188, 192, 204 })
            {
                for (var offset = 0; offset < size && offset < probe.Length; offset++)
                {
                    var hits = 0;
                    for (var p = offset; p < probe.Length && hits < 5; p += size)
                    {
                        if (probe[p] != 0x47) break;
                        hits++;
                    }
                    var needed = Math.Min(5, Math.Max(2, (probe.Length - offset) / size));
                    if (hits >= needed && hits >= 2)
                    {
                        packetSize = size;
                        syncOffset = offset;
                        return true;
                    }
                }
            }
            packetSize = syncOffset = 0;
            return false;
        }

        public static void Parse(ProbeReader reader, int packetSize, int syncOffset, List<VideoTrack> tracks)
        {
            var pmtPids = new HashSet<int>();
            var parsedPmts = new HashSet<int>();
            var sections = new Dictionary<int, List<byte>>();
            var patSeen = false;
            var packet = new byte[188];
            var limit = Math.Min(reader.Length, MaxTransportScanBytes);
            for (long position = syncOffset; position + 188 <= limit; position += packetSize)
            {
                if (!reader.TryRead(position, packet) || packet[0] != 0x47) continue;
                var pid = (packet[1] & 0x1F) << 8 | packet[2];
                var start = (packet[1] & 0x40) != 0;
                var adaptation = (packet[3] >> 4) & 0x3;
                if (pid != 0 && !pmtPids.Contains(pid)) continue;
                if (adaptation is 0 or 2) continue;
                var payload = 4;
                if (adaptation == 3) payload += 1 + packet[4];
                if (payload >= 188) continue;
                if (start)
                {
                    var pointer = packet[payload];
                    payload += 1 + pointer;
                    if (payload >= 188) continue;
                    sections[pid] = [.. packet.AsSpan(payload)];
                }
                else if (sections.TryGetValue(pid, out var pending) && pending.Count < 4096) pending.AddRange(packet.AsSpan(payload).ToArray());
                else continue;

                var section = sections[pid];
                if (section.Count < 3) continue;
                var sectionLength = (section[1] & 0x0F) << 8 | section[2];
                if (section.Count < 3 + sectionLength) continue;
                var bytes = section.GetRange(0, 3 + sectionLength).ToArray();
                sections.Remove(pid);
                if (pid == 0 && bytes[0] == 0x00 && !patSeen)
                {
                    patSeen = true;
                    for (var p = 8; p + 4 <= bytes.Length - 4; p += 4)
                    {
                        var program = bytes[p] << 8 | bytes[p + 1];
                        if (program != 0) pmtPids.Add((bytes[p + 2] & 0x1F) << 8 | bytes[p + 3]);
                    }
                }
                else if (pid != 0 && bytes[0] == 0x02 && parsedPmts.Add(pid))
                {
                    ParsePmt(bytes, packetSize == 192, tracks);
                    if (parsedPmts.Count >= pmtPids.Count) return;
                }
            }
        }

        private static void ParsePmt(byte[] section, bool blueRay, List<VideoTrack> tracks)
        {
            if (section.Length < 16) return;
            var programInfoLength = (section[10] & 0x0F) << 8 | section[11];
            var end = section.Length - 4; // CRC
            for (var p = 12 + programInfoLength; p + 5 <= end;)
            {
                var streamType = section[p];
                var esInfoLength = (section[p + 3] & 0x0F) << 8 | section[p + 4];
                var descriptors = section.AsSpan(p + 5, Math.Max(0, Math.Min(esInfoLength, end - p - 5)));
                var track = Classify(streamType, descriptors, blueRay);
                if (track is not null) Add(tracks, track);
                p += 5 + esInfoLength;
            }
        }

        private static VideoTrack? Classify(byte streamType, ReadOnlySpan<byte> descriptors, bool blueRay)
        {
            var tag = $"0x{streamType:X2}";
            VideoTrack Video(VideoCodec codec) => new(VideoTrackKind.Video, codec, tag);
            VideoTrack Audio(VideoCodec codec) => new(VideoTrackKind.Audio, codec, tag);
            switch (streamType)
            {
                case 0x01: return Video(VideoCodecCatalog.Mpeg1);
                case 0x02: return Video(VideoCodecCatalog.Mpeg2);
                case 0x10: return Video(VideoCodecCatalog.Mpeg4Part2);
                case 0x1B: case 0x20: return Video(VideoCodecCatalog.H264);
                case 0x24: return Video(VideoCodecCatalog.Hevc);
                case 0x33: return Video(VideoCodecCatalog.Vvc);
                case 0xEA: return Video(VideoCodecCatalog.Vc1);
                case 0x03: case 0x04: return Audio(VideoCodecCatalog.MpegAudio);
                case 0x0F: case 0x11: return Audio(VideoCodecCatalog.Aac);
                case 0x81: return Audio(VideoCodecCatalog.Ac3);
                case 0x87: case 0x84: case 0xA1: return Audio(VideoCodecCatalog.Eac3);
                case 0x82: case 0x85: case 0x86: case 0xA2: return Audio(VideoCodecCatalog.Dts);
                case 0x83: return Audio(VideoCodecCatalog.TrueHd);
                case 0x80: return blueRay ? Audio(VideoCodecCatalog.Pcm) : null;
                case 0x06: return FromDescriptors(descriptors, tag);
            }
            return null; // subtitles, data and private streams Glide doesn't play
        }

        private static VideoTrack? FromDescriptors(ReadOnlySpan<byte> descriptors, string tag)
        {
            for (var p = 0; p + 2 <= descriptors.Length;)
            {
                var descriptor = descriptors[p];
                var length = descriptors[p + 1];
                var body = descriptors.Slice(p + 2, Math.Min(length, descriptors.Length - p - 2));
                switch (descriptor)
                {
                    case 0x6A: return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Ac3, tag);
                    case 0x7A: return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Eac3, tag);
                    case 0x7B: return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Dts, tag);
                    case 0x7F when body.Length >= 1 && body[0] == 0x15: return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Ac4, tag);
                    case 0x05 when body.Length >= 4:
                        switch (Ascii(body[..4]))
                        {
                            case "Opus": return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Opus, "Opus");
                            case "AC-3": return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Ac3, "AC-3");
                            case "EAC3": return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Eac3, "EAC3");
                            case "DTS1": case "DTS2": case "DTS3": return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Dts, "DTS");
                            case "AV01": return new VideoTrack(VideoTrackKind.Video, VideoCodecCatalog.Av1, "AV01");
                            case "HEVC": return new VideoTrack(VideoTrackKind.Video, VideoCodecCatalog.Hevc, "HEVC");
                            case "VC-1": return new VideoTrack(VideoTrackKind.Video, VideoCodecCatalog.Vc1, "VC-1");
                        }
                        break;
                }
                p += 2 + length;
            }
            return null;
        }
    }

    // ===================================================================================== MPEG program stream / elementary video
    private static class MpegProgram
    {
        public static void Parse(ProbeReader reader, List<VideoTrack> tracks)
        {
            var data = reader.ReadBlock(0, reader.Length, MaxProgramScanBytes);
            var mpeg2 = data.Length > 4 && (data[4] & 0xC0) == 0x40;
            var seen = new HashSet<int>();
            var packets = 0;
            for (var p = 0; p + 4 <= data.Length && packets < 4096;)
            {
                if (data[p] != 0 || data[p + 1] != 0 || data[p + 2] != 1) { p++; continue; }
                var id = data[p + 3];
                if (id == 0xBA) { p += 4; continue; }
                if (id < 0xBC) { p += 4; continue; } // a start code inside a payload
                if (p + 6 > data.Length) break;
                packets++;
                var length = data[p + 4] << 8 | data[p + 5];
                if (id is >= 0xE0 and <= 0xEF && seen.Add(id))
                {
                    var codec = mpeg2 ? VideoCodecCatalog.Mpeg2 : VideoCodecCatalog.Mpeg1;
                    var (width, height) = SequenceSize(data.AsSpan(p, Math.Min(length + 6, data.Length - p)));
                    Add(tracks, new VideoTrack(VideoTrackKind.Video, codec, $"0x{id:X2}", width, height));
                }
                else if (id is >= 0xC0 and <= 0xDF && seen.Add(id)) Add(tracks, new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.MpegAudio, $"0x{id:X2}"));
                else if (id == 0xBD && mpeg2 && p + 9 <= data.Length)
                {
                    var substream = p + 9 + data[p + 8];
                    if (substream < data.Length && seen.Add(0x100 | data[substream]))
                    {
                        var sub = data[substream];
                        VideoCodec? codec = sub switch
                        {
                            >= 0x80 and <= 0x87 => VideoCodecCatalog.Ac3,
                            >= 0x88 and <= 0x8F => VideoCodecCatalog.Dts,
                            >= 0xA0 and <= 0xA7 => VideoCodecCatalog.Pcm,
                            _ => null, // subpictures
                        };
                        if (codec is not null) Add(tracks, new VideoTrack(VideoTrackKind.Audio, codec, $"0xBD/0x{sub:X2}"));
                    }
                }
                p += 6 + length;
            }
        }

        public static void ParseElementaryVideo(ProbeReader reader, List<VideoTrack> tracks)
        {
            var data = reader.ReadBlock(0, reader.Length, 64 * 1024);
            var mpeg2 = false;
            for (var p = 0; p + 4 <= data.Length; p++)
                if (data[p] == 0 && data[p + 1] == 0 && data[p + 2] == 1 && data[p + 3] == 0xB5) { mpeg2 = true; break; }
            var (width, height) = SequenceSize(data);
            Add(tracks, new VideoTrack(VideoTrackKind.Video, mpeg2 ? VideoCodecCatalog.Mpeg2 : VideoCodecCatalog.Mpeg1, "B3", width, height));
        }

        private static (int Width, int Height) SequenceSize(ReadOnlySpan<byte> data)
        {
            for (var p = 0; p + 7 <= data.Length; p++)
                if (data[p] == 0 && data[p + 1] == 0 && data[p + 2] == 1 && data[p + 3] == 0xB3)
                    return (data[p + 4] << 4 | data[p + 5] >> 4, (data[p + 5] & 0x0F) << 8 | data[p + 6]);
            return (0, 0);
        }
    }

    // ===================================================================================== Ogg
    private static class Ogg
    {
        public static void Parse(ProbeReader reader, List<VideoTrack> tracks)
        {
            long position = 0;
            var header = new byte[27];
            var segments = new byte[255];
            for (var page = 0; page < 64 && reader.TryRead(position, header) && header.AsSpan(0, 4).SequenceEqual("OggS"u8); page++)
            {
                var bos = (header[5] & 0x02) != 0;
                if (!bos) return; // all beginning-of-stream pages come first
                var count = header[26];
                if (!reader.TryRead(position + 27, segments.AsSpan(0, count))) return;
                var dataLength = 0;
                var firstPacket = 0;
                var firstDone = false;
                for (var i = 0; i < count; i++)
                {
                    dataLength += segments[i];
                    if (!firstDone) { firstPacket += segments[i]; if (segments[i] < 255) firstDone = true; }
                }
                var packet = reader.ReadBlock(position + 27 + count, Math.Min(firstPacket, 64), 64);
                var track = Identify(packet);
                if (track is not null) Add(tracks, track);
                position += 27 + count + dataLength;
            }
        }

        private static VideoTrack? Identify(ReadOnlySpan<byte> packet)
        {
            if (packet.Length >= 7 && packet[0] == 0x80 && packet[1..7].SequenceEqual("theora"u8))
            {
                int width = 0, height = 0;
                if (packet.Length >= 14) { width = (packet[10] << 8 | packet[11]) * 16; height = (packet[12] << 8 | packet[13]) * 16; }
                return new VideoTrack(VideoTrackKind.Video, VideoCodecCatalog.Theora, "theora", width, height);
            }
            if (packet.Length >= 7 && packet[0] == 0x01 && packet[1..7].SequenceEqual("vorbis"u8)) return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Vorbis, "vorbis");
            if (packet.Length >= 8 && packet[..8].SequenceEqual("OpusHead"u8)) return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Opus, "OpusHead");
            if (packet.Length >= 5 && packet[0] == 0x7F && packet[1..5].SequenceEqual("FLAC"u8)) return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Flac, "FLAC");
            if (packet.Length >= 8 && packet[..8].SequenceEqual("Speex   "u8)) return new VideoTrack(VideoTrackKind.Audio, VideoCodecCatalog.Speex, "Speex");
            if (packet.Length >= 5 && packet[0] == 0x80 && packet[1..5].SequenceEqual("kate"u8)) return null; // subtitles
            if (packet.Length >= 8 && packet[..8].SequenceEqual("fishead\0"u8)) return null; // skeleton metadata
            if (packet.Length >= 6 && packet[0] == 0x80 && packet[1..6].SequenceEqual("daala"u8)) return new VideoTrack(VideoTrackKind.Video, VideoCodecCatalog.Unnamed(VideoTrackKind.Video, "daala"), "daala");
            return null;
        }
    }
}
