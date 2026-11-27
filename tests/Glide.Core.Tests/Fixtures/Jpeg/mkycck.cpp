// mkycck <in.raw (w*h*4 stored-sense CMYK)> <w> <h> <out.jpg> <subsamp 0=444 1=422 2=420> <ycck 1|0> <progressive 1|0>
#include <cstdio>
#include <cstdlib>
#include <vector>
#include "turbojpeg.h"
int main(int argc, char** argv)
{
    FILE* f = fopen(argv[1], "rb");
    int w = atoi(argv[2]), h = atoi(argv[3]);
    std::vector<unsigned char> px((size_t)w * h * 4);
    fread(px.data(), 1, px.size(), f); fclose(f);
    tjhandle t = tj3Init(TJINIT_COMPRESS);
    tj3Set(t, TJPARAM_SUBSAMP, atoi(argv[5]) == 0 ? TJSAMP_444 : atoi(argv[5]) == 1 ? TJSAMP_422 : TJSAMP_420);
    tj3Set(t, TJPARAM_QUALITY, 90);
    if (atoi(argv[6])) tj3Set(t, TJPARAM_COLORSPACE, TJCS_YCCK);
    if (atoi(argv[7])) tj3Set(t, TJPARAM_PROGRESSIVE, 1);
    unsigned char* out = nullptr; size_t n = 0;
    int r = tj3Compress8(t, px.data(), w, 0, h, TJPF_CMYK, &out, &n);
    if (r) { fprintf(stderr, "compress failed: %s\n", tj3GetErrorStr(t)); return 1; }
    FILE* o = fopen(argv[4], "wb"); fwrite(out, 1, n, o); fclose(o);
    printf("wrote %zu bytes\n", n);
    return 0;
}
