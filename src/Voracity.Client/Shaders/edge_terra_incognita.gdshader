shader_type canvas_item;

uniform float time_seconds = 0.0;
uniform float camera_zoom = 1.0;

// World rectangle mapped over an extended canvas [-1400, -1000] .. [3320, 2120] (total 4720 x 3120)
// Known map rectangle sits inside normalized rect [0.2966, 0.3205] .. [0.7034, 0.6795]
const vec2 WORLD_UV_MIN = vec2(1400.0 / 4720.0, 1000.0 / 3120.0);
const vec2 WORLD_UV_MAX = vec2(3320.0 / 4720.0, 2120.0 / 3120.0);

float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float noise2d(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++) {
        v += amp * noise2d(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return v;
}

void fragment() {
    // Distinct "Uncharted Edge Fog" aesthetic: pale parchment clouds / soft silver-cream Terra Incognita
    // (visually unmistakable compared to the dark ash/hatch fog over hostile Veasperia)
    vec2 drift1 = vec2(time_seconds * 0.012, -time_seconds * 0.007);
    vec2 drift2 = vec2(-time_seconds * 0.009, time_seconds * 0.011);

    float n1 = fbm(UV * vec2(18.0, 12.0) + drift1);
    float n2 = fbm(UV * vec2(34.0, 24.0) + drift2);
    float billow = clamp(n1 * 0.65 + n2 * 0.45, 0.0, 1.0);

    // Distance from known cartographic world bounds
    vec2 dx = max(WORLD_UV_MIN - UV, UV - WORLD_UV_MAX);
    float out_dist = max(0.0, max(dx.x, dx.y));
    float edge_fade = smoothstep(0.0, 0.14, out_dist);

    vec3 inner_cloud = vec3(0.56, 0.54, 0.49);
    vec3 bright_wisp = vec3(0.75, 0.72, 0.66);
    vec3 outer_void = vec3(0.30, 0.31, 0.34);

    vec3 col = mix(inner_cloud, bright_wisp, smoothstep(0.32, 0.85, billow));
    col = mix(col, outer_void, edge_fade * 0.72);

    // Faint cartographic compass/rhumb lines drifting into the unknown mist
    vec2 g = abs(fract(UV * vec2(26.0, 17.0) - 0.5) - 0.5) / fwidth(UV * vec2(26.0, 17.0));
    float line = 1.0 - min(min(g.x, g.y), 1.0);
    col = mix(col, vec3(0.45, 0.42, 0.37), line * 0.14 * (1.0 - edge_fade * 0.75));

    COLOR = vec4(col, 1.0);
}
