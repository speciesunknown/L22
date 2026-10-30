namespace L22.Editor.Models.Rendering;

// GLSL ES 3.00 (what Avalonia/ANGLE gives us on Windows). Works for GL 3.3
// core too since this is forward-compatible if we need WGL later.
public static class ShaderSource
{
    public const string TerrainVertex = @"#version 300 es
precision highp float;
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aColor;
layout(location = 2) in vec3 aNormal;

uniform mat4 uView;
uniform mat4 uProj;

out vec3 vColor;
out vec3 vNormal;
out vec3 vWorldPos;

void main() {
    vColor = aColor;
    vNormal = aNormal;
    vWorldPos = aPos;
    gl_Position = uProj * uView * vec4(aPos, 1.0);
}
";

    public const string TerrainFragment = @"#version 300 es
precision highp float;
in vec3 vColor;
in vec3 vNormal;
in vec3 vWorldPos;

uniform vec3 uBrushCenter;
uniform float uBrushRadius;
uniform int uBrushVisible;
uniform int uGreyscale;
uniform float uElevationScale;
// Dynamic greyscale range in world Y units. Minimum width is one
// ElevationScale (= painted range 0..1); extends out when any cell or
// chunk corner pushes the world outside that bracket.
uniform float uGreyLowY;
uniform float uGreyHighY;
// When 1, fragments whose vertex color is pure black (meaning no terrain
// weighting) are discarded — the 3D view treats them as absent geometry.
uniform int uHideUnset;

out vec4 oColor;

void main() {
    if (uHideUnset == 1 && vColor.r + vColor.g + vColor.b < 0.001) discard;
    vec3 baseColor = vColor;
    if (uGreyscale == 1) {
        // Elevation editing mode: map the current vertex Y onto a dynamic
        // greyscale range. Flat ground reads as its terrain colour; peaks
        // read toward white; sunken areas toward black. The range widens
        // as the world does, so the gradient keeps meaningful contrast.
        float range = max(uGreyHighY - uGreyLowY, 0.001);
        float g = clamp((vWorldPos.y - uGreyLowY) / range, 0.0, 1.0);
        baseColor = mix(vColor * 0.55, mix(vColor, vec3(1.0), g), g * 0.5 + 0.5);
    }
    vec3 N = normalize(vNormal);
    vec3 L = normalize(vec3(0.4, 0.8, 0.5));
    float ndotl = max(dot(N, L), 0.0);
    vec3 ambient = vec3(0.35, 0.38, 0.45);
    vec3 lit = baseColor * (ambient + 0.9 * ndotl);
    if (uGreyscale == 1) lit = baseColor;  // skip shading for cleaner read

    if (uBrushVisible == 1) {
        vec2 d2 = vWorldPos.xz - uBrushCenter.xz;
        float dist = length(d2);
        float ring = 1.0 - smoothstep(uBrushRadius - 0.25, uBrushRadius + 0.05, dist);
        float inner = 1.0 - smoothstep(uBrushRadius - 0.55, uBrushRadius - 0.25, dist);
        float edge = clamp(ring - inner, 0.0, 1.0);
        lit = mix(lit, vec3(1.0, 0.9, 0.2), edge * 0.9);
    }

    oColor = vec4(lit, 1.0);
}
";

    public const string GridVertex = @"#version 300 es
precision highp float;
layout(location = 0) in vec3 aPos;
uniform mat4 uView;
uniform mat4 uProj;
void main() {
    gl_Position = uProj * uView * vec4(aPos, 1.0);
}
";

    public const string GridFragment = @"#version 300 es
precision highp float;
uniform vec4 uLineColor;
out vec4 oColor;
void main() {
    oColor = uLineColor;
}
";

    // Pink corner markers for the Corner Elevation tool. Draws one point per
    // chunk vertex using GL_POINTS; the fragment shader rounds each quad to a
    // soft circle so the markers read as dots, not squares.
    public const string CornerPointVertex = @"#version 300 es
precision highp float;
layout(location = 0) in vec3 aPos;
uniform mat4 uView;
uniform mat4 uProj;
uniform float uPointSize;
void main() {
    gl_Position = uProj * uView * vec4(aPos, 1.0);
    gl_PointSize = uPointSize;
}
";

    public const string CornerPointFragment = @"#version 300 es
precision highp float;
uniform int uRingMode;
uniform vec4 uColor;
out vec4 oColor;
void main() {
    vec2 d = gl_PointCoord - vec2(0.5);
    float r = length(d);
    if (r > 0.5) discard;
    // Ring mode: keep only the outer band.
    if (uRingMode == 1 && r < 0.38) discard;
    oColor = uColor;
}
";
}
