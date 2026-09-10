#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include "image_decode.h"
#include <windows.h>
#include <wincodec.h>
#include <propvarutil.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cctype>
#include <cstdint>
#include <climits>
#include <cstring>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <new>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

namespace GlideDecode {

static std::atomic<bool> gPreferColorProgressivePreview{true};
void SetPreferColorProgressivePreview(bool enabled) { gPreferColorProgressivePreview.store(enabled, std::memory_order_relaxed); }

static std::wstring Lower(std::wstring s) {
    std::transform(s.begin(), s.end(), s.begin(), [](wchar_t c){ return static_cast<wchar_t>(towlower(c)); });
    return s;
}
static bool IsJpegPath(const std::wstring& path) {
    const std::wstring ext = Lower(fs::path(path).extension().wstring());
    return ext == L".jpg" || ext == L".jpeg" || ext == L".jpe" || ext == L".jfif";
}

static WICBitmapTransformOptions OrientationToTransform(UINT orientation) {
    switch (orientation) {
        case 2: return WICBitmapTransformFlipHorizontal;
        case 3: return WICBitmapTransformRotate180;
        case 4: return WICBitmapTransformFlipVertical;
        case 5: return static_cast<WICBitmapTransformOptions>(WICBitmapTransformRotate90 | WICBitmapTransformFlipHorizontal);
        case 6: return WICBitmapTransformRotate90;
        case 7: return static_cast<WICBitmapTransformOptions>(WICBitmapTransformRotate270 | WICBitmapTransformFlipHorizontal);
        case 8: return WICBitmapTransformRotate270;
        default: return WICBitmapTransformRotate0;
    }
}

static UINT ReadExifOrientation(IWICBitmapFrameDecode* frame) {
    ComPtr<IWICMetadataQueryReader> reader;
    if (FAILED(frame->GetMetadataQueryReader(&reader)) || !reader) return 1;

    PROPVARIANT value{};
    PropVariantInit(&value);
    UINT orientation = 1;
    if (SUCCEEDED(reader->GetMetadataByName(L"/app1/ifd/{ushort=274}", &value))) {
        if (value.vt == VT_UI2) orientation = value.uiVal;
        else if (value.vt == VT_UI4) orientation = value.ulVal;
    }
    PropVariantClear(&value);
    return orientation;
}


static bool ReadWholeFileBytes(const std::wstring& path, std::vector<BYTE>& out) {
    std::ifstream f(fs::path(path), std::ios::binary);
    if (!f) return false;
    f.seekg(0, std::ios::end);
    const auto n = f.tellg();
    if (n <= 0 || n > static_cast<std::streamoff>(512ull * 1024ull * 1024ull)) return false;
    f.seekg(0, std::ios::beg);
    out.resize(static_cast<size_t>(n));
    f.read(reinterpret_cast<char*>(out.data()), static_cast<std::streamsize>(n));
    return f.good() || f.eof();
}

static uint16_t Be16(const BYTE* p) { return static_cast<uint16_t>((p[0] << 8) | p[1]); }
static uint32_t Be32(const BYTE* p) { return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|uint32_t(p[3]); }

static bool PackBitsRow(const BYTE*& src, const BYTE* end, BYTE* dst, size_t count) {
    size_t out = 0;
    while (out < count && src < end) {
        const int8_t n = static_cast<int8_t>(*src++);
        if (n >= 0) {
            const size_t run = static_cast<size_t>(n) + 1;
            if (src + run > end || out + run > count) return false;
            memcpy(dst + out, src, run); src += run; out += run;
        } else if (n != -128) {
            const size_t run = static_cast<size_t>(1 - n);
            if (src >= end || out + run > count) return false;
            memset(dst + out, *src++, run); out += run;
        }
    }
    return out == count;
}

static DecodedImage DecodePsdComposite(const DecodeRequest& req) {
    DecodedImage r; r.path=req.path; r.generation=req.generation; r.currentRequest=req.currentRequest; r.preview=req.preview; r.refinement=req.refinement;
    std::vector<BYTE> b; if (!ReadWholeFileBytes(req.path,b) || b.size()<30 || memcmp(b.data(),"8BPS",4)!=0) return r;
    const BYTE* p=b.data()+4; const BYTE* end=b.data()+b.size();
    if (Be16(p)!=1) return r; p+=2; p+=6;
    if (p+12>end) return r;
    const uint16_t channels=Be16(p); p+=2; const uint32_t h=Be32(p); p+=4; const uint32_t w=Be32(p); p+=4; const uint16_t depth=Be16(p); p+=2; const uint16_t mode=Be16(p); p+=2;
    if (!w || !h || w>50000 || h>50000 || depth!=8 || channels<1 || channels>16 || (mode!=1 && mode!=3)) return r;
    for(int section=0; section<3; ++section){ if(p+4>end)return r; uint32_t n=Be32(p);p+=4; if(size_t(end-p)<n)return r; p+=n; }
    if(p+2>end)return r; const uint16_t compression=Be16(p); p+=2;
    const uint64_t plane64=uint64_t(w)*h; if(plane64>SIZE_MAX || plane64*channels>512ull*1024ull*1024ull)return r; const size_t plane=static_cast<size_t>(plane64);
    std::vector<BYTE> ch(static_cast<size_t>(channels)*plane);
    if(compression==0){ if(size_t(end-p)<ch.size())return r; memcpy(ch.data(),p,ch.size()); }
    else if(compression==1){
        const size_t rows=static_cast<size_t>(channels)*h; if(size_t(end-p)<rows*2)return r; std::vector<uint16_t> lens(rows); for(size_t i=0;i<rows;++i){lens[i]=Be16(p);p+=2;}
        for(uint16_t c=0;c<channels;++c){ for(uint32_t y=0;y<h;++y){ const size_t idx=size_t(c)*h+y; if(size_t(end-p)<lens[idx])return r; const BYTE* rowEnd=p+lens[idx]; const BYTE* q=p; if(!PackBitsRow(q,rowEnd,ch.data()+size_t(c)*plane+size_t(y)*w,w))return r; p=rowEnd; } }
    } else return r;
    r.sourceWidth=w; r.sourceHeight=h; r.width=w; r.height=h; r.stride=w*4; const uint64_t bytes=uint64_t(r.stride)*h; if(bytes>UINT_MAX)return r; r.pixels.resize(static_cast<size_t>(bytes));
    for(size_t i=0;i<plane;++i){ BYTE rr,gg,bb,aa=255; if(mode==3){ rr=ch[i];gg=channels>1?ch[plane+i]:rr;bb=channels>2?ch[plane*2+i]:rr; if(channels>3)aa=ch[plane*3+i]; }else{ rr=gg=bb=ch[i]; if(channels>1)aa=ch[plane+i]; } BYTE* d=&r.pixels[i*4]; d[0]=BYTE((uint16_t(bb)*aa+127)/255);d[1]=BYTE((uint16_t(gg)*aa+127)/255);d[2]=BYTE((uint16_t(rr)*aa+127)/255);d[3]=aa; }
    r.hr=S_OK; return r;
}

static DecodedImage DecodeTga(const DecodeRequest& req) {
    DecodedImage r; r.path=req.path; r.generation=req.generation; r.currentRequest=req.currentRequest; r.preview=req.preview; r.refinement=req.refinement;
    std::vector<BYTE> b; if(!ReadWholeFileBytes(req.path,b)||b.size()<18)return r; const BYTE* h=b.data(); const BYTE idLen=h[0], cmap=h[1], type=h[2]; const uint16_t w=uint16_t(h[12]|(h[13]<<8)), hh=uint16_t(h[14]|(h[15]<<8)); const BYTE bpp=h[16], desc=h[17];
    if(cmap!=0||!w||!hh||!((type==2||type==10)&&(bpp==24||bpp==32))&&!((type==3||type==11)&&bpp==8))return r; const BYTE* p0=b.data()+18+idLen,*end=b.data()+b.size(); if(p0>end)return r;
    const size_t pixels=size_t(w)*hh; std::vector<BYTE> raw(pixels*(bpp/8)); const size_t pxbytes=bpp/8; const BYTE* p=p0;
    if(type==2||type==3){ if(size_t(end-p)<raw.size())return r; memcpy(raw.data(),p,raw.size()); }
    else { size_t out=0; while(out<pixels&&p<end){ BYTE head=*p++; size_t run=(head&0x7f)+1; if(head&0x80){ if(size_t(end-p)<pxbytes)return r; for(size_t i=0;i<run&&out<pixels;++i,++out)memcpy(raw.data()+out*pxbytes,p,pxbytes); p+=pxbytes; }else{ size_t n=run*pxbytes;if(size_t(end-p)<n||out+run>pixels)return r;memcpy(raw.data()+out*pxbytes,p,n);p+=n;out+=run;} } if(out!=pixels)return r; }
    r.sourceWidth=r.width=w;r.sourceHeight=r.height=hh;r.stride=w*4;r.pixels.resize(size_t(r.stride)*hh); const bool top=(desc&0x20)!=0;
    for(uint32_t y=0;y<hh;++y){ uint32_t sy=top?y:(hh-1-y); for(uint32_t x=0;x<w;++x){ const BYTE* sp=&raw[(size_t(sy)*w+x)*pxbytes];BYTE* d=&r.pixels[(size_t(y)*w+x)*4];BYTE a=pxbytes==4?sp[3]:255;if(type==3||type==11){d[0]=d[1]=d[2]=sp[0];a=255;}else{d[0]=BYTE((uint16_t(sp[0])*a+127)/255);d[1]=BYTE((uint16_t(sp[1])*a+127)/255);d[2]=BYTE((uint16_t(sp[2])*a+127)/255);}d[3]=a;} }
    r.hr=S_OK;return r;
}

static bool PnmToken(const BYTE*& p, const BYTE* end, std::string& tok) {
    tok.clear();
    for (;;) {
        while (p < end && std::isspace(static_cast<unsigned char>(*p))) ++p;
        if (p < end && *p == '#') {
            while (p < end && *p != '\n') ++p;
            continue;
        }
        break;
    }
    while (p < end && !std::isspace(static_cast<unsigned char>(*p)) && *p != '#') {
        if (tok.size() >= 32) return false; // numeric PNM header tokens should be tiny
        tok.push_back(static_cast<char>(*p++));
    }
    return !tok.empty();
}

static bool ParseUint32Token(const std::string& token, uint32_t& value) noexcept {
    if (token.empty()) return false;
    uint64_t parsed = 0;
    for (unsigned char c : token) {
        if (c < '0' || c > '9') return false;
        parsed = parsed * 10u + static_cast<uint64_t>(c - '0');
        if (parsed > UINT32_MAX) return false;
    }
    value = static_cast<uint32_t>(parsed);
    return true;
}

static DecodedImage DecodePnm(const DecodeRequest& req) {
    DecodedImage r;
    r.path=req.path; r.generation=req.generation; r.currentRequest=req.currentRequest;
    r.preview=req.preview; r.refinement=req.refinement;

    std::vector<BYTE> b;
    if (!ReadWholeFileBytes(req.path, b)) return r;
    const BYTE* p=b.data(); const BYTE* end=b.data()+b.size();
    std::string token;
    if (!PnmToken(p,end,token) || (token!="P5" && token!="P6")) return r;
    const bool rgb=token=="P6";

    uint32_t w=0,h=0,maxv=0;
    if (!PnmToken(p,end,token) || !ParseUint32Token(token,w)) return r;
    if (!PnmToken(p,end,token) || !ParseUint32Token(token,h)) return r;
    if (!PnmToken(p,end,token) || !ParseUint32Token(token,maxv)) return r;
    if (!w || !h || !maxv || maxv>255) return r;

    // P5/P6 pixel bytes may themselves equal whitespace values. Consume only the
    // required header separator (and tolerate a CRLF pair), never a run of bytes.
    if (p>=end || !std::isspace(static_cast<unsigned char>(*p))) return r;
    if (*p=='\r' && p+1<end && p[1]=='\n') p+=2; else ++p;
    const uint64_t pixelCount64=static_cast<uint64_t>(w)*static_cast<uint64_t>(h);
    constexpr uint64_t kFallbackPixelBytesLimit=512ull*1024ull*1024ull;
    if (!pixelCount64 || pixelCount64>kFallbackPixelBytesLimit/4ull) return r;
    const uint64_t sourceBytes64=pixelCount64*(rgb?3ull:1ull);
    const uint64_t stride64=static_cast<uint64_t>(w)*4ull;
    const uint64_t outputBytes64=pixelCount64*4ull;
    if (stride64>UINT_MAX || outputBytes64>static_cast<uint64_t>(SIZE_MAX) ||
        sourceBytes64>static_cast<uint64_t>(end-p)) return r;

    r.sourceWidth=r.width=w; r.sourceHeight=r.height=h; r.stride=static_cast<UINT>(stride64);
    r.pixels.resize(static_cast<size_t>(outputBytes64));
    const size_t pixelCount=static_cast<size_t>(pixelCount64);
    for (size_t i=0;i<pixelCount;++i) {
        BYTE* d=&r.pixels[i*4];
        if (rgb) { d[2]=p[i*3]; d[1]=p[i*3+1]; d[0]=p[i*3+2]; }
        else d[0]=d[1]=d[2]=p[i];
        d[3]=255;
    }
    r.hr=S_OK;
    return r;
}

static DecodedImage DecodeFallbackRaster(const DecodeRequest& req) {
    DecodedImage failed;
    failed.path=req.path; failed.generation=req.generation; failed.currentRequest=req.currentRequest;
    failed.preview=req.preview; failed.refinement=req.refinement;
    try {
        const auto ext=Lower(fs::path(req.path).extension().wstring());
        if (ext==L".psd") return DecodePsdComposite(req);
        if (ext==L".tga") return DecodeTga(req);
        if (ext==L".pnm" || ext==L".ppm" || ext==L".pgm") return DecodePnm(req);
    } catch (const std::bad_alloc&) {
        failed.hr=E_OUTOFMEMORY;
    } catch (...) {
        failed.hr=E_FAIL;
    }
    return failed;
}

DecodedImage DecodeFile(IWICImagingFactory* wic, const DecodeRequest& req, const std::atomic<uint64_t>* latestGeneration) {
    DecodedImage result;
    result.path = req.path;
    result.generation = req.generation;
    result.currentRequest = req.currentRequest;
    result.preview = req.preview;
    result.refinement = req.refinement;
    auto cancelled = [&]() { return req.currentRequest && latestGeneration && req.generation != latestGeneration->load(std::memory_order_relaxed); };
    if (cancelled()) { result.hr = HRESULT_FROM_WIN32(ERROR_CANCELLED); return result; }

    ComPtr<IWICBitmapDecoder> decoder;
    HRESULT hr = wic->CreateDecoderFromFilename(req.path.c_str(), nullptr, GENERIC_READ,
                                                WICDecodeMetadataCacheOnDemand, &decoder);
    result.wicFilenameHr = hr;
    if (FAILED(hr)) {
        // Some installed WIC codecs advertise an extension but do not bind
        // reliably through CreateDecoderFromFilename (notably modern WebP/
        // AVIF variants). Retry through a content-sniffed IStream before Glide
        // considers a native fallback. This keeps the decoder WIC-first and
        // avoids extension-specific codec selection hacks.
        ComPtr<IWICStream> input;
        result.wicStreamInitHr = wic->CreateStream(&input);
        if (SUCCEEDED(result.wicStreamInitHr))
            result.wicStreamInitHr = input->InitializeFromFilename(req.path.c_str(), GENERIC_READ);
        if (SUCCEEDED(result.wicStreamInitHr)) {
            result.wicStreamDecoderHr = wic->CreateDecoderFromStream(
                input.Get(), nullptr, WICDecodeMetadataCacheOnDemand, &decoder);
            if (SUCCEEDED(result.wicStreamDecoderHr)) {
                result.wicStreamUsed = true;
                hr = result.wicStreamDecoderHr;
            } else {
                hr = result.wicStreamDecoderHr;
            }
        } else {
            result.wicStreamDecoderHr = result.wicStreamInitHr;
        }
        if (FAILED(hr)) {
            auto fb = DecodeFallbackRaster(req);
            if (SUCCEEDED(fb.hr)) {
                fb.wicFilenameHr = result.wicFilenameHr;
                fb.wicStreamInitHr = result.wicStreamInitHr;
                fb.wicStreamDecoderHr = result.wicStreamDecoderHr;
                fb.wicStreamUsed = result.wicStreamUsed;
                fb.nativeFallback = true;
                return fb;
            }
            result.hr = hr;
            return result;
        }
    }
    if (cancelled()) { result.hr = HRESULT_FROM_WIN32(ERROR_CANCELLED); return result; }

    ComPtr<IWICBitmapFrameDecode> frame;
    hr = decoder->GetFrame(0, &frame);
    if (FAILED(hr)) { result.hr = hr; return result; }
    if (cancelled()) { result.hr = HRESULT_FROM_WIN32(ERROR_CANCELLED); return result; }

    const UINT orientation = ReadExifOrientation(frame.Get());
    const WICBitmapTransformOptions transform = OrientationToTransform(orientation);

    // Glide.6 progressive-JPEG first-colour policy. Some progressive files put
    // luminance in scan/level 0 and chroma in the next scans, producing a brief
    // monochrome first frame. Glide now asks WIC for the earliest small level that
    // normally includes Y + Cb + Cr DC information (up to level 2), while preserving
    // the same progressive preview/refinement architecture. Users may opt back into
    // absolute level-0 speed in Performance settings.
    bool progressivePreviewLevel = false;
    if (req.preview && IsJpegPath(req.path)) {
        ComPtr<IWICProgressiveLevelControl> progressive;
        if (SUCCEEDED(frame.As(&progressive)) && progressive) {
            UINT count=0; progressive->GetLevelCount(&count);
            UINT wanted=0;
            // During rapid browsing, latency wins: level 0 can use the decoder-native
            // reduced-size JPEG path below. Once navigation settles, normal requests keep
            // Glide's colour-first progressive preview policy.
            if(!req.fastNavigation && gPreferColorProgressivePreview.load(std::memory_order_relaxed) && count>1)
                wanted=std::min<UINT>(2,count-1);
            if (SUCCEEDED(progressive->SetCurrentLevel(wanted))) progressivePreviewLevel = (wanted > 0);
        }
    }

    // Stage 12 JPEG fast path: ask the codec for a decoder-native reduced frame
    // before falling back to a full decode followed by IWICBitmapScaler.  Microsoft's
    // JPEG WIC decoder can satisfy these reduced-size requests much more cheaply for
    // large photographs because it avoids materialising every full-resolution pixel.
    if (req.preview && !progressivePreviewLevel && req.previewMaxWidth > 0 && req.previewMaxHeight > 0 && transform == WICBitmapTransformRotate0) {
        UINT fw=0, fh=0; frame->GetSize(&fw,&fh);
        if (fw && fh && (fw > req.previewMaxWidth || fh > req.previewMaxHeight)) {
            const double scale=std::min(double(req.previewMaxWidth)/fw,double(req.previewMaxHeight)/fh);
            UINT tw=std::max<UINT>(1,UINT(std::lround(fw*scale))), th=std::max<UINT>(1,UINT(std::lround(fh*scale)));
            ComPtr<IWICBitmapSourceTransform> xform;
            if (SUCCEEDED(frame.As(&xform)) && xform) {
                UINT cw=tw,ch=th;
                if (SUCCEEDED(xform->GetClosestSize(&cw,&ch)) && cw && ch) {
                    WICPixelFormatGUID pf=GUID_WICPixelFormat24bppBGR;
                    if (SUCCEEDED(xform->GetClosestPixelFormat(&pf))) {
                        UINT bpp=0;
                        if (IsEqualGUID(pf,GUID_WICPixelFormat24bppBGR)) bpp=3;
                        else if (IsEqualGUID(pf,GUID_WICPixelFormat32bppBGRA) || IsEqualGUID(pf,GUID_WICPixelFormat32bppPBGRA) || IsEqualGUID(pf,GUID_WICPixelFormat32bppBGR)) bpp=4;
                        if (bpp) {
                            const uint64_t stride64=uint64_t(cw)*bpp, bytes64=stride64*ch;
                            if (stride64<=UINT_MAX && bytes64<=UINT_MAX) {
                                std::vector<BYTE> native(static_cast<size_t>(bytes64));
                                auto requestedPf=pf;
                                HRESULT thr=xform->CopyPixels(nullptr,cw,ch,&requestedPf,WICBitmapTransformRotate0,static_cast<UINT>(stride64),static_cast<UINT>(bytes64),native.data());
                                if (SUCCEEDED(thr) && IsEqualGUID(requestedPf,pf)) {
                                    result.sourceWidth=fw;result.sourceHeight=fh;result.width=cw;result.height=ch;result.stride=cw*4;result.pixels.resize(size_t(result.stride)*ch);
                                    for(size_t i=0;i<size_t(cw)*ch;++i){const BYTE*sp=&native[i*bpp];BYTE*d=&result.pixels[i*4];d[0]=sp[0];d[1]=sp[1];d[2]=sp[2];d[3]=(bpp==4&&!IsEqualGUID(pf,GUID_WICPixelFormat32bppBGR))?sp[3]:255;if(IsEqualGUID(pf,GUID_WICPixelFormat32bppBGRA)&&d[3]!=255){d[0]=BYTE((uint16_t(d[0])*d[3]+127)/255);d[1]=BYTE((uint16_t(d[1])*d[3]+127)/255);d[2]=BYTE((uint16_t(d[2])*d[3]+127)/255);}}
                                    result.hr=S_OK;return result;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    ComPtr<IWICBitmapSource> source = frame;
    ComPtr<IWICBitmapFlipRotator> rotator;
    if (transform != WICBitmapTransformRotate0) {
        hr = wic->CreateBitmapFlipRotator(&rotator);
        if (SUCCEEDED(hr)) hr = rotator->Initialize(frame.Get(), transform);
        if (FAILED(hr)) { result.hr = hr; return result; }
        source = rotator;
    }

    UINT sourceW = 0, sourceH = 0;
    hr = source->GetSize(&sourceW, &sourceH);
    if (FAILED(hr) || sourceW == 0 || sourceH == 0) {
        result.hr = FAILED(hr) ? hr : E_FAIL;
        return result;
    }
    result.sourceWidth = sourceW;
    result.sourceHeight = sourceH;

    ComPtr<IWICBitmapScaler> scaler;
    if (req.preview && req.previewMaxWidth > 0 && req.previewMaxHeight > 0 &&
        (sourceW > req.previewMaxWidth || sourceH > req.previewMaxHeight)) {
        const double scale = std::min(
            static_cast<double>(req.previewMaxWidth) / static_cast<double>(sourceW),
            static_cast<double>(req.previewMaxHeight) / static_cast<double>(sourceH));
        const UINT scaledW = std::max<UINT>(1, static_cast<UINT>(std::lround(sourceW * scale)));
        const UINT scaledH = std::max<UINT>(1, static_cast<UINT>(std::lround(sourceH * scale)));

        hr = wic->CreateBitmapScaler(&scaler);
        if (SUCCEEDED(hr)) {
            hr = scaler->Initialize(source.Get(), scaledW, scaledH, WICBitmapInterpolationModeFant);
        }
        if (SUCCEEDED(hr)) source = scaler;
        else scaler.Reset();
    }

    if (cancelled()) { result.hr = HRESULT_FROM_WIN32(ERROR_CANCELLED); return result; }
    ComPtr<IWICFormatConverter> converter;
    hr = wic->CreateFormatConverter(&converter);
    if (FAILED(hr)) { result.hr = hr; return result; }

    hr = converter->Initialize(source.Get(), GUID_WICPixelFormat32bppPBGRA,
                               WICBitmapDitherTypeNone, nullptr, 0.0,
                               WICBitmapPaletteTypeCustom);
    if (FAILED(hr)) { result.hr = hr; return result; }

    UINT w = 0, h = 0;
    hr = converter->GetSize(&w, &h);
    if (FAILED(hr) || w == 0 || h == 0) { result.hr = FAILED(hr) ? hr : E_FAIL; return result; }

    const uint64_t stride64 = static_cast<uint64_t>(w) * 4ull;
    const uint64_t bytes64 = stride64 * static_cast<uint64_t>(h);
    if (stride64 > UINT_MAX || bytes64 > UINT_MAX) {
        result.hr = HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
        return result;
    }

    result.width = w;
    result.height = h;
    result.stride = static_cast<UINT>(stride64);
    if (cancelled()) { result.hr = HRESULT_FROM_WIN32(ERROR_CANCELLED); return result; }
    result.pixels.resize(static_cast<size_t>(bytes64));
    hr = converter->CopyPixels(nullptr, result.stride,
                               static_cast<UINT>(result.pixels.size()), result.pixels.data());
    result.hr = hr;
    if (FAILED(hr)) result.pixels.clear();
    return result;
}


} // namespace GlideDecode
