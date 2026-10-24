#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// The giant serpent's deep silhouette: the entity's own mesh, posed with the
// bone matrices the game used for the real model this frame. The vertex
// layout and the transform are those of the game's entityanimated shader,
// so every vertex lands exactly where the real model's did.

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 5) in int jointId;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;   // camera-relative, from the entity renderer
uniform float cameraY;      // world Y of the camera

layout (std140) uniform Animation
{
    mat4 values[MAXANIMATEDELEMENTS];   // defined by the game when it loads the shader
} ElementTransforms;

out vec2 uv;
out float worldY;
out float dist;
out vec3 viewPos;

void main() {
    mat4 animModelMat = modelMatrix * ElementTransforms.values[jointId];
    vec4 worldPos = animModelMat * vec4(vertexPositionIn, 1.0);
    vec4 cameraPos = viewMatrix * worldPos;
    gl_Position = projectionMatrix * cameraPos;
    viewPos = cameraPos.xyz;
    uv = uvIn;
    worldY = worldPos.y + cameraY;
    dist = length(worldPos.xyz);
}
