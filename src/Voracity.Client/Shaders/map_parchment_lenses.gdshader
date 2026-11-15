shader_type canvas_item;

// Voracity 65k Hybrid Cartography Shader:
// - Exact integer 16-bit province lookup via province_data_tex (RG = 16-bit province ID, B = terrain ID)
// - Precomputed Chamfer Coast UDF, elevation, ridges & parchment grain via height_coast_tex
// - Dynamic 256x768 GPU lookup palette: 3 rows per 256-province bank:
//     row 0: Lens RGB + Alpha
//     row 1: [nationTagLo, nationTagHi, stateTagLo, stateTagHi] (65k unique IDs without overflow!)
//     row 2: [occupiedFlag, occupierHue, editorHighlight, reserved]
uniform sampler2D province_data_tex : filter_nearest, repeat_disable;
uniform sampler2D height_coast_tex : filter_linear, repeat_disable;
uniform sampler2D province_lookup_tex : filter_nearest, repeat_disable;

uniform float camera_zoom = 1.0;
uniform float time_seconds = 0.0;
uniform int hovered_province_idx = 0;
uniform int selected_province_idx = 0;
uniform int selected_state_tag = 0;
uniform bool is_political_lens = true;
uniform float terrain_relief_strength = 1.0;
uniform float shader_border_strength = 1.0;

int decode_prov(vec4 pdata) {
    return int(round(pdata.r * 255.0)) + int(round(pdata.g * 255.0)) * 256;
}

int decode_u16(float lo, float hi) {
    return int(round(lo * 255.0)) + int(round(hi * 255.0)) * 256;
}

ivec2 lookup_coord(int prov_idx, int row_offset) {
    int p = clamp(prov_idx, 0, 65535);
    int px = p & 255;
    int py = (p >> 8) * 3 + row_offset;
    return ivec2(px, py);
}

vec4 fetch_lookup_color(int prov_idx) {
    return texelFetch(province_lookup_tex, lookup_coord(prov_idx, 0), 0);
}

vec4 fetch_lookup_tags(int prov_idx) {
    return texelFetch(province_lookup_tex, lookup_coord(prov_idx, 1), 0);
}

vec4 fetch_lookup_state(int prov_idx) {
    return texelFetch(province_lookup_tex, lookup_coord(prov_idx, 2), 0);
}

vec3 terrain_biome_color(int terrain_id, float elev, vec2 world_uv) {
    float micro = fract(sin(dot(floor(world_uv * vec2(780.0, 460.0)), vec2(12.9898, 78.233))) * 43758.5453);
    vec3 col;
    if (terrain_id == 2) {
        // Farmlands: fertile golden-emerald fields with subtle patchwork
        float strip = sin(world_uv.x * 260.0 + world_uv.y * 90.0) * 0.035;
        col = vec3(0.38, 0.50, 0.25) + vec3(strip, strip * 0.7, -strip * 0.4);
    } else if (terrain_id == 3) {
        // Forests: rich emerald canopy
        col = vec3(0.16, 0.30, 0.19) + (micro - 0.5) * 0.05;
    } else if (terrain_id == 4) {
        // Hills: olive-ochre highland pasture
        col = vec3(0.45, 0.40, 0.27) + (elev - 0.65) * 0.18;
    } else if (terrain_id == 5) {
        // Mountains: slate rock transitioning into alpine snow peaks
        float snow = smoothstep(0.84, 0.96, elev);
        col = mix(vec3(0.39, 0.38, 0.41), vec3(0.84, 0.87, 0.91), snow);
    } else if (terrain_id == 6) {
        // Wetlands: misty teal-moss marsh
        col = vec3(0.22, 0.33, 0.29) + (micro - 0.5) * 0.04;
    } else if (terrain_id == 7) {
        // Desert: warm sun-baked dunes
        float dune = sin(world_uv.x * 160.0 - world_uv.y * 120.0) * 0.03;
        col = vec3(0.65, 0.53, 0.34) + vec3(dune, dune * 0.8, 0.0);
    } else if (terrain_id == 8) {
        // Coastal: sunlit Mediterranean sage/sand
        col = vec3(0.47, 0.49, 0.35);
    } else {
        // Plains
        col = vec3(0.39, 0.44, 0.27) + (micro - 0.5) * 0.03;
    }
    return clamp(col, vec3(0.05), vec3(0.95));
}

void fragment() {
    vec2 tex_size = vec2(textureSize(province_data_tex, 0));
    ivec2 max_coord = ivec2(tex_size) - ivec2(1);
    ivec2 p_coord = clamp(ivec2(UV * tex_size), ivec2(0), max_coord);

    vec4 pdata = texelFetch(province_data_tex, p_coord, 0);
    vec4 hc = texture(height_coast_tex, UV);
    float elev = hc.r;
    float coast_prox = hc.g;
    float grain = hc.b;

    int prov_idx = decode_prov(pdata);
    int terrain_id = int(round(pdata.b * 255.0));

    // Detail mix controls smooth semantic zoom transition: Paper/Political -> Relief Terrain
    float detail_mix = smoothstep(0.82, 1.95, camera_zoom);

    if (prov_idx <= 0) {
        if (terrain_id >= 240) {
            // Inaccessible Eastern Continent (Veasperia) beyond the Bastion Wall: dark Terra Incognita fog-of-war
            vec2 duv = 1.8 / tex_size;
            float h_r = texture(height_coast_tex, UV + vec2(duv.x, 0.0)).r;
            float h_d = texture(height_coast_tex, UV + vec2(0.0, duv.y)).r;
            vec3 w_nrm = normalize(vec3((elev - h_r) * 14.0, (elev - h_d) * 14.0, 1.0));
            float w_light = clamp(dot(w_nrm, normalize(vec3(-0.55, -0.65, 0.48))), 0.35, 1.25);

            vec3 ash_base = mix(vec3(0.075, 0.072, 0.088), vec3(0.165, 0.155, 0.182), elev) * w_light;

            // Drifting volumetric fog-of-war veil + diagonal cartographic hatch lines
            float fog_a = sin(UV.x * 28.0 + time_seconds * 0.38 + UV.y * 14.0) * 0.5 + 0.5;
            float fog_b = cos(UV.y * 23.0 - time_seconds * 0.27 + UV.x * 19.0) * 0.5 + 0.5;
            float fog_density = mix(0.45, 0.88, fog_a * 0.6 + fog_b * 0.4) * (0.86 + grain * 0.24);
            vec3 mist_col = mix(vec3(0.11, 0.12, 0.15), vec3(0.22, 0.23, 0.27), fog_density);

            float hatch = smoothstep(2.8, 0.8, mod(FRAGCOORD.x + FRAGCOORD.y, 13.0));
            vec3 wall_shadow = smoothstep(0.942, 0.922, UV.x) * vec3(0.04, 0.035, 0.045);

            vec3 waste_col = mix(ash_base, mist_col, 0.62) - wall_shadow;
            waste_col = mix(waste_col, vec3(0.055, 0.055, 0.068), hatch * 0.28);

            float waste_shore = smoothstep(0.84, 0.99, coast_prox);
            waste_col = mix(waste_col, vec3(0.04, 0.045, 0.06), waste_shore * 0.80);
            COLOR = vec4(waste_col, 1.0);
        } else {
            // Oceanic cartography: bathymetric shelf + historical parchment sea graticule + impassable Eastern Abyss
            float shelf = smoothstep(0.05, 0.45, elev);
            vec3 deep_sea = vec3(0.075, 0.125, 0.185);
            vec3 shallow_sea = vec3(0.145, 0.245, 0.305);
            vec3 sea_col = mix(deep_sea, shallow_sea, shelf * 0.65 + coast_prox * 0.55);

            // Darken eastern expanse (Mar das Tormentas) separating Pontaria from Veasperia
            float east_abyss = smoothstep(0.76, 0.88, UV.x) * (1.0 - smoothstep(0.92, 0.96, UV.x));
            float abyss_swirl = sin(UV.y * 34.0 + time_seconds * 0.9 + sin(UV.x * 22.0) * 2.0) * 0.5 + 0.5;
            sea_col = mix(sea_col, vec3(0.035, 0.055, 0.095), east_abyss * (0.48 + abyss_swirl * 0.22));

            // Cartographic lat/lon rhumb & grid hairlines
            vec2 grid = abs(fract(UV * vec2(18.0, 10.0) - 0.5) - 0.5) / fwidth(UV * vec2(18.0, 10.0));
            float grid_line = 1.0 - min(min(grid.x, grid.y), 1.0);
            sea_col = mix(sea_col, vec3(0.24, 0.33, 0.39), grid_line * (1.0 - detail_mix * 0.45) * 0.24);

            // Animated coastal surf ripples
            float wave = sin(coast_prox * 28.0 - time_seconds * 1.4 + UV.x * 40.0) * 0.5 + 0.5;
            sea_col += vec3(0.04, 0.08, 0.09) * smoothstep(0.55, 0.98, coast_prox) * wave;

            // Crisp ink shoreline outline
            float shore_ring = smoothstep(0.84, 0.99, coast_prox);
            sea_col = mix(sea_col, vec3(0.07, 0.09, 0.11), shore_ring * 0.68);
            sea_col *= 0.90 + grain * 0.14;

            COLOR = vec4(sea_col, 1.0);
        }
    } else {
        vec4 lens_col = fetch_lookup_color(prov_idx);
        vec4 tags = fetch_lookup_tags(prov_idx);
        vec4 state_flags = fetch_lookup_state(prov_idx);

        int my_nation_tag = decode_u16(tags.r, tags.g);
        int my_state_tag = decode_u16(tags.b, tags.a);

        // Neighbor probes for shoreline ink & inner rim shading
        vec4 p_r1 = texelFetch(province_data_tex, clamp(p_coord + ivec2(1, 0), ivec2(0), max_coord), 0);
        vec4 p_l1 = texelFetch(province_data_tex, clamp(p_coord + ivec2(-1, 0), ivec2(0), max_coord), 0);
        vec4 p_u1 = texelFetch(province_data_tex, clamp(p_coord + ivec2(0, -1), ivec2(0), max_coord), 0);
        vec4 p_d1 = texelFetch(province_data_tex, clamp(p_coord + ivec2(0, 1), ivec2(0), max_coord), 0);

        vec4 p_r2 = texelFetch(province_data_tex, clamp(p_coord + ivec2(2, 0), ivec2(0), max_coord), 0);
        vec4 p_d2 = texelFetch(province_data_tex, clamp(p_coord + ivec2(0, 2), ivec2(0), max_coord), 0);

        int n_r1 = decode_prov(p_r1);
        int n_l1 = decode_prov(p_l1);
        int n_u1 = decode_prov(p_u1);
        int n_d1 = decode_prov(p_d1);
        int n_r2 = decode_prov(p_r2);
        int n_d2 = decode_prov(p_d2);

        vec4 t_r1 = fetch_lookup_tags(n_r1);
        vec4 t_l1 = fetch_lookup_tags(n_l1);
        vec4 t_u1 = fetch_lookup_tags(n_u1);
        vec4 t_d1 = fetch_lookup_tags(n_d1);
        vec4 t_r2 = fetch_lookup_tags(n_r2);
        vec4 t_d2 = fetch_lookup_tags(n_d2);

        int nr_r1 = decode_u16(t_r1.r, t_r1.g);
        int nr_l1 = decode_u16(t_l1.r, t_l1.g);
        int nr_u1 = decode_u16(t_u1.r, t_u1.g);
        int nr_d1 = decode_u16(t_d1.r, t_d1.g);
        int nr_r2 = decode_u16(t_r2.r, t_r2.g);
        int nr_d2 = decode_u16(t_d2.r, t_d2.g);

        int sr_r1 = decode_u16(t_r1.b, t_r1.a);
        int sr_l1 = decode_u16(t_l1.b, t_l1.a);
        int sr_u1 = decode_u16(t_u1.b, t_u1.a);
        int sr_d1 = decode_u16(t_d1.b, t_d1.a);

        bool is_shore = (n_r1 == 0 || n_l1 == 0 || n_u1 == 0 || n_d1 == 0);
        bool diff_nation_1 = (n_r1 > 0 && nr_r1 != my_nation_tag)
                          || (n_l1 > 0 && nr_l1 != my_nation_tag)
                          || (n_u1 > 0 && nr_u1 != my_nation_tag)
                          || (n_d1 > 0 && nr_d1 != my_nation_tag);
        bool diff_nation_2 = (n_r2 > 0 && nr_r2 != my_nation_tag)
                          || (n_d2 > 0 && nr_d2 != my_nation_tag);
        bool diff_state = !diff_nation_1 && (
                             (n_r1 > 0 && sr_r1 != my_state_tag)
                          || (n_l1 > 0 && sr_l1 != my_state_tag)
                          || (n_u1 > 0 && sr_u1 != my_state_tag)
                          || (n_d1 > 0 && sr_d1 != my_state_tag));
        bool diff_prov = !diff_nation_1 && !diff_state && (
                             (n_r1 > 0 && n_r1 != prov_idx)
                          || (n_l1 > 0 && n_l1 != prov_idx)
                          || (n_u1 > 0 && n_u1 != prov_idx)
                          || (n_d1 > 0 && n_d1 != prov_idx));

        // Directional terrain hillshading from height_coast_tex
        vec2 duv = 1.8 / tex_size;
        float h_r = texture(height_coast_tex, UV + vec2(duv.x, 0.0)).r;
        float h_d = texture(height_coast_tex, UV + vec2(0.0, duv.y)).r;
        vec3 nrm = normalize(vec3((elev - h_r) * 12.0 * terrain_relief_strength, (elev - h_d) * 12.0 * terrain_relief_strength, 1.0));
        float light = clamp(dot(nrm, normalize(vec3(-0.58, -0.68, 0.45))), 0.30, 1.38);

        vec3 terrain_col = terrain_biome_color(terrain_id, elev, UV) * light;
        vec3 parchment_tint = mix(lens_col.rgb, lens_col.rgb * vec3(1.06, 0.99, 0.88), 0.32);
        parchment_tint *= (0.85 + grain * 0.22) * mix(0.84, 1.16, light * 0.78);

        // Political rim shadow inside nation borders (classic Paradox map depth)
        float inner_rim = (diff_nation_1 || diff_nation_2) ? 0.74 : 1.0;
        vec3 base_col;
        if (is_political_lens) {
            vec3 terrain_blended = mix(terrain_col, lens_col.rgb * (0.64 + light * 0.40), 0.28);
            base_col = mix(parchment_tint, terrain_blended, detail_mix * terrain_relief_strength) * inner_rim;
        } else {
            // Thematic data lenses keep strong legibility at all zooms
            base_col = mix(parchment_tint, mix(terrain_col, lens_col.rgb, 0.68), detail_mix * 0.65 * terrain_relief_strength) * inner_rim;
        }

        // Diagonal occupation stripes when province is held by wartime enemy
        if (state_flags.r > 0.5) {
            float stripe = mod(FRAGCOORD.x + FRAGCOORD.y, 11.0);
            if (stripe < 4.5) {
                vec3 occ_col = mix(vec3(0.75, 0.16, 0.14), vec3(0.95, 0.74, 0.25), state_flags.g * 0.45);
                base_col = mix(base_col, occ_col, 0.72);
            }
        }

        // State & Province selection + Hover feedback (now 100% matched by exact integer state_tag!)
        if (selected_state_tag > 0 && my_state_tag == selected_state_tag && prov_idx != selected_province_idx) {
            base_col = mix(base_col, vec3(0.96, 0.87, 0.58), 0.18);
        }
        if (state_flags.b > 0.5) {
            base_col = mix(base_col, vec3(0.98, 0.55, 0.18), 0.28);
        }
        if (prov_idx == selected_province_idx) {
            base_col = mix(base_col, vec3(1.0, 0.90, 0.52), 0.28);
        }
        if (prov_idx == hovered_province_idx) {
            base_col += vec3(0.11, 0.10, 0.07);
        }

        // Subtle shader perimeter micro-fallback controlled by shader_border_strength (batched ArrayMesh handles crisp vectors)
        if (shader_border_strength > 0.01) {
            if (is_shore) {
                base_col = mix(base_col, vec3(0.08, 0.07, 0.06), 0.72 * shader_border_strength);
            } else if (diff_nation_1) {
                base_col = mix(base_col, vec3(0.11, 0.08, 0.06), 0.45 * shader_border_strength);
            } else if (diff_state) {
                base_col = mix(base_col, vec3(0.18, 0.15, 0.12), 0.28 * shader_border_strength);
            } else if (diff_prov) {
                float prov_alpha = mix(0.12, 0.24, smoothstep(0.60, 1.50, camera_zoom));
                base_col = mix(base_col, vec3(0.22, 0.18, 0.14), prov_alpha * shader_border_strength);
            }
        }

        // Selected / Hovered province perimeter crisp outline
        if (prov_idx == selected_province_idx && (n_r1 != prov_idx || n_l1 != prov_idx || n_u1 != prov_idx || n_d1 != prov_idx)) {
            base_col = vec3(0.99, 0.85, 0.34);
        } else if (prov_idx == hovered_province_idx && (n_r1 != prov_idx || n_l1 != prov_idx || n_u1 != prov_idx || n_d1 != prov_idx)) {
            base_col = vec3(0.98, 0.93, 0.72);
        }

        COLOR = vec4(base_col, 1.0);
    }

    // Soft organic feathering along outer map boundaries so known sea/Veasperia blends into Uncharted Edge Fog
    float edge_jitter = (grain - 0.5) * 0.020;
    float fade_w = smoothstep(0.0, 0.032, UV.x + edge_jitter);
    float fade_n = smoothstep(0.0, 0.032, UV.y + edge_jitter);
    float fade_s = smoothstep(0.0, 0.032, (1.0 - UV.y) + edge_jitter);
    float fade_e = smoothstep(0.0, 0.022, (1.0 - UV.x) + edge_jitter * 0.6);
    float world_alpha = fade_w * fade_n * fade_s * fade_e;
    COLOR.a *= world_alpha;
}
