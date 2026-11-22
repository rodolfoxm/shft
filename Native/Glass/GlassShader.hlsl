// shft. real glass. Refraction curve adapted from Glasmio (Copyright 2026 NEORUAA, Apache-2.0),
// itself adapted from AndroidLiquidGlass (Copyright 2025 Kyant, Apache-2.0). https://www.apache.org/licenses/LICENSE-2.0
// The source is the magnifier's image of the screen without our own windows, so nothing here masks the Dock.
cbuffer Params : register(b0)
{
    float2 size;        // glass size, px
    float radius;
    float height;       // refraction band depth
    float amount;       // max displacement
    float blurRadius;
    float dark;
    float clear;        // 1 = Clear style (thin frost)
    float2 srcOffset;   // glass (0,0) inside the source image
    float2 srcSize;
    float4 tint;        // rgb + strength (a = 0: no tint)
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

float3 Tap(float2 p) { return src.SampleLevel(samp, (p + srcOffset) / srcSize, 0).rgb; }

float3 Body(float2 c)
{
    float3 col = 0; float wsum = 0;
    [loop] for (int i = -4; i <= 4; i++)
    [loop] for (int j = -4; j <= 4; j++)
    {
        float w = exp(-(i * i + j * j) / 8.0);
        col += Tap(c + float2(i, j) * (blurRadius / 4.0)) * w; wsum += w;
    }
    return col / wsum;
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
    float frost = clear > 0.5 ? 0.08 : 0.22;
    col = dark > 0.5 ? lerp(col, float3(0.10, 0.10, 0.12), frost) : lerp(col, float3(1, 1, 1), frost);
    col = lerp(col, tint.rgb, tint.a);
    float edge = 1 - smoothstep(0, 2.5, -sd);
    float light = saturate(dot(n, normalize(float2(-0.6, -0.8))));
    float back = saturate(dot(n, normalize(float2(0.6, 0.8))));
    col += edge * (0.45 * light + 0.18 * back + 0.05);
    col *= 1 - 0.06 * saturate(p.y / size.y - 0.6); // slight depth at the bottom
    return float4(saturate(col) * a, a);
}

// 1/8 copy of the source for the "did anything change?" check.
float4 PSCopy(VSOut i) : SV_Target { return src.SampleLevel(samp, i.uv, 0); }
