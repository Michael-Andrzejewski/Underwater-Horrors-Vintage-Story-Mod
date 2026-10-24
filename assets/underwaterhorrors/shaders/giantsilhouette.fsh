#version 330 core

// A flat dark copy of the giant serpent where it is too deep (seen from
// above the water) or too far into the murk (seen from underwater) for its
// real model to show. Where the model itself is visible, this fades out.

in vec2 uv;
in float worldY;
in float dist;
in vec3 viewPos;
out vec4 outColor;

uniform sampler2D entityTex;
uniform mat4 projectionMatrix;
uniform float surfaceY;      // world Y of the water surface over the serpent
uniform int camUnderwater;   // 1 when the camera is in water
uniform vec3 tint;           // near-black blue
uniform float maxAlpha;
uniform float depthPull;     // blocks the shape is moved toward the camera for depth testing
uniform float depthPullPerBlock;   // plus this much per block of distance
uniform float nearerBy;      // then this much nearer, in depth buffer units
uniform float fadeStart;     // distance where the shape starts to fade out
uniform float fadeEnd;       // and where it is gone

void main() {
    // Cut-out texels of the model are holes in the shape too.
    if (texture(entityTex, uv).a < 0.05) discard;

    float a;
    if (camUnderwater == 1) {
        // The water fog swallows the model a couple of dozen blocks out;
        // the shape takes over from there.
        a = smoothstep(10.0, 34.0, dist);
    } else {
        // From above, the water hides anything a dozen blocks down. Show
        // the shape below that, a little fainter the deeper it goes.
        float depth = surfaceY - worldY;
        a = smoothstep(2.0, 12.0, depth) * mix(1.0, 0.6, smoothstep(25.0, 90.0, depth));
    }
    a *= maxAlpha * (1.0 - smoothstep(fadeStart, fadeEnd, dist));
    if (a < 0.004) discard;

    // Depth-test as if nearer the camera (see GiantSerpentSilhouette for the
    // two passes this makes one even layer from).
    float len = length(viewPos);
    float pull = depthPull + depthPullPerBlock * len;
    vec4 clip = projectionMatrix * vec4(viewPos * max(0.05, 1.0 - pull / max(len, 0.001)), 1.0);
    gl_FragDepth = clip.z / clip.w * 0.5 + 0.5 - nearerBy;

    outColor = vec4(tint, a);
}
