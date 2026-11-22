// Spike: live Liquid Glass. Refraction profile adapted from Glasmio (Apache-2.0), itself after AndroidLiquidGlass.
cbuffer Params : register(b0)
{
    float2 size;        // glass size, px
    float radius;
    float height;       // refraction band depth
    float amount;       // max displacement
    float blurRadius;
    float mode;         // 0 naive, 1 masked
    float dark;
    float2 srcOffset;   // glass (0,0) inside the captured region
    float2 srcSize;
    float4 masks[24];   // owner content rects, glass-local px (x, y, w, h)
    float maskCount;
    float3 pad;
};

Texture2D src : register(t0);
SamplerState samp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float RoundedSdf(float2 p, out float2 normal)
{
    float2 c = size * 0.5;
    float2 d = p - c;
    float2 q = abs(d) - (c - radius);
    float outside = length(max(q, 0));
    float inside = min(max(q.x, q.y), 0);
    if (q.x > 0 && q.y > 0) normal = normalize(q) * sign(d);
    else if (q.x > q.y) normal = float2(sign(d.x), 0);
    else normal = float2(0, sign(d.y));
    return outside + inside - radius;
}

float Masked(float2 p)
{
    if (mode < 0.5) return 0;
    for (int i = 0; i < (int)maskCount; i++)
    {
        float4 r = masks[i];
        if (p.x >= r.x - 2 && p.y >= r.y - 2 && p.x <= r.x + r.z + 2 && p.y <= r.y + r.w + 2) return 1;
    }
    return 0;
}

float3 Tap(float2 p) { return src.SampleLevel(samp, (p + srcOffset) / srcSize, 0).rgb; }

float3 Body(float2 c)
{
    float3 col = 0; float wsum = 0;
    [loop] for (int i = -4; i <= 4; i++)
    [loop] for (int j = -4; j <= 4; j++)
    {
        float2 s = c + float2(i, j) * (blurRadius / 4.0);
        float w = exp(-(i * i + j * j) / 8.0) * (1 - Masked(s));
        col += Tap(s) * w; wsum += w;
    }
    if (wsum < 0.05)
    {
        // everything nearby is the owner's own content: reach further out for real background
        [loop] for (int k = 0; k < 16; k++)
        {
            float a = k * 0.3927;
            [loop] for (int ring = 1; ring <= 4; ring++)
            {
                float2 s = c + float2(cos(a), sin(a)) * (blurRadius + 10.0) * ring;
                float w = (1 - Masked(s)) / ring;
                col += Tap(s) * w; wsum += w;
            }
        }
    }
    return wsum > 0.001 ? col / wsum : float3(0.15, 0.15, 0.17);
}

float4 PS(VSOut i) : SV_Target
{
    float2 p = i.uv * size;
    float2 n;
    float sd = RoundedSdf(p, n);
    float a = saturate(0.5 - sd);
    if (a <= 0) return 0;
    float u = saturate(-sd / height);
    float disp = amount * (1 - saturate(sqrt(u * (2 - u))));
    float3 col = Body(p - n * disp);
    float l = dot(col, float3(0.299, 0.587, 0.114));
    col = lerp(float3(l, l, l), col, 1.2);
    col = dark > 0.5 ? lerp(col, float3(0.10, 0.10, 0.12), 0.22) : lerp(col, float3(1, 1, 1), 0.22);
    float edge = 1 - smoothstep(0, 2.5, -sd);
    float light = saturate(dot(n, normalize(float2(-0.6, -0.8))));
    float back = saturate(dot(n, normalize(float2(0.6, 0.8))));
    col += edge * (0.45 * light + 0.18 * back + 0.05);
    return float4(saturate(col) * a, a);
}

// Tiny copy of the source, for the "did anything change?" check (read back 1/8 size).
float4 PSCopy(VSOut i) : SV_Target { return src.SampleLevel(samp, i.uv, 0); }
