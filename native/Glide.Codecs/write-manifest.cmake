# Writes <name>.glidecodec.json for the freshly linked provider DLL (invoked as a POST_BUILD step).
# Inputs: DLL, OUT, ID, VERSION, LICENSE, EXTENSIONS (;-list). The field set mirrors
# CodecProviderInventory.TryReadManifestMetadata and tools/generate-codec-index.ps1.
file(SHA256 "${DLL}" _hash)
set(_exts "")
foreach(_ext IN LISTS EXTENSIONS)
  if(_exts)
    string(APPEND _exts ", ")
  endif()
  string(APPEND _exts "\"${_ext}\"")
endforeach()
# structSize = sizeof(glide_codec_abi::ProviderInfo) on x64 (2*u32 + 4 pointers + 2*u32).
file(WRITE "${OUT}" "{
  \"id\": \"${ID}\",
  \"version\": \"${VERSION}\",
  \"license\": \"${LICENSE}\",
  \"sha256\": \"${_hash}\",
  \"abiVersion\": 2,
  \"structSize\": 48,
  \"extensions\": [${_exts}],
  \"preview\": true,
  \"full\": true,
  \"metadata\": false,
  \"frames\": false
}
")
