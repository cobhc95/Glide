// Glide.ShellThumbnail — decoding core.
//
// Everything here runs inside Explorer's isolated thumbnail host. It must never load .NET, Avalonia
// or any Glide UI component, must never show UI, and must never throw across a COM boundary. Every
// path is total: malformed, truncated, hostile or gigantic input returns a failed DecodeResult so
// Explorer can fall back to its normal icon.

#include "ThumbnailCore.h"

#include <windows.h>
#include <wincodec.h>
#include <d2d1_3.h>
#include <d2d1svg.h>
#include <d3d11.h>
#include <dxgi.h>
#include <shlwapi.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <sstream>
#include <string>

#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "shlwapi.lib")

namespace glide::thumb
{
    namespace
    {
        constexpr std::uint32_t MaxDimension = 65'535;
        constexpr std::uint64_t MaxPixels = 256'000'000ull;   // ~1 GiB at 32bpp
        constexpr std::size_t MaxSourceBytes = 1ull << 30;     // 1 GiB
        constexpr std::uint32_t HardCapLongestSide = 2048;     // never render a 6000px source for a 256px thumb

        std::uint64_t NowMicros()
        {
            LARGE_INTEGER counter{}, frequency{};
            QueryPerformanceCounter(&counter);
            QueryPerformanceFrequency(&frequency);
            if (frequency.QuadPart == 0) return 0;
            return static_cast<std::uint64_t>((counter.QuadPart * 1'000'000ull) / frequency.QuadPart);
        }

        std::wstring NormalizeExtension(const wchar_t* extension)
        {
            std::wstring value = extension ? extension : L"";
            std::transform(value.begin(), value.end(), value.begin(),
                           [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
            return value;
        }

        bool HasPrefix(const std::uint8_t* data, std::size_t size, const char* magic, std::size_t length)
        {
            return size >= length && std::memcmp(data, magic, length) == 0;
        }

        bool AllocateImage(std::uint32_t width, std::uint32_t height, Image& image)
        {
            if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension) return false;
            if (static_cast<std::uint64_t>(width) * height > MaxPixels) return false;
            const std::size_t bytes = static_cast<std::size_t>(width) * height * 4u;
            image.width = width;
            image.height = height;
            try
            {
                image.pixels.assign(bytes, 0);
            }
            catch (...)
            {
                return false;
            }
            return true;
        }

        std::uint32_t ClampEdge(std::uint32_t value, std::uint32_t fallback)
        {
            if (value == 0) value = fallback;
            if (value > HardCapLongestSide) value = HardCapLongestSide;
            return value;
        }

        // Bilinear downscale. Only ever shrinks; callers bound the source first.
        void Downscale(const Image& source, std::uint32_t targetWidth, std::uint32_t targetHeight, Image& target)
        {
            if (!AllocateImage(targetWidth, targetHeight, target)) return;
            const double scaleX = static_cast<double>(source.width) / targetWidth;
            const double scaleY = static_cast<double>(source.height) / targetHeight;
            for (std::uint32_t y = 0; y < targetHeight; ++y)
            {
                const double sy = (y + 0.5) * scaleY - 0.5;
                std::uint32_t y0 = static_cast<std::uint32_t>(std::max(0.0, std::floor(sy)));
                std::uint32_t y1 = std::min(y0 + 1, source.height - 1);
                const double fy = std::clamp(sy - y0, 0.0, 1.0);
                for (std::uint32_t x = 0; x < targetWidth; ++x)
                {
                    const double sx = (x + 0.5) * scaleX - 0.5;
                    std::uint32_t x0 = static_cast<std::uint32_t>(std::max(0.0, std::floor(sx)));
                    std::uint32_t x1 = std::min(x0 + 1, source.width - 1);
                    const double fx = std::clamp(sx - x0, 0.0, 1.0);
                    const std::uint8_t* p00 = source.pixels.data() + (static_cast<std::size_t>(y0) * source.width + x0) * 4;
                    const std::uint8_t* p10 = source.pixels.data() + (static_cast<std::size_t>(y0) * source.width + x1) * 4;
                    const std::uint8_t* p01 = source.pixels.data() + (static_cast<std::size_t>(y1) * source.width + x0) * 4;
                    const std::uint8_t* p11 = source.pixels.data() + (static_cast<std::size_t>(y1) * source.width + x1) * 4;
                    std::uint8_t* dst = target.pixels.data() + (static_cast<std::size_t>(y) * targetWidth + x) * 4;
                    for (int c = 0; c < 4; ++c)
                    {
                        const double top = p00[c] + (p10[c] - p00[c]) * fx;
                        const double bottom = p01[c] + (p11[c] - p01[c]) * fx;
                        dst[c] = static_cast<std::uint8_t>(std::clamp(top + (bottom - top) * fy, 0.0, 255.0));
                    }
                }
            }
        }

        void ShrinkToFit(Image& image, std::uint32_t maxWidth, std::uint32_t maxHeight)
        {
            if (!image.Valid() || maxWidth == 0 || maxHeight == 0) return;
            const std::uint32_t longest = std::max(image.width, image.height);
            const std::uint32_t cap = std::max(maxWidth, maxHeight);
            if (longest <= cap) return;
            const double ratio = static_cast<double>(cap) / longest;
            const auto targetWidth = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(std::floor(image.width * ratio)));
            const auto targetHeight = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(std::floor(image.height * ratio)));
            Image scaled;
            Downscale(image, targetWidth, targetHeight, scaled);
            if (scaled.Valid()) image = std::move(scaled);
        }

        // ---------------------------------------------------------------- WIC ----------------

        struct ComPtr
        {
            template <typename T> static void Release(T*& p) { if (p) { p->Release(); p = nullptr; } }
        };

        bool CopySourceToImage(IWICImagingFactory* factory, IWICBitmapSource* source,
                               std::uint32_t maxWidth, std::uint32_t maxHeight, Image& out)
        {
            if (!factory || !source) return false;
            UINT sourceWidth = 0, sourceHeight = 0;
            if (FAILED(source->GetSize(&sourceWidth, &sourceHeight)) || sourceWidth == 0 || sourceHeight == 0) return false;
            if (sourceWidth > MaxDimension || sourceHeight > MaxDimension) return false;

            UINT targetWidth = sourceWidth;
            UINT targetHeight = sourceHeight;
            if (maxWidth > 0 && maxHeight > 0 && (sourceWidth > maxWidth || sourceHeight > maxHeight))
            {
                const double ratio = std::min(static_cast<double>(maxWidth) / sourceWidth,
                                              static_cast<double>(maxHeight) / sourceHeight);
                targetWidth = std::max<UINT>(1, static_cast<UINT>(std::floor(sourceWidth * ratio)));
                targetHeight = std::max<UINT>(1, static_cast<UINT>(std::floor(sourceHeight * ratio)));
            }

            IWICBitmapScaler* scaler = nullptr;
            IWICFormatConverter* converter = nullptr;
            bool ok = false;
            IWICBitmapSource* decodeSource = source;
            if (targetWidth != sourceWidth || targetHeight != sourceHeight)
            {
                if (FAILED(factory->CreateBitmapScaler(&scaler)) || !scaler) goto cleanup;
                if (FAILED(scaler->Initialize(source, targetWidth, targetHeight, WICBitmapInterpolationModeFant))) goto cleanup;
                decodeSource = scaler;
            }
            if (FAILED(factory->CreateFormatConverter(&converter)) || !converter) goto cleanup;
            if (FAILED(converter->Initialize(decodeSource, GUID_WICPixelFormat32bppBGRA,
                                             WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
                goto cleanup;

            if (AllocateImage(targetWidth, targetHeight, out))
            {
                const UINT stride = targetWidth * 4;
                if (SUCCEEDED(converter->CopyPixels(nullptr, stride, stride * targetHeight, out.pixels.data())))
                    ok = true;
            }

        cleanup:
            ComPtr::Release(converter);
            ComPtr::Release(scaler);
            if (!ok) out = {};
            return ok;
        }

        std::uint16_t ReadOrientation(IWICBitmapFrameDecode* frame)
        {
            if (!frame) return 1;
            IWICMetadataQueryReader* reader = nullptr;
            if (FAILED(frame->GetMetadataQueryReader(&reader)) || !reader) return 1;
            std::uint16_t orientation = 1;
            const wchar_t* paths[] = { L"/ifd/{ushort=274}", L"/app1/ifd/{ushort=274}" };
            for (const auto* query : paths)
            {
                PROPVARIANT value;
                PropVariantInit(&value);
                if (SUCCEEDED(reader->GetMetadataByName(query, &value)))
                {
                    if (value.vt == VT_UI2) orientation = value.uiVal;
                    else if (value.vt == VT_UI4) orientation = static_cast<std::uint16_t>(value.ulVal);
                    PropVariantClear(&value);
                    if (orientation >= 1 && orientation <= 8) break;
                    orientation = 1;
                }
                else
                {
                    PropVariantClear(&value);
                }
            }
            reader->Release();
            return orientation;
        }

        void ApplyOrientation(Image& image, std::uint16_t orientation)
        {
            if (orientation < 2 || orientation > 8 || !image.Valid()) return;
            const bool swap = orientation >= 5;
            const std::uint32_t outWidth = swap ? image.height : image.width;
            const std::uint32_t outHeight = swap ? image.width : image.height;
            Image oriented;
            if (!AllocateImage(outWidth, outHeight, oriented)) return;
            for (std::uint32_t y = 0; y < image.height; ++y)
            {
                for (std::uint32_t x = 0; x < image.width; ++x)
                {
                    std::uint32_t dx = x, dy = y;
                    switch (orientation)
                    {
                    case 2: dx = image.width - 1 - x; break;
                    case 3: dx = image.width - 1 - x; dy = image.height - 1 - y; break;
                    case 4: dy = image.height - 1 - y; break;
                    case 5: dx = y; dy = x; break;
                    case 6: dx = image.height - 1 - y; dy = x; break;
                    case 7: dx = image.height - 1 - y; dy = image.width - 1 - x; break;
                    case 8: dx = y; dy = image.width - 1 - x; break;
                    default: break;
                    }
                    std::memcpy(oriented.pixels.data() + (static_cast<std::size_t>(dy) * outWidth + dx) * 4,
                                image.pixels.data() + (static_cast<std::size_t>(y) * image.width + x) * 4, 4);
                }
            }
            image = std::move(oriented);
        }

        bool DecodeWithWic(const std::uint8_t* data, std::size_t size,
                           std::uint32_t maxWidth, std::uint32_t maxHeight,
                           bool preferEmbedded, Image& out, bool& embeddedUsed)
        {
            embeddedUsed = false;
            if (size == 0 || size > static_cast<std::size_t>(std::numeric_limits<DWORD>::max())) return false;

            IWICImagingFactory* factory = nullptr;
            IWICStream* stream = nullptr;
            IWICBitmapDecoder* decoder = nullptr;
            IWICBitmapFrameDecode* frame = nullptr;
            IWICBitmapSource* embedded = nullptr;
            bool ok = false;

            if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))) || !factory)
                goto cleanup;
            if (FAILED(factory->CreateStream(&stream)) || !stream) goto cleanup;
            if (FAILED(stream->InitializeFromMemory(const_cast<BYTE*>(data), static_cast<DWORD>(size)))) goto cleanup;
            if (FAILED(factory->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnDemand, &decoder)) || !decoder)
                goto cleanup;
            if (FAILED(decoder->GetFrame(0, &frame)) || !frame) goto cleanup;

            if (preferEmbedded && SUCCEEDED(frame->GetThumbnail(&embedded)) && embedded)
            {
                UINT tw = 0, th = 0;
                if (SUCCEEDED(embedded->GetSize(&tw, &th)) && tw > 0 && th > 0 &&
                    CopySourceToImage(factory, embedded, maxWidth, maxHeight, out))
                {
                    embeddedUsed = true;
                    ok = true;
                    goto cleanup;
                }
            }

            {
                const std::uint16_t orientation = ReadOrientation(frame);
                if (CopySourceToImage(factory, frame, maxWidth, maxHeight, out))
                {
                    ApplyOrientation(out, orientation);
                    ok = out.Valid();
                }
            }

        cleanup:
            ComPtr::Release(embedded);
            ComPtr::Release(frame);
            ComPtr::Release(decoder);
            ComPtr::Release(stream);
            ComPtr::Release(factory);
            if (!ok) out = {};
            return ok;
        }

        // ------------------------------------------------------- compact decoders --------------

        class Reader
        {
        public:
            Reader(const std::uint8_t* data, std::size_t size) : data_(data), size_(size) {}
            std::size_t Position() const { return position_; }
            std::size_t Size() const { return size_; }
            bool Seek(std::size_t position) { if (position > size_) return false; position_ = position; return true; }
            bool Skip(std::size_t count) { return Seek(position_ + count); }
            std::uint8_t U8() { return position_ < size_ ? data_[position_++] : 0; }
            std::uint16_t U16Le() { const auto a = U8(); return static_cast<std::uint16_t>(a | (U8() << 8)); }
            std::uint16_t U16Be() { const auto a = U8(); return static_cast<std::uint16_t>((a << 8) | U8()); }
            std::uint32_t U32Le() { const auto a = U16Le(); return static_cast<std::uint32_t>(a | (U16Le() << 16)); }
            std::uint32_t U32Be() { const auto a = U16Be(); return static_cast<std::uint32_t>((a << 16) | U16Be()); }
            bool Bytes(void* destination, std::size_t count)
            {
                if (position_ + count > size_) return false;
                std::memcpy(destination, data_ + position_, count);
                position_ += count;
                return true;
            }
            const std::uint8_t* Raw() const { return data_; }
            bool Remaining(std::size_t count) const { return position_ + count <= size_; }

        private:
            const std::uint8_t* data_;
            std::size_t size_;
            std::size_t position_ = 0;
        };

        void SetPixel(Image& image, std::uint32_t x, std::uint32_t y, std::uint8_t r, std::uint8_t g, std::uint8_t b, std::uint8_t a = 255)
        {
            std::uint8_t* p = image.pixels.data() + (static_cast<std::size_t>(y) * image.width + x) * 4;
            p[0] = b; p[1] = g; p[2] = r; p[3] = a;
        }

        std::uint32_t ReadU32BeAt(const std::uint8_t* data, std::size_t size, std::size_t position)
        {
            if (position + 4 > size) return 0;
            return (static_cast<std::uint32_t>(data[position]) << 24) |
                   (static_cast<std::uint32_t>(data[position + 1]) << 16) |
                   (static_cast<std::uint32_t>(data[position + 2]) << 8) |
                   static_cast<std::uint32_t>(data[position + 3]);
        }

        bool DecodeTga(const std::uint8_t* data, std::size_t size, Image& out)
        {
            Reader r(data, size);
            const std::uint8_t idLength = r.U8();
            const std::uint8_t colorMapType = r.U8();
            const std::uint8_t imageType = r.U8();
            r.U16Le(); r.U16Le();            // color map first entry + length
            const std::uint8_t colorMapEntrySize = r.U8();
            r.U16Le(); r.U16Le();            // x/y origin
            const std::uint16_t width = r.U16Le();
            const std::uint16_t height = r.U16Le();
            const std::uint8_t bpp = r.U8();
            const std::uint8_t descriptor = r.U8();
            if (width == 0 || height == 0 || !r.Skip(idLength)) return false;
            if (!AllocateImage(width, height, out)) return false;

            const bool rle = imageType == 9 || imageType == 10 || imageType == 11;
            const int base = imageType >= 9 ? imageType - 8 : imageType;
            const bool colorMapped = base == 1;
            const bool trueColor = base == 2;
            const bool gray = base == 3;
            if (!colorMapped && !trueColor && !gray) return false;

            const std::uint8_t* palette = nullptr;
            std::size_t paletteCount = 0;
            if (colorMapType == 1)
            {
                paletteCount = r.U16Le();
                const std::uint8_t firstEntry = r.U8();
                (void)firstEntry;
                const std::uint8_t entryBytes = static_cast<std::uint8_t>((colorMapEntrySize + 7) / 8);
                const std::size_t paletteBytes = paletteCount * entryBytes;
                if (!r.Remaining(paletteBytes)) return false;
                palette = r.Raw() + r.Position();
                r.Skip(paletteBytes);
            }

            const int pixelBytes = (bpp + 7) / 8;
            const bool topLeft = (descriptor & 0x20) != 0;
            const std::uint32_t total = static_cast<std::uint32_t>(width) * height;
            std::uint32_t produced = 0;

            auto readColor = [&](std::uint8_t* bgra) -> bool
            {
                if (trueColor)
                {
                    if (pixelBytes == 2)
                    {
                        const std::uint16_t value = r.U16Le();
                        bgra[0] = static_cast<std::uint8_t>(((value & 0x1F) * 255) / 31);
                        bgra[1] = static_cast<std::uint8_t>((((value >> 5) & 0x1F) * 255) / 31);
                        bgra[2] = static_cast<std::uint8_t>((((value >> 10) & 0x1F) * 255) / 31);
                        bgra[3] = (descriptor & 0x0F) ? 255 : 255;
                        return true;
                    }
                    if (!r.Bytes(bgra, pixelBytes)) return false;
                    bgra[3] = pixelBytes >= 4 ? bgra[3] : 255;
                    return true;
                }
                if (gray)
                {
                    const std::uint8_t value = r.U8();
                    bgra[0] = bgra[1] = bgra[2] = value;
                    bgra[3] = 255;
                    return true;
                }
                // colour-mapped
                const std::uint8_t index = r.U8();
                if (!palette || index >= paletteCount) return false;
                const std::uint8_t* entry = palette + static_cast<std::size_t>(index) * ((colorMapEntrySize + 7) / 8);
                if (colorMapEntrySize >= 24)
                {
                    bgra[0] = entry[0]; bgra[1] = entry[1]; bgra[2] = entry[2];
                    bgra[3] = colorMapEntrySize >= 32 ? entry[3] : 255;
                }
                else
                {
                    bgra[0] = bgra[1] = bgra[2] = entry[0];
                    bgra[3] = 255;
                }
                return true;
            };

            while (produced < total)
            {
                int run = 1;
                if (rle)
                {
                    std::uint8_t packet = r.U8();
                    run = (packet & 0x7F) + 1;
                    if (packet & 0x80) // raw packet
                    {
                        for (int i = 0; i < run && produced < total; ++i, ++produced)
                        {
                            std::uint8_t bgra[4] = { 0, 0, 0, 255 };
                            if (!readColor(bgra)) return false;
                            const std::uint32_t index = produced;
                            const std::uint32_t x = index % width;
                            const std::uint32_t y = index / width;
                            const std::uint32_t fy = topLeft ? y : height - 1 - y;
                            SetPixel(out, x, fy, bgra[2], bgra[1], bgra[0], bgra[3]);
                        }
                        continue;
                    }
                }
                std::uint8_t bgra[4] = { 0, 0, 0, 255 };
                if (!readColor(bgra)) return false;
                for (int i = 0; i < run && produced < total; ++i, ++produced)
                {
                    const std::uint32_t index = produced;
                    const std::uint32_t x = index % width;
                    const std::uint32_t y = index / width;
                    const std::uint32_t fy = topLeft ? y : height - 1 - y;
                    SetPixel(out, x, fy, bgra[2], bgra[1], bgra[0], bgra[3]);
                }
            }
            return true;
        }

        bool DecodePcx(const std::uint8_t* data, std::size_t size, Image& out)
        {
            if (size < 128) return false;
            Reader r(data, size);
            r.U8();                           // manufacturer
            r.U8();                           // version
            const std::uint8_t encoding = r.U8();
            const std::uint8_t bitsPerPixel = r.U8();
            const std::uint16_t xMin = r.U16Le(), yMin = r.U16Le(), xMax = r.U16Le(), yMax = r.U16Le();
            r.Skip(4);                        // horizontal/vertical resolution
            r.Skip(48);                       // 16-colour palette
            r.U8();                           // reserved
            const std::uint8_t planes = r.U8();
            r.U16Le();                        // bytes per line
            r.U16Le();                        // palette type
            r.Skip(58);                       // header filler
            if (encoding != 1 || bitsPerPixel != 1 && bitsPerPixel != 2 && bitsPerPixel != 4 && bitsPerPixel != 8)
                return false;
            const std::uint32_t width = static_cast<std::uint32_t>(xMax - xMin) + 1;
            const std::uint32_t height = static_cast<std::uint32_t>(yMax - yMin) + 1;
            if (!AllocateImage(width, height, out)) return false;

            std::uint8_t palette[256][3] = {};
            if (bitsPerPixel == 8)
            {
                // VGA palette lives in the final 769 bytes: 0x0C then 256 RGB triples.
                if (size < 769) return false;
                const std::uint8_t* tail = data + size - 769;
                if (tail[0] != 0x0C) return false;
                for (int i = 0; i < 256; ++i)
                {
                    palette[i][0] = tail[1 + i * 3 + 0];
                    palette[i][1] = tail[1 + i * 3 + 1];
                    palette[i][2] = tail[1 + i * 3 + 2];
                }
            }
            else if (bitsPerPixel == 4 || bitsPerPixel == 1)
            {
                for (int i = 0; i < 16; ++i)
                {
                    palette[i][0] = data[16 + i * 3 + 0];
                    palette[i][1] = data[16 + i * 3 + 1];
                    palette[i][2] = data[16 + i * 3 + 2];
                }
            }

            const std::uint32_t bytesPerLine = planes * ((width * bitsPerPixel + 7) / 8);
            std::vector<std::uint8_t> line(bytesPerLine);
            std::size_t read = 128;
            for (std::uint32_t y = 0; y < height; ++y)
            {
                std::size_t produced = 0;
                while (produced < bytesPerLine)
                {
                    if (read >= size) return false;
                    std::uint8_t value = data[read++];
                    if ((value & 0xC0) == 0xC0)
                    {
                        const int run = value & 0x3F;
                        if (read >= size) return false;
                        const std::uint8_t repeated = data[read++];
                        for (int i = 0; i < run && produced < bytesPerLine; ++i) line[produced++] = repeated;
                    }
                    else
                    {
                        line[produced++] = value;
                    }
                }
                for (std::uint32_t x = 0; x < width; ++x)
                {
                    std::uint8_t r8 = 0, g8 = 0, b8 = 0;
                    if (bitsPerPixel == 8)
                    {
                        const std::uint8_t index = line[x];
                        r8 = palette[index][0]; g8 = palette[index][1]; b8 = palette[index][2];
                    }
                    else if (planes >= 3)
                    {
                        const std::uint32_t rowBytes = (width * bitsPerPixel + 7) / 8;
                        b8 = line[x];
                        g8 = line[rowBytes + x];
                        r8 = line[rowBytes * 2 + x];
                    }
                    else
                    {
                        const std::uint32_t rowBytes = (width * bitsPerPixel + 7) / 8;
                        std::uint32_t index = 0;
                        if (bitsPerPixel == 1)
                        {
                            index = (line[x / 8] >> (7 - (x % 8))) & 1;
                            index = index ? 15 : 0;
                        }
                        else if (bitsPerPixel == 2)
                        {
                            index = (line[x / 4] >> (6 - 2 * (x % 4))) & 3;
                        }
                        else
                        {
                            index = (line[x / 2] >> (4 - 4 * (x % 2))) & 0x0F;
                        }
                        (void)rowBytes;
                        r8 = palette[index][0]; g8 = palette[index][1]; b8 = palette[index][2];
                    }
                    SetPixel(out, x, y, r8, g8, b8);
                }
            }
            return true;
        }

        bool DecodePnm(const std::uint8_t* data, std::size_t size, Image& out)
        {
            Reader r(data, size);
            auto skipWhitespaceAndComments = [&]()
            {
                while (r.Remaining(1))
                {
                    const std::uint8_t c = data[r.Position()];
                    if (c == '#') { while (r.Remaining(1) && data[r.Position()] != '\n') r.Skip(1); }
                    else if (c == ' ' || c == '\t' || c == '\r' || c == '\n') r.Skip(1);
                    else break;
                }
            };
            auto readNumber = [&]() -> long
            {
                skipWhitespaceAndComments();
                long value = 0;
                bool any = false;
                while (r.Remaining(1))
                {
                    const std::uint8_t c = data[r.Position()];
                    if (c < '0' || c > '9') break;
                    value = value * 10 + (c - '0');
                    any = true;
                    r.Skip(1);
                }
                return any ? value : -1;
            };

            if (!HasPrefix(data, size, "P", 1)) return false;
            const std::uint8_t kind = data[1];
            r.Skip(2);

            if (kind == 7) // PAM
            {
                std::uint32_t width = 0, height = 0, depth = 0, maxValue = 255;
                // Parse the header line by line until ENDHDR.
                while (r.Remaining(1))
                {
                    std::string line;
                    while (r.Remaining(1) && data[r.Position()] != '\n') line.push_back(static_cast<char>(r.U8()));
                    if (r.Remaining(1)) r.Skip(1);
                    if (line.rfind("WIDTH", 0) == 0) width = static_cast<std::uint32_t>(std::strtoul(line.c_str() + 5, nullptr, 10));
                    else if (line.rfind("HEIGHT", 0) == 0) height = static_cast<std::uint32_t>(std::strtoul(line.c_str() + 6, nullptr, 10));
                    else if (line.rfind("DEPTH", 0) == 0) depth = static_cast<std::uint32_t>(std::strtoul(line.c_str() + 5, nullptr, 10));
                    else if (line.rfind("MAXVAL", 0) == 0) maxValue = static_cast<std::uint32_t>(std::strtoul(line.c_str() + 6, nullptr, 10));
                    else if (line.rfind("ENDHDR", 0) == 0) break;
                }
                if (width == 0 || height == 0 || depth == 0 || depth > 4) return false;
                if (!AllocateImage(width, height, out)) return false;
                const std::size_t bytesPerSample = maxValue > 255 ? 2 : 1;
                for (std::uint32_t y = 0; y < height; ++y)
                {
                    for (std::uint32_t x = 0; x < width; ++x)
                    {
                        std::uint8_t samples[4] = { 0, 0, 0, 255 };
                        for (std::uint32_t c = 0; c < depth; ++c)
                        {
                            std::uint32_t value = 0;
                            if (bytesPerSample == 2) value = r.U16Be();
                            else value = r.U8();
                            if (maxValue != 0 && maxValue != 255)
                                value = static_cast<std::uint32_t>((value * 255u) / maxValue);
                            samples[c] = static_cast<std::uint8_t>(value);
                        }
                        if (depth >= 3) SetPixel(out, x, y, samples[0], samples[1], samples[2], depth >= 4 ? samples[3] : 255);
                        else SetPixel(out, x, y, samples[0], samples[0], samples[0]);
                    }
                }
                return true;
            }

            const long widthLong = readNumber();
            const long heightLong = readNumber();
            long maxValue = 1;
            if (kind != 1 && kind != 4) maxValue = readNumber();
            if (widthLong <= 0 || heightLong <= 0 || maxValue <= 0) return false;
            const auto width = static_cast<std::uint32_t>(widthLong);
            const auto height = static_cast<std::uint32_t>(heightLong);
            if (!AllocateImage(width, height, out)) return false;

            const bool ascii = kind == 1 || kind == 2 || kind == 3;
            const std::size_t bytesPerSample = maxValue > 255 ? 2 : 1;
            auto nextSample = [&]() -> std::uint32_t
            {
                if (ascii)
                {
                    const long value = readNumber();
                    return value < 0 ? 0u : static_cast<std::uint32_t>(value);
                }
                if (bytesPerSample == 2) return r.U16Be();
                return r.U8();
            };
            auto scale = [&](std::uint32_t value) -> std::uint8_t
            {
                if (maxValue == 0 || maxValue == 255) return static_cast<std::uint8_t>(value);
                return static_cast<std::uint8_t>((value * 255u) / static_cast<std::uint32_t>(maxValue));
            };

            if (kind == 4)
            {
                // Binary PBM pads every row to a byte boundary.
                const std::size_t rowBytes = (static_cast<std::size_t>(width) + 7) / 8;
                std::vector<std::uint8_t> row(rowBytes);
                for (std::uint32_t y = 0; y < height; ++y)
                {
                    if (!r.Bytes(row.data(), rowBytes)) return false;
                    for (std::uint32_t x = 0; x < width; ++x)
                    {
                        const bool set = (row[x / 8] >> (7 - (x % 8))) & 1;
                        const std::uint8_t value = set ? 0 : 255;
                        SetPixel(out, x, y, value, value, value);
                    }
                }
                return true;
            }

            for (std::uint32_t y = 0; y < height; ++y)
            {
                for (std::uint32_t x = 0; x < width; ++x)
                {
                    if (kind == 1) // ASCII PBM bitmap: 1 = black
                    {
                        const std::uint32_t bit = nextSample();
                        const std::uint8_t value = bit ? 0 : 255;
                        SetPixel(out, x, y, value, value, value);
                    }
                    else if (kind == 2 || kind == 5) // grey
                    {
                        const std::uint8_t value = scale(nextSample());
                        SetPixel(out, x, y, value, value, value);
                    }
                    else // P3/P6 colour
                    {
                        const std::uint8_t red = scale(nextSample());
                        const std::uint8_t green = scale(nextSample());
                        const std::uint8_t blue = scale(nextSample());
                        SetPixel(out, x, y, red, green, blue);
                    }
                }
            }
            return true;
        }

        bool DecodeQoi(const std::uint8_t* data, std::size_t size, Image& out)
        {
            if (size < 14 || std::memcmp(data, "qoif", 4) != 0) return false;
            Reader r(data, size);
            r.Skip(4);
            const std::uint32_t width = r.U32Be();
            const std::uint32_t height = r.U32Be();
            const std::uint8_t channels = r.U8();
            r.U8(); // colour space
            if (!AllocateImage(width, height, out)) return false;
            (void)channels;

            std::uint8_t index[64][4] = {};
            std::uint8_t px[4] = { 0, 0, 0, 255 };
            const std::uint32_t total = width * height;
            std::uint32_t produced = 0;
            while (produced < total)
            {
                const std::uint8_t b1 = r.U8();
                if (b1 == 0xFE)
                {
                    px[0] = r.U8(); px[1] = r.U8(); px[2] = r.U8();
                }
                else if (b1 == 0xFF)
                {
                    px[0] = r.U8(); px[1] = r.U8(); px[2] = r.U8(); px[3] = r.U8();
                }
                else
                {
                    switch (b1 & 0xC0)
                    {
                    case 0x00: // INDEX
                        px[0] = index[b1 & 0x3F][0];
                        px[1] = index[b1 & 0x3F][1];
                        px[2] = index[b1 & 0x3F][2];
                        px[3] = index[b1 & 0x3F][3];
                        break;
                    case 0x40: // DIFF (single byte)
                        px[0] = static_cast<std::uint8_t>(px[0] + ((b1 >> 4) & 0x03) - 2);
                        px[1] = static_cast<std::uint8_t>(px[1] + ((b1 >> 2) & 0x03) - 2);
                        px[2] = static_cast<std::uint8_t>(px[2] + (b1 & 0x03) - 2);
                        break;
                    case 0x80:
                    {
                        const std::uint8_t b2 = r.U8();
                        const int dg = (b1 & 0x3F) - 32;
                        px[0] = static_cast<std::uint8_t>(px[0] + dg + (b2 >> 4) - 8);
                        px[1] = static_cast<std::uint8_t>(px[1] + dg);
                        px[2] = static_cast<std::uint8_t>(px[2] + dg + (b2 & 0x0F) - 8);
                        break;
                    }
                    default: // 0xC0 run
                    {
                        const int run = (b1 & 0x3F) + 1;
                        for (int i = 0; i < run && produced < total; ++i, ++produced)
                            SetPixel(out, produced % width, produced / width, px[0], px[1], px[2], px[3]);
                        const std::uint8_t hash = static_cast<std::uint8_t>((px[0] * 3 + px[1] * 5 + px[2] * 7 + px[3] * 11) % 64);
                        std::memcpy(index[hash], px, 4);
                        continue;
                    }
                    }
                }
                const std::uint8_t hash = static_cast<std::uint8_t>((px[0] * 3 + px[1] * 5 + px[2] * 7 + px[3] * 11) % 64);
                std::memcpy(index[hash], px, 4);
                SetPixel(out, produced % width, produced / width, px[0], px[1], px[2], px[3]);
                ++produced;
            }
            return true;
        }

        bool DecodeHdr(const std::uint8_t* data, std::size_t size, Image& out)
        {
            // Radiance RGBE. Header is text until a blank line, then a resolution line.
            if (!HasPrefix(data, size, "#?", 2)) return false;
            std::size_t position = 0;
            bool sawBlank = false;
            while (position < size && !sawBlank)
            {
                const std::size_t lineStart = position;
                while (position < size && data[position] != '\n') ++position;
                std::size_t lineEnd = position;
                if (lineEnd > lineStart && data[lineEnd - 1] == '\r') --lineEnd;
                if (position < size) ++position;   // consume the newline
                if (lineEnd == lineStart) sawBlank = true;
            }
            // Resolution line: "-Y height +X width" (or variants).
            int heightSign = 0, widthSign = 0;
            std::uint32_t height = 0, width = 0;
            {
                while (position < size && (data[position] == ' ' || data[position] == '\t')) ++position;
                if (position >= size) return false;
                heightSign = data[position] == '-' ? -1 : 1;
                ++position;
                while (position < size && data[position] >= '0' && data[position] <= '9')
                    height = height * 10 + (data[position++] - '0');
                while (position < size && data[position] == ' ') ++position;
                if (position >= size) return false;
                widthSign = data[position] == '-' ? -1 : 1;
                ++position;
                while (position < size && data[position] >= '0' && data[position] <= '9')
                    width = width * 10 + (data[position++] - '0');
                if (position < size && data[position] == '\n') ++position;
            }
            if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension) return false;
            if (!AllocateImage(width, height, out)) return false;

            auto writeRgbe = [&](std::uint32_t x, std::uint32_t y, std::uint8_t re, std::uint8_t g, std::uint8_t b, std::uint8_t e)
            {
                std::uint8_t r = 0, gg = 0, bb = 0;
                if (e != 0)
                {
                    const double scale = std::ldexp(1.0, e - (128 + 8));
                    r = static_cast<std::uint8_t>(std::clamp(std::pow(re * scale, 1.0 / 2.2) * 255.0, 0.0, 255.0));
                    gg = static_cast<std::uint8_t>(std::clamp(std::pow(g * scale, 1.0 / 2.2) * 255.0, 0.0, 255.0));
                    bb = static_cast<std::uint8_t>(std::clamp(std::pow(b * scale, 1.0 / 2.2) * 255.0, 0.0, 255.0));
                }
                const std::uint32_t fy = heightSign < 0 ? y : height - 1 - y;
                const std::uint32_t fx = widthSign < 0 ? width - 1 - x : x;
                SetPixel(out, fx, fy, r, gg, bb);
            };

            std::vector<std::uint8_t> scanline(width * 4);
            for (std::uint32_t y = 0; y < height; ++y)
            {
                if (position + 4 > size) return false;
                const bool newRle = width >= 8 && width < 32768 && data[position] == 2 && data[position + 1] == 2 &&
                                    (data[position + 2] & 0x80) == 0;
                if (newRle)
                {
                    const std::uint32_t scanWidth = (data[position + 2] << 8) | data[position + 3];
                    position += 4;
                    if (scanWidth != width) return false;
                    for (int channel = 0; channel < 4; ++channel)
                    {
                        std::uint32_t x = 0;
                        while (x < width)
                        {
                            if (position >= size) return false;
                            std::uint8_t count = data[position++];
                            if (count > 128)
                            {
                                const int run = count - 128;
                                if (position >= size) return false;
                                const std::uint8_t value = data[position++];
                                for (int i = 0; i < run && x < width; ++i) scanline[x++ * 4 + channel] = value;
                            }
                            else
                            {
                                for (int i = 0; i < count && x < width; ++i)
                                {
                                    if (position >= size) return false;
                                    scanline[x++ * 4 + channel] = data[position++];
                                }
                            }
                        }
                    }
                }
                else
                {
                    for (std::uint32_t x = 0; x < width; ++x)
                    {
                        if (position + 4 > size) return false;
                        scanline[x * 4 + 0] = data[position++];
                        scanline[x * 4 + 1] = data[position++];
                        scanline[x * 4 + 2] = data[position++];
                        scanline[x * 4 + 3] = data[position++];
                    }
                }
                for (std::uint32_t x = 0; x < width; ++x)
                    writeRgbe(x, y, scanline[x * 4 + 0], scanline[x * 4 + 1], scanline[x * 4 + 2], scanline[x * 4 + 3]);
            }
            return true;
        }

        bool DecodeWbmp(const std::uint8_t* data, std::size_t size, Image& out)
        {
            Reader r(data, size);
            const std::uint8_t type = r.U8();
            if (type != 0) return false; // only type 0 (no compression) is defined
            r.U8(); // fixed header
            auto readMb = [&]() -> std::uint32_t
            {
                std::uint32_t value = 0;
                for (int i = 0; i < 5; ++i)
                {
                    const std::uint8_t b = r.U8();
                    value = (value << 7) | (b & 0x7F);
                    if ((b & 0x80) == 0) break;
                }
                return value;
            };
            const std::uint32_t width = readMb();
            const std::uint32_t height = readMb();
            if (!AllocateImage(width, height, out)) return false;
            for (std::uint32_t y = 0; y < height; ++y)
            {
                std::uint32_t x = 0;
                while (x < width)
                {
                    if (!r.Remaining(1)) return false;
                    const std::uint8_t b = r.U8();
                    for (int bit = 7; bit >= 0 && x < width; --bit, ++x)
                    {
                        const std::uint8_t value = (b >> bit) & 1 ? 0 : 255; // 1 = black
                        SetPixel(out, x, y, value, value, value);
                    }
                }
            }
            return true;
        }

        bool DecodeXbm(const std::uint8_t* data, std::size_t size, Image& out)
        {
            std::string text(reinterpret_cast<const char*>(data), size);
            auto findDefine = [&](const char* suffix) -> long
            {
                std::size_t position = 0;
                while ((position = text.find("#define", position)) != std::string::npos)
                {
                    const std::size_t end = text.find('\n', position);
                    const std::string line = text.substr(position, end == std::string::npos ? std::string::npos : end - position);
                    if (line.find(suffix) != std::string::npos)
                    {
                        const std::size_t valueStart = line.find_last_of(" \t");
                        if (valueStart != std::string::npos)
                            return std::strtol(line.c_str() + valueStart + 1, nullptr, 10);
                    }
                    position = end == std::string::npos ? text.size() : end + 1;
                }
                return -1;
            };
            const long width = findDefine("_width");
            const long height = findDefine("_height");
            if (width <= 0 || height <= 0) return false;
            if (!AllocateImage(static_cast<std::uint32_t>(width), static_cast<std::uint32_t>(height), out)) return false;

            std::vector<std::uint8_t> bits;
            std::size_t position = 0;
            while ((position = text.find("0x", position)) != std::string::npos)
            {
                const std::size_t comma = text.find_first_of(",}", position);
                const std::string token = text.substr(position + 2, (comma == std::string::npos ? text.size() : comma) - position - 2);
                bits.push_back(static_cast<std::uint8_t>(std::strtoul(token.c_str(), nullptr, 16)));
                position = comma == std::string::npos ? text.size() : comma + 1;
                if (bits.size() > (static_cast<std::size_t>(width) * height + 7) / 8 + 8) break;
            }
            const std::size_t rowBytes = (static_cast<std::size_t>(width) + 7) / 8;
            for (long y = 0; y < height; ++y)
            {
                for (long x = 0; x < width; ++x)
                {
                    const std::size_t index = static_cast<std::size_t>(y) * rowBytes + x / 8;
                    if (index >= bits.size()) return false;
                    const bool set = (bits[index] >> (x % 8)) & 1; // LSB first
                    const std::uint8_t value = set ? 0 : 255;
                    SetPixel(out, static_cast<std::uint32_t>(x), static_cast<std::uint32_t>(y), value, value, value);
                }
            }
            return true;
        }

        bool DecodeXpm(const std::uint8_t* data, std::size_t size, Image& out)
        {
            std::string text(reinterpret_cast<const char*>(data), size);
            const std::size_t values = text.find('"');
            if (values == std::string::npos) return false;
            const std::size_t valuesEnd = text.find('"', values + 1);
            if (valuesEnd == std::string::npos) return false;
            std::istringstream header(text.substr(values + 1, valuesEnd - values - 1));
            int width = 0, height = 0, colors = 0, chars = 0;
            if (!(header >> width >> height >> colors >> chars) || width <= 0 || height <= 0 || chars <= 0) return false;
            if (!AllocateImage(static_cast<std::uint32_t>(width), static_cast<std::uint32_t>(height), out)) return false;

            std::vector<std::string> keys(colors);
            std::vector<std::array<std::uint8_t, 4>> valuesRgba(colors, { 0, 0, 0, 255 });
            std::size_t position = valuesEnd + 1;
            for (int i = 0; i < colors; ++i)
            {
                const std::size_t start = text.find('"', position);
                if (start == std::string::npos) return false;
                const std::size_t end = text.find('"', start + 1);
                if (end == std::string::npos) return false;
                const std::string entry = text.substr(start + 1, end - start - 1);
                position = end + 1;
                if (static_cast<int>(entry.size()) < chars) return false;
                keys[i] = entry.substr(0, chars);
                const std::size_t colorAt = entry.find('c');
                if (colorAt == std::string::npos) continue;
                std::string color = entry.substr(colorAt + 1);
                while (!color.empty() && (color.front() == ' ' || color.front() == '\t')) color.erase(color.begin());
                if (color.rfind("None", 0) == 0) { valuesRgba[i][3] = 0; continue; }
                if (color.size() >= 7 && color[0] == '#')
                {
                    const unsigned long value = std::strtoul(color.c_str() + 1, nullptr, 16);
                    valuesRgba[i][0] = static_cast<std::uint8_t>((value >> 16) & 0xFF);
                    valuesRgba[i][1] = static_cast<std::uint8_t>((value >> 8) & 0xFF);
                    valuesRgba[i][2] = static_cast<std::uint8_t>(value & 0xFF);
                }
            }
            for (int y = 0; y < height; ++y)
            {
                const std::size_t start = text.find('"', position);
                if (start == std::string::npos) return false;
                const std::size_t end = text.find('"', start + 1);
                if (end == std::string::npos) return false;
                const std::string row = text.substr(start + 1, end - start - 1);
                position = end + 1;
                for (int x = 0; x < width; ++x)
                {
                    const std::string key = row.substr(static_cast<std::size_t>(x) * chars, chars);
                    for (int i = 0; i < colors; ++i)
                    {
                        if (keys[i] == key)
                        {
                            SetPixel(out, static_cast<std::uint32_t>(x), static_cast<std::uint32_t>(y),
                                     valuesRgba[i][0], valuesRgba[i][1], valuesRgba[i][2], valuesRgba[i][3]);
                            break;
                        }
                    }
                }
            }
            return true;
        }

        bool DecodeSgi(const std::uint8_t* data, std::size_t size, Image& out)
        {
            Reader r(data, size);
            if (r.U16Be() != 0x01DA) return false;
            const std::uint8_t storage = r.U8();
            const std::uint8_t bytesPerChannel = r.U8();
            r.U16Be(); // dimension
            const std::uint16_t width = r.U16Be();
            const std::uint16_t height = r.U16Be();
            const std::uint16_t channels = r.U16Be();
            r.Skip(500 - 12);
            if (width == 0 || height == 0 || channels < 1 || channels > 4) return false;
            if (bytesPerChannel != 1 && bytesPerChannel != 2) return false;
            if (!AllocateImage(width, height, out)) return false;

            const std::uint32_t planeSize = static_cast<std::uint32_t>(width) * height;
            std::vector<std::uint8_t> planar(static_cast<std::size_t>(planeSize) * channels * bytesPerChannel, 0);

            if (storage == 1) // RLE
            {
                const std::size_t tableBytes = static_cast<std::size_t>(height) * channels * 4 * 2;
                if (!r.Remaining(tableBytes)) return false;
                std::size_t tablePosition = r.Position();
                r.Skip(tableBytes);
                for (std::uint32_t channel = 0; channel < channels; ++channel)
                {
                    for (std::uint32_t y = 0; y < height; ++y)
                    {
                        const std::uint32_t offset = ReadU32BeAt(data, size, tablePosition); tablePosition += 4;
                        const std::uint32_t length = ReadU32BeAt(data, size, tablePosition); tablePosition += 4;
                        if (offset + length > size) return false;
                        std::size_t source = offset;
                        std::size_t produced = 0;
                        std::uint8_t* plane = planar.data() + (static_cast<std::size_t>(channel) * planeSize + y * width) * bytesPerChannel;
                        while (produced < width && source < offset + length)
                        {
                            std::uint8_t packet = data[source++];
                            const int count = packet & 0x7F;
                            if (count == 0) break;
                            if (packet & 0x80)
                            {
                                for (int i = 0; i < count && produced < width; ++i)
                                {
                                    for (int b = 0; b < bytesPerChannel; ++b)
                                        if (source < offset + length) plane[produced * bytesPerChannel + b] = data[source++];
                                    ++produced;
                                }
                            }
                            else
                            {
                                for (int i = 0; i < count && produced < width; ++i)
                                    for (int b = 0; b < bytesPerChannel; ++b)
                                        plane[produced * bytesPerChannel + b] = source < offset + length ? data[source] : 0;
                                if (source < offset + length) ++source;
                                produced += count;
                            }
                        }
                    }
                }
            }
            else
            {
                const std::size_t needed = static_cast<std::size_t>(planeSize) * channels * bytesPerChannel;
                if (!r.Remaining(needed)) return false;
                r.Bytes(planar.data(), needed);
            }

            for (std::uint32_t y = 0; y < height; ++y)
            {
                for (std::uint32_t x = 0; x < width; ++x)
                {
                    const std::size_t index = static_cast<std::size_t>(y) * width + x;
                    auto sample = [&](std::uint32_t channel) -> std::uint8_t
                    {
                        const std::uint8_t* base = planar.data() + (static_cast<std::size_t>(channel) * planeSize + index) * bytesPerChannel;
                        return bytesPerChannel == 2 ? base[0] : base[0]; // 16-bit: take the high byte
                    };
                    const std::uint8_t red = sample(0);
                    const std::uint8_t green = channels >= 3 ? sample(1) : red;
                    const std::uint8_t blue = channels >= 3 ? sample(2) : red;
                    const std::uint8_t alpha = channels == 4 ? sample(3) : 255;
                    SetPixel(out, x, y, red, green, blue, alpha);
                }
            }
            return true;
        }

        bool DecodeCompact(const std::uint8_t* data, std::size_t size, const std::wstring& extension, Image& out)
        {
            if (extension == L".tga" || extension == L".targa" || extension == L".icb" || extension == L".vda" || extension == L".vst")
                return DecodeTga(data, size, out);
            if (extension == L".pcx") return DecodePcx(data, size, out);
            if (extension == L".pnm" || extension == L".ppm" || extension == L".pgm" || extension == L".pbm" || extension == L".pam")
                return DecodePnm(data, size, out);
            if (extension == L".qoi") return DecodeQoi(data, size, out);
            if (extension == L".hdr" || extension == L".rgbe") return DecodeHdr(data, size, out);
            if (extension == L".wbmp") return DecodeWbmp(data, size, out);
            if (extension == L".xbm") return DecodeXbm(data, size, out);
            if (extension == L".xpm") return DecodeXpm(data, size, out);
            if (extension == L".sgi" || extension == L".rgb" || extension == L".rgba" || extension == L".bw")
                return DecodeSgi(data, size, out);
            return false;
        }

        // ------------------------------------------------------------- SVG (Direct2D) ---------

        // Reads the intrinsic size from an SVG's width/height/viewBox attributes. D2D can scale to a
        // viewport, but we need the aspect ratio before we can choose that viewport.
        bool ReadSvgIntrinsicSize(const std::uint8_t* data, std::size_t size, float& width, float& height)
        {
            const std::size_t limit = std::min<std::size_t>(size, 16 * 1024);
            std::string head(reinterpret_cast<const char*>(data), limit);
            auto attribute = [&](const char* name) -> double
            {
                const std::string needle = std::string(name) + "=\"";
                const std::size_t at = head.find(needle);
                if (at == std::string::npos) return 0.0;
                return std::strtod(head.c_str() + at + needle.size(), nullptr);
            };
            double w = attribute("width");
            double h = attribute("height");
            if (w <= 0 || h <= 0)
            {
                const std::size_t at = head.find("viewBox=\"");
                if (at != std::string::npos)
                {
                    double x = 0, y = 0, vbw = 0, vbh = 0;
                    if (std::sscanf(head.c_str() + at + 9, "%lf %lf %lf %lf", &x, &y, &vbw, &vbh) == 4 && vbw > 0 && vbh > 0)
                    {
                        if (w <= 0) w = vbw;
                        if (h <= 0) h = vbh;
                    }
                }
            }
            if (w <= 0 || h <= 0) return false;
            width = static_cast<float>(w);
            height = static_cast<float>(h);
            return true;
        }

        bool RenderSvg(const std::uint8_t* data, std::size_t size, std::uint32_t maxEdge, Image& out)
        {
            float intrinsicWidth = 0, intrinsicHeight = 0;
            if (!ReadSvgIntrinsicSize(data, size, intrinsicWidth, intrinsicHeight)) return false;

            const double ratio = std::min(static_cast<double>(maxEdge) / intrinsicWidth,
                                          static_cast<double>(maxEdge) / intrinsicHeight);
            const auto targetWidth = static_cast<std::uint32_t>(std::max(1.0, std::floor(intrinsicWidth * ratio)));
            const auto targetHeight = static_cast<std::uint32_t>(std::max(1.0, std::floor(intrinsicHeight * ratio)));
            if (!AllocateImage(targetWidth, targetHeight, out)) return false;

            IStream* stream = SHCreateMemStream(data, static_cast<UINT>(size));
            if (!stream) { out = {}; return false; }

            ID3D11Device* device = nullptr;
            ID3D11DeviceContext* context = nullptr;
            IDXGIDevice* dxgiDevice = nullptr;
            ID2D1Factory1* factory = nullptr;
            ID2D1Device* d2dDevice = nullptr;
            ID2D1DeviceContext* deviceContext = nullptr;
            ID2D1DeviceContext5* deviceContext5 = nullptr;
            ID3D11Texture2D* targetTexture = nullptr;
            ID3D11Texture2D* stagingTexture = nullptr;
            ID2D1Bitmap1* targetBitmap = nullptr;
            ID2D1SvgDocument* svg = nullptr;
            bool ok = false;

            D3D_FEATURE_LEVEL level{};
            const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1 };
            if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                         levels, 3, D3D11_SDK_VERSION, &device, &level, &context)) || !device)
                goto cleanup;
            if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice))) || !dxgiDevice) goto cleanup;
            if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, IID_PPV_ARGS(&factory))) || !factory) goto cleanup;
            if (FAILED(factory->CreateDevice(dxgiDevice, &d2dDevice)) || !d2dDevice) goto cleanup;
            if (FAILED(d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &deviceContext)) || !deviceContext) goto cleanup;
            if (FAILED(deviceContext->QueryInterface(IID_PPV_ARGS(&deviceContext5))) || !deviceContext5) goto cleanup;

            {
                D3D11_TEXTURE2D_DESC description{};
                description.Width = targetWidth;
                description.Height = targetHeight;
                description.MipLevels = 1;
                description.ArraySize = 1;
                description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                description.SampleDesc.Count = 1;
                description.Usage = D3D11_USAGE_DEFAULT;
                description.BindFlags = D3D11_BIND_RENDER_TARGET;
                if (FAILED(device->CreateTexture2D(&description, nullptr, &targetTexture)) || !targetTexture) goto cleanup;
                description.Usage = D3D11_USAGE_STAGING;
                description.BindFlags = 0;
                description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                if (FAILED(device->CreateTexture2D(&description, nullptr, &stagingTexture)) || !stagingTexture) goto cleanup;
            }

            {
                IDXGISurface* surface = nullptr;
                if (FAILED(targetTexture->QueryInterface(IID_PPV_ARGS(&surface))) || !surface) goto cleanup;
                const D2D1_BITMAP_PROPERTIES1 properties = D2D1::BitmapProperties1(
                    D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
                    D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
                const HRESULT hr = deviceContext5->CreateBitmapFromDxgiSurface(surface, &properties, &targetBitmap);
                surface->Release();
                if (FAILED(hr) || !targetBitmap) goto cleanup;
            }

            {
                const D2D1_SIZE_F viewport = D2D1::SizeF(static_cast<float>(targetWidth), static_cast<float>(targetHeight));
                if (FAILED(deviceContext5->CreateSvgDocument(stream, viewport, &svg)) || !svg) goto cleanup;
                deviceContext5->SetTarget(targetBitmap);
                deviceContext5->SetDpi(96.0f, 96.0f);
                deviceContext5->BeginDraw();
                deviceContext5->Clear(D2D1::ColorF(D2D1::ColorF::White));
                // Direct2D draws the document at its intrinsic size; scale it to fill the target.
                deviceContext5->SetTransform(D2D1::Matrix3x2F::Scale(
                    static_cast<float>(targetWidth) / intrinsicWidth,
                    static_cast<float>(targetHeight) / intrinsicHeight));
                deviceContext5->DrawSvgDocument(svg);
                if (FAILED(deviceContext5->EndDraw())) goto cleanup;
                deviceContext5->SetTarget(nullptr);
            }

            {
                context->CopyResource(stagingTexture, targetTexture);
                D3D11_MAPPED_SUBRESOURCE mapped{};
                if (FAILED(context->Map(stagingTexture, 0, D3D11_MAP_READ, 0, &mapped))) goto cleanup;
                for (std::uint32_t y = 0; y < targetHeight; ++y)
                {
                    const std::uint8_t* source = static_cast<const std::uint8_t*>(mapped.pData) + static_cast<std::size_t>(y) * mapped.RowPitch;
                    std::memcpy(out.pixels.data() + static_cast<std::size_t>(y) * targetWidth * 4, source, static_cast<std::size_t>(targetWidth) * 4);
                }
                context->Unmap(stagingTexture, 0);
            }
            ok = true;

        cleanup:
            ComPtr::Release(svg);
            ComPtr::Release(targetBitmap);
            ComPtr::Release(stagingTexture);
            ComPtr::Release(targetTexture);
            ComPtr::Release(deviceContext5);
            ComPtr::Release(deviceContext);
            ComPtr::Release(d2dDevice);
            ComPtr::Release(factory);
            ComPtr::Release(dxgiDevice);
            ComPtr::Release(context);
            ComPtr::Release(device);
            stream->Release();
            if (!ok) out = {};
            return ok;
        }
    } // namespace

    bool IsCompactRasterExtension(const wchar_t* extension)
    {
        const std::wstring value = NormalizeExtension(extension);
        return value == L".tga" || value == L".targa" || value == L".icb" || value == L".vda" || value == L".vst" ||
               value == L".pcx" || value == L".pnm" || value == L".ppm" || value == L".pgm" || value == L".pbm" ||
               value == L".pam" || value == L".qoi" || value == L".hdr" || value == L".rgbe" || value == L".wbmp" ||
               value == L".xbm" || value == L".xpm" || value == L".sgi" || value == L".rgb" || value == L".rgba" ||
               value == L".bw";
    }

    bool IsVectorExtension(const wchar_t* extension)
    {
        const std::wstring value = NormalizeExtension(extension);
        return value == L".svg" || value == L".svgz";
    }

    DecodeResult DecodeForThumbnail(const std::uint8_t* data, std::size_t size, const wchar_t* extension,
                                    const DecodeOptions& options)
    {
        DecodeResult result;
        if (!data || size == 0 || size > MaxSourceBytes) return result;

        const std::wstring extensionValue = NormalizeExtension(extension);
        const std::uint32_t maxEdge = ClampEdge(std::max(options.maxWidth, options.maxHeight), HardCapLongestSide);
        const std::uint32_t maxWidth = options.maxWidth ? std::min(options.maxWidth, maxEdge) : maxEdge;
        const std::uint32_t maxHeight = options.maxHeight ? std::min(options.maxHeight, maxEdge) : maxEdge;

        const std::uint64_t started = NowMicros();

        // 1. Vector documents render directly near the requested size.
        if (IsVectorExtension(extensionValue.c_str()) || HasPrefix(data, size, "<?xml", 5) || HasPrefix(data, size, "<svg", 4))
        {
            if (RenderSvg(data, size, maxEdge, result.image))
            {
                result.ok = true;
                result.decoder = "svg";
                result.vector = true;
                result.decodeMicros = NowMicros() - started;
                return result;
            }
        }

        // 2. Windows Imaging Component (covers JPEG/PNG/BMP/GIF/TIFF/WebP/ICO/DDS/JPEG-XR and any
        //    installed HEIF/AVIF/JXL codec), preferring an embedded thumbnail when present.
        bool embeddedUsed = false;
        {
            const std::uint64_t wicStarted = NowMicros();
            if (DecodeWithWic(data, size, maxWidth, maxHeight, options.preferEmbedded, result.image, embeddedUsed))
            {
                result.ok = true;
                result.decoder = embeddedUsed ? "embedded" : "wic";
                result.embedded = embeddedUsed;
                result.decodeMicros = NowMicros() - wicStarted;
                return result;
            }
        }

        // 3. Compact native decoders for the simple formats WIC does not cover.
        if (IsCompactRasterExtension(extensionValue.c_str()) || extensionValue.empty())
        {
            const std::uint64_t compactStarted = NowMicros();
            Image decoded;
            if (DecodeCompact(data, size, extensionValue, decoded) && decoded.Valid())
            {
                ShrinkToFit(decoded, maxWidth, maxHeight);
                result.image = std::move(decoded);
                result.ok = true;
                result.decoder = "compact";
                result.decodeMicros = NowMicros() - compactStarted;
                return result;
            }
        }

        return result;
    }

    Image ComposeThumbnail(const Image& source, std::uint32_t edge)
    {
        Image composed;
        if (!source.Valid() || edge == 0) return composed;
        const double ratio = std::min(static_cast<double>(edge) / source.width,
                                      static_cast<double>(edge) / source.height);
        const auto targetWidth = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(std::floor(source.width * ratio)));
        const auto targetHeight = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(std::floor(source.height * ratio)));
        Image scaled;
        if (targetWidth == source.width && targetHeight == source.height) scaled = source;
        else Downscale(source, targetWidth, targetHeight, scaled);
        if (!scaled.Valid()) return composed;

        // Composite onto opaque white so Explorer never shows a broken alpha thumbnail.
        for (std::size_t i = 0; i < scaled.pixels.size(); i += 4)
        {
            const unsigned alpha = scaled.pixels[i + 3];
            if (alpha == 255) continue;
            scaled.pixels[i + 0] = static_cast<std::uint8_t>((scaled.pixels[i + 0] * alpha + 255 * (255 - alpha)) / 255);
            scaled.pixels[i + 1] = static_cast<std::uint8_t>((scaled.pixels[i + 1] * alpha + 255 * (255 - alpha)) / 255);
            scaled.pixels[i + 2] = static_cast<std::uint8_t>((scaled.pixels[i + 2] * alpha + 255 * (255 - alpha)) / 255);
            scaled.pixels[i + 3] = 255;
        }
        return scaled;
    }
}
