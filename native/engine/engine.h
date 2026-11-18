// Cutroom native engine: FFmpeg decode + Direct3D 11 compositor.
// One renderer draws both preview frames (into window swapchains) and export frames (read back to memory),
// so preview and export always match.
#pragma once
#include <stdint.h>

#ifdef CUTROOM_ENGINE_EXPORTS
#define CR_API extern "C" __declspec(dllexport)
#else
#define CR_API extern "C" __declspec(dllimport)
#endif

enum CrLayerKind : int32_t
{
    CR_LAYER_VIDEO = 0,    // a decoded frame of an opened media file
    CR_LAYER_TEXTURE = 1,  // an uploaded BGRA bitmap (text, image, shape)
    CR_LAYER_COLOR = 2,    // a solid (optionally rounded, soft-edged) rectangle
    CR_LAYER_PAPER = 3,    // procedural animated notebook page
    CR_LAYER_BLUR = 4,     // blurs and dims everything drawn so far (blur cards)
};

#pragma pack(push, 8)
struct CrLayer
{
    int32_t kind;
    int32_t media;          // CR_LAYER_VIDEO
    double sourceSeconds;   // CR_LAYER_VIDEO
    int32_t texture;        // CR_LAYER_TEXTURE
    int32_t fit;            // 0 stretch to dest, 1 fit inside dest keeping aspect, 2 fill dest keeping aspect (crop)
    float color[4];         // straight RGBA for CR_LAYER_COLOR; tint for paper
    float cx, cy, w, h;     // destination center and size as fractions of the canvas
    float rotation;         // radians
    float opacity;
    float u0, v0, u1, v1;   // source crop in 0..1
    float cornerRadius;     // canvas pixels
    float softness;         // edge feather in canvas pixels (shadows)
    float blur;             // 0..1 (CR_LAYER_BLUR strength)
    float dim;              // 0..1 (CR_LAYER_BLUR darkening)
    float time;             // seconds, for animated shaders
    int32_t wipeDir;        // 0 none, 1 left, 2 right, 3 up, 4 down
    float wipe;             // 0..1 reveal progress for wipes
    float pad;
};

struct CrMediaInfo
{
    int32_t width, height;
    double fps;
    double duration;
    int32_t hasVideo, hasAudio;
};
#pragma pack(pop)

CR_API void* cr_create(int32_t width, int32_t height, int32_t useWarp);
CR_API void cr_destroy(void* engine);
CR_API const char* cr_last_error();
CR_API const char* cr_adapter(void* engine);

CR_API int32_t cr_open(void* engine, const wchar_t* path, int32_t maxHeight);
CR_API int32_t cr_media_info(void* engine, int32_t media, CrMediaInfo* info);
CR_API void cr_close(void* engine, int32_t media);

CR_API int32_t cr_upload(void* engine, int32_t id, int32_t width, int32_t height, const void* bgra, int32_t stride);
CR_API void cr_release_texture(void* engine, int32_t id);

CR_API void cr_begin(void* engine, float r, float g, float b);
CR_API int32_t cr_draw(void* engine, const CrLayer* layer);

CR_API int32_t cr_attach(void* engine, void* hwnd, int32_t width, int32_t height);
CR_API int32_t cr_resize(void* engine, int32_t swap, int32_t width, int32_t height);
CR_API void cr_detach(void* engine, int32_t swap);
CR_API int32_t cr_present(void* engine, int32_t swap);
CR_API int32_t cr_readback(void* engine, void* bgra, int32_t stride);
