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

    // Pink-cube fallback for object instances with no icon. Flat per-object
    // color with cheap positional shading so cube faces read as 3D.
    public const string CubeVertex = @"#version 300 es
precision highp float;
layout(location = 0) in vec3 aPos;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uCenter;
uniform float uSize;
out vec3 vLocal;
void main() {
    vLocal = aPos;
    vec3 world = uCenter + aPos * uSize;
    gl_Position = uProj * uView * vec4(world, 1.0);
}
";

    // River strip. Flat alpha-blended color at the terrain surface + a small
    // Y lift so it doesn't z-fight the ground; Phase 4 will push the terrain
    // down to meet it rather than lifting the river up.
    public const string RiverVertex = @"#version 300 es
precision highp float;
layout(location = 0) in vec3 aPos;
uniform mat4 uView;
uniform mat4 uProj;
void main() {
    gl_Position = uProj * uView * vec4(aPos, 1.0);
}
";

    public const string RiverFragment = @"#version 300 es
precision highp float;
uniform vec4 uColor;
out vec4 oColor;
void main() {
    oColor = uColor;
}
";

    public const string CubeFragment = @"#version 300 es
precision highp float;
in vec3 vLocal;
uniform vec4 uColor;
out vec4 oColor;
void main() {
    // Brighter on +Y, darker on -Y so the cube reads as 3D even unlit.
    float shade = 0.70 + 0.30 * (vLocal.y + 0.5);
    oColor = vec4(uColor.rgb * shade, uColor.a);
}
";

    // Icon billboard. The quad orientation is derived from the inverse of
    // uView's rotation so the sprite stays facing the camera as it moves
    // (classic 'spherical' billboard — reads correctly in both ortho and
    // perspective). uUvRect lets a single texture act as a sprite sheet:
    // (u0, v0) = top-left of chosen cell, (u1, v1) = bottom-right.
    public const string BillboardVertex = @"#version 300 es
precision highp float;
layout(location = 0) in vec2 aLocal;
layout(location = 1) in vec2 aUV;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uCenter;
uniform vec2 uSize;
uniform vec4 uUvRect;
out vec2 vUV;
void main() {
    // Camera right/up live in the first two columns of uView's rotation.
    vec3 camRight = vec3(uView[0][0], uView[1][0], uView[2][0]);
    vec3 camUp    = vec3(uView[0][1], uView[1][1], uView[2][1]);
    vec3 world = uCenter + camRight * aLocal.x * uSize.x + camUp * aLocal.y * uSize.y;
    gl_Position = uProj * uView * vec4(world, 1.0);
    vUV = mix(uUvRect.xy, uUvRect.zw, aUV);
}
";

    public const string BillboardFragment = @"#version 300 es
precision highp float;
in vec2 vUV;
uniform sampler2D uTex;
out vec4 oColor;
void main() {
    vec4 c = texture(uTex, vUV);
    if (c.a < 0.02) discard;
    oColor = c;
}
";
}
