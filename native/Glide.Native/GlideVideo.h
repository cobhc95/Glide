// Glide.Native — overlay video playback interface (Windows Media Foundation only).
#pragma once

#include <cstdint>

struct GlideVideoInfo
{
    std::int32_t state;        // 0 loading, 1 ready, -1 error
    std::int32_t width;        // display width (pixel aspect applied)
    std::int32_t height;
    std::int32_t playing;
    std::int32_t ended;
    std::int32_t muted;
    std::int32_t loop;
    std::int32_t error;        // MF_MEDIA_ENGINE_ERR / HRESULT when state == -1
    double duration;           // seconds; 0 when unknown/live
    double position;           // seconds
    double volume;             // 0..1
    std::int32_t backend;      // always 1 (Media Foundation); kept for ABI layout
    std::int32_t reserved;
};

// One playing video. Every method is called with no lock held by the caller and must be
// thread-safe: managed code polls frames on a pump thread while the UI thread sends commands.
class GlideVideoBackend
{
public:
    virtual ~GlideVideoBackend() = default;
    virtual void GetInfo(GlideVideoInfo& info) = 0;
    virtual int SetPlaying(bool playing) = 0;
    virtual int Seek(double seconds) = 0;
    virtual int SetMuted(bool muted) = 0;
    virtual int SetVolume(double volume) = 0;
    virtual int SetLoop(bool loop) = 0;
    // 1 = new frame written, 0 = nothing new, < 0 = HRESULT failure.
    virtual int ReadFrame(std::uint8_t* destination, std::int32_t width, std::int32_t height, std::int32_t stride, bool force) = 0;
};
