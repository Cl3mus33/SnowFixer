#include <d3d11.h>
#include <DirectXTex.h>

#include <wrl/client.h>

#include <string>

using namespace DirectX;
using Microsoft::WRL::ComPtr;

namespace {
// Same lazy-cached shared device pattern as AutoBlend's own AutoBlendTexTools - DirectXTex's
// DirectCompute-based GPU compressor (Compress(ID3D11Device*, ...)) does a 4K BC7 encode in a
// fraction of the CPU encoder's time. Falls back to the CPU path automatically if no device is
// available or GPU compression fails for any reason.
auto getSharedD3D11Device() -> ID3D11Device*
{
    static const ComPtr<ID3D11Device> device = [] {
        ComPtr<ID3D11Device> result;
        const D3D_DRIVER_TYPE driverTypes[] = { D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP };
        for (const auto driverType : driverTypes) {
            if (SUCCEEDED(D3D11CreateDevice(
                    nullptr, driverType, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &result, nullptr, nullptr))) {
                return result;
            }
        }
        return ComPtr<ID3D11Device> {};
    }();
    return device.Get();
}

// Loads either a DDS file or a plain image (PNG/etc, via WIC) into a single uncompressed RGBA8
// image - a hand-painted alpha mask is authored/exported as a PNG, while a texture pulled straight
// from the load order is always DDS. Decompresses block-compressed DDS formats (BC1/BC3/BC7/...)
// since their alpha bits are packed per 4x4 block alongside color data, not editable/readable
// per-pixel directly. outWasWic reports whether the source went through WIC (a plain image) rather
// than DDS - see compositeAlphaDiffuse's own comment for why the caller needs to know this for the
// alpha source specifically.
auto loadAsRgba8(const wchar_t* path, ScratchImage& out, bool& outWasWic) -> HRESULT
{
    const std::wstring pathStr(path);
    const auto dot = pathStr.find_last_of(L'.');
    const bool isDds = dot != std::wstring::npos && _wcsicmp(pathStr.c_str() + dot, L".dds") == 0;
    outWasWic = !isDds;

    ScratchImage loaded;
    TexMetadata metadata {};
    if (isDds) {
        if (FAILED(LoadFromDDSFile(path, DDS_FLAGS_NONE, &metadata, loaded))) {
            return E_FAIL;
        }
    } else {
        if (FAILED(LoadFromWICFile(path, WIC_FLAGS_NONE, &metadata, loaded))) {
            return E_FAIL;
        }
    }

    if (!IsCompressed(metadata.format) && metadata.format == DXGI_FORMAT_R8G8B8A8_UNORM) {
        out = std::move(loaded);
        return S_OK;
    }

    if (IsCompressed(metadata.format)) {
        return Decompress(loaded.GetImages(), loaded.GetImageCount(), metadata, DXGI_FORMAT_R8G8B8A8_UNORM, out);
    }

    return Convert(loaded.GetImages(), loaded.GetImageCount(), metadata, DXGI_FORMAT_R8G8B8A8_UNORM,
        TEX_FILTER_DEFAULT, TEX_THRESHOLD_DEFAULT, out);
}

// Composites a new diffuse: RGB taken from colorSourcePath, Alpha taken from alphaSourcePath -
// resized to colorSourcePath's own dimensions first if they differ, so an alpha mask authored at a
// different resolution than whichever texture resolution (1K/2K/4K) actually happens to be
// installed still lines up correctly. Recompressed to BC1 sRGB (isPbr) or BC7 (otherwise), same
// convention as AutoBlend's own stripAlphaToOpaque, for the same reason: Skyrim's PBR pipeline
// reads a PBR diffuse as BC1 sRGB specifically, not BC7.
auto compositeAlphaDiffuse(const wchar_t* colorSourcePath, const wchar_t* alphaSourcePath, const wchar_t* dstPath, bool isPbr) -> int
{
    ScratchImage colorImage;
    bool colorWasWic = false;
    if (FAILED(loadAsRgba8(colorSourcePath, colorImage, colorWasWic))) {
        return 1;
    }

    ScratchImage alphaImage;
    bool alphaWasWic = false;
    if (FAILED(loadAsRgba8(alphaSourcePath, alphaImage, alphaWasWic))) {
        return 2;
    }

    const auto& colorMeta = colorImage.GetMetadata();
    const auto& alphaMeta = alphaImage.GetMetadata();

    if (alphaMeta.width != colorMeta.width || alphaMeta.height != colorMeta.height) {
        ScratchImage resizedAlpha;
        if (FAILED(Resize(alphaImage.GetImages(), alphaImage.GetImageCount(), alphaMeta,
                colorMeta.width, colorMeta.height, TEX_FILTER_DEFAULT, resizedAlpha))) {
            return 3;
        }
        alphaImage = std::move(resizedAlpha);
    }

    ScratchImage composite;
    if (FAILED(composite.Initialize2D(DXGI_FORMAT_R8G8B8A8_UNORM, colorMeta.width, colorMeta.height, 1, 1))) {
        return 4;
    }

    const Image* colorImg = colorImage.GetImages();
    const Image* alphaImg = alphaImage.GetImages();
    const Image* dstImg = composite.GetImages();

    for (size_t y = 0; y < colorMeta.height; ++y) {
        const auto* colorRow = reinterpret_cast<const uint32_t*>(colorImg->pixels + y * colorImg->rowPitch);
        const auto* alphaRow = reinterpret_cast<const uint32_t*>(alphaImg->pixels + y * alphaImg->rowPitch);
        auto* dstRow = reinterpret_cast<uint32_t*>(dstImg->pixels + y * dstImg->rowPitch);
        for (size_t x = 0; x < colorMeta.width; ++x) {
            const uint32_t colorPixel = colorRow[x];
            const uint32_t alphaPixel = alphaRow[x];
            // R8G8B8A8 byte layout (little-endian): byte 0 = R, byte 3 = A. A real DDS texture's
            // own alpha channel (byte 3) carries the mask value there. A plain WIC-loaded grayscale
            // PNG has no alpha channel at all - WIC/DirectXTex's conversion to RGBA8 always sets
            // alpha=255 (opaque) and puts the grayscale luminance in R=G=B instead, so the mask
            // value for that case has to be read from byte 0, not byte 3. Confirmed directly: using
            // byte 3 unconditionally produced a fully-opaque composite with a real hand-painted
            // grayscale mask, since its "alpha" was uniformly 255.
            const uint32_t alphaByte = alphaWasWic ? (alphaPixel & 0xFF) : ((alphaPixel >> 24) & 0xFF);
            dstRow[x] = (colorPixel & 0x00FFFFFF) | (alphaByte << 24);
        }
    }

    // Full mip chain down to 1x1 (levels=0), matching the source textures' own - a single-level
    // output (what composite alone would produce) is missing every mip below the top, which caused
    // a real, visible size mismatch against the original DDS's own file size the first time this
    // ran without this step.
    ScratchImage mipChain;
    if (FAILED(GenerateMipMaps(composite.GetImages(), composite.GetImageCount(), composite.GetMetadata(),
            TEX_FILTER_DEFAULT, 0, mipChain))) {
        return 7;
    }

    const DXGI_FORMAT targetFormat = isPbr ? DXGI_FORMAT_BC1_UNORM_SRGB : DXGI_FORMAT_BC7_UNORM;

    ScratchImage recompressed;
    HRESULT compressHr = E_FAIL;
    if (auto* device = getSharedD3D11Device(); device != nullptr) {
        compressHr = Compress(device, mipChain.GetImages(), mipChain.GetImageCount(), mipChain.GetMetadata(),
            targetFormat, TEX_COMPRESS_DEFAULT, 1.0f, recompressed);
    }
    if (FAILED(compressHr)) {
        const auto compressFlags = static_cast<TEX_COMPRESS_FLAGS>(TEX_COMPRESS_PARALLEL | TEX_COMPRESS_BC7_QUICK);
        compressHr = Compress(mipChain.GetImages(), mipChain.GetImageCount(), mipChain.GetMetadata(),
            targetFormat, compressFlags, TEX_THRESHOLD_DEFAULT, recompressed);
    }
    if (FAILED(compressHr)) {
        return 5;
    }

    if (FAILED(SaveToDDSFile(
            recompressed.GetImages(), recompressed.GetImageCount(), recompressed.GetMetadata(), DDS_FLAGS_NONE, dstPath))) {
        return 6;
    }

    return 0;
}
}

/**
 * @brief Writes a new diffuse DDS at dstPath, combining colorSourcePath's own RGB with
 * alphaSourcePath's own Alpha channel - see compositeAlphaDiffuse's own comment for the exact
 * mechanics. alphaSourcePath may be a DDS or any WIC-readable image (PNG, ...); resized to
 * colorSourcePath's resolution first if they differ. Recompressed to BC1 sRGB when isPbr is
 * nonzero, or BC7 otherwise. Returns 0 on success, non-zero on failure - never throws across the
 * P/Invoke boundary, matching AutoBlendTexTools' own convention for the same direction.
 *
 * Called in-process via P/Invoke (not a texconv.exe subprocess) for the same MO2/USVFS
 * CreateProcess-hook reason AutoBlend's own equivalent DLL documents.
 */
extern "C" __declspec(dllexport) int __stdcall sf_composite_alpha_diffuse(
    const wchar_t* colorSourcePath, const wchar_t* alphaSourcePath, const wchar_t* dstPath, int isPbr)
{
    if (colorSourcePath == nullptr || alphaSourcePath == nullptr || dstPath == nullptr) {
        return -1;
    }

    try {
        return compositeAlphaDiffuse(colorSourcePath, alphaSourcePath, dstPath, isPbr != 0);
    } catch (...) {
        return -2;
    }
}
