#version 330 core

// A dark, soft-edged shape where the giant serpent is too deep (seen from
// above the water) or too far into the murk (seen from underwater) for its
// real model to show. Where the model itself is visible, this fades out.

in float facing;
in float worldY;
in float dist;
in vec3 viewPos;
out vec4 outColor;

uniform mat4 projectionMatrix;
uniform float surfaceY;      // world Y of the water surface over the serpent
uniform int camUnderwater;   // 1 when the camera is in water
uniform vec3 tint;           // near-black blue
uniform float maxAlpha;
uniform float depthPull;     // blocks the shape is moved toward the camera for depth testing

void main() {
    float a;
    if (camUnderwater == 1) {
        // The water fog swallows the model a couple of dozen blocks out;
        // the shape takes over from there and fades only far away.
        a = smoothstep(10.0, 34.0, dist) * (1.0 - smoothstep(180.0, 260.0, dist));
    } else {
        // From above, the water hides anything a dozen blocks down. Show
        // the shape below that, a little fainter the deeper it goes.
        float depth = surfaceY - worldY;
        a = smoothstep(2.0, 12.0, depth)
          * mix(1.0, 0.6, smoothstep(25.0, 90.0, depth))
          * (1.0 - smoothstep(220.0, 320.0, dist));
    }
    // Soft outline: surfaces seen edge-on fade out.
    a *= maxAlpha * smoothstep(0.15, 0.85, facing);
    if (a < 0.004) discard;

    // Depth-test as if a little nearer the camera. The model's own fins
    // and spikes stick out past this tube and would otherwise punch jagged
    // holes in it; terrain in front of the serpent still hides it.
    float len = length(viewPos);
    vec4 clip = projectionMatrix * vec4(viewPos * max(0.05, 1.0 - depthPull / max(len, 0.001)), 1.0);
    gl_FragDepth = clip.z / clip.w * 0.5 + 0.5;

    outColor = vec4(tint, a);
}
