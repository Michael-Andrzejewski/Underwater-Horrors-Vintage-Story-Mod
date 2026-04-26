#version 330 core

// Mode-driven glow fragment shader. The renderer drives this with
// modeBranch, glowColor, and diffuseScale to produce different visual
// effects without recompiling for each test variant.
//
//   modeBranch 0 - quadratic soft falloff (the original cyan halo)
//   modeBranch 1 - solid disk (no falloff, full color)
//   modeBranch 2 - pulsing soft falloff (alpha modulated by sin(uTime))
//   modeBranch 3 - gaussian falloff (smoother edges, more diffuse)
//   modeBranch 4 - soft halo only (linear, no bright center)
//
// diffuseScale is forwarded from the renderer per-mode (typically 0..1)
// and is multiplied by viewZ inside this shader to give a small "more
// diffuse with distance" effect that approximates underwater scattering.

in vec2 uv;
in float vViewZ;       // camera-to-billboard distance in blocks (vertex shader)
out vec4 outColor;

uniform vec4 glowColor;
uniform int modeBranch;
uniform float uTime;
uniform float diffuseScale;   // 0 = no extra diffusion; >0 widens falloff with viewZ

void main() {
    vec2 centered = uv * 2.0 - 1.0;
    float dist = length(centered);
    if (dist > 1.0) discard;

    // Solid disk: skip everything else.
    if (modeBranch == 1) {
        outColor = glowColor;
        return;
    }

    // Per-pixel diffusion factor. At viewZ=0 we want falloff = 2.0 (the
    // original quadratic curve). Each block of camera-to-billboard
    // distance softens the curve slightly, capped so the look stays
    // recognizable. diffuseScale=0 disables the effect entirely.
    float falloffPow = max(1.0, 2.0 - diffuseScale * vViewZ * 0.01);

    if (modeBranch == 2) {
        float pulse = 0.5 + 0.5 * sin(uTime * 4.18879);
        float f = pow(max(0.0, 1.0 - dist), falloffPow);
        f *= pulse;
        outColor = vec4(glowColor.rgb * f, glowColor.a * f);
        return;
    }

    if (modeBranch == 3) {
        // Gaussian: exp(-dist^2 / (2*sigma^2)). Bigger sigma at distance
        // gives a softer, more diffuse circle.
        float sigma = 0.45 + diffuseScale * vViewZ * 0.005;
        float f = exp(-(dist * dist) / (2.0 * sigma * sigma));
        // Clamp so center isn't blown out.
        f = clamp(f, 0.0, 1.0);
        outColor = vec4(glowColor.rgb * f, glowColor.a * f);
        return;
    }

    if (modeBranch == 4) {
        // Soft halo only - linear falloff, no bright concentrated center.
        float f = max(0.0, 1.0 - dist);
        // Cube it for a softer halo without a hot core.
        f = f * f * f;
        outColor = vec4(glowColor.rgb * f, glowColor.a * f);
        return;
    }

    // Default (modeBranch 0) - quadratic with optional distance softening.
    float f0 = pow(max(0.0, 1.0 - dist), falloffPow);
    outColor = vec4(glowColor.rgb * f0, glowColor.a * f0);
}
