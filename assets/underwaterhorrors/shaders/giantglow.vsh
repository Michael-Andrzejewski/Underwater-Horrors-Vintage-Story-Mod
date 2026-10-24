#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// The glowing leviathan's bioluminescent dots: camera-facing sprites built
// on the CPU (GiantSerpentGlow), positions relative to the camera.

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;      // -1..1 across the sprite
layout(location = 2) in vec4 colorIn;   // rgb glow color, a brightness

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;

out vec2 uv;
out vec4 color;
out vec3 viewPos;

void main() {
    vec4 cameraPos = viewMatrix * vec4(vertexPositionIn, 1.0);
    gl_Position = projectionMatrix * cameraPos;
    viewPos = cameraPos.xyz;
    uv = uvIn;
    color = colorIn;
}
