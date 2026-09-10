#include "image_decode_extended_internal.h"
#include <array>
#include <cstring>
#include <sstream>
#include <cstdlib>

namespace GlideDecode::Extended {
DecodedImage DecodeQoi(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);
    std::vector<BYTE> b;
    if(!ReadWholeFileBytes(req.path,b)||b.size()<14||memcmp(b.data(),"qoif",4)!=0)return r;
    const uint32_t w=Be32(b.data()+4),h=Be32(b.data()+8);
    const BYTE channels=b[12];
    if(!w||!h||(channels!=3&&channels!=4)||!PrepareBgra(r,w,h))return MakeResultBase(req);
    struct Px{BYTE r,g,b,a;};
    std::array<Px,64> index{};
    Px px{0,0,0,255};
    const BYTE* p=b.data()+14;const BYTE* end=b.data()+b.size();
    int run=0;
    const uint64_t count=static_cast<uint64_t>(w)*h;
    for(uint64_t i=0;i<count;++i){
        if(run>0){--run;}
        else{
            if(p>=end)return MakeResultBase(req);
            const BYTE op=*p++;
            if(op==0xFE){if(p+3>end)return MakeResultBase(req);px.r=*p++;px.g=*p++;px.b=*p++;}
            else if(op==0xFF){if(p+4>end)return MakeResultBase(req);px.r=*p++;px.g=*p++;px.b=*p++;px.a=*p++;}
            else{
                switch(op&0xC0){
                    case 0x00:px=index[op&0x3F];break;
                    case 0x40:px.r=BYTE(px.r+((op>>4)&0x03)-2);px.g=BYTE(px.g+((op>>2)&0x03)-2);px.b=BYTE(px.b+(op&0x03)-2);break;
                    case 0x80:{if(p>=end)return MakeResultBase(req);const BYTE b2=*p++;const int dg=(op&0x3F)-32;px.r=BYTE(px.r+dg+((b2>>4)&0x0F)-8);px.g=BYTE(px.g+dg);px.b=BYTE(px.b+dg+(b2&0x0F)-8);break;}
                    case 0xC0:run=(op&0x3F);break;
                }
            }
            const size_t hash=(static_cast<size_t>(px.r)*3u+static_cast<size_t>(px.g)*5u+static_cast<size_t>(px.b)*7u+static_cast<size_t>(px.a)*11u)%64u;
            index[hash]=px;
        }
        PutPremulBgra(&r.pixels[static_cast<size_t>(i)*4],px.r,px.g,px.b,px.a);
    }
    r.hr=S_OK;
    return r;
}

DecodedImage DecodeFarbfeld(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);
    std::vector<BYTE> b;
    if(!ReadWholeFileBytes(req.path,b)||b.size()<16||memcmp(b.data(),"farbfeld",8)!=0)return r;
    const uint32_t w=Be32(b.data()+8),h=Be32(b.data()+12);
    if(!w||!h||!PrepareBgra(r,w,h))return MakeResultBase(req);
    const uint64_t count=static_cast<uint64_t>(w)*h;
    if(count>UINT64_MAX/8ull || 16ull+count*8ull>b.size())return MakeResultBase(req);
    const BYTE* p=b.data()+16;
    for(uint64_t i=0;i<count;++i,p+=8){
        auto to8=[](uint16_t v)->BYTE{return static_cast<BYTE>((static_cast<uint32_t>(v)+128u)/257u);};
        PutPremulBgra(&r.pixels[static_cast<size_t>(i)*4],to8(Be16(p)),to8(Be16(p+2)),to8(Be16(p+4)),to8(Be16(p+6)));
    }
    r.hr=S_OK;
    return r;
}

static bool DecodePcxRleRow(const BYTE*& p,const BYTE* end,BYTE* dst,size_t bytes) {
    size_t out=0;
    while(out<bytes&&p<end){
        BYTE v=*p++;size_t run=1;
        if((v&0xC0)==0xC0){run=v&0x3F;if(!run||p>=end)return false;v=*p++;}
        if(out+run>bytes)return false;
        memset(dst+out,v,run);out+=run;
    }
    return out==bytes;
}

DecodedImage DecodePcx(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);
    std::vector<BYTE> b;
    if(!ReadWholeFileBytes(req.path,b)||b.size()<128||b[0]!=0x0A||b[2]!=1)return r;
    const BYTE bits=b[3],planes=b[65];
    const uint16_t xmin=Le16(b.data()+4),ymin=Le16(b.data()+6),xmax=Le16(b.data()+8),ymax=Le16(b.data()+10);
    const uint16_t bytesPerLine=Le16(b.data()+66);
    if(xmax<xmin||ymax<ymin||!bytesPerLine||!planes)return r;
    const uint32_t w=uint32_t(xmax)-xmin+1u,h=uint32_t(ymax)-ymin+1u;
    if(!PrepareBgra(r,w,h))return MakeResultBase(req);

    const BYTE* p=b.data()+128;
    const BYTE* dataEnd=b.data()+b.size();
    const BYTE* palette256=nullptr;
    if(bits==8&&planes==1&&b.size()>=769&&b[b.size()-769]==0x0C){palette256=b.data()+b.size()-768;dataEnd=b.data()+b.size()-769;}
    const size_t rowBytes=static_cast<size_t>(bytesPerLine)*planes;
    if(rowBytes>64ull*1024ull*1024ull)return MakeResultBase(req);
    std::vector<BYTE> row(rowBytes);
    for(uint32_t y=0;y<h;++y){
        if(!DecodePcxRleRow(p,dataEnd,row.data(),rowBytes))return MakeResultBase(req);
        for(uint32_t x=0;x<w;++x){
            BYTE rr=0,gg=0,bb=0;
            if(bits==8&&planes>=3){
                if(x>=bytesPerLine)return MakeResultBase(req);
                rr=row[x];gg=row[bytesPerLine+x];bb=row[static_cast<size_t>(bytesPerLine)*2+x];
            }else if(bits==8&&planes==1){
                if(x>=bytesPerLine)return MakeResultBase(req);
                const BYTE idx=row[x];
                if(palette256){rr=palette256[idx*3];gg=palette256[idx*3+1];bb=palette256[idx*3+2];}
                else if(idx<16){const BYTE* pal=b.data()+16+idx*3;rr=pal[0];gg=pal[1];bb=pal[2];}
                else rr=gg=bb=idx;
            }else if(bits==1&&planes<=4){
                if(x/8>=bytesPerLine)return MakeResultBase(req);
                BYTE idx=0;
                for(BYTE pl=0;pl<planes;++pl)if(row[static_cast<size_t>(pl)*bytesPerLine+x/8]&(0x80u>>(x&7)))idx|=BYTE(1u<<pl);
                const BYTE* pal=b.data()+16+idx*3;rr=pal[0];gg=pal[1];bb=pal[2];
            }else return MakeResultBase(req);
            PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*w+x)*4],rr,gg,bb,255);
        }
    }
    r.hr=S_OK;
    return r;
}

static BYTE ToneMapHdr(float linear) {
    if(!(linear>0.0f))return 0;
    const float mapped=linear/(1.0f+linear);
    const float srgb=std::pow(std::clamp(mapped,0.0f,1.0f),1.0f/2.2f);
    return static_cast<BYTE>(std::clamp<int>(static_cast<int>(std::lround(srgb*255.0f)),0,255));
}

DecodedImage DecodeRadianceHdr(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);
    std::vector<BYTE> b;
    if(!ReadWholeFileBytes(req.path,b)||b.size()<16)return r;
    const BYTE* p=b.data(),*end=b.data()+b.size();
    std::string line;
    if(!ReadAsciiLine(p,end,line)||(line!="#?RADIANCE"&&line!="#?RGBE"))return r;
    bool xyze=false,blank=false;
    while(ReadAsciiLine(p,end,line)){
        if(line.empty()){blank=true;break;}
        if(line.find("FORMAT=32-bit_rle_xyze")!=std::string::npos)xyze=true;
    }
    if(!blank||!ReadAsciiLine(p,end,line))return r;
    std::istringstream rs(line);
    std::string sy,sx;uint32_t h=0,w=0;
    if(!(rs>>sy>>h>>sx>>w)||!w||!h||(sy!="-Y"&&sy!="+Y")||(sx!="+X"&&sx!="-X"))return r;
    if(!PrepareBgra(r,w,h))return MakeResultBase(req);
    std::vector<BYTE> scan(static_cast<size_t>(w)*4);
    for(uint32_t fy=0;fy<h;++fy){
        if(p+4>end)return MakeResultBase(req);
        BYTE first[4]{p[0],p[1],p[2],p[3]};p+=4;
        const bool newRle=w>=8&&w<=32767&&first[0]==2&&first[1]==2&&(first[2]&0x80)==0&&((uint32_t(first[2])<<8)|first[3])==w;
        if(newRle){
            for(int c=0;c<4;++c){
                size_t x=0;
                while(x<w){
                    if(p>=end)return MakeResultBase(req);
                    const BYTE code=*p++;
                    if(code>128){
                        const size_t run=code-128;
                        if(!run||p>=end||x+run>w)return MakeResultBase(req);
                        const BYTE v=*p++;
                        for(size_t i=0;i<run;++i)scan[(x+i)*4+c]=v;
                        x+=run;
                    }else{
                        const size_t run=code;
                        if(!run||p+run>end||x+run>w)return MakeResultBase(req);
                        for(size_t i=0;i<run;++i)scan[(x+i)*4+c]=*p++;
                        x+=run;
                    }
                }
            }
        }else{
            memcpy(scan.data(),first,4);
            const size_t rest=(static_cast<size_t>(w)-1u)*4u;
            if(p+rest>end)return MakeResultBase(req);
            memcpy(scan.data()+4,p,rest);p+=rest;
        }
        const uint32_t oy=sy=="-Y"?fy:(h-1-fy);
        for(uint32_t fx=0;fx<w;++fx){
            const BYTE* s=&scan[static_cast<size_t>(fx)*4];
            float a=0,bv=0,c=0;
            if(s[3]){
                const float f=std::ldexp(1.0f,static_cast<int>(s[3])-(128+8));
                a=s[0]*f;bv=s[1]*f;c=s[2]*f;
            }
            float lr=a,lg=bv,lb=c;
            if(xyze){
                lr=3.2406f*a-1.5372f*bv-0.4986f*c;
                lg=-0.9689f*a+1.8758f*bv+0.0415f*c;
                lb=0.0557f*a-0.2040f*bv+1.0570f*c;
            }
            const uint32_t ox=sx=="+X"?fx:(w-1-fx);
            PutPremulBgra(&r.pixels[(static_cast<size_t>(oy)*w+ox)*4],ToneMapHdr(lr),ToneMapHdr(lg),ToneMapHdr(lb),255);
        }
    }
    r.hr=S_OK;
    return r;
}

struct Rgba8{BYTE r,g,b,a;};

} // namespace GlideDecode::Extended

// Additional tiny Glide codec layer fallbacks. These formats are simple enough
// that native support costs far less than depending on a general codec suite.
namespace GlideDecode::Extended {

DecodedImage DecodeWbmp(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);std::vector<BYTE>b;
    if(!ReadWholeFileBytes(req.path,b)||b.size()<5)return r;
    size_t p=0;if(b[p++]!=0||b[p++]!=0)return r; // WBMP type 0 + fixed header
    auto readMb=[&](uint32_t&v)->bool{v=0;int n=0;for(;p<b.size()&&n<5;++n){BYTE c=b[p++];v=(v<<7)|(c&0x7f);if(!(c&0x80))return true;}return false;};
    uint32_t w=0,h=0;if(!readMb(w)||!readMb(h)||!w||!h||w>100000||h>100000||!PrepareBgra(r,w,h))return MakeResultBase(req);
    const size_t row=(static_cast<size_t>(w)+7)/8;if(row>SIZE_MAX/h||p+row*h>b.size())return MakeResultBase(req);
    for(uint32_t y=0;y<h;++y)for(uint32_t x=0;x<w;++x){const bool black=(b[p+static_cast<size_t>(y)*row+x/8]&(0x80u>>(x&7)))!=0;const BYTE v=black?0:255;PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*w+x)*4],v,v,v,255);}r.hr=S_OK;return r;
}

DecodedImage DecodePfm(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);std::vector<BYTE>b;
    if(!ReadWholeFileBytes(req.path,b)||b.size()<16)return r;const BYTE*p=b.data(),*end=b.data()+b.size();std::string magic,line;
    if(!ReadAsciiLine(p,end,magic)||(magic!="PF"&&magic!="Pf"))return r;
    do{if(!ReadAsciiLine(p,end,line))return r;}while(!line.empty()&&line[0]=='#');std::istringstream dims(line);uint32_t w=0,h=0;if(!(dims>>w>>h)||!w||!h)return r;
    if(!ReadAsciiLine(p,end,line))return r;char*tail=nullptr;float scale=std::strtof(line.c_str(),&tail);if(!tail||tail==line.c_str()||scale==0)return r;const bool little=scale<0;const int channels=magic=="PF"?3:1;
    const uint64_t samples=uint64_t(w)*h*channels;if(samples>SIZE_MAX/sizeof(float)||uint64_t(end-p)<samples*sizeof(float)||!PrepareBgra(r,w,h))return MakeResultBase(req);
    auto sample=[&](const BYTE*q)->float{uint32_t u;if(little)u=uint32_t(q[0])|(uint32_t(q[1])<<8)|(uint32_t(q[2])<<16)|(uint32_t(q[3])<<24);else u=(uint32_t(q[0])<<24)|(uint32_t(q[1])<<16)|(uint32_t(q[2])<<8)|q[3];float f;memcpy(&f,&u,4);return std::isfinite(f)?f:0.0f;};
    auto to8=[](float v)->BYTE{v=std::clamp(v,0.0f,1.0f);v=std::pow(v,1.0f/2.2f);return static_cast<BYTE>(std::lround(v*255.0f));};
    for(uint32_t fy=0;fy<h;++fy){const uint32_t y=h-1-fy;for(uint32_t x=0;x<w;++x){const BYTE*q=p+(uint64_t(fy)*w+x)*channels*4;float rr=sample(q),gg=channels==3?sample(q+4):rr,bb=channels==3?sample(q+8):rr;PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*w+x)*4],to8(rr),to8(gg),to8(bb),255);}}r.hr=S_OK;return r;
}

DecodedImage DecodeXbm(const DecodeRequest& req) {
    DecodedImage r=MakeResultBase(req);std::vector<BYTE>b;if(!ReadWholeFileBytes(req.path,b)||b.empty())return r;
    std::string t(reinterpret_cast<const char*>(b.data()),b.size());
    auto defineValue=[&](const char*suf,uint32_t&v)->bool{size_t pos=t.find(suf);if(pos==std::string::npos)return false;pos=t.find_first_of("0123456789",pos);if(pos==std::string::npos)return false;char*e=nullptr;unsigned long q=std::strtoul(t.c_str()+pos,&e,10);if(!e||e==t.c_str()+pos||q==0||q>100000)return false;v=static_cast<uint32_t>(q);return true;};
    uint32_t w=0,h=0;if(!defineValue("_width",w)||!defineValue("_height",h)||!PrepareBgra(r,w,h))return MakeResultBase(req);
    size_t pos=t.find('{');if(pos==std::string::npos)return MakeResultBase(req);++pos;const size_t row=(static_cast<size_t>(w)+7)/8,need=row*h;std::vector<BYTE>bits;bits.reserve(need);
    while(pos<t.size()&&bits.size()<need){pos=t.find("0x",pos);if(pos==std::string::npos)break;char*e=nullptr;unsigned long v=std::strtoul(t.c_str()+pos,&e,16);if(!e||e==t.c_str()+pos)break;bits.push_back(static_cast<BYTE>(v&0xff));pos=static_cast<size_t>(e-t.c_str());}
    if(bits.size()<need)return MakeResultBase(req);
    for(uint32_t y=0;y<h;++y)for(uint32_t x=0;x<w;++x){const bool black=(bits[static_cast<size_t>(y)*row+x/8]&(1u<<(x&7)))!=0;const BYTE v=black?0:255;PutPremulBgra(&r.pixels[(static_cast<size_t>(y)*w+x)*4],v,v,v,255);}r.hr=S_OK;return r;
}

} // namespace GlideDecode::Extended
