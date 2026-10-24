#version 330 core

// A soft bioluminescent dot: a bright, slightly whitened core and a faint
// halo, drawn additively (blend mode Glow).

in vec2 uv;
in vec4 color;
in vec3 viewPos;
out vec4 outColor;

uniform mat4 projectionMatrix;
uniform float depthPull;           // blocks toward the camera for the depth test
uniform float depthPullPerBlock;   // plus this much per block of distance
uniform float nearerBy;            // then this much nearer, in depth buffer units

void main() {
    float d2 = dot(uv, uv);
    if (d2 > 1.0) discard;
    float core = exp(-d2 * 18.0);
    float halo = 0.3 * exp(-d2 * 3.5) * (1.0 - d2);
    float f = (core + halo) * color.a;
    if (f < 0.003) discard;

    // Depth-test like the silhouette, a little nearer still, so a dot on
    // the serpent's nearest surface passes and one behind it does not.
    float len = length(viewPos);
    float pull = depthPull + depthPullPerBlock * len;
    vec4 clip = projectionMatrix * vec4(viewPos * max(0.05, 1.0 - pull / max(len, 0.001)), 1.0);
    gl_FragDepth = clip.z / clip.w * 0.5 + 0.5 - nearerBy;

    vec3 rgb = mix(color.rgb, vec3(1.0), 0.45 * core);
    outColor = vec4(rgb * 1.5, f);
}
