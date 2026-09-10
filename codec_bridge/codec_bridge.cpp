#ifdef _WIN32
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#define GLIDE_CODEC_EXPORT extern "C" __declspec(dllexport)
#define GLIDE_CODEC_CALL __cdecl
#else
#define GLIDE_CODEC_EXPORT extern "C" __attribute__((visibility("default")))
#define GLIDE_CODEC_CALL
#endif
#include "../codec_bridge_api.h"
#include <webp/decode.h>
#include <webp/demux.h>
#include <libheif/heif.h>
#include <jxl/decode.h>
#include <openjpeg.h>
#include <tinyexr.h>
#include <algorithm>
#include <cmath>
#include <climits>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <limits>
#include <string>
#include <vector>

namespace fs = std::filesystem;
namespace {

enum : std::uint32_t { CODEC_WEBP=1, CODEC_HEIF=2, CODEC_JXL=3, CODEC_JP2=4, CODEC_EXR=5 };

void SetError(wchar_t* out, std::uint32_t chars, const wchar_t* text) {
    if (!out || !chars) return;
    const wchar_t* src = text ? text : L"Codec decode failed";
#ifdef _WIN32
    wcsncpy_s(out, chars, src, _TRUNCATE);
#else
    if (chars > 0) { std::wcsncpy(out, src, chars - 1); out[chars - 1] = L'\0'; }
#endif
}

bool ReadAll(const wchar_t* path, std::vector<unsigned char>& data) {
    try {
        std::ifstream f(fs::path(path), std::ios::binary | std::ios::ate);
        if (!f) return false;
        const auto end = f.tellg();
        if (end <= 0 || static_cast<unsigned long long>(end) > (1ull<<32)) return false;
        data.resize(static_cast<size_t>(end));
        f.seekg(0, std::ios::beg);
        f.read(reinterpret_cast<char*>(data.data()), static_cast<std::streamsize>(data.size()));
        return f.good() || f.gcount() == static_cast<std::streamsize>(data.size());
    } catch (...) { return false; }
}

bool AllocImage(GlideCodecImageV1* out, std::uint32_t w, std::uint32_t h, std::uint32_t codec) {
    if (!out || !w || !h || w > 65535 || h > 65535) return false;
    const std::uint64_t stride = static_cast<std::uint64_t>(w) * 4ull;
    const std::uint64_t bytes = stride * h;
    if (bytes > static_cast<std::uint64_t>(std::numeric_limits<size_t>::max())) return false;
    auto* p = static_cast<unsigned char*>(std::malloc(static_cast<size_t>(bytes)));
    if (!p) return false;
    out->structSize = sizeof(*out); out->width=w; out->height=h; out->stride=static_cast<std::uint32_t>(stride);
    out->dataSize=bytes; out->pixels=p; out->codecId=codec; out->reserved=0; return true;
}

inline void PremultiplyBgra(unsigned char* p, size_t pixels) {
    for (size_t i=0;i<pixels;++i) {
        unsigned char* q=p+i*4; const unsigned a=q[3];
        q[0]=static_cast<unsigned char>((unsigned(q[0])*a+127)/255);
        q[1]=static_cast<unsigned char>((unsigned(q[1])*a+127)/255);
        q[2]=static_cast<unsigned char>((unsigned(q[2])*a+127)/255);
    }
}

int DecodeWebP(const std::vector<unsigned char>& d, GlideCodecImageV1* out) {
    WebPBitstreamFeatures features{};
    if (WebPGetFeatures(d.data(), d.size(), &features) != VP8_STATUS_OK || features.width <= 0 || features.height <= 0) return 0;
    if (features.has_animation) {
        WebPData wd{d.data(), d.size()};
        WebPAnimDecoderOptions options{};
        if (!WebPAnimDecoderOptionsInit(&options)) return 0;
        options.color_mode = MODE_BGRA;
        WebPAnimDecoder* dec = WebPAnimDecoderNew(&wd, &options);
        if (!dec) return 0;
        WebPAnimInfo info{};
        uint8_t* frame = nullptr; int timestamp = 0;
        const bool got = WebPAnimDecoderGetInfo(dec, &info) && WebPAnimDecoderHasMoreFrames(dec) &&
                         WebPAnimDecoderGetNext(dec, &frame, &timestamp) && frame && info.canvas_width && info.canvas_height;
        bool ok = got && AllocImage(out, info.canvas_width, info.canvas_height, CODEC_WEBP);
        if (ok) {
            const size_t bytes = static_cast<size_t>(info.canvas_width) * info.canvas_height * 4u;
            std::memcpy(out->pixels, frame, bytes);
            PremultiplyBgra(out->pixels, static_cast<size_t>(info.canvas_width) * info.canvas_height);
        }
        WebPAnimDecoderDelete(dec);
        return ok ? 1 : 0;
    }
    int w=0,h=0;
    uint8_t* decoded=WebPDecodeBGRA(d.data(),d.size(),&w,&h); if(!decoded||w<=0||h<=0) return 0;
    const bool ok=AllocImage(out,static_cast<uint32_t>(w),static_cast<uint32_t>(h),CODEC_WEBP);
    if(ok){std::memcpy(out->pixels,decoded,static_cast<size_t>(out->dataSize));PremultiplyBgra(out->pixels,static_cast<size_t>(w)*h);} WebPFree(decoded);
    return ok?1:0;
}

int DecodeHeif(const std::vector<unsigned char>& d, GlideCodecImageV1* out) {
    struct HeifGuard { bool active{}; HeifGuard(){ active=(heif_init(nullptr).code==heif_error_Ok); } ~HeifGuard(){ if(active) heif_deinit(); } } guard;
    if(!guard.active) return 0;
    heif_context* ctx=heif_context_alloc(); if(!ctx) return 0;
    heif_error e=heif_context_read_from_memory_without_copy(ctx,d.data(),d.size(),nullptr); if(e.code!=heif_error_Ok){heif_context_free(ctx);return 0;}
    heif_image_handle* handle=nullptr; e=heif_context_get_primary_image_handle(ctx,&handle); if(e.code!=heif_error_Ok||!handle){heif_context_free(ctx);return 0;}
    heif_image* img=nullptr; e=heif_decode_image(handle,&img,heif_colorspace_RGB,heif_chroma_interleaved_RGBA,nullptr);
    if(e.code!=heif_error_Ok||!img){if(img)heif_image_release(img);heif_image_handle_release(handle);heif_context_free(ctx);return 0;}
    // libheif may apply orientation/crop transforms while decoding. The decoded
    // interleaved plane is authoritative; never copy using pre-transform handle dimensions.
    const int w=heif_image_get_width(img,heif_channel_interleaved);
    const int h=heif_image_get_height(img,heif_channel_interleaved);
    int srcStride=0; const uint8_t* src=heif_image_get_plane_readonly(img,heif_channel_interleaved,&srcStride);
    const uint64_t rowBytes=w>0?static_cast<uint64_t>(w)*4ull:0ull;
    bool ok=src&&w>0&&h>0&&rowBytes<=static_cast<uint64_t>(INT_MAX)&&srcStride>=static_cast<int>(rowBytes)&&AllocImage(out,static_cast<uint32_t>(w),static_cast<uint32_t>(h),CODEC_HEIF);
    if(ok){for(int y=0;y<h;++y){const uint8_t* s=src+static_cast<size_t>(y)*srcStride;unsigned char* q=out->pixels+static_cast<size_t>(y)*out->stride;for(int x=0;x<w;++x){const unsigned a=s[x*4+3];q[x*4+0]=static_cast<unsigned char>((unsigned(s[x*4+2])*a+127)/255);q[x*4+1]=static_cast<unsigned char>((unsigned(s[x*4+1])*a+127)/255);q[x*4+2]=static_cast<unsigned char>((unsigned(s[x*4+0])*a+127)/255);q[x*4+3]=static_cast<unsigned char>(a);}}}
    heif_image_release(img);heif_image_handle_release(handle);heif_context_free(ctx);return ok?1:0;
}

int DecodeJxl(const std::vector<unsigned char>& d, GlideCodecImageV1* out) {
    JxlDecoder* dec=JxlDecoderCreate(nullptr); if(!dec) return 0;
    JxlDecoderSubscribeEvents(dec,JXL_DEC_BASIC_INFO|JXL_DEC_FULL_IMAGE);
    JxlDecoderSetInput(dec,d.data(),d.size());JxlDecoderCloseInput(dec);
    JxlBasicInfo info{}; JxlPixelFormat fmt{4,JXL_TYPE_UINT8,JXL_NATIVE_ENDIAN,0}; std::vector<unsigned char> rgba; bool gotInfo=false,done=false;
    for(;;){const JxlDecoderStatus st=JxlDecoderProcessInput(dec);if(st==JXL_DEC_ERROR||st==JXL_DEC_NEED_MORE_INPUT)break;if(st==JXL_DEC_BASIC_INFO){if(JxlDecoderGetBasicInfo(dec,&info)!=JXL_DEC_SUCCESS||!info.xsize||!info.ysize)break;gotInfo=true;}else if(st==JXL_DEC_NEED_IMAGE_OUT_BUFFER){if(!gotInfo)break;size_t n=0;if(JxlDecoderImageOutBufferSize(dec,&fmt,&n)!=JXL_DEC_SUCCESS||!n)break;rgba.resize(n);if(JxlDecoderSetImageOutBuffer(dec,&fmt,rgba.data(),rgba.size())!=JXL_DEC_SUCCESS)break;}else if(st==JXL_DEC_FULL_IMAGE){done=true;}else if(st==JXL_DEC_SUCCESS){break;}}
    const uint64_t pixelCount=static_cast<uint64_t>(info.xsize)*static_cast<uint64_t>(info.ysize);
    const uint64_t requiredBytes=pixelCount*4ull;
    bool ok=done&&gotInfo&&pixelCount&&requiredBytes<=rgba.size()&&requiredBytes<=static_cast<uint64_t>(std::numeric_limits<size_t>::max())&&AllocImage(out,info.xsize,info.ysize,CODEC_JXL);
    if(ok){for(size_t i=0,n=static_cast<size_t>(info.xsize)*info.ysize;i<n;++i){const unsigned char* s=&rgba[i*4];unsigned char* q=&out->pixels[i*4];const unsigned a=s[3];q[0]=static_cast<unsigned char>((unsigned(s[2])*a+127)/255);q[1]=static_cast<unsigned char>((unsigned(s[1])*a+127)/255);q[2]=static_cast<unsigned char>((unsigned(s[0])*a+127)/255);q[3]=static_cast<unsigned char>(a);}}
    JxlDecoderDestroy(dec);return ok?1:0;
}

struct MemStream{const unsigned char* p{};size_t n{};size_t pos{};};
OPJ_SIZE_T OPJ_CALLCONV JpRead(void* b,OPJ_SIZE_T n,void* u){auto*m=static_cast<MemStream*>(u);if(!m||m->pos>=m->n)return static_cast<OPJ_SIZE_T>(-1);const size_t take=std::min<size_t>(n,m->n-m->pos);std::memcpy(b,m->p+m->pos,take);m->pos+=take;return take;}
OPJ_OFF_T OPJ_CALLCONV JpSkip(OPJ_OFF_T n,void* u){auto*m=static_cast<MemStream*>(u);if(!m||n<0)return -1;const size_t take=std::min<size_t>(static_cast<size_t>(n),m->n-m->pos);m->pos+=take;return static_cast<OPJ_OFF_T>(take);}
OPJ_BOOL OPJ_CALLCONV JpSeek(OPJ_OFF_T p,void* u){auto*m=static_cast<MemStream*>(u);if(!m||p<0||static_cast<uint64_t>(p)>m->n)return OPJ_FALSE;m->pos=static_cast<size_t>(p);return OPJ_TRUE;}
inline unsigned char Sample8(const opj_image_comp_t& c,size_t idx){if(!c.data||idx>=static_cast<size_t>(c.w)*c.h)return 0;long long v=c.data[idx];const int prec=std::clamp<int>(c.prec,1,31);if(c.sgnd)v+=(1ll<<(prec-1));const long long maxv=(1ll<<prec)-1;v=std::clamp<long long>(v,0,maxv);return static_cast<unsigned char>((v*255+maxv/2)/maxv);}
int DecodeJp2(const std::vector<unsigned char>& d, const std::wstring& ext, GlideCodecImageV1* out) {
    const OPJ_CODEC_FORMAT kind = (ext == L".jp2" || ext == L".jpf" || ext == L".jpx") ? OPJ_CODEC_JP2 : OPJ_CODEC_J2K;
    opj_dparameters_t prm{};
    opj_set_default_decoder_parameters(&prm);
    opj_codec_t* codec = opj_create_decompress(kind);
    if (!codec) return 0;
    if (!opj_setup_decoder(codec, &prm)) { opj_destroy_codec(codec); return 0; }

    MemStream ms{d.data(), d.size(), 0};
    opj_stream_t* stream = opj_stream_create(1 << 16, OPJ_TRUE);
    if (!stream) { opj_destroy_codec(codec); return 0; }
    opj_stream_set_user_data(stream, &ms, nullptr);
    opj_stream_set_user_data_length(stream, d.size());
    opj_stream_set_read_function(stream, JpRead);
    opj_stream_set_skip_function(stream, JpSkip);
    opj_stream_set_seek_function(stream, JpSeek);

    opj_image_t* im = nullptr;
    const bool decoded = opj_read_header(stream, codec, &im) && im &&
                         opj_decode(codec, stream, im) && opj_end_decompress(codec, stream);
    bool ok = false;
    if (decoded && im && im->numcomps >= 1 && im->comps[0].w && im->comps[0].h) {
        const uint32_t w = im->comps[0].w;
        const uint32_t h = im->comps[0].h;
        ok = AllocImage(out, w, h, CODEC_JP2);
        if (ok) {
            auto sampleAt = [&](uint32_t ci, uint32_t x, uint32_t y) -> unsigned char {
                if (ci >= im->numcomps) return 255;
                const auto& c = im->comps[ci];
                if (!c.w || !c.h) return 0;
                const uint32_t cx = std::min<uint32_t>(c.w - 1, static_cast<uint32_t>((uint64_t(x) * c.w) / w));
                const uint32_t cy = std::min<uint32_t>(c.h - 1, static_cast<uint32_t>((uint64_t(y) * c.h) / h));
                return Sample8(c, static_cast<size_t>(cy) * c.w + cx);
            };
            for (uint32_t y = 0; y < h; ++y) {
                for (uint32_t x = 0; x < w; ++x) {
                    unsigned char r = 0, g = 0, b = 0, a = 255;
                    if (im->numcomps == 1) {
                        r = g = b = sampleAt(0, x, y);
                    } else if (im->color_space == OPJ_CLRSPC_SYCC && im->numcomps >= 3) {
                        const int Y = sampleAt(0, x, y);
                        const int Cb = int(sampleAt(1, x, y)) - 128;
                        const int Cr = int(sampleAt(2, x, y)) - 128;
                        r = static_cast<unsigned char>(std::clamp<int>(int(std::lround(Y + 1.402 * Cr)), 0, 255));
                        g = static_cast<unsigned char>(std::clamp<int>(int(std::lround(Y - 0.344136 * Cb - 0.714136 * Cr)), 0, 255));
                        b = static_cast<unsigned char>(std::clamp<int>(int(std::lround(Y + 1.772 * Cb)), 0, 255));
                    } else {
                        r = sampleAt(0, x, y);
                        g = sampleAt(1, x, y);
                        b = sampleAt(2, x, y);
                    }
                    // OpenJPEG exposes component alpha semantics explicitly. Do not
                    // assume an arbitrary fourth colour component is transparency.
                    for (uint32_t ci=0; ci<im->numcomps; ++ci) {
                        if (im->comps[ci].alpha) { a=sampleAt(ci,x,y); break; }
                    }
                    unsigned char* q = out->pixels + (static_cast<size_t>(y) * w + x) * 4u;
                    q[0] = static_cast<unsigned char>((unsigned(b) * a + 127) / 255);
                    q[1] = static_cast<unsigned char>((unsigned(g) * a + 127) / 255);
                    q[2] = static_cast<unsigned char>((unsigned(r) * a + 127) / 255);
                    q[3] = a;
                }
            }
        }
    }
    if (im) opj_image_destroy(im);
    opj_stream_destroy(stream);
    opj_destroy_codec(codec);
    return ok ? 1 : 0;
}

inline float DisplayMap(float v){if(!std::isfinite(v)||v<=0.f)return 0.f;v=v/(1.f+v);return v<=0.0031308f?12.92f*v:1.055f*std::pow(v,1.f/2.4f)-0.055f;}
int DecodeExr(const std::vector<unsigned char>& d,GlideCodecImageV1* out){float* rgba=nullptr;int w=0,h=0;const char* err=nullptr;const int rc=LoadEXRFromMemory(&rgba,&w,&h,d.data(),d.size(),&err);if(rc!=TINYEXR_SUCCESS||!rgba||w<=0||h<=0){if(err)FreeEXRErrorMessage(err);if(rgba)std::free(rgba);return 0;}bool ok=AllocImage(out,static_cast<uint32_t>(w),static_cast<uint32_t>(h),CODEC_EXR);if(ok){for(size_t i=0,n=static_cast<size_t>(w)*h;i<n;++i){const float a=std::clamp(rgba[i*4+3],0.f,1.f);const unsigned A=static_cast<unsigned>(std::lround(a*255.f));const unsigned R=static_cast<unsigned>(std::lround(std::clamp(DisplayMap(rgba[i*4+0]),0.f,1.f)*255.f));const unsigned G=static_cast<unsigned>(std::lround(std::clamp(DisplayMap(rgba[i*4+1]),0.f,1.f)*255.f));const unsigned B=static_cast<unsigned>(std::lround(std::clamp(DisplayMap(rgba[i*4+2]),0.f,1.f)*255.f));unsigned char*q=&out->pixels[i*4];q[0]=static_cast<unsigned char>((B*A+127)/255);q[1]=static_cast<unsigned char>((G*A+127)/255);q[2]=static_cast<unsigned char>((R*A+127)/255);q[3]=static_cast<unsigned char>(A);}}std::free(rgba);if(err)FreeEXRErrorMessage(err);return ok?1:0;}

std::wstring LowerExt(const wchar_t* path){std::wstring e=fs::path(path).extension().wstring();std::transform(e.begin(),e.end(),e.begin(),[](wchar_t c){return static_cast<wchar_t>(towlower(c));});return e;}
}

GLIDE_CODEC_EXPORT const wchar_t* GLIDE_CODEC_CALL GlideCodecVersion(){return L"Glide Codec Bridge Alpha 0.12107 (WebP/HEIF/AVIF/JXL/JP2/EXR)";}
GLIDE_CODEC_EXPORT void GLIDE_CODEC_CALL GlideCodecFree(void* p){std::free(p);}
GLIDE_CODEC_EXPORT int GLIDE_CODEC_CALL GlideCodecDecodeFileW(const wchar_t* path,GlideCodecImageV1* out,wchar_t* err,std::uint32_t errChars){
    try {
        if(!path||!out||out->structSize!=sizeof(GlideCodecImageV1)){SetError(err,errChars,L"Invalid codec ABI arguments");return 0;}
        *out=GlideCodecImageV1{sizeof(GlideCodecImageV1)};
        std::vector<unsigned char>d;if(!ReadAll(path,d)){SetError(err,errChars,L"Could not read source file");return 0;}
        const std::wstring ext=LowerExt(path);int ok=0;
        if(ext==L".webp")ok=DecodeWebP(d,out);
        else if(ext==L".avif"||ext==L".avifs"||ext==L".heif"||ext==L".heifs"||ext==L".heic"||ext==L".heics"||ext==L".hif")ok=DecodeHeif(d,out);
        else if(ext==L".jxl")ok=DecodeJxl(d,out);
        else if(ext==L".jp2"||ext==L".j2k"||ext==L".j2c"||ext==L".jpc"||ext==L".jpf"||ext==L".jpx")ok=DecodeJp2(d,ext,out);
        else if(ext==L".exr")ok=DecodeExr(d,out);
        if(!ok)SetError(err,errChars,L"Modular codec could not decode this file");return ok;
    } catch(const std::bad_alloc&) { SetError(err,errChars,L"Codec allocation failed"); return 0; }
      catch(...) { SetError(err,errChars,L"Codec decoder exception contained at ABI boundary"); return 0; }
}
