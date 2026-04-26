#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Camera-facing billboard for kraken bioluminescent glow.
// Each draw is a unit quad in the [-0.5, +0.5] XY range; we transform
// the billboard CENTER to view space first, then offset by the
// vertex's XY in view space (so the quad always faces the camera
// regardless of camera orientation). Z is preserved from the center,
// which keeps depth-testing correct: the quad still gets occluded by
// terrain that's closer than the billboard center.

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform vec3 worldPos;   // billboard center in player-relative world space
uniform float scale;     // billboard width/height in blocks

out vec2 uv;
out float vViewZ;        // camera-to-billboard distance, used by fragment shader

void main() {
    vec4 viewCenter = modelViewMatrix * vec4(worldPos, 1.0);
    vec4 viewVertex = vec4(
        viewCenter.x + vertexPositionIn.x * scale,
        viewCenter.y + vertexPositionIn.y * scale,
        viewCenter.z,
        1.0
    );
    gl_Position = projectionMatrix * viewVertex;
    uv = uvIn;
    // worldPos is already camera-relative (renderer subtracts cameraPos
    // before passing). length(worldPos) IS the view-space distance.
    vViewZ = length(worldPos);
}
