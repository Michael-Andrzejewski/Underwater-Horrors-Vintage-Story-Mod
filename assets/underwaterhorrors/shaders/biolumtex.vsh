#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Per-entity additive emissive renderer.
// Vertex positions are in MODEL space (the segment_mid shape's local
// coordinate frame). modelMatrix transforms them into camera-relative
// world space; viewMatrix (CameraMatrixOriginf) rotates into view
// space; projection finishes the job.

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

out vec2 uv;
out float worldY;        // world-space Y of this vertex (for depth attenuation)

void main() {
    vec4 worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);
    gl_Position   = projectionMatrix * viewMatrix * worldPos;
    uv     = uvIn;
    worldY = worldPos.y;
}
