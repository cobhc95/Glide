#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include "image_decode.h"
#include "image_decode_extended_internal.h"
#include "codec_bridge_api.h"
#include <windows.h>
#include <shobjidl.h>
#include <array>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <new>
#include <chrono>
#include <sstream>
#include <algorithm>
#include <cwctype>

namespace fs = std::filesystem;

namespace GlideDecode {

// image_decode.cpp is compiled with /DDecodeFile=DecodeFileWicBase. This
// preserves the accepted 1.2.57 WIC/JPEG/PSD/TGA/PNM decoder implementation
// and places GlideA behind it as a zero-regression fallback layer.
DecodedImage DecodeFileWicBase(IWICImagingFactory* wic, const DecodeRequest& req,
                               const std::atomic<uint64_t>* latestGeneration);

namespace {

enum class FallbackKind { Unknown, Dib, Tga, Pnm, Qoi, Pcx, RadianceHdr, Farbfeld, Dds, Wbmp, Pfm, Xbm };

FallbackKind DetectFallbackKind(const std::wstring& path) {
    std::array<BYTE,32> head{};
    std::ifstream f(fs::path(path),std::ios::binary);
    if(f) f.read(reinterpret_cast<char*>(head.data()),static_cast<std::streamsize>(head.size()));
    const std::streamsize got=f.gcount();
    if(got>=4&&memcmp(head.data(),"qoif",4)==0)return FallbackKind::Qoi;
    if(got>=8&&memcmp(head.data(),"farbfeld",8)==0)return FallbackKind::Farbfeld;
    if(got>=4&&memcmp(head.data(),"DDS ",4)==0)return FallbackKind::Dds;
    if(got>=10&&(memcmp(head.data(),"#?RADIANCE",10)==0||(got>=6&&memcmp(head.data(),"#?RGBE",6)==0)))return FallbackKind::RadianceHdr;
    if(got>=2&&head[0]=='P'&&head[1]>='1'&&head[1]<='7')return FallbackKind::Pnm;
    if(got>=3&&head[0]=='P'&&(head[1]=='F'||head[1]=='f')&&(head[2]=='\n'||head[2]=='\r'||head[2]==' '))return FallbackKind::Pfm;
    if(got>=7&&memcmp(head.data(),"#define",7)==0)return FallbackKind::Xbm;
    if(got>=4&&head[0]==0x0A&&head[2]==1&&(head[3]==1||head[3]==8))return FallbackKind::Pcx;

    const std::wstring ext=Extended::Lower(fs::path(path).extension().wstring());
    if(ext==L".dib"||ext==L".rle")return FallbackKind::Dib;
    if(ext==L".tga"||ext==L".targa"||ext==L".icb"||ext==L".vda"||ext==L".vst")return FallbackKind::Tga;
    if(ext==L".pnm"||ext==L".ppm"||ext==L".pgm"||ext==L".pbm"||ext==L".pam")return FallbackKind::Pnm;
    if(ext==L".qoi")return FallbackKind::Qoi;
    if(ext==L".pcx")return FallbackKind::Pcx;
    if(ext==L".hdr"||ext==L".rgbe"||ext==L".xyze")return FallbackKind::RadianceHdr;
    if(ext==L".ff")return FallbackKind::Farbfeld;
    if(ext==L".dds")return FallbackKind::Dds;
    if(ext==L".wbmp")return FallbackKind::Wbmp;
    if(ext==L".pfm")return FallbackKind::Pfm;
    if(ext==L".xbm")return FallbackKind::Xbm;
    return FallbackKind::Unknown;
}

DecodedImage DownscaleFallback(DecodedImage r,const DecodeRequest& req) {
    if(FAILED(r.hr)||!req.preview||!req.previewMaxWidth||!req.previewMaxHeight||!r.width||!r.height||
       (r.width<=req.previewMaxWidth&&r.height<=req.previewMaxHeight))return r;
    const UINT sw=r.width,sh=r.height;
    const double scale=std::min(double(req.previewMaxWidth)/sw,double(req.previewMaxHeight)/sh);
    const UINT dw=std::max<UINT>(1,static_cast<UINT>(std::lround(sw*scale)));
    const UINT dh=std::max<UINT>(1,static_cast<UINT>(std::lround(sh*scale)));
    const uint64_t stride64=static_cast<uint64_t>(dw)*4ull,bytes64=stride64*dh;
    if(stride64>UINT_MAX||bytes64>UINT_MAX)return r;
    std::vector<BYTE> out(static_cast<size_t>(bytes64));
    for(UINT y=0;y<dh;++y){
        const double sy=(static_cast<double>(y)+0.5)*sh/dh-0.5;
        const UINT y0=static_cast<UINT>(std::clamp<int>(static_cast<int>(std::floor(sy)),0,static_cast<int>(sh)-1));
        const UINT y1=std::min<UINT>(y0+1,sh-1);const double fy=std::clamp(sy-y0,0.0,1.0);
        for(UINT x=0;x<dw;++x){
            const double sx=(static_cast<double>(x)+0.5)*sw/dw-0.5;
            const UINT x0=static_cast<UINT>(std::clamp<int>(static_cast<int>(std::floor(sx)),0,static_cast<int>(sw)-1));
            const UINT x1=std::min<UINT>(x0+1,sw-1);const double fx=std::clamp(sx-x0,0.0,1.0);
            BYTE* d=&out[(static_cast<size_t>(y)*dw+x)*4];
            for(int c=0;c<4;++c){
                const double a=r.pixels[(static_cast<size_t>(y0)*sw+x0)*4+c]*(1.0-fx)+r.pixels[(static_cast<size_t>(y0)*sw+x1)*4+c]*fx;
                const double b=r.pixels[(static_cast<size_t>(y1)*sw+x0)*4+c]*(1.0-fx)+r.pixels[(static_cast<size_t>(y1)*sw+x1)*4+c]*fx;
                d[c]=static_cast<BYTE>(std::clamp<int>(static_cast<int>(std::lround(a*(1.0-fy)+b*fy)),0,255));
            }
        }
    }
    r.width=dw;r.height=dh;r.stride=static_cast<UINT>(stride64);r.pixels=std::move(out);r.preview=true;
    return r;
}



DecodedImage DecodeModularCodecFallback(const DecodeRequest& req) {
    DecodedImage out=Extended::MakeResultBase(req);
    wchar_t exePath[MAX_PATH]{};
    const DWORD n=GetModuleFileNameW(nullptr,exePath,MAX_PATH);
    if(!n||n>=MAX_PATH){out.hr=E_FAIL;return out;}
    fs::path dll=fs::path(exePath).parent_path()/L"codecs"/L"GlideCodecBridge.dll";
    HMODULE mod=LoadLibraryExW(dll.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if(!mod){out.hr=HRESULT_FROM_WIN32(GetLastError());return out;}
    const auto decode=reinterpret_cast<GlideCodecDecodeFileWFn>(GetProcAddress(mod,"GlideCodecDecodeFileW"));
    const auto freeFn=reinterpret_cast<GlideCodecFreeFn>(GetProcAddress(mod,"GlideCodecFree"));
    if(!decode||!freeFn){FreeLibrary(mod);out.hr=HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);return out;}
    GlideCodecImageV1 image{}; image.structSize=sizeof(image); wchar_t err[256]{};
    const int ok=decode(req.path.c_str(),&image,err,256u);
    if(!ok||!image.pixels||!image.width||!image.height||image.stride<image.width*4ull||image.dataSize<static_cast<std::uint64_t>(image.stride)*image.height){
        if(image.pixels)freeFn(image.pixels);FreeLibrary(mod);out.hr=E_FAIL;return out;
    }
    try {
        out.width=image.width; out.height=image.height; out.sourceWidth=image.width; out.sourceHeight=image.height; out.stride=image.stride;
        out.pixels.assign(image.pixels,image.pixels+static_cast<size_t>(image.dataSize)); out.hr=S_OK; out.nativeFallback=true;
    } catch(...) { out.hr=E_OUTOFMEMORY; out.pixels.clear(); }
    freeFn(image.pixels); FreeLibrary(mod);
    return DownscaleFallback(std::move(out),req);
}


DecodedImage DecodeShellThumbnailFallback(const DecodeRequest& req) {
    DecodedImage out=Extended::MakeResultBase(req);
    // Last-resort Windows shell thumbnail/preview bridge. This keeps Glide Core
    // tiny while allowing installed application thumbnail providers (Affinity,
    // Krita, XCF/PSD tools, camera suites, SVG/PDF handlers, etc.) to participate
    // without linking their SDKs into Glide. THUMBNAILONLY is deliberate: never
    // accept a generic file icon as if it were decoded image content.
    Microsoft::WRL::ComPtr<IShellItem> item;
    HRESULT hr=SHCreateItemFromParsingName(req.path.c_str(),nullptr,IID_PPV_ARGS(&item));
    if(FAILED(hr)){out.hr=hr;return out;}
    Microsoft::WRL::ComPtr<IShellItemImageFactory> factory;
    hr=item.As(&factory);if(FAILED(hr)){out.hr=hr;return out;}
    const int maxW=req.preview&&req.previewMaxWidth?static_cast<int>(req.previewMaxWidth):8192;
    const int maxH=req.preview&&req.previewMaxHeight?static_cast<int>(req.previewMaxHeight):8192;
    SIZE requested{std::clamp(maxW,64,8192),std::clamp(maxH,64,8192)};
    HBITMAP bmp=nullptr;
    hr=factory->GetImage(requested,static_cast<SIIGBF>(SIIGBF_BIGGERSIZEOK|SIIGBF_RESIZETOFIT|SIIGBF_THUMBNAILONLY),&bmp);
    if(FAILED(hr)||!bmp){out.hr=FAILED(hr)?hr:E_FAIL;return out;}
    BITMAP bm{};if(!GetObjectW(bmp,sizeof(bm),&bm)||bm.bmWidth<=0||bm.bmHeight==0){DeleteObject(bmp);out.hr=E_FAIL;return out;}
    const UINT w=static_cast<UINT>(bm.bmWidth),h=static_cast<UINT>(bm.bmHeight<0?-bm.bmHeight:bm.bmHeight);
    if(!Extended::PrepareBgra(out,w,h)){DeleteObject(bmp);out.hr=E_OUTOFMEMORY;return out;}
    BITMAPINFO bi{};bi.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);bi.bmiHeader.biWidth=static_cast<LONG>(w);bi.bmiHeader.biHeight=-static_cast<LONG>(h);
    bi.bmiHeader.biPlanes=1;bi.bmiHeader.biBitCount=32;bi.bmiHeader.biCompression=BI_RGB;
    HDC dc=GetDC(nullptr);const int rows=GetDIBits(dc,bmp,0,h,out.pixels.data(),&bi,DIB_RGB_COLORS);ReleaseDC(nullptr,dc);DeleteObject(bmp);
    if(rows!=static_cast<int>(h)){out.pixels.clear();out.hr=E_FAIL;return out;}
    // Shell providers vary: some return straight-alpha BGRA, others leave alpha
    // zero for fully opaque thumbnails. Normalize to Glide's premultiplied BGRA.
    bool anyAlpha=false,looksStraight=false;
    for(size_t i=0;i+3<out.pixels.size();i+=4){const BYTE a=out.pixels[i+3];if(a)anyAlpha=true;if(a<255&&(out.pixels[i]>a||out.pixels[i+1]>a||out.pixels[i+2]>a))looksStraight=true;}
    for(size_t i=0;i+3<out.pixels.size();i+=4){BYTE&a=out.pixels[i+3];if(!anyAlpha)a=255;if(looksStraight){const unsigned alpha=a;out.pixels[i]=BYTE((unsigned(out.pixels[i])*alpha+127)/255);out.pixels[i+1]=BYTE((unsigned(out.pixels[i+1])*alpha+127)/255);out.pixels[i+2]=BYTE((unsigned(out.pixels[i+2])*alpha+127)/255);}}
    out.hr=S_OK;out.nativeFallback=true;out.preview=req.preview;return out;
}

DecodedImage DecodeFallback(const DecodeRequest& req) {
    DecodedImage out=Extended::MakeResultBase(req);
    try {
        switch(DetectFallbackKind(req.path)) {
            case FallbackKind::Dib: out=Extended::DecodeDib(req);break;
            case FallbackKind::Tga: out=Extended::DecodeTga(req);break;
            case FallbackKind::Pnm: out=Extended::DecodePnm(req);break;
            case FallbackKind::Qoi: out=Extended::DecodeQoi(req);break;
            case FallbackKind::Pcx: out=Extended::DecodePcx(req);break;
            case FallbackKind::RadianceHdr: out=Extended::DecodeRadianceHdr(req);break;
            case FallbackKind::Farbfeld: out=Extended::DecodeFarbfeld(req);break;
            case FallbackKind::Dds: out=Extended::DecodeDds(req);break;
            case FallbackKind::Wbmp: out=Extended::DecodeWbmp(req);break;
            case FallbackKind::Pfm: out=Extended::DecodePfm(req);break;
            case FallbackKind::Xbm: out=Extended::DecodeXbm(req);break;
            default:out.hr=E_FAIL;return out;
        }
        return DownscaleFallback(std::move(out),req);
    } catch(const std::bad_alloc&) {
        out.hr=E_OUTOFMEMORY;
    } catch(...) {
        out.hr=E_FAIL;
    }
    return out;
}

} // namespace

DecodedImage DecodeFile(IWICImagingFactory* wic, const DecodeRequest& req,
                        const std::atomic<uint64_t>* latestGeneration) {
    // WIC-first remains Glide's cheapest path. Formats that Windows cannot decode
    // fall through to Glide's compact native decoders, then the bundled modular
    // GlideCodecBridge. No external converter process or temporary interchange file.
    DecodedImage base=DecodeFileWicBase(wic,req,latestGeneration);
    if(SUCCEEDED(base.hr))return base;
    if(req.currentRequest&&latestGeneration&&
       req.generation!=latestGeneration->load(std::memory_order_relaxed))return base;
    DecodedImage fallback=DecodeFallback(req);
    if(FAILED(fallback.hr)) fallback=DecodeModularCodecFallback(req);
    if(FAILED(fallback.hr)) fallback=DecodeShellThumbnailFallback(req);
    if(SUCCEEDED(fallback.hr)) {
        fallback.wicFilenameHr=base.wicFilenameHr;
        fallback.wicStreamInitHr=base.wicStreamInitHr;
        fallback.wicStreamDecoderHr=base.wicStreamDecoderHr;
        fallback.wicStreamUsed=base.wicStreamUsed;
        fallback.nativeFallback=true;
        return fallback;
    }
    return base;
}

} // namespace GlideDecode
