#include "image_decode_extended_internal.h"
#include <cstring>

namespace GlideDecode::Extended {

struct Rgba8{BYTE r,g,b,a;};
static Rgba8 Decode565(uint16_t c){
    Rgba8 p{};
    p.r=static_cast<BYTE>(((c>>11)&31)*255/31);
    p.g=static_cast<BYTE>(((c>>5)&63)*255/63);
    p.b=static_cast<BYTE>((c&31)*255/31);
    p.a=255;
    return p;
}
static BYTE LerpByte(BYTE a,BYTE b,int wa,int wb,int div){return static_cast<BYTE>((int(a)*wa+int(b)*wb+div/2)/div);}

static void DecodeBcColor(const BYTE* block,bool allowTransparent,Rgba8 out[16]){
    const uint16_t c0=Le16(block),c1=Le16(block+2);
    Rgba8 pal[4]{Decode565(c0),Decode565(c1),{}, {}};
    if(!allowTransparent||c0>c1){
        pal[2]={LerpByte(pal[0].r,pal[1].r,2,1,3),LerpByte(pal[0].g,pal[1].g,2,1,3),LerpByte(pal[0].b,pal[1].b,2,1,3),255};
        pal[3]={LerpByte(pal[0].r,pal[1].r,1,2,3),LerpByte(pal[0].g,pal[1].g,1,2,3),LerpByte(pal[0].b,pal[1].b,1,2,3),255};
    }else{
        pal[2]={LerpByte(pal[0].r,pal[1].r,1,1,2),LerpByte(pal[0].g,pal[1].g,1,1,2),LerpByte(pal[0].b,pal[1].b,1,1,2),255};
        pal[3]={0,0,0,0};
    }
    const uint32_t bits=Le32(block+4);
    for(int i=0;i<16;++i)out[i]=pal[(bits>>(i*2))&3u];
}

static void DecodeBcAlpha(const BYTE* block,BYTE out[16]){
    const BYTE a0=block[0],a1=block[1];
    BYTE pal[8]{a0,a1,0,0,0,0,0,0};
    if(a0>a1){
        for(int i=1;i<=6;++i)pal[i+1]=static_cast<BYTE>(((7-i)*int(a0)+i*int(a1)+3)/7);
    }else{
        for(int i=1;i<=4;++i)pal[i+1]=static_cast<BYTE>(((5-i)*int(a0)+i*int(a1)+2)/5);
        pal[6]=0;pal[7]=255;
    }
    uint64_t bits=0;
    for(int i=0;i<6;++i)bits|=uint64_t(block[2+i])<<(8*i);
    for(int i=0;i<16;++i)out[i]=pal[(bits>>(3*i))&7u];
}

static void StoreDdsBlock(DecodedImage& r,UINT bx,UINT by,const Rgba8 px[16]){
    for(UINT iy=0;iy<4;++iy){
        const UINT y=by*4+iy;if(y>=r.height)continue;
        for(UINT ix=0;ix<4;++ix){
            const UINT x=bx*4+ix;if(x>=r.width)continue;
            const Rgba8& s=px[iy*4+ix];
            PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*r.width+x)*4],s.r,s.g,s.b,s.a);
        }
    }
}

static BYTE MaskToByte(uint32_t value,uint32_t mask){
    if(!mask)return 0;
    unsigned shift=0;while(((mask>>shift)&1u)==0u&&shift<31)++shift;
    uint32_t m=mask>>shift;unsigned bits=0;while((m&1u)&&bits<32){++bits;m>>=1;}
    if(!bits)return 0;
    const uint32_t raw=(value&mask)>>shift;
    const uint64_t maxv=bits>=32?UINT32_MAX:((uint64_t(1)<<bits)-1ull);
    return static_cast<BYTE>((uint64_t(raw)*255ull+maxv/2ull)/maxv);
}

DecodedImage DecodeDds(const DecodeRequest& req){
    DecodedImage r=MakeResultBase(req);
    std::vector<BYTE> b;
    if(!ReadWholeFileBytes(req.path,b)||b.size()<128||memcmp(b.data(),"DDS ",4)!=0||Le32(b.data()+4)!=124||Le32(b.data()+76)!=32)return r;
    const uint32_t h=Le32(b.data()+12),w=Le32(b.data()+16),pitch=Le32(b.data()+20);
    if(!w||!h||!PrepareBgra(r,w,h))return MakeResultBase(req);
    const uint32_t pfFlags=Le32(b.data()+80),fourcc=Le32(b.data()+84),rgbBits=Le32(b.data()+88);
    uint32_t rMask=Le32(b.data()+92),gMask=Le32(b.data()+96),bMask=Le32(b.data()+100),aMask=Le32(b.data()+104);
    size_t dataOffset=128;
    enum class Kind{Unknown,BC1,BC2,BC3,BC4,BC5,RGBA,BGRA,Masked};
    Kind kind=Kind::Unknown;
    auto tag=[](char a,char bb,char c,char d)->uint32_t{return uint32_t(BYTE(a))|(uint32_t(BYTE(bb))<<8)|(uint32_t(BYTE(c))<<16)|(uint32_t(BYTE(d))<<24);};
    if(fourcc==tag('D','X','T','1'))kind=Kind::BC1;
    else if(fourcc==tag('D','X','T','3'))kind=Kind::BC2;
    else if(fourcc==tag('D','X','T','5'))kind=Kind::BC3;
    else if(fourcc==tag('A','T','I','1')||fourcc==tag('B','C','4','U'))kind=Kind::BC4;
    else if(fourcc==tag('A','T','I','2')||fourcc==tag('B','C','5','U'))kind=Kind::BC5;
    else if(fourcc==tag('D','X','1','0')){
        if(b.size()<148)return MakeResultBase(req);
        const uint32_t dxgi=Le32(b.data()+128);dataOffset=148;
        if(dxgi==71||dxgi==72)kind=Kind::BC1;
        else if(dxgi==74||dxgi==75)kind=Kind::BC2;
        else if(dxgi==77||dxgi==78)kind=Kind::BC3;
        else if(dxgi==80)kind=Kind::BC4;
        else if(dxgi==83)kind=Kind::BC5;
        else if(dxgi==28)kind=Kind::RGBA;
        else if(dxgi==87)kind=Kind::BGRA;
    }else if((pfFlags&0x40u)!=0u&&(rgbBits==16||rgbBits==24||rgbBits==32))kind=Kind::Masked;

    const BYTE* p=b.data()+dataOffset,*end=b.data()+b.size();
    if(kind==Kind::BC1||kind==Kind::BC2||kind==Kind::BC3||kind==Kind::BC4||kind==Kind::BC5){
        const size_t blockBytes=(kind==Kind::BC1||kind==Kind::BC4)?8u:16u;
        const UINT bw=(w+3)/4,bh=(h+3)/4;
        if(static_cast<uint64_t>(bw)*bh*blockBytes>static_cast<uint64_t>(end-p))return MakeResultBase(req);
        for(UINT by=0;by<bh;++by)for(UINT bx=0;bx<bw;++bx){
            Rgba8 px[16]{};
            if(kind==Kind::BC1){DecodeBcColor(p,true,px);}
            else if(kind==Kind::BC2){
                DecodeBcColor(p+8,false,px);
                uint64_t abits=0;for(int i=0;i<8;++i)abits|=uint64_t(p[i])<<(8*i);
                for(int i=0;i<16;++i)px[i].a=static_cast<BYTE>(((abits>>(i*4))&0xFu)*17u);
            }else if(kind==Kind::BC3){
                BYTE alpha[16];DecodeBcAlpha(p,alpha);DecodeBcColor(p+8,false,px);for(int i=0;i<16;++i)px[i].a=alpha[i];
            }else if(kind==Kind::BC4){
                BYTE red[16];DecodeBcAlpha(p,red);for(int i=0;i<16;++i)px[i]={red[i],red[i],red[i],255};
            }else{
                BYTE red[16],green[16];DecodeBcAlpha(p,red);DecodeBcAlpha(p+8,green);for(int i=0;i<16;++i)px[i]={red[i],green[i],0,255};
            }
            StoreDdsBlock(r,bx,by,px);p+=blockBytes;
        }
    }else if(kind==Kind::RGBA||kind==Kind::BGRA){
        const size_t rowBytes=static_cast<size_t>(w)*4u;
        if(static_cast<uint64_t>(rowBytes)*h>static_cast<uint64_t>(end-p))return MakeResultBase(req);
        for(uint32_t y=0;y<h;++y)for(uint32_t x=0;x<w;++x){
            const BYTE* s=p+static_cast<size_t>(y)*rowBytes+x*4u;
            if(kind==Kind::RGBA)PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*w+x)*4],s[0],s[1],s[2],s[3]);
            else PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*w+x)*4],s[2],s[1],s[0],s[3]);
        }
    }else if(kind==Kind::Masked){
        const size_t bytesPerPixel=rgbBits/8u;
        const size_t tight=static_cast<size_t>(w)*bytesPerPixel;
        size_t rowPitch=tight;
        if((Le32(b.data()+8)&0x8u)!=0u&&pitch>=tight)rowPitch=pitch;
        if(static_cast<uint64_t>(rowPitch)*h>static_cast<uint64_t>(end-p))return MakeResultBase(req);
        for(uint32_t y=0;y<h;++y)for(uint32_t x=0;x<w;++x){
            const BYTE* s=p+static_cast<size_t>(y)*rowPitch+static_cast<size_t>(x)*bytesPerPixel;
            uint32_t v=0;for(size_t i=0;i<bytesPerPixel;++i)v|=uint32_t(s[i])<<(8*i);
            const BYTE rr=MaskToByte(v,rMask),gg=MaskToByte(v,gMask),bb=MaskToByte(v,bMask),aa=aMask?MaskToByte(v,aMask):255;
            PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*w+x)*4],rr,gg,bb,aa);
        }
    }else return MakeResultBase(req);
    r.hr=S_OK;
    return r;
}


} // namespace GlideDecode::Extended
