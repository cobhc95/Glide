#pragma once

namespace GlideFormats {

// Comprehensive Glide image-format registry.
//
// Decode policy:
//   1) WIC first for codecs supplied by Windows/vendor extensions.
//   2) Glide compact native decoders for selected lightweight raster formats.
//   3) Windows Shell thumbnail/preview fallback for formats whose installed
//      application provides a shell handler (Affinity/Krita/XCF/RAW/SVG/PDF/etc.).
// This keeps Glide Core small while making the open/navigation boundary broad.
inline constexpr const wchar_t* kImageExtensions[] = {
    // JPEG family
    L".jpg", L".jpeg", L".jpe", L".jfif", L".jif", L".jfi",
    L".pjpeg", L".pjpg",
    // PNG / bitmap / TIFF / GIF / icons
    L".png", L".apng", L".mng", L".jng", L".bmp", L".dib",
    L".rle", L".wbmp", L".tif", L".tiff", L".btf", L".gif",
    L".ico", L".cur", L".ani", L".icns",
    // Modern web / vector / phone
    L".webp", L".heic", L".heif", L".heics", L".heifs", L".hif",
    L".avif", L".avifs", L".svg", L".svgz",
    // JPEG XR / JPEG 2000 / JPEG XL
    L".jxr", L".wdp", L".hdp", L".jp2", L".j2k", L".j2c",
    L".jpc", L".jpx", L".jpf", L".jpm", L".mj2", L".jxl",
    // Professional / editor / interchange
    L".psd", L".psb", L".pdd", L".xcf", L".ora", L".kra",
    L".afphoto", L".afdesign", L".afpub", L".clip", L".csp", L".pdn",
    L".psp", L".pspimage", L".pxr", L".tga", L".targa", L".icb",
    L".vda", L".vst", L".dpx", L".cin", L".sgi", L".rgb",
    L".rgba", L".bw", L".ras", L".sun", L".iff", L".lbm",
    L".ilbm", L".img", L".pic", L".pict", L".pct", L".pict2",
    L".eps", L".epsf", L".ai", L".pdf",
    // Portable / scientific / HDR
    L".pnm", L".ppm", L".pgm", L".pbm", L".pam", L".pfm",
    L".pcx", L".qoi", L".hdr", L".rgbe", L".xyze", L".exr",
    L".dds", L".ff", L".fits", L".fit", L".fts", L".fts.gz",
    L".hdr.gz",
    // X11 / legacy
    L".xbm", L".xpm", L".xwd", L".cut", L".mac", L".mpo",
    L".jps", L".pns",
    // Camera RAW
    L".dng", L".cr2", L".cr3", L".crw", L".nef", L".nrw",
    L".arw", L".srf", L".sr2", L".raf", L".orf", L".ori",
    L".rw2", L".rwl", L".pef", L".ptx", L".3fr", L".fff",
    L".iiq", L".cap", L".eip", L".mef", L".mos", L".mrw",
    L".x3f", L".erf", L".kdc", L".dcr", L".k25", L".bay",
    L".srw", L".rwz", L".gpr", L".mdc", L".raw", L".r3d",
    L".ari", L".cinema", L".dcs", L".drf", L".dsc", L".r2d",
    L".rw1",
    // Medical / microscopy / astronomy and specialist
    L".dcm", L".dicom", L".ima", L".nii", L".nii.gz", L".mha",
    L".mhd", L".nrrd", L".ndpi", L".svs", L".vms", L".vmu",
    L".scn", L".mrxs", L".bif", L".czi", L".lif", L".lsm",
    L".ome.tif", L".ome.tiff", L".vsi",
    // Game / texture / sprite containers
    L".ktx", L".ktx2", L".pvr", L".astc", L".basis", L".tex",
    L".vtf", L".wal", L".spr",
    // Design/CAD preview-capable via installed shell providers
    L".cdr", L".cmx", L".cpt", L".emf", L".wmf", L".emz",
    L".wmz", L".dwg", L".dxf", L".skp",
};

inline constexpr wchar_t kImageDialogPattern[] =
    L"*.jpg;*.jpeg;*.jpe;*.jfif;*.jif;*.jfi;*.pjpeg;*.pjpg;*.png;*.apng;*.mng;*.jng;*.bmp;*.dib;*.rle;*.wbmp;"
    L"*.tif;*.tiff;*.btf;*.gif;*.ico;*.cur;*.ani;*.icns;*.webp;*.heic;*.heif;*.heics;*.heifs;*.hif;*.avif;"
    L"*.avifs;*.svg;*.svgz;*.jxr;*.wdp;*.hdp;*.jp2;*.j2k;*.j2c;*.jpc;*.jpx;*.jpf;*.jpm;*.mj2;*.jxl;*.psd;*.psb;"
    L"*.pdd;*.xcf;*.ora;*.kra;*.afphoto;*.afdesign;*.afpub;*.clip;*.csp;*.pdn;*.psp;*.pspimage;*.pxr;*.tga;"
    L"*.targa;*.icb;*.vda;*.vst;*.dpx;*.cin;*.sgi;*.rgb;*.rgba;*.bw;*.ras;*.sun;*.iff;*.lbm;*.ilbm;*.img;*.pic;"
    L"*.pict;*.pct;*.pict2;*.eps;*.epsf;*.ai;*.pdf;*.pnm;*.ppm;*.pgm;*.pbm;*.pam;*.pfm;*.pcx;*.qoi;*.hdr;*.rgbe;"
    L"*.xyze;*.exr;*.dds;*.ff;*.fits;*.fit;*.fts;*.fts.gz;*.hdr.gz;*.xbm;*.xpm;*.xwd;*.cut;*.mac;*.mpo;*.jps;"
    L"*.pns;*.dng;*.cr2;*.cr3;*.crw;*.nef;*.nrw;*.arw;*.srf;*.sr2;*.raf;*.orf;*.ori;*.rw2;*.rwl;*.pef;*.ptx;"
    L"*.3fr;*.fff;*.iiq;*.cap;*.eip;*.mef;*.mos;*.mrw;*.x3f;*.erf;*.kdc;*.dcr;*.k25;*.bay;*.srw;*.rwz;*.gpr;"
    L"*.mdc;*.raw;*.r3d;*.ari;*.cinema;*.dcs;*.drf;*.dsc;*.r2d;*.rw1;*.dcm;*.dicom;*.ima;*.nii;*.nii.gz;*.mha;"
    L"*.mhd;*.nrrd;*.ndpi;*.svs;*.vms;*.vmu;*.scn;*.mrxs;*.bif;*.czi;*.lif;*.lsm;*.ome.tif;*.ome.tiff;*.vsi;"
    L"*.ktx;*.ktx2;*.pvr;*.astc;*.basis;*.tex;*.vtf;*.wal;*.spr;*.cdr;*.cmx;*.cpt;*.emf;*.wmf;*.emz;*.wmz;*.dwg;"
    L"*.dxf;*.skp";

// OPENFILENAME requires a double-NUL-terminated label/pattern list.
inline constexpr wchar_t kImageOpenFileFilter[] =
    L"Images\0"
    L"*.jpg;*.jpeg;*.jpe;*.jfif;*.jif;*.jfi;*.pjpeg;*.pjpg;*.png;*.apng;*.mng;*.jng;*.bmp;*.dib;*.rle;*.wbmp;"
    L"*.tif;*.tiff;*.btf;*.gif;*.ico;*.cur;*.ani;*.icns;*.webp;*.heic;*.heif;*.heics;*.heifs;*.hif;*.avif;"
    L"*.avifs;*.svg;*.svgz;*.jxr;*.wdp;*.hdp;*.jp2;*.j2k;*.j2c;*.jpc;*.jpx;*.jpf;*.jpm;*.mj2;*.jxl;*.psd;*.psb;"
    L"*.pdd;*.xcf;*.ora;*.kra;*.afphoto;*.afdesign;*.afpub;*.clip;*.csp;*.pdn;*.psp;*.pspimage;*.pxr;*.tga;"
    L"*.targa;*.icb;*.vda;*.vst;*.dpx;*.cin;*.sgi;*.rgb;*.rgba;*.bw;*.ras;*.sun;*.iff;*.lbm;*.ilbm;*.img;*.pic;"
    L"*.pict;*.pct;*.pict2;*.eps;*.epsf;*.ai;*.pdf;*.pnm;*.ppm;*.pgm;*.pbm;*.pam;*.pfm;*.pcx;*.qoi;*.hdr;*.rgbe;"
    L"*.xyze;*.exr;*.dds;*.ff;*.fits;*.fit;*.fts;*.fts.gz;*.hdr.gz;*.xbm;*.xpm;*.xwd;*.cut;*.mac;*.mpo;*.jps;"
    L"*.pns;*.dng;*.cr2;*.cr3;*.crw;*.nef;*.nrw;*.arw;*.srf;*.sr2;*.raf;*.orf;*.ori;*.rw2;*.rwl;*.pef;*.ptx;"
    L"*.3fr;*.fff;*.iiq;*.cap;*.eip;*.mef;*.mos;*.mrw;*.x3f;*.erf;*.kdc;*.dcr;*.k25;*.bay;*.srw;*.rwz;*.gpr;"
    L"*.mdc;*.raw;*.r3d;*.ari;*.cinema;*.dcs;*.drf;*.dsc;*.r2d;*.rw1;*.dcm;*.dicom;*.ima;*.nii;*.nii.gz;*.mha;"
    L"*.mhd;*.nrrd;*.ndpi;*.svs;*.vms;*.vmu;*.scn;*.mrxs;*.bif;*.czi;*.lif;*.lsm;*.ome.tif;*.ome.tiff;*.vsi;"
    L"*.ktx;*.ktx2;*.pvr;*.astc;*.basis;*.tex;*.vtf;*.wal;*.spr;*.cdr;*.cmx;*.cpt;*.emf;*.wmf;*.emz;*.wmz;*.dwg;"
    L"*.dxf;*.skp"
    L"\0All files\0*.*\0\0";

} // namespace GlideFormats
