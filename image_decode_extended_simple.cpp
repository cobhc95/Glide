#include "image_decode_extended_internal.h"
#include <cctype>
#include <sstream>
#include <cstring>

namespace GlideDecode::Extended {

// BI_ALPHABITFIELDS (compression value 6) is part of the DIB specification,
// but is absent from some Windows SDK header sets.  Keep the decoder portable
// across the Visual Studio versions Glide supports instead of relying on a
// conditional SDK macro.
static constexpr uint32_t kBiAlphaBitfields = 6u;

static BYTE DibMaskToByte(uint32_t value,uint32_t mask) {
    if(!mask)return 0;
    unsigned shift=0;while(shift<32&&((mask>>shift)&1u)==0u)++shift;
    if(shift>=32)return 0;
    uint32_t bitsMask=mask>>shift;unsigned bits=0;
    while(bits<32&&(bitsMask&1u)){++bits;bitsMask>>=1;}
    if(!bits)return 0;
    const uint32_t raw=(value&mask)>>shift;
    const uint64_t maxValue=bits==32?UINT32_MAX:((uint64_t(1)<<bits)-1ull);
    return static_cast<BYTE>((uint64_t(raw)*255ull+maxValue/2ull)/maxValue);
}

static uint32_t DibPixelValue(const BYTE* p,unsigned bytes) {
    uint32_t value=0;
    for(unsigned i=0;i<bytes;++i)value|=uint32_t(p[i])<<(8u*i);
    return value;
}

DecodedImage DecodeDib(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);
    std::vector<BYTE> b;
    if(!ReadWholeFileBytes(req.path,b)||b.size()<40)return r;

    const uint32_t headerSize=Le32(b.data());
    if(headerSize<40||headerSize>b.size())return r;
    const int32_t signedW=static_cast<int32_t>(Le32(b.data()+4));
    const int32_t signedH=static_cast<int32_t>(Le32(b.data()+8));
    const uint16_t planes=Le16(b.data()+12),bits=Le16(b.data()+14);
    const uint32_t compression=Le32(b.data()+16),colorsUsed=Le32(b.data()+32);
    if(signedW<=0||signedH==0||signedH==INT32_MIN||planes!=1||
       (bits!=1&&bits!=4&&bits!=8&&bits!=16&&bits!=24&&bits!=32))return r;
    if(compression!=BI_RGB&&compression!=BI_BITFIELDS&&compression!=kBiAlphaBitfields)return r;
    const uint32_t w=static_cast<uint32_t>(signedW);
    const uint32_t h=static_cast<uint32_t>(signedH<0?-static_cast<int64_t>(signedH):signedH);
    if(!w||!h||w>50000||h>50000)return r;

    uint32_t redMask=0,greenMask=0,blueMask=0,alphaMask=0;
    size_t dataOffset=headerSize;
    if(compression==BI_BITFIELDS||compression==kBiAlphaBitfields) {
        if(headerSize>=52) {
            redMask=Le32(b.data()+40);greenMask=Le32(b.data()+44);blueMask=Le32(b.data()+48);
            if(headerSize>=56)alphaMask=Le32(b.data()+52);
        } else {
            const size_t maskBytes=compression==kBiAlphaBitfields?16u:12u;
            if(b.size()<40u+maskBytes)return r;
            redMask=Le32(b.data()+40);greenMask=Le32(b.data()+44);blueMask=Le32(b.data()+48);
            if(maskBytes==16)alphaMask=Le32(b.data()+52);
            dataOffset=40u+maskBytes;
        }
        if(!redMask||!greenMask||!blueMask)return r;
    } else if(bits==16) {
        // BI_RGB 16-bit DIBs use the Windows 5-5-5 layout.
        redMask=0x7C00u;greenMask=0x03E0u;blueMask=0x001Fu;
    } else if(bits==32) {
        redMask=0x00FF0000u;greenMask=0x0000FF00u;blueMask=0x000000FFu;
    }

    if(bits<=8) {
        const uint32_t maxPalette=1u<<bits;
        const uint32_t paletteCount=std::min<uint32_t>(colorsUsed?colorsUsed:maxPalette,maxPalette);
        const uint64_t paletteBytes=uint64_t(paletteCount)*4ull;
        if(paletteBytes>SIZE_MAX||dataOffset+static_cast<size_t>(paletteBytes)>b.size())return r;
        dataOffset+=static_cast<size_t>(paletteBytes);
    }
    const uint64_t rowBits=uint64_t(w)*bits;
    const uint64_t rowBytes=((rowBits+31ull)/32ull)*4ull;
    const uint64_t imageBytes=rowBytes*h;
    if(rowBytes>SIZE_MAX||imageBytes>SIZE_MAX||dataOffset> b.size()||imageBytes>b.size()-dataOffset)return r;
    if(!PrepareBgra(r,w,h))return r;
    const bool topDown=signedH<0;
    const uint32_t paletteCount=bits<=8?std::min<uint32_t>(colorsUsed?colorsUsed:(1u<<bits),1u<<bits):0;
    const size_t paletteOffset=(bits<=8)?(dataOffset-static_cast<size_t>(uint64_t(paletteCount)*4ull)):0;

    for(uint32_t y=0;y<h;++y) {
        const uint32_t sourceY=topDown?y:(h-1u-y);
        const BYTE* row=b.data()+dataOffset+static_cast<size_t>(sourceY*rowBytes);
        for(uint32_t x=0;x<w;++x) {
            BYTE red=0,green=0,blue=0,alpha=255;
            if(bits<=8) {
                uint32_t index=0;
                if(bits==8)index=row[x];
                else if(bits==4)index=(row[x/2]>>((x&1u)?0:4))&0xFu;
                else index=(row[x/8]>>(7u-(x&7u)))&1u;
                if(index>=paletteCount)return MakeResultBase(req);
                const BYTE* p=b.data()+paletteOffset+static_cast<size_t>(index)*4u;
                blue=p[0];green=p[1];red=p[2];
            } else if(bits==24) {
                const BYTE* p=row+static_cast<size_t>(x)*3u;blue=p[0];green=p[1];red=p[2];
            } else if(bits==16) {
                const uint32_t value=DibPixelValue(row+static_cast<size_t>(x)*2u,2);
                red=DibMaskToByte(value,redMask);green=DibMaskToByte(value,greenMask);blue=DibMaskToByte(value,blueMask);
                if(alphaMask)alpha=DibMaskToByte(value,alphaMask);
            } else {
                const uint32_t value=DibPixelValue(row+static_cast<size_t>(x)*4u,4);
                red=DibMaskToByte(value,redMask);green=DibMaskToByte(value,greenMask);blue=DibMaskToByte(value,blueMask);
                if(alphaMask)alpha=DibMaskToByte(value,alphaMask);
            }
            PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*w+x)*4],red,green,blue,alpha);
        }
    }
    r.hr=S_OK;
    return r;
}

DecodedImage DecodeTga(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);
    std::vector<BYTE> b;
    if(!ReadWholeFileBytes(req.path,b)||b.size()<18)return r;
    const BYTE* h=b.data();
    const BYTE idLen=h[0], cmap=h[1], type=h[2];
    const uint16_t w=uint16_t(h[12]|(h[13]<<8)), hh=uint16_t(h[14]|(h[15]<<8));
    const BYTE bpp=h[16], desc=h[17];
    const bool trueColor=(type==2||type==10)&&(bpp==24||bpp==32);
    const bool gray=(type==3||type==11)&&bpp==8;
    if(cmap!=0||!w||!hh||(!trueColor&&!gray))return r;
    const BYTE* p0=b.data()+18+idLen,*end=b.data()+b.size();
    if(p0>end)return r;
    const size_t pixels=size_t(w)*hh;
    const size_t pxbytes=bpp/8;
    if (pixels > SIZE_MAX/pxbytes) return r;
    std::vector<BYTE> raw(pixels*pxbytes);
    const BYTE* p=p0;
    if(type==2||type==3){
        if(size_t(end-p)<raw.size())return r;
        memcpy(raw.data(),p,raw.size());
    } else {
        size_t out=0;
        while(out<pixels&&p<end){
            const BYTE head=*p++;
            const size_t run=(head&0x7f)+1;
            if(head&0x80){
                if(size_t(end-p)<pxbytes || out+run>pixels)return r;
                for(size_t i=0;i<run;++i,++out)memcpy(raw.data()+out*pxbytes,p,pxbytes);
                p+=pxbytes;
            }else{
                const size_t n=run*pxbytes;
                if(size_t(end-p)<n||out+run>pixels)return r;
                memcpy(raw.data()+out*pxbytes,p,n);p+=n;out+=run;
            }
        }
        if(out!=pixels)return r;
    }
    if(!PrepareBgra(r,w,hh))return r;
    const bool top=(desc&0x20)!=0;
    const bool right=(desc&0x10)!=0;
    for(uint32_t y=0;y<hh;++y){
        const uint32_t sy=top?y:(hh-1-y);
        for(uint32_t x=0;x<w;++x){
            const uint32_t sx=right?(w-1-x):x;
            const BYTE* sp=&raw[(size_t(sy)*w+sx)*pxbytes];
            BYTE* d=&r.pixels[(size_t(y)*w+x)*4];
            if(type==3||type==11) PutPremulBgra(d,sp[0],sp[0],sp[0],255);
            else PutPremulBgra(d,sp[2],sp[1],sp[0],pxbytes==4?sp[3]:255);
        }
    }
    r.hr=S_OK;
    return r;
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
        if (tok.size() >= 64) return false;
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

static bool ConsumePnmSeparator(const BYTE*& p, const BYTE* end) {
    if (p>=end || !std::isspace(static_cast<unsigned char>(*p))) return false;
    if (*p=='\r' && p+1<end && p[1]=='\n') p+=2;
    else ++p;
    return true;
}

static BYTE ScalePnmSample(uint32_t v, uint32_t maxv) {
    if (v>maxv) v=maxv;
    return static_cast<BYTE>((static_cast<uint64_t>(v)*255ull + maxv/2u)/maxv);
}

static bool ReadPnmBinarySample(const BYTE*& p, const BYTE* end, uint32_t maxv, uint32_t& value) {
    if (maxv<256) {
        if (p>=end) return false;
        value=*p++;
        return true;
    }
    if (p+2>end) return false;
    value=Be16(p);p+=2;
    return true;
}

static DecodedImage DecodePam(const DecodeRequest& req, const std::vector<BYTE>& b, const BYTE* p) {
    DecodedImage r=MakeResultBase(req);
    const BYTE* end=b.data()+b.size();
    // Move from the P7 token to the next header line.
    if(p<end && *p=='\r')++p;
    if(p<end && *p=='\n')++p;
    else if(p<end && std::isspace(static_cast<unsigned char>(*p)))++p;

    uint32_t w=0,h=0,depth=0,maxv=0;
    bool endHeader=false;
    std::string line;
    while(ReadAsciiLine(p,end,line)) {
        const auto hash=line.find('#');
        if(hash!=std::string::npos)line.resize(hash);
        std::istringstream iss(line);
        std::string key,value;
        if(!(iss>>key))continue;
        if(key=="ENDHDR"){endHeader=true;break;}
        if(!(iss>>value))continue;
        uint32_t parsed=0;
        if((key=="WIDTH"||key=="HEIGHT"||key=="DEPTH"||key=="MAXVAL") && !ParseUint32Token(value,parsed))return r;
        if(key=="WIDTH")w=parsed;
        else if(key=="HEIGHT")h=parsed;
        else if(key=="DEPTH")depth=parsed;
        else if(key=="MAXVAL")maxv=parsed;
    }
    if(!endHeader||!w||!h||depth<1||depth>4||!maxv||maxv>65535)return r;
    if(!PrepareBgra(r,w,h))return r;
    const uint64_t samples=static_cast<uint64_t>(w)*h*depth;
    const uint64_t sourceBytes=samples*(maxv<256?1ull:2ull);
    if(sourceBytes>static_cast<uint64_t>(end-p))return MakeResultBase(req);
    for(uint64_t i=0;i<static_cast<uint64_t>(w)*h;++i){
        uint32_t s[4]{0,0,0,maxv};
        for(uint32_t c=0;c<depth;++c)if(!ReadPnmBinarySample(p,end,maxv,s[c]))return MakeResultBase(req);
        BYTE rr=0,gg=0,bb=0,aa=255;
        if(depth==1){rr=gg=bb=ScalePnmSample(s[0],maxv);}
        else if(depth==2){rr=gg=bb=ScalePnmSample(s[0],maxv);aa=ScalePnmSample(s[1],maxv);}
        else {rr=ScalePnmSample(s[0],maxv);gg=ScalePnmSample(s[1],maxv);bb=ScalePnmSample(s[2],maxv);if(depth==4)aa=ScalePnmSample(s[3],maxv);}
        PutPremulBgra(&r.pixels[static_cast<size_t>(i)*4],rr,gg,bb,aa);
    }
    r.hr=S_OK;
    return r;
}

DecodedImage DecodePnm(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);
    std::vector<BYTE> b;
    if (!ReadWholeFileBytes(req.path, b)) return r;
    const BYTE* p=b.data(); const BYTE* end=b.data()+b.size();
    std::string token;
    if (!PnmToken(p,end,token) || token.size()!=2 || token[0]!='P' || token[1]<'1' || token[1]>'7') return r;
    const int kind=token[1]-'0';
    if(kind==7)return DecodePam(req,b,p);

    uint32_t w=0,h=0,maxv=1;
    if (!PnmToken(p,end,token) || !ParseUint32Token(token,w)) return r;
    if (!PnmToken(p,end,token) || !ParseUint32Token(token,h)) return r;
    if(kind!=1 && kind!=4){
        if (!PnmToken(p,end,token) || !ParseUint32Token(token,maxv)) return r;
        if(!maxv||maxv>65535)return r;
    }
    if(!w||!h)return r;
    if(!PrepareBgra(r,w,h))return r;
    const uint64_t pixels=static_cast<uint64_t>(w)*h;

    if(kind>=1 && kind<=3){
        for(uint64_t i=0;i<pixels;++i){
            uint32_t a=0,g=0,bb=0;
            if(!PnmToken(p,end,token)||!ParseUint32Token(token,a))return MakeResultBase(req);
            BYTE rr=0,gg=0,bl=0;
            if(kind==1){rr=gg=bl=(a==0?255:0);}
            else if(kind==2){rr=gg=bl=ScalePnmSample(a,maxv);}
            else{
                if(!PnmToken(p,end,token)||!ParseUint32Token(token,g))return MakeResultBase(req);
                if(!PnmToken(p,end,token)||!ParseUint32Token(token,bb))return MakeResultBase(req);
                rr=ScalePnmSample(a,maxv);gg=ScalePnmSample(g,maxv);bl=ScalePnmSample(bb,maxv);
            }
            PutPremulBgra(&r.pixels[static_cast<size_t>(i)*4],rr,gg,bl,255);
        }
    } else if(kind==4){
        if(!ConsumePnmSeparator(p,end))return MakeResultBase(req);
        const size_t rowBytes=(static_cast<size_t>(w)+7u)/8u;
        if(static_cast<uint64_t>(rowBytes)*h>static_cast<uint64_t>(end-p))return MakeResultBase(req);
        for(uint32_t y=0;y<h;++y){
            const BYTE* row=p+static_cast<size_t>(y)*rowBytes;
            for(uint32_t x=0;x<w;++x){
                const bool black=(row[x/8]&(0x80u>>(x&7)))!=0;
                const BYTE v=black?0:255;
                PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*w+x)*4],v,v,v,255);
            }
        }
    } else {
        if(!ConsumePnmSeparator(p,end))return MakeResultBase(req);
        const int channels=kind==6?3:1;
        for(uint64_t i=0;i<pixels;++i){
            uint32_t s0=0,s1=0,s2=0;
            if(!ReadPnmBinarySample(p,end,maxv,s0))return MakeResultBase(req);
            if(channels==3){
                if(!ReadPnmBinarySample(p,end,maxv,s1)||!ReadPnmBinarySample(p,end,maxv,s2))return MakeResultBase(req);
            }else s1=s2=s0;
            PutPremulBgra(&r.pixels[static_cast<size_t>(i)*4],ScalePnmSample(s0,maxv),ScalePnmSample(s1,maxv),ScalePnmSample(s2,maxv),255);
        }
    }
    r.hr=S_OK;
    return r;
}


} // namespace GlideDecode::Extended
