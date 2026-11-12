#ifndef REALTIME_INK_NOISE_INCLUDED
#define REALTIME_INK_NOISE_INCLUDED

uint InkHash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    return value ^ (value >> 16);
}

uint InkHash2(int2 p)
{
    return InkHash(asuint(p.x) ^ InkHash(asuint(p.y) + 0x9e3779b9u));
}

float InkRandom(int2 p)
{
    return (InkHash2(p) & 0x00ffffffu) / 16777216.0;
}

float2 InkGradient(int2 p)
{
    uint h = InkHash2(p) & 7u;
    if (h < 4u)
        return float2((h & 1u) != 0u ? 0.70710678 : -0.70710678,
                      (h & 2u) != 0u ? 0.70710678 : -0.70710678);
    return h < 6u ? float2(h == 4u ? 1.0 : -1.0, 0.0) : float2(0.0, h == 6u ? 1.0 : -1.0);
}

// 真正的 gradient Perlin noise；不是用随机散点冒充 Perlin。
float InkPerlin(float2 p)
{
    int2 cell = (int2)floor(p);
    float2 f = frac(p);
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = dot(InkGradient(cell), f);
    float b = dot(InkGradient(cell + int2(1, 0)), f - float2(1, 0));
    float c = dot(InkGradient(cell + int2(0, 1)), f - float2(0, 1));
    float d = dot(InkGradient(cell + int2(1, 1)), f - float2(1, 1));
    return 1.41421356 * lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

#endif
