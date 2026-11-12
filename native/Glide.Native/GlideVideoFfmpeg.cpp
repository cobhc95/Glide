// Glide.Native — bundled FFmpeg fallback for overlay video playback.
//
// Used only when Media Foundation cannot play a file on this PC (typically WebM/VP8/VP9, HEVC, AV1
// or MPEG-2 without the matching Microsoft Store extension). Decoding is software-only.
//
//   decode thread : demuxes, decodes video into a short frame queue and audio into float chunks
//   audio thread  : feeds WASAPI (shared mode), aligning samples to the playback clock
//   ReadFrame     : (managed pump thread) presents the newest frame due on the clock, scaled and
//                   rotated to opaque BGRA
//
// The playback clock is the system performance counter; audio is written so that each sample plays
// when the clock reaches its timestamp, which keeps A/V in sync without an audio-master clock.
// FFmpeg DLLs live in "<Glide.Native dir>\ffmpeg" and are delay-loaded, so Glide never maps them
// unless a video actually needs this path.

#include <windows.h>
#include "GlideVideo.h"

#if GLIDE_HAS_FFMPEG

#include <mmdeviceapi.h>
#include <audioclient.h>
#include <ksmedia.h>
#include <delayimp.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <deque>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <vector>

extern "C"
{
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/display.h>
#include <libavutil/imgutils.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
}

namespace
{
    // ------------------------------------------------------------------ DLL loading -------------

    std::mutex ffmpegGate;
    std::wstring ffmpegDirectoryOverride; // a downloaded decoder pack; guarded by ffmpegGate

    std::wstring FfmpegDirectory()
    {
        {
            std::lock_guard lock(ffmpegGate);
            if (!ffmpegDirectoryOverride.empty()) return ffmpegDirectoryOverride;
        }
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&FfmpegDirectory), &self);
        wchar_t buffer[MAX_PATH * 2]{};
        const DWORD length = GetModuleFileNameW(self, buffer, ARRAYSIZE(buffer));
        std::wstring path(buffer, length);
        const auto slash = path.find_last_of(L"\\/");
        return (slash == std::wstring::npos ? std::wstring() : path.substr(0, slash + 1)) + L"ffmpeg\\";
    }

    // Loads every FFmpeg DLL by full path once, dependencies first. Delay-load resolution then finds
    // them by module name; a missing or broken install is reported instead of raising the delay-load
    // exception inside FFmpeg calls. A failure is retried on the next call, because the decoder pack
    // can be downloaded while Glide is running.
    bool EnsureFfmpegLoaded()
    {
        static std::mutex gate;
        static bool loaded = false;
        std::lock_guard lock(gate);
        if (loaded) return true;
        const auto dir = FfmpegDirectory();
        for (const wchar_t* name : { GLIDE_FFMPEG_AVUTIL, GLIDE_FFMPEG_SWRESAMPLE, GLIDE_FFMPEG_SWSCALE, GLIDE_FFMPEG_AVCODEC, GLIDE_FFMPEG_AVFORMAT })
        {
            if (!LoadLibraryExW((dir + name).c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
                return false;
        }
        av_log_set_level(AV_LOG_QUIET); // Glide is a GUI app; decoder chatter has nowhere useful to go
        loaded = true;
        return true;
    }

    FARPROC WINAPI FfmpegDelayLoadHook(unsigned notification, PDelayLoadInfo info)
    {
        if (notification != dliNotePreLoadLibrary || !info || !info->szDll) return nullptr;
        std::wstring name(info->szDll, info->szDll + std::strlen(info->szDll));
        if (_wcsnicmp(name.c_str(), L"av", 2) != 0 && _wcsnicmp(name.c_str(), L"sw", 2) != 0) return nullptr;
        return reinterpret_cast<FARPROC>(LoadLibraryExW((FfmpegDirectory() + name).c_str(), nullptr,
                                                        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS));
    }

    // H.264 beyond the GPU decode limit is software-decoded here, so it is decoded more cheaply: non-reference
    // frames are skipped (no later frame predicts from them) and the 960 px downscale uses the fast filter.
    bool IsLargeH264(const AVCodecParameters* parameters)
    {
        return parameters->codec_id == AV_CODEC_ID_H264 &&
               (parameters->width > HardwareDecodeMaxSide || parameters->height > HardwareDecodeMaxSide);
    }

    double Now()
    {
        static const double frequency = []
        {
            LARGE_INTEGER f{};
            QueryPerformanceFrequency(&f);
            return static_cast<double>(f.QuadPart);
        }();
        LARGE_INTEGER counter{};
        QueryPerformanceCounter(&counter);
        return counter.QuadPart / frequency;
    }

    // --------------------------------------------------------------- file I/O -----------------

    struct SharedFile
    {
        HANDLE handle = INVALID_HANDLE_VALUE;

        static int Read(void* opaque, std::uint8_t* buffer, int size)
        {
            DWORD read = 0;
            if (!ReadFile(static_cast<SharedFile*>(opaque)->handle, buffer, static_cast<DWORD>(size), &read, nullptr)) return AVERROR(EIO);
            return read == 0 ? AVERROR_EOF : static_cast<int>(read);
        }

        static std::int64_t Seek(void* opaque, std::int64_t offset, int whence)
        {
            auto* file = static_cast<SharedFile*>(opaque);
            if (whence & AVSEEK_SIZE)
            {
                LARGE_INTEGER size{};
                return GetFileSizeEx(file->handle, &size) ? size.QuadPart : -1;
            }
            whence &= ~AVSEEK_FORCE;
            LARGE_INTEGER move{}, result{};
            move.QuadPart = offset;
            const DWORD method = whence == SEEK_CUR ? FILE_CURRENT : whence == SEEK_END ? FILE_END : FILE_BEGIN;
            return SetFilePointerEx(file->handle, move, &result, method) ? result.QuadPart : -1;
        }
    };

    struct QueuedFrame { AVFrame* frame; double pts; };
    struct AudioChunk { std::vector<float> samples; double pts; std::size_t offset = 0; }; // stereo interleaved

    constexpr int MaxQueuedVideoFrames = 6;
    constexpr double AudioQueueTarget = 0.75;  // seconds decoded ahead
    constexpr double AudioBufferSeconds = 0.2; // WASAPI buffer
    constexpr double SyncTolerance = 0.03;     // audio drift before drop/pad correction

    class FfmpegBackend final : public GlideVideoBackend
    {
    public:
        ~FfmpegBackend() override
        {
            stop_ = true;
            if (decodeThread_.joinable()) decodeThread_.join();
            if (audioThread_.joinable()) audioThread_.join();
            {
                std::lock_guard lock(gate_);
                ClearQueuesLocked();
                if (current_) av_frame_free(&current_);
            }
            if (sws_) sws_freeContext(sws_);
            if (swr_) swr_free(&swr_);
            if (videoDecoder_) avcodec_free_context(&videoDecoder_);
            if (audioDecoder_) avcodec_free_context(&audioDecoder_);
            if (format_) avformat_close_input(&format_);
            if (io_) { av_freep(&io_->buffer); avio_context_free(&io_); }
            if (file_.handle != INVALID_HANDLE_VALUE) CloseHandle(file_.handle);
        }

        int Open(const wchar_t* path)
        {
            file_.handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                       OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
            if (file_.handle == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());

            constexpr int ioBufferSize = 1 << 16;
            auto* ioBuffer = static_cast<std::uint8_t*>(av_malloc(ioBufferSize));
            if (!ioBuffer) return E_OUTOFMEMORY;
            io_ = avio_alloc_context(ioBuffer, ioBufferSize, 0, &file_, &SharedFile::Read, nullptr, &SharedFile::Seek);
            if (!io_) { av_free(ioBuffer); return E_OUTOFMEMORY; }
            format_ = avformat_alloc_context();
            if (!format_) return E_OUTOFMEMORY;
            format_->pb = io_;
            format_->flags |= AVFMT_FLAG_CUSTOM_IO;

            // UTF-8 path only as a format-probing hint (the extension); I/O goes through io_.
            char hint[MAX_PATH * 3]{};
            WideCharToMultiByte(CP_UTF8, 0, path, -1, hint, sizeof(hint) - 1, nullptr, nullptr);
            if (avformat_open_input(&format_, hint, nullptr, nullptr) < 0) { format_ = nullptr; return E_FAIL; }
            if (avformat_find_stream_info(format_, nullptr) < 0) return E_FAIL;

            videoIndex_ = av_find_best_stream(format_, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
            if (videoIndex_ < 0) return E_FAIL; // audio-only files are not overlay content
            largeH264_ = IsLargeH264(format_->streams[videoIndex_]->codecpar);
            audioIndex_ = av_find_best_stream(format_, AVMEDIA_TYPE_AUDIO, -1, videoIndex_, nullptr, 0);
            for (unsigned i = 0; i < format_->nb_streams; ++i)
                if (static_cast<int>(i) != videoIndex_ && static_cast<int>(i) != audioIndex_) format_->streams[i]->discard = AVDISCARD_ALL;

            if (!OpenDecoder(videoIndex_, videoDecoder_)) return E_FAIL;
            if (audioIndex_ >= 0 && !OpenDecoder(audioIndex_, audioDecoder_)) audioIndex_ = -1;
            if (audioIndex_ >= 0 && !CreateResampler()) audioIndex_ = -1;

            startOffset_ = format_->start_time != AV_NOPTS_VALUE ? format_->start_time / static_cast<double>(AV_TIME_BASE) : 0.0;
            duration_ = format_->duration != AV_NOPTS_VALUE && format_->duration > 0 ? format_->duration / static_cast<double>(AV_TIME_BASE) : 0.0;

            auto* stream = format_->streams[videoIndex_];
            const auto sar = av_guess_sample_aspect_ratio(format_, stream, nullptr);
            double width = videoDecoder_->width;
            if (sar.num > 0 && sar.den > 0) width = width * sar.num / sar.den;
            displayWidth_ = static_cast<int>(width + 0.5);
            displayHeight_ = videoDecoder_->height;
            if (const auto* matrix = av_packet_side_data_get(stream->codecpar->coded_side_data, stream->codecpar->nb_coded_side_data, AV_PKT_DATA_DISPLAYMATRIX))
            {
                const double angle = -av_display_rotation_get(reinterpret_cast<const std::int32_t*>(matrix->data));
                rotation_ = (static_cast<int>(std::lround(angle / 90.0)) % 4 + 4) % 4; // quarter turns clockwise
            }
            if (rotation_ % 2 == 1) std::swap(displayWidth_, displayHeight_);
            if (displayWidth_ <= 0 || displayHeight_ <= 0) return E_FAIL;

            decodeThread_ = std::thread([this] { DecodeLoop(); });
            if (audioIndex_ >= 0) audioThread_ = std::thread([this] { AudioLoop(); });
            return S_OK;
        }

        void GetInfo(GlideVideoInfo& info) override
        {
            std::lock_guard lock(gate_);
            info.backend = 2;
            info.state = failed_ ? -1 : 1;
            info.width = displayWidth_;
            info.height = displayHeight_;
            info.playing = playing_ ? 1 : 0;
            info.ended = ended_ ? 1 : 0;
            info.muted = muted_ ? 1 : 0;
            info.loop = loop_ ? 1 : 0;
            info.duration = duration_;
            const double position = ClockLocked();
            info.position = duration_ > 0 ? std::clamp(position, 0.0, duration_) : std::max(0.0, position);
            info.volume = volume_;
        }

        int SetPlaying(bool playing) override
        {
            std::lock_guard lock(gate_);
            if (playing == playing_ && !(playing && ended_)) return S_OK;
            if (playing && ended_) { RequestSeekLocked(0); }
            clockBase_ = ClockLocked();
            clockStart_ = Now();
            playing_ = playing;
            return S_OK;
        }

        int Seek(double seconds) override
        {
            std::lock_guard lock(gate_);
            RequestSeekLocked(duration_ > 0 ? std::clamp(seconds, 0.0, duration_) : std::max(0.0, seconds));
            return S_OK;
        }

        int SetMuted(bool muted) override { std::lock_guard lock(gate_); muted_ = muted; return S_OK; }
        int SetVolume(double volume) override { std::lock_guard lock(gate_); volume_ = std::clamp(volume, 0.0, 1.0); return S_OK; }
        int SetLoop(bool loop) override { std::lock_guard lock(gate_); loop_ = loop; return S_OK; }

        int ReadFrame(std::uint8_t* destination, std::int32_t width, std::int32_t height, std::int32_t stride, bool force) override
        {
            AVFrame* frame = nullptr;
            {
                std::lock_guard lock(gate_);
                const double now = ClockLocked();
                bool changed = false;
                while (!videoQueue_.empty())
                {
                    auto& next = videoQueue_.front();
                    if (current_ && next.pts > now + 0.005) break;
                    if (current_) av_frame_free(&current_);
                    current_ = next.frame;
                    videoQueue_.pop_front();
                    changed = true;
                }
                if (!current_ || (!changed && !force)) return 0;
                frame = av_frame_clone(current_);
            }
            if (!frame) return E_OUTOFMEMORY;
            const int result = Convert(frame, destination, width, height, stride);
            av_frame_free(&frame);
            return result;
        }

    private:
        bool OpenDecoder(int index, AVCodecContext*& context)
        {
            auto* parameters = format_->streams[index]->codecpar;
            const AVCodec* codec = avcodec_find_decoder(parameters->codec_id);
            if (!codec) return false;
            context = avcodec_alloc_context3(codec);
            if (!context || avcodec_parameters_to_context(context, parameters) < 0) return false;
            context->thread_count = 0; // auto
            context->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
            if (index == videoIndex_ && largeH264_) context->skip_frame = AVDISCARD_NONREF;
            context->pkt_timebase = format_->streams[index]->time_base;
            return avcodec_open2(context, codec, nullptr) >= 0;
        }

        bool CreateResampler()
        {
            AVChannelLayout stereo = AV_CHANNEL_LAYOUT_STEREO;
            outputRate_ = audioDecoder_->sample_rate > 0 ? audioDecoder_->sample_rate : 48000;
            if (swr_alloc_set_opts2(&swr_, &stereo, AV_SAMPLE_FMT_FLT, outputRate_,
                                    &audioDecoder_->ch_layout, audioDecoder_->sample_fmt, audioDecoder_->sample_rate, 0, nullptr) < 0)
                return false;
            return swr_init(swr_) >= 0;
        }

        // ---------------------------------------------------------------- clock ---------------

        double ClockLocked() const
        {
            return playing_ && !buffering_ ? clockBase_ + (Now() - clockStart_) : clockBase_;
        }

        void RequestSeekLocked(double seconds)
        {
            pendingSeek_ = seconds;
            clockBase_ = seconds;
            clockStart_ = Now();
            buffering_ = true;
            ended_ = false;
        }

        void ClearQueuesLocked()
        {
            for (auto& queued : videoQueue_) av_frame_free(&queued.frame);
            videoQueue_.clear();
            audioQueue_.clear();
            audioQueuedSeconds_ = 0;
            lastVideoPts_ = 0;
        }

        // ---------------------------------------------------------------- decode --------------

        void DecodeLoop()
        {
            AVPacket* packet = av_packet_alloc();
            AVFrame* frame = av_frame_alloc();
            if (!packet || !frame) { std::lock_guard lock(gate_); failed_ = true; }
            bool demuxEof = false, videoDrained = false;
            double dropBefore = 0;
            while (!stop_ && packet && frame)
            {
                double seekTarget = -1;
                bool needVideo, needAudio;
                {
                    std::lock_guard lock(gate_);
                    if (pendingSeek_ >= 0) { seekTarget = pendingSeek_; pendingSeek_ = -1; }
                    needVideo = static_cast<int>(videoQueue_.size()) < MaxQueuedVideoFrames;
                    needAudio = audioIndex_ >= 0 && audioQueuedSeconds_ < AudioQueueTarget;
                }
                if (seekTarget >= 0)
                {
                    SeekDemuxer(seekTarget);
                    avcodec_flush_buffers(videoDecoder_);
                    if (audioDecoder_) avcodec_flush_buffers(audioDecoder_);
                    if (swr_) { swr_close(swr_); swr_init(swr_); }
                    demuxEof = videoDrained = false;
                    dropBefore = seekTarget - 0.001;
                    std::lock_guard lock(gate_);
                    ClearQueuesLocked();
                    audioReset_ = true;
                    continue;
                }

                if (demuxEof)
                {
                    if (!videoDrained) videoDrained = DrainVideo(frame, dropBefore, true);
                    std::unique_lock lock(gate_);
                    if (videoDrained && videoQueue_.empty() && !buffering_ && ClockLocked() >= lastVideoPts_)
                    {
                        if (loop_) { RequestSeekLocked(0); continue; }
                        if (!ended_) { ended_ = true; clockBase_ = ClockLocked(); clockStart_ = Now(); playing_ = false; }
                    }
                    if (videoDrained && buffering_) { buffering_ = false; clockStart_ = Now(); } // seek past the last frame
                    lock.unlock();
                    Sleep(5);
                    continue;
                }

                if (!needVideo && !needAudio) { Sleep(3); continue; }

                const int read = av_read_frame(format_, packet);
                if (read < 0)
                {
                    demuxEof = true;
                    avcodec_send_packet(videoDecoder_, nullptr);
                    if (audioDecoder_) avcodec_send_packet(audioDecoder_, nullptr);
                    if (audioDecoder_) DrainAudio(frame, dropBefore);
                    continue;
                }
                if (packet->stream_index == videoIndex_)
                {
                    if (avcodec_send_packet(videoDecoder_, packet) >= 0) DrainVideo(frame, dropBefore, false);
                }
                else if (packet->stream_index == audioIndex_)
                {
                    if (avcodec_send_packet(audioDecoder_, packet) >= 0) DrainAudio(frame, dropBefore);
                }
                av_packet_unref(packet);
            }
            av_frame_free(&frame);
            av_packet_free(&packet);
        }

        // MPEG-TS/PS have no index and unreliable timestamps for searching, so (like ffplay) they
        // seek by byte position; other containers seek by timestamp, falling back to bytes.
        void SeekDemuxer(double target)
        {
            const bool byBytes = (format_->iformat->flags & AVFMT_TS_DISCONT) && std::strcmp(format_->iformat->name, "ogg") != 0;
            if (!byBytes)
            {
                const auto timestamp = static_cast<std::int64_t>((target + startOffset_) * AV_TIME_BASE);
                if (avformat_seek_file(format_, -1, INT64_MIN, timestamp, timestamp, 0) >= 0) return;
            }
            SeekBytes(target);
        }

        // A byte position is only an estimate of a time. Land a little before the target, check
        // the first video timestamp there, and step back until it is at or before the target, so
        // the decoder starts from a keyframe that precedes it (decoding then runs forward to it).
        void SeekBytes(double target)
        {
            const std::int64_t size = avio_size(format_->pb);
            if (size <= 0 || duration_ <= 0) return;
            AVPacket* probe = av_packet_alloc();
            double aim = target - 1.0;
            std::int64_t position = 0;
            for (int attempt = 0; attempt < 6; ++attempt)
            {
                position = static_cast<std::int64_t>(size * std::clamp(aim / duration_, 0.0, 1.0));
                if (avformat_seek_file(format_, -1, INT64_MIN, position, position, AVSEEK_FLAG_BYTE) < 0 || !probe || position == 0) break;
                double found = -1;
                for (int i = 0; i < 2000 && found < 0 && av_read_frame(format_, probe) >= 0; ++i)
                {
                    if (probe->stream_index == videoIndex_ && probe->pts != AV_NOPTS_VALUE)
                        found = Seconds(probe->pts, videoIndex_);
                    av_packet_unref(probe);
                }
                if (found >= 0 && found <= target) break;
                aim -= (found >= 0 ? found - target : 0) + 1.0;
            }
            av_packet_free(&probe);
            avformat_seek_file(format_, -1, INT64_MIN, position, position, AVSEEK_FLAG_BYTE);
        }

        // Returns true once the decoder reported end of stream.
        bool DrainVideo(AVFrame* frame, double dropBefore, bool draining)
        {
            while (true)
            {
                const int result = avcodec_receive_frame(videoDecoder_, frame);
                if (result == AVERROR_EOF) return true;
                if (result < 0) return false;
                const double pts = Seconds(frame->best_effort_timestamp, videoIndex_);
                if (pts < dropBefore) { av_frame_unref(frame); continue; }
                AVFrame* kept = av_frame_clone(frame);
                av_frame_unref(frame);
                if (!kept) continue;
                std::lock_guard lock(gate_);
                videoQueue_.push_back({ kept, pts });
                lastVideoPts_ = std::max(lastVideoPts_, pts);
                if (buffering_)
                {
                    // First frame after open/seek: start the clock at that frame.
                    buffering_ = false;
                    clockBase_ = std::max(clockBase_, pts);
                    clockStart_ = Now();
                }
                if (!draining && static_cast<int>(videoQueue_.size()) > MaxQueuedVideoFrames * 8) return false;
            }
        }

        void DrainAudio(AVFrame* frame, double dropBefore)
        {
            while (avcodec_receive_frame(audioDecoder_, frame) >= 0)
            {
                const double pts = Seconds(frame->best_effort_timestamp, audioIndex_);
                if (pts + frame->nb_samples / static_cast<double>(std::max(1, frame->sample_rate)) < dropBefore) { av_frame_unref(frame); continue; }
                const int capacity = swr_get_out_samples(swr_, frame->nb_samples);
                if (capacity <= 0) { av_frame_unref(frame); continue; }
                AudioChunk chunk;
                chunk.samples.resize(static_cast<std::size_t>(capacity) * 2);
                auto* out = reinterpret_cast<std::uint8_t*>(chunk.samples.data());
                const int converted = swr_convert(swr_, &out, capacity, const_cast<const std::uint8_t**>(frame->extended_data), frame->nb_samples);
                av_frame_unref(frame);
                if (converted <= 0) continue;
                chunk.samples.resize(static_cast<std::size_t>(converted) * 2);
                chunk.pts = pts;
                std::lock_guard lock(gate_);
                audioQueuedSeconds_ += converted / static_cast<double>(outputRate_);
                audioQueue_.push_back(std::move(chunk));
            }
        }

        double Seconds(std::int64_t timestamp, int stream) const
        {
            if (timestamp == AV_NOPTS_VALUE) return lastVideoPts_;
            return timestamp * av_q2d(format_->streams[stream]->time_base) - startOffset_;
        }

        // ---------------------------------------------------------------- audio ---------------

        void AudioLoop()
        {
            const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            IMMDeviceEnumerator* enumerator = nullptr;
            IMMDevice* device = nullptr;
            IAudioClient* client = nullptr;
            IAudioRenderClient* render = nullptr;
            UINT32 bufferFrames = 0;
            bool started = false;
            do
            {
                if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)))) break;
                if (FAILED(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device))) break;
                if (FAILED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client)))) break;
                WAVEFORMATEXTENSIBLE format{};
                format.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
                format.Format.nChannels = 2;
                format.Format.nSamplesPerSec = static_cast<DWORD>(outputRate_);
                format.Format.wBitsPerSample = 32;
                format.Format.nBlockAlign = 8;
                format.Format.nAvgBytesPerSec = format.Format.nSamplesPerSec * 8;
                format.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
                format.Samples.wValidBitsPerSample = 32;
                format.dwChannelMask = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
                format.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
                const auto bufferDuration = static_cast<REFERENCE_TIME>(AudioBufferSeconds * 10'000'000);
                if (FAILED(client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                              AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                              bufferDuration, 0, &format.Format, nullptr))) break;
                if (FAILED(client->GetBufferSize(&bufferFrames))) break;
                if (FAILED(client->GetService(IID_PPV_ARGS(&render)))) break;

                while (!stop_)
                {
                    bool reset, running;
                    {
                        std::lock_guard lock(gate_);
                        reset = audioReset_;
                        audioReset_ = false;
                        running = playing_ && !buffering_;
                    }
                    if (reset || !running)
                    {
                        if (started) { client->Stop(); started = false; }
                        if (reset) client->Reset();
                        Sleep(5);
                        continue;
                    }
                    UINT32 padding = 0;
                    if (FAILED(client->GetCurrentPadding(&padding))) break;
                    const UINT32 available = bufferFrames - padding;
                    if (available < bufferFrames / 8) { Sleep(5); continue; }
                    BYTE* data = nullptr;
                    if (FAILED(render->GetBuffer(available, &data))) break;
                    const UINT32 written = FillAudio(reinterpret_cast<float*>(data), available, padding);
                    render->ReleaseBuffer(written, 0);
                    if (written > 0 && !started) { client->Start(); started = true; }
                    if (written == 0) Sleep(5);
                }
                if (started) client->Stop();
            } while (false);
            if (render) render->Release();
            if (client) client->Release();
            if (device) device->Release();
            if (enumerator) enumerator->Release();
            if (SUCCEEDED(com)) CoUninitialize();
        }

        // Writes up to `capacity` stereo frames so that each sample plays when the clock reaches its
        // timestamp: late samples are dropped, early gaps are padded with silence.
        UINT32 FillAudio(float* out, UINT32 capacity, UINT32 padding)
        {
            std::lock_guard lock(gate_);
            const double rate = outputRate_;
            double nextPts = ClockLocked() + padding / rate;
            const float gain = muted_ ? 0.0f : static_cast<float>(volume_);
            UINT32 written = 0;
            while (written < capacity && !audioQueue_.empty())
            {
                auto& chunk = audioQueue_.front();
                const std::size_t frames = chunk.samples.size() / 2;
                const double chunkPts = chunk.pts + chunk.offset / rate;
                if (chunkPts < nextPts - SyncTolerance)
                {
                    const auto skip = std::min<std::size_t>(frames - chunk.offset, static_cast<std::size_t>((nextPts - chunkPts) * rate));
                    chunk.offset += std::max<std::size_t>(1, skip);
                    audioQueuedSeconds_ -= std::max<std::size_t>(1, skip) / rate;
                }
                else if (chunkPts > nextPts + SyncTolerance)
                {
                    const auto silence = std::min<UINT32>(capacity - written, static_cast<UINT32>((chunkPts - nextPts) * rate));
                    std::memset(out + written * 2, 0, static_cast<std::size_t>(silence) * 2 * sizeof(float));
                    written += silence;
                    nextPts += silence / rate;
                    if (silence == 0) break;
                    continue;
                }
                else
                {
                    const auto count = std::min<std::size_t>(frames - chunk.offset, capacity - written);
                    const float* source = chunk.samples.data() + chunk.offset * 2;
                    for (std::size_t i = 0; i < count * 2; ++i) out[written * 2 + i] = source[i] * gain;
                    chunk.offset += count;
                    written += static_cast<UINT32>(count);
                    nextPts += count / rate;
                    audioQueuedSeconds_ -= count / rate;
                }
                if (chunk.offset >= frames) audioQueue_.pop_front();
            }
            if (audioQueuedSeconds_ < 0) audioQueuedSeconds_ = 0;
            return written;
        }

        // ---------------------------------------------------------------- frames --------------

        int Convert(AVFrame* frame, std::uint8_t* destination, int width, int height, int stride)
        {
            std::lock_guard lock(scaleGate_);
            // Scale into the pre-rotation orientation, then rotate while copying out.
            const bool quarter = rotation_ % 2 == 1;
            const int scaledWidth = quarter ? height : width;
            const int scaledHeight = quarter ? width : height;
            sws_ = sws_getCachedContext(sws_, frame->width, frame->height, static_cast<AVPixelFormat>(frame->format),
                                        scaledWidth, scaledHeight, AV_PIX_FMT_BGRA, largeH264_ ? SWS_FAST_BILINEAR : SWS_BILINEAR,
                                        nullptr, nullptr, nullptr);
            if (!sws_) return E_FAIL;
            std::uint8_t* target = destination;
            int targetStride = stride;
            if (rotation_ != 0)
            {
                scratch_.resize(static_cast<std::size_t>(scaledWidth) * scaledHeight * 4);
                target = scratch_.data();
                targetStride = scaledWidth * 4;
            }
            std::uint8_t* planes[4] = { target, nullptr, nullptr, nullptr };
            const int strides[4] = { targetStride, 0, 0, 0 };
            if (sws_scale(sws_, frame->data, frame->linesize, 0, frame->height, planes, strides) <= 0) return E_FAIL;

            if (rotation_ != 0)
            {
                const auto* src = reinterpret_cast<const std::uint32_t*>(scratch_.data());
                for (int y = 0; y < height; ++y)
                {
                    auto* row = reinterpret_cast<std::uint32_t*>(destination + static_cast<std::size_t>(y) * stride);
                    for (int x = 0; x < width; ++x)
                    {
                        int sx, sy;
                        switch (rotation_)
                        {
                        case 1: sx = y; sy = scaledHeight - 1 - x; break;               // 90 clockwise
                        case 2: sx = scaledWidth - 1 - x; sy = scaledHeight - 1 - y; break;
                        default: sx = scaledWidth - 1 - y; sy = x; break;                // 270 clockwise
                        }
                        row[x] = src[static_cast<std::size_t>(sy) * scaledWidth + sx];
                    }
                }
            }
            for (int y = 0; y < height; ++y)
            {
                auto* row = destination + static_cast<std::size_t>(y) * stride;
                for (int x = 0; x < width; ++x) row[x * 4 + 3] = 255;
            }
            return 1;
        }

        SharedFile file_;
        AVIOContext* io_ = nullptr;
        AVFormatContext* format_ = nullptr;
        AVCodecContext* videoDecoder_ = nullptr;
        AVCodecContext* audioDecoder_ = nullptr;
        SwrContext* swr_ = nullptr;
        SwsContext* sws_ = nullptr;
        std::vector<std::uint8_t> scratch_;
        std::mutex scaleGate_;
        int videoIndex_ = -1, audioIndex_ = -1, outputRate_ = 48000;
        int displayWidth_ = 0, displayHeight_ = 0, rotation_ = 0;
        bool largeH264_ = false;
        double startOffset_ = 0, duration_ = 0;
        std::thread decodeThread_, audioThread_;
        std::atomic<bool> stop_{ false };

        // Everything below is guarded by gate_.
        std::mutex gate_;
        std::deque<QueuedFrame> videoQueue_;
        std::deque<AudioChunk> audioQueue_;
        double audioQueuedSeconds_ = 0;
        AVFrame* current_ = nullptr;
        double lastVideoPts_ = 0;
        double clockBase_ = 0, clockStart_ = 0, pendingSeek_ = -1;
        bool playing_ = true, buffering_ = true, ended_ = false, loop_ = true, muted_ = false, failed_ = false, audioReset_ = false;
        double volume_ = 1.0;
    };
}

extern "C" const PfnDliHook __pfnDliNotifyHook2 = FfmpegDelayLoadHook;

// Points the loader at a downloaded decoder pack. The path must end with a directory separator.
extern "C" __declspec(dllexport) int __cdecl GlideVideoSetFfmpegDirectoryW(const wchar_t* directory)
{
    if (!directory) return E_POINTER;
    std::lock_guard lock(ffmpegGate);
    ffmpegDirectoryOverride = directory;
    return S_OK;
}

int CreateFfmpegVideoBackend(const wchar_t* path, GlideVideoBackend** backend)
{
    *backend = nullptr;
    if (!EnsureFfmpegLoaded()) return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
    auto* player = new (std::nothrow) FfmpegBackend();
    if (!player) return E_OUTOFMEMORY;
    const int hr = player->Open(path);
    if (hr < 0) { delete player; return hr; }
    *backend = player;
    return S_OK;
}

#else

extern "C" __declspec(dllexport) int __cdecl GlideVideoSetFfmpegDirectoryW(const wchar_t*)
{
    return E_NOTIMPL;
}

int CreateFfmpegVideoBackend(const wchar_t*, GlideVideoBackend** backend)
{
    *backend = nullptr;
    return E_NOTIMPL;
}

#endif
