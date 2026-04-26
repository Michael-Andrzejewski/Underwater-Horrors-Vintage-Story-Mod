#version 330 core

// Samples the segment's existing texture (bellhead) and emits additive
// cyan based on per-pixel "orange-ness" of the source. Bright orange
// dashes get high glow, dark trunk gets none, so the glow inherently
// matches whatever pattern the artist painted into the texture.
//
// Depth attenuation: vertices below the surface get progressively
// dimmer based on (surfaceY - worldY), so the top of a tall tentacle
// near the surface stays bright while its base ~30 blocks down dims
// noticeably. depthFactor controls how strong the effect is per mode
// (0 = no depth fade, 1 = strong fade).

in vec2 uv;
in float worldY;
out vec4 outColor;

uniform sampler2D entityTex;   // VS entity texture atlas
uniform vec3 glowColor;        // additive tint, e.g. cyan (0.35, 0.95, 1.0)
uniform float intensity;       // global brightness multiplier
uniform float depthFactor;     // 0..1+ how strongly worldY attenuates the glow
uniform float surfaceY;        // world Y of the water surface (default ~110)

void main() {
    vec4 t = texture(entityTex, uv);
    // Skip transparent texels entirely - the segment shape has alpha.
    if (t.a < 0.04) discard;

    // Orange-ness mask: bright in R, weaker in B. The orange dashes on
    // the bellhead texture have R near 1.0 and B near 0.2, so this
    // weight peaks there and falls to ~0 on the dark trunk.
    float orange = clamp((t.r - t.b) * 1.4, 0.0, 1.0);
    if (orange < 0.01) discard;

    // Depth fade: 1.0 at the surface, falls toward 0 as worldY drops.
    // Floored at 0.25 so deep tentacles don't disappear entirely.
    float depthBelow = max(0.0, surfaceY - worldY);
    float depthAtten = clamp(1.0 - depthBelow * depthFactor * 0.01, 0.25, 1.0);

    float glow = orange * intensity * depthAtten;
    outColor = vec4(glowColor * glow, glow);
}
