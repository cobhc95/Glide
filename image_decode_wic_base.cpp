// Build-only wrapper for the accepted legacy WIC decoder.
// Keeps image_decode.cpp source unchanged while allowing the complete Glide
// source set to compile in one MSVC /MP parallel pass.
#define DecodeFile DecodeFileWicBase
#include "image_decode.cpp"
