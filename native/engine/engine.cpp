#define CUTROOM_ENGINE_EXPORTS
#define NOMINMAX
#include "engine.h"

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <map>
#include <memory>
#include <string>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/imgutils.h>
#include <libswscale/swscale.h>
}

using Microsoft::WRL::ComPtr;

namespace {

thread_local std::string g_error;

bool Fail(const std::string& message, HRESULT hr = S_OK)
{
    char code[32] = {};
    if (FAILED(hr)) sprintf_s(code, " (0x%08X)", static_cast<unsigned>(hr));
    g_error = message + code;
    return false;
}

const char* kShaders = R"(
cbuffer Params : register(b0)
{
    float2 center; float2 halfSize;
    float2 canvas; float rotation; float opacity;
    float4 uvRect;
    float4 color;
    float radius; float softness; float time; float wipe;
    int wipeDir; float dim; float2 texelStep;
};
Texture2D tex0 : register(t0);
SamplerState samp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float2 local : TEXCOORD1; };

VSOut vs_main(uint id : SV_VertexID)
{
    float2 corner = float2((id & 1) ? 1.0 : -1.0, (id & 2) ? 1.0 : -1.0);
    float2 p = corner * halfSize;
    float s = sin(rotation), c = cos(rotation);
    float2 r = float2(p.x * c - p.y * s, p.x * s + p.y * c) + center;
    VSOut o;
    o.pos = float4(r.x / canvas.x * 2.0 - 1.0, 1.0 - r.y / canvas.y * 2.0, 0, 1);
    float2 t = corner * 0.5 + 0.5;
    o.uv = float2(lerp(uvRect.x, uvRect.z, t.x), lerp(uvRect.y, uvRect.w, t.y));
    o.local = p;
    return o;
}

float shape_mask(float2 local, float2 uvPos)
{
    float m = 1.0;
    if (radius > 0.0 || softness > 0.0)
    {
        float2 q = abs(local) - halfSize + radius;
        float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        m = softness > 0.0 ? 1.0 - smoothstep(-softness, softness, d) : 1.0 - smoothstep(-0.75, 0.25, d);
    }
    if (wipeDir == 1) m *= uvPos.x >= 1.0 - wipe ? 1.0 : 0.0;
    if (wipeDir == 2) m *= uvPos.x <= wipe ? 1.0 : 0.0;
    if (wipeDir == 3) m *= uvPos.y >= 1.0 - wipe ? 1.0 : 0.0;
    if (wipeDir == 4) m *= uvPos.y <= wipe ? 1.0 : 0.0;
    return m;
}

float4 ps_texture(VSOut i) : SV_Target
{
    float4 c = tex0.Sample(samp, i.uv);
    float2 pos = i.local / (2.0 * halfSize) + 0.5;
    return c * opacity * shape_mask(i.local, pos);
}

float4 ps_color(VSOut i) : SV_Target
{
    float2 pos = i.local / (2.0 * halfSize) + 0.5;
    float a = color.a * opacity * shape_mask(i.local, pos);
    return float4(color.rgb * a, a);
}

float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash(i), hash(i + float2(1, 0)), u.x), lerp(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0, a = 0.5; for (int k = 0; k < 5; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }

// A slightly animated crinkled notebook page: warm paper, blue rules, red margin.
float4 ps_paper(VSOut i) : SV_Target
{
    float2 px = (i.local + halfSize);
    float2 uv = px / canvas.y;
    float wobble = 0.6 * sin(time * 0.7);
    float2 warp = float2(fbm(uv * 3.0 + time * 0.03), fbm(uv * 3.0 + 7.3 - time * 0.02)) - 0.5;
    float crinkle = fbm(uv * 9.0 + warp * (1.5 + wobble * 0.2));
    float3 paper = float3(0.965, 0.945, 0.9) * (0.9 + 0.12 * crinkle) + (color.rgb - 0.5) * 0.08;
    float shade = smoothstep(0.35, 0.75, fbm(uv * 2.0 + warp));
    paper *= 0.94 + 0.08 * shade;
    float lineY = frac((px.y + warp.y * 6.0) / (canvas.y / 18.0));
    float rule = 1.0 - smoothstep(0.0, 0.035, abs(lineY - 0.5) - 0.012);
    paper = lerp(paper, float3(0.55, 0.7, 0.9), rule * 0.45);
    float margin = 1.0 - smoothstep(0.0, 1.5, abs(px.x + warp.x * 4.0 - canvas.x * 0.12) - 1.2);
    paper = lerp(paper, float3(0.85, 0.35, 0.35), margin * 0.6);
    float vignette = 1.0 - 0.35 * pow(length((px / canvas) - 0.5) * 1.3, 2.0);
    paper *= vignette;
    return float4(paper * opacity, opacity);
}

static const float weights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };
float4 ps_blur(VSOut i) : SV_Target
{
    float4 c = tex0.Sample(samp, i.uv) * weights[0];
    for (int k = 1; k < 5; k++)
    {
        c += tex0.Sample(samp, i.uv + texelStep * k) * weights[k];
        c += tex0.Sample(samp, i.uv - texelStep * k) * weights[k];
    }
    return c;
}

float4 ps_dim(VSOut i) : SV_Target
{
    float4 c = tex0.Sample(samp, i.uv);
    c.rgb *= (1.0 - dim);
    c.a = 1.0;
    return c * opacity;
}
)";

struct Constants
{
    float center[2]; float halfSize[2];
    float canvas[2]; float rotation; float opacity;
    float uvRect[4];
    float color[4];
    float radius; float softness; float time; float wipe;
    int32_t wipeDir; float dim; float texelStep[2];
};
static_assert(sizeof(Constants) % 16 == 0, "constant buffer must be 16-byte aligned");

struct Target
{
    ComPtr<ID3D11Texture2D> tex;
    ComPtr<ID3D11RenderTargetView> rtv;
    ComPtr<ID3D11ShaderResourceView> srv;
    int w = 0, h = 0;
};

struct Texture
{
    ComPtr<ID3D11Texture2D> tex;
    ComPtr<ID3D11ShaderResourceView> srv;
    int w = 0, h = 0;
};

struct Media
{
    AVFormatContext* fmt = nullptr;
    AVCodecContext* dec = nullptr;
    AVFrame* cur = nullptr;
    AVFrame* next = nullptr;
    AVPacket* pkt = nullptr;
    SwsContext* sws = nullptr;
    int stream = -1;
    int outW = 0, outH = 0;
    double tb = 0, start = 0, fps = 30, duration = 0;
    bool hasCur = false, hasNext = false, eof = false, converted = false, hasAudio = false;
    double curPts = -1;
    Texture texture;
    std::vector<uint8_t> pixels;

    ~Media()
    {
        if (sws) sws_freeContext(sws);
        if (pkt) av_packet_free(&pkt);
        if (cur) av_frame_free(&cur);
        if (next) av_frame_free(&next);
        if (dec) avcodec_free_context(&dec);
        if (fmt) avformat_close_input(&fmt);
    }

    double PtsOf(const AVFrame* f) const
    {
        int64_t ts = f->best_effort_timestamp != AV_NOPTS_VALUE ? f->best_effort_timestamp : f->pts;
        return ts == AV_NOPTS_VALUE ? 0 : ts * tb - start;
    }

    // Decodes the next frame into `next`. Returns false at end of stream.
    bool DecodeNext()
    {
        while (true)
        {
            int r = avcodec_receive_frame(dec, next);
            if (r == 0) { hasNext = true; return true; }
            if (r == AVERROR_EOF) { eof = true; hasNext = false; return false; }
            if (r != AVERROR(EAGAIN)) { hasNext = false; return false; }
            r = av_read_frame(fmt, pkt);
            if (r < 0)
            {
                avcodec_send_packet(dec, nullptr);
                continue;
            }
            if (pkt->stream_index == stream) avcodec_send_packet(dec, pkt);
            av_packet_unref(pkt);
        }
    }

    void Seek(double t)
    {
        AVStream* s = fmt->streams[stream];
        int64_t ts = static_cast<int64_t>((std::max(0.0, t) + start) / tb);
        av_seek_frame(fmt, stream, ts, AVSEEK_FLAG_BACKWARD);
        avcodec_flush_buffers(dec);
        (void)s;
        hasCur = hasNext = eof = false;
        curPts = -1;
    }

    // Positions `cur` on the latest frame with pts <= t. Returns true when `cur` changed.
    bool Advance(double t)
    {
        const double eps = 0.25 / std::max(1.0, fps);
        if (hasCur && (t < curPts - eps || t > curPts + 2.5)) Seek(t);
        else if (!hasCur && !hasNext) Seek(t);
        bool changed = false;
        if (!hasNext && !eof) DecodeNext();
        while (hasNext && (PtsOf(next) <= t + eps || !hasCur))
        {
            av_frame_unref(cur);
            av_frame_move_ref(cur, next);
            hasCur = true;
            curPts = PtsOf(cur);
            changed = true;
            hasNext = false;
            if (!DecodeNext()) break;
        }
        return changed;
    }
};

struct Swap
{
    ComPtr<IDXGISwapChain1> chain;
    ComPtr<ID3D11RenderTargetView> rtv;
    int w = 0, h = 0;
};

struct Engine
{
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> ctx;
    ComPtr<IDXGIFactory2> factory;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> psTexture, psColor, psPaper, psBlur, psDim;
    ComPtr<ID3D11Buffer> cbuffer;
    ComPtr<ID3D11SamplerState> linear;
    ComPtr<ID3D11BlendState> premultiplied, opaque;
    ComPtr<ID3D11RasterizerState> raster;
    ComPtr<ID3D11Texture2D> staging;
    Target canvas, copy, half, quarterA, quarterB;
    std::map<int, std::unique_ptr<Media>> media;
    std::map<int, Texture> textures;
    std::map<int, Swap> swaps;
    int nextMedia = 1, nextSwap = 1;
    int width = 0, height = 0;
    std::string adapter;

    bool MakeTarget(Target& t, int w, int h)
    {
        D3D11_TEXTURE2D_DESC d = {};
        d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
        d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
        d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        HRESULT hr = device->CreateTexture2D(&d, nullptr, &t.tex);
        if (FAILED(hr)) return Fail("Could not create a render target", hr);
        device->CreateRenderTargetView(t.tex.Get(), nullptr, &t.rtv);
        device->CreateShaderResourceView(t.tex.Get(), nullptr, &t.srv);
        t.w = w; t.h = h;
        return true;
    }

    bool Init(int w, int h, bool warp)
    {
        width = w; height = h;
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1 };
        HRESULT hr = E_FAIL;
        if (!warp)
            hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels, 3, D3D11_SDK_VERSION, &device, nullptr, &ctx);
        if (FAILED(hr))
            hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags, levels, 3, D3D11_SDK_VERSION, &device, nullptr, &ctx);
        if (FAILED(hr)) return Fail("Direct3D 11 is not available", hr);

        ComPtr<IDXGIDevice> dxgi;
        device.As(&dxgi);
        ComPtr<IDXGIAdapter> a;
        dxgi->GetAdapter(&a);
        DXGI_ADAPTER_DESC desc = {};
        a->GetDesc(&desc);
        char name[256] = {};
        WideCharToMultiByte(CP_UTF8, 0, desc.Description, -1, name, sizeof(name), nullptr, nullptr);
        adapter = name;
        a->GetParent(IID_PPV_ARGS(&factory));

        ComPtr<ID3DBlob> code, errors;
        auto compile = [&](const char* entry, const char* target, ComPtr<ID3DBlob>& out) {
            HRESULT r = D3DCompile(kShaders, strlen(kShaders), "cutroom", nullptr, nullptr, entry, target, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &out, &errors);
            if (FAILED(r)) return Fail(std::string("Shader compile failed: ") + (errors ? static_cast<const char*>(errors->GetBufferPointer()) : entry), r);
            return true;
        };
        if (!compile("vs_main", "vs_5_0", code)) return false;
        device->CreateVertexShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &vs);
        struct { const char* entry; ComPtr<ID3D11PixelShader>* ps; } pixel[] = {
            { "ps_texture", &psTexture }, { "ps_color", &psColor }, { "ps_paper", &psPaper }, { "ps_blur", &psBlur }, { "ps_dim", &psDim } };
        for (auto& p : pixel)
        {
            ComPtr<ID3DBlob> b;
            if (!compile(p.entry, "ps_5_0", b)) return false;
            device->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, p.ps->GetAddressOf());
        }

        D3D11_BUFFER_DESC cb = {};
        cb.ByteWidth = sizeof(Constants); cb.Usage = D3D11_USAGE_DEFAULT; cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        device->CreateBuffer(&cb, nullptr, &cbuffer);

        D3D11_SAMPLER_DESC sd = {};
        sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        device->CreateSamplerState(&sd, &linear);

        D3D11_BLEND_DESC bd = {};
        bd.RenderTarget[0].BlendEnable = TRUE;
        bd.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
        bd.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
        bd.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
        bd.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
        bd.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
        bd.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
        bd.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        device->CreateBlendState(&bd, &premultiplied);
        bd.RenderTarget[0].BlendEnable = FALSE;
        device->CreateBlendState(&bd, &opaque);

        D3D11_RASTERIZER_DESC rd = {};
        rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE;
        device->CreateRasterizerState(&rd, &raster);

        if (!MakeTarget(canvas, w, h) || !MakeTarget(copy, w, h) || !MakeTarget(half, w / 2, h / 2) ||
            !MakeTarget(quarterA, w / 4, h / 4) || !MakeTarget(quarterB, w / 4, h / 4)) return false;

        D3D11_TEXTURE2D_DESC sdesc = {};
        sdesc.Width = w; sdesc.Height = h; sdesc.MipLevels = 1; sdesc.ArraySize = 1;
        sdesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; sdesc.SampleDesc.Count = 1;
        sdesc.Usage = D3D11_USAGE_STAGING; sdesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        hr = device->CreateTexture2D(&sdesc, nullptr, &staging);
        if (FAILED(hr)) return Fail("Could not create the readback buffer", hr);
        return true;
    }

    void Bind(ID3D11RenderTargetView* rtv, int w, int h)
    {
        ctx->OMSetRenderTargets(1, &rtv, nullptr);
        D3D11_VIEWPORT vp = { 0, 0, static_cast<float>(w), static_cast<float>(h), 0, 1 };
        ctx->RSSetViewports(1, &vp);
        ctx->RSSetState(raster.Get());
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        ctx->IASetInputLayout(nullptr);
        ctx->VSSetShader(vs.Get(), nullptr, 0);
        ctx->VSSetConstantBuffers(0, 1, cbuffer.GetAddressOf());
        ctx->PSSetConstantBuffers(0, 1, cbuffer.GetAddressOf());
        ctx->PSSetSamplers(0, 1, linear.GetAddressOf());
    }

    void Quad(const Constants& c, ID3D11PixelShader* ps, ID3D11ShaderResourceView* srv, bool blend)
    {
        ctx->UpdateSubresource(cbuffer.Get(), 0, nullptr, &c, 0, 0);
        ctx->PSSetShader(ps, nullptr, 0);
        ID3D11ShaderResourceView* views[1] = { srv };
        ctx->PSSetShaderResources(0, 1, views);
        float factor[4] = {};
        ctx->OMSetBlendState(blend ? premultiplied.Get() : opaque.Get(), factor, 0xffffffff);
        ctx->Draw(4, 0);
        ID3D11ShaderResourceView* none[1] = {};
        ctx->PSSetShaderResources(0, 1, none);
    }

    static Constants FullScreen(int w, int h)
    {
        Constants c = {};
        c.canvas[0] = static_cast<float>(w); c.canvas[1] = static_cast<float>(h);
        c.center[0] = w / 2.f; c.center[1] = h / 2.f;
        c.halfSize[0] = w / 2.f; c.halfSize[1] = h / 2.f;
        c.uvRect[2] = c.uvRect[3] = 1;
        c.opacity = 1;
        return c;
    }

    void BlurCanvas(float strength, float dim, float opacity)
    {
        ctx->CopyResource(copy.tex.Get(), canvas.tex.Get());
        Bind(half.rtv.Get(), half.w, half.h);
        Quad(FullScreen(half.w, half.h), psTexture.Get(), copy.srv.Get(), false);
        Bind(quarterA.rtv.Get(), quarterA.w, quarterA.h);
        Quad(FullScreen(quarterA.w, quarterA.h), psTexture.Get(), half.srv.Get(), false);
        int passes = 1 + static_cast<int>(std::round(std::clamp(strength, 0.f, 1.f) * 5));
        float spread = 0.6f + strength * 1.4f;
        for (int p = 0; p < passes; p++)
        {
            Constants h = FullScreen(quarterB.w, quarterB.h);
            h.texelStep[0] = spread / quarterA.w;
            Bind(quarterB.rtv.Get(), quarterB.w, quarterB.h);
            Quad(h, psBlur.Get(), quarterA.srv.Get(), false);
            Constants v = FullScreen(quarterA.w, quarterA.h);
            v.texelStep[1] = spread / quarterA.h;
            Bind(quarterA.rtv.Get(), quarterA.w, quarterA.h);
            Quad(v, psBlur.Get(), quarterB.srv.Get(), false);
        }
        Bind(canvas.rtv.Get(), width, height);
        Constants c = FullScreen(width, height);
        c.dim = dim;
        c.opacity = opacity;
        Quad(c, psDim.Get(), quarterA.srv.Get(), true);
    }

    bool Draw(const CrLayer& l)
    {
        Bind(canvas.rtv.Get(), width, height);
        if (l.kind == CR_LAYER_BLUR) { BlurCanvas(l.blur, l.dim, l.opacity); return true; }

        Constants c = {};
        c.canvas[0] = static_cast<float>(width); c.canvas[1] = static_cast<float>(height);
        c.center[0] = l.cx * width; c.center[1] = l.cy * height;
        float hw = l.w * width / 2.f, hh = l.h * height / 2.f;
        c.rotation = l.rotation;
        c.opacity = std::clamp(l.opacity, 0.f, 1.f);
        c.uvRect[0] = l.u0; c.uvRect[1] = l.v0; c.uvRect[2] = l.u1; c.uvRect[3] = l.v1;
        if (c.uvRect[2] <= c.uvRect[0]) { c.uvRect[0] = 0; c.uvRect[2] = 1; }
        if (c.uvRect[3] <= c.uvRect[1]) { c.uvRect[1] = 0; c.uvRect[3] = 1; }
        std::copy(l.color, l.color + 4, c.color);
        c.radius = l.cornerRadius; c.softness = l.softness; c.time = l.time;
        c.wipe = l.wipe; c.wipeDir = l.wipeDir;

        ID3D11ShaderResourceView* srv = nullptr;
        int srcW = 0, srcH = 0;
        if (l.kind == CR_LAYER_VIDEO)
        {
            auto it = media.find(l.media);
            if (it == media.end()) return Fail("Unknown media handle");
            Media& m = *it->second;
            if (m.stream < 0) return true;
            if (!Frame(m, l.sourceSeconds)) return false;
            if (!m.hasCur) return true;
            srv = m.texture.srv.Get();
            srcW = m.outW; srcH = m.outH;
        }
        else if (l.kind == CR_LAYER_TEXTURE)
        {
            auto it = textures.find(l.texture);
            if (it == textures.end()) return Fail("Unknown texture");
            srv = it->second.srv.Get();
            srcW = it->second.w; srcH = it->second.h;
        }

        if (srcW > 0 && srcH > 0 && l.fit != 0)
        {
            float cropW = (c.uvRect[2] - c.uvRect[0]) * srcW, cropH = (c.uvRect[3] - c.uvRect[1]) * srcH;
            float srcAspect = cropW / cropH, dstAspect = hw / hh;
            if (l.fit == 1)
            {
                if (srcAspect > dstAspect) hh = hw / srcAspect; else hw = hh * srcAspect;
            }
            else
            {
                if (srcAspect > dstAspect)
                {
                    float keep = dstAspect / srcAspect, mid = (c.uvRect[0] + c.uvRect[2]) / 2, span = (c.uvRect[2] - c.uvRect[0]) * keep / 2;
                    c.uvRect[0] = mid - span; c.uvRect[2] = mid + span;
                }
                else
                {
                    float keep = srcAspect / dstAspect, mid = (c.uvRect[1] + c.uvRect[3]) / 2, span = (c.uvRect[3] - c.uvRect[1]) * keep / 2;
                    c.uvRect[1] = mid - span; c.uvRect[3] = mid + span;
                }
            }
        }
        c.halfSize[0] = hw; c.halfSize[1] = hh;

        switch (l.kind)
        {
        case CR_LAYER_VIDEO:
        case CR_LAYER_TEXTURE: Quad(c, psTexture.Get(), srv, true); break;
        case CR_LAYER_COLOR: Quad(c, psColor.Get(), nullptr, true); break;
        case CR_LAYER_PAPER: Quad(c, psPaper.Get(), nullptr, true); break;
        default: return Fail("Unknown layer kind");
        }
        return true;
    }

    bool Frame(Media& m, double t)
    {
        t = std::clamp(t, 0.0, std::max(0.0, m.duration - 0.5 / m.fps));
        if (!m.Advance(t) && m.converted) return true;
        if (!m.hasCur) return true;
        AVFrame* f = m.cur;
        m.sws = sws_getCachedContext(m.sws, f->width, f->height, static_cast<AVPixelFormat>(f->format),
            m.outW, m.outH, AV_PIX_FMT_BGRA, SWS_BILINEAR, nullptr, nullptr, nullptr);
        if (!m.sws) return Fail("Could not convert video frame");
        uint8_t* dst[4] = { m.pixels.data() };
        int stride[4] = { m.outW * 4 };
        sws_scale(m.sws, f->data, f->linesize, 0, f->height, dst, stride);
        ctx->UpdateSubresource(m.texture.tex.Get(), 0, nullptr, m.pixels.data(), m.outW * 4, 0);
        m.converted = true;
        return true;
    }

    int Open(const wchar_t* path, int maxHeight)
    {
        char utf8[4096] = {};
        WideCharToMultiByte(CP_UTF8, 0, path, -1, utf8, sizeof(utf8), nullptr, nullptr);
        auto m = std::make_unique<Media>();
        if (avformat_open_input(&m->fmt, utf8, nullptr, nullptr) < 0) { Fail("Could not open media"); return -1; }
        if (avformat_find_stream_info(m->fmt, nullptr) < 0) { Fail("Could not read media streams"); return -1; }
        m->duration = m->fmt->duration > 0 ? m->fmt->duration / static_cast<double>(AV_TIME_BASE) : 0;
        m->hasAudio = av_find_best_stream(m->fmt, AVMEDIA_TYPE_AUDIO, -1, -1, nullptr, 0) >= 0;
        const AVCodec* codec = nullptr;
        m->stream = av_find_best_stream(m->fmt, AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
        if (m->stream >= 0)
        {
            AVStream* s = m->fmt->streams[m->stream];
            m->dec = avcodec_alloc_context3(codec);
            avcodec_parameters_to_context(m->dec, s->codecpar);
            m->dec->thread_count = 0;
            if (avcodec_open2(m->dec, codec, nullptr) < 0) { Fail("Could not open the video decoder"); return -1; }
            m->tb = av_q2d(s->time_base);
            m->start = s->start_time != AV_NOPTS_VALUE ? s->start_time * m->tb : 0;
            AVRational rate = av_guess_frame_rate(m->fmt, s, nullptr);
            m->fps = rate.num > 0 && rate.den > 0 ? av_q2d(rate) : 30;
            if (s->duration > 0) m->duration = std::max(m->duration, s->duration * m->tb);
            int w = m->dec->width, h = m->dec->height;
            if (w <= 0 || h <= 0) { Fail("Video has no size"); return -1; }
            if (maxHeight > 0 && h > maxHeight) { w = static_cast<int>(std::lround(w * (maxHeight / static_cast<double>(h)) / 2.0) * 2); h = maxHeight; }
            m->outW = w; m->outH = h;
            m->pixels.resize(static_cast<size_t>(w) * h * 4);
            m->cur = av_frame_alloc(); m->next = av_frame_alloc(); m->pkt = av_packet_alloc();
            D3D11_TEXTURE2D_DESC d = {};
            d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
            d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
            d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            HRESULT hr = device->CreateTexture2D(&d, nullptr, &m->texture.tex);
            if (FAILED(hr)) { Fail("Could not create video texture", hr); return -1; }
            device->CreateShaderResourceView(m->texture.tex.Get(), nullptr, &m->texture.srv);
            m->texture.w = w; m->texture.h = h;
        }
        int id = nextMedia++;
        media[id] = std::move(m);
        return id;
    }

    int Upload(int id, int w, int h, const void* bgra, int stride)
    {
        auto it = textures.find(id);
        if (it == textures.end() || it->second.w != w || it->second.h != h)
        {
            Texture t;
            D3D11_TEXTURE2D_DESC d = {};
            d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
            d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
            d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            HRESULT hr = device->CreateTexture2D(&d, nullptr, &t.tex);
            if (FAILED(hr)) { Fail("Could not create texture", hr); return -1; }
            device->CreateShaderResourceView(t.tex.Get(), nullptr, &t.srv);
            t.w = w; t.h = h;
            textures[id] = t;
        }
        ctx->UpdateSubresource(textures[id].tex.Get(), 0, nullptr, bgra, stride, 0);
        return id;
    }

    int Attach(HWND hwnd, int w, int h)
    {
        DXGI_SWAP_CHAIN_DESC1 d = {};
        d.Width = std::max(1, w); d.Height = std::max(1, h);
        d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        d.SampleDesc.Count = 1;
        d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        d.BufferCount = 2;
        d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        Swap s;
        HRESULT hr = factory->CreateSwapChainForHwnd(device.Get(), hwnd, &d, nullptr, nullptr, &s.chain);
        if (FAILED(hr)) { Fail("Could not create the preview surface", hr); return -1; }
        factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);
        s.w = d.Width; s.h = d.Height;
        if (!BackBuffer(s)) return -1;
        int id = nextSwap++;
        swaps[id] = s;
        return id;
    }

    bool BackBuffer(Swap& s)
    {
        ComPtr<ID3D11Texture2D> back;
        HRESULT hr = s.chain->GetBuffer(0, IID_PPV_ARGS(&back));
        if (FAILED(hr)) return Fail("Could not get the preview buffer", hr);
        hr = device->CreateRenderTargetView(back.Get(), nullptr, &s.rtv);
        if (FAILED(hr)) return Fail("Could not bind the preview buffer", hr);
        return true;
    }

    bool Present(int id)
    {
        auto it = swaps.find(id);
        if (it == swaps.end()) return Fail("Unknown preview surface");
        Swap& s = it->second;
        float black[4] = { 0, 0, 0, 1 };
        ctx->ClearRenderTargetView(s.rtv.Get(), black);
        Bind(s.rtv.Get(), s.w, s.h);
        Constants c = FullScreen(s.w, s.h);
        float aspect = static_cast<float>(width) / height;
        float hw = s.w / 2.f, hh = s.h / 2.f;
        if (hw / hh > aspect) hw = hh * aspect; else hh = hw / aspect;
        c.halfSize[0] = hw; c.halfSize[1] = hh;
        Quad(c, psTexture.Get(), canvas.srv.Get(), false);
        HRESULT hr = s.chain->Present(0, 0);
        if (FAILED(hr)) return Fail("Preview present failed", hr);
        return true;
    }
};

Engine* E(void* p) { return static_cast<Engine*>(p); }

} // namespace

CR_API void* cr_create(int32_t width, int32_t height, int32_t useWarp)
{
    auto e = std::make_unique<Engine>();
    if (!e->Init(width, height, useWarp != 0)) return nullptr;
    return e.release();
}

CR_API void cr_destroy(void* engine) { delete E(engine); }
CR_API const char* cr_last_error() { return g_error.c_str(); }
CR_API const char* cr_adapter(void* engine) { return E(engine)->adapter.c_str(); }

CR_API int32_t cr_open(void* engine, const wchar_t* path, int32_t maxHeight) { return E(engine)->Open(path, maxHeight); }

CR_API int32_t cr_media_info(void* engine, int32_t media, CrMediaInfo* info)
{
    auto it = E(engine)->media.find(media);
    if (it == E(engine)->media.end()) return Fail("Unknown media handle") ? 0 : -1;
    Media& m = *it->second;
    info->width = m.outW; info->height = m.outH; info->fps = m.fps; info->duration = m.duration;
    info->hasVideo = m.stream >= 0; info->hasAudio = m.hasAudio;
    return 0;
}

CR_API void cr_close(void* engine, int32_t media) { E(engine)->media.erase(media); }

CR_API int32_t cr_upload(void* engine, int32_t id, int32_t width, int32_t height, const void* bgra, int32_t stride)
{
    return E(engine)->Upload(id, width, height, bgra, stride);
}

CR_API void cr_release_texture(void* engine, int32_t id) { E(engine)->textures.erase(id); }

CR_API void cr_begin(void* engine, float r, float g, float b)
{
    float c[4] = { r, g, b, 1 };
    E(engine)->ctx->ClearRenderTargetView(E(engine)->canvas.rtv.Get(), c);
}

CR_API int32_t cr_draw(void* engine, const CrLayer* layer) { return E(engine)->Draw(*layer) ? 0 : -1; }

CR_API int32_t cr_attach(void* engine, void* hwnd, int32_t width, int32_t height)
{
    return E(engine)->Attach(static_cast<HWND>(hwnd), width, height);
}

CR_API int32_t cr_resize(void* engine, int32_t swap, int32_t width, int32_t height)
{
    auto it = E(engine)->swaps.find(swap);
    if (it == E(engine)->swaps.end()) return -1;
    Swap& s = it->second;
    s.rtv.Reset();
    E(engine)->ctx->OMSetRenderTargets(0, nullptr, nullptr);
    E(engine)->ctx->Flush();
    HRESULT hr = s.chain->ResizeBuffers(0, std::max(1, width), std::max(1, height), DXGI_FORMAT_UNKNOWN, 0);
    if (FAILED(hr)) return Fail("Could not resize the preview", hr) ? 0 : -1;
    s.w = std::max(1, width); s.h = std::max(1, height);
    return E(engine)->BackBuffer(s) ? 0 : -1;
}

CR_API void cr_detach(void* engine, int32_t swap)
{
    E(engine)->ctx->OMSetRenderTargets(0, nullptr, nullptr);
    E(engine)->swaps.erase(swap);
}

CR_API int32_t cr_present(void* engine, int32_t swap) { return E(engine)->Present(swap) ? 0 : -1; }

CR_API int32_t cr_readback(void* engine, void* bgra, int32_t stride)
{
    Engine* e = E(engine);
    e->ctx->CopyResource(e->staging.Get(), e->canvas.tex.Get());
    D3D11_MAPPED_SUBRESOURCE map = {};
    HRESULT hr = e->ctx->Map(e->staging.Get(), 0, D3D11_MAP_READ, 0, &map);
    if (FAILED(hr)) return Fail("Could not read the frame back", hr) ? 0 : -1;
    for (int y = 0; y < e->height; y++)
        memcpy(static_cast<uint8_t*>(bgra) + static_cast<size_t>(y) * stride, static_cast<uint8_t*>(map.pData) + static_cast<size_t>(y) * map.RowPitch, static_cast<size_t>(e->width) * 4);
    e->ctx->Unmap(e->staging.Get(), 0);
    return 0;
}
