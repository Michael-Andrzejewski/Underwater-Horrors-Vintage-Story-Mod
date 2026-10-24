#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// The giant serpent's deep silhouette: a tube around its spine, rebuilt by
// EntityBehaviorGiantSerpentBody every frame. Vertex positions are relative
// to the serpent's mouth; originRel moves them to camera-relative space.

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;     // x: how squarely the surface faces the camera (0 = outline)

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform vec3 originRel;    // mouth minus camera position
uniform float originY;     // world Y of the mouth

out float facing;
out float worldY;
out float dist;
out vec3 viewPos;

void main() {
    vec3 rel = vertexPositionIn + originRel;
    vec4 view = viewMatrix * vec4(rel, 1.0);
    gl_Position = projectionMatrix * view;
    viewPos = view.xyz;
    facing = uvIn.x;
    worldY = vertexPositionIn.y + originY;
    dist = length(rel);
}
