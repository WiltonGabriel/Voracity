shader_type canvas_item;

// OpenVic / CK3 / Victoria 3 inspired hybrid grand-strategy map shader:
// - Exact integer province lookup via province_data_tex (RG = 16-bit province ID, B = terrain ID)
// - Precomputed heightmap elevation, coast distance & parchment grain via height_coast_tex
// - Dynamic 512x2 lookup palette (row 0 = lens RGB, row 1 = [nationTag, stateTag, occupiedFlag, occupierHue])
uniform sampler2D province_data_tex : filter_nearest, repeat_disable;
uniform sampler2D height_coast_tex : filter_linear, repeat_disable;
uniform sampler2D province_lookup_tex : filter_nearest, repeat_disable;

uniform float camera_zoom = 1.0;
uniform float time_seconds = 0.0;
uniform int hovered_province_idx = 0;
uniform int selected_province_idx = 0;
uniform int selected_state_tag = 0;
uniform bool is_political_lens = true;

int decode_prov(vec4 pdata) {
    return int(round(pdata.r * 255.0)) + int(round(pdata.g * 255.0)) * 256;
}

vec4 fetch_lookup_color(int prov_idx) {
    return texelFetch(province_lookup_tex, ivec2(clamp(prov_idx, 0, 511), 0), 0);
}

vec4 fetch_lookup_meta(int prov_idx) {
    return texelFetch(province_lookup_tex, ivec2(clamp(prov_idx, 0, 511), 1), 0);
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
        // Oceanic cartography: bathymetric shelf + historical parchment sea graticule
        float shelf = smoothstep(0.05, 0.45, elev);
        vec3 deep_sea = vec3(0.075, 0.125, 0.185);
        vec3 shallow_sea = vec3(0.145, 0.245, 0.305);
        vec3 sea_col = mix(deep_sea, shallow_sea, shelf * 0.65 + coast_prox * 0.55);

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
    } else {
        vec4 lens_col = fetch_lookup_color(prov_idx);
        vec4 meta = fetch_lookup_meta(prov_idx);

        // Neighbor probes for 3-tier cartographic borders + shoreline ink
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

        vec4 m_r1 = fetch_lookup_meta(n_r1);
        vec4 m_l1 = fetch_lookup_meta(n_l1);
        vec4 m_u1 = fetch_lookup_meta(n_u1);
        vec4 m_d1 = fetch_lookup_meta(n_d1);
        vec4 m_r2 = fetch_lookup_meta(n_r2);
        vec4 m_d2 = fetch_lookup_meta(n_d2);

        bool is_shore = (n_r1 == 0 || n_l1 == 0 || n_u1 == 0 || n_d1 == 0);
        bool diff_nation_1 = (n_r1 > 0 && abs(m_r1.r - meta.r) > 0.003)
                          || (n_l1 > 0 && abs(m_l1.r - meta.r) > 0.003)
                          || (n_u1 > 0 && abs(m_u1.r - meta.r) > 0.003)
                          || (n_d1 > 0 && abs(m_d1.r - meta.r) > 0.003);
        bool diff_nation_2 = (n_r2 > 0 && abs(m_r2.r - meta.r) > 0.003)
                          || (n_d2 > 0 && abs(m_d2.r - meta.r) > 0.003);
        bool diff_state = !diff_nation_1 && (
                             (n_r1 > 0 && abs(m_r1.g - meta.g) > 0.003)
                          || (n_l1 > 0 && abs(m_l1.g - meta.g) > 0.003)
                          || (n_u1 > 0 && abs(m_u1.g - meta.g) > 0.003)
                          || (n_d1 > 0 && abs(m_d1.g - meta.g) > 0.003));
        bool diff_prov = !diff_nation_1 && !diff_state && (
                             (n_r1 > 0 && n_r1 != prov_idx)
                          || (n_l1 > 0 && n_l1 != prov_idx)
                          || (n_u1 > 0 && n_u1 != prov_idx)
                          || (n_d1 > 0 && n_d1 != prov_idx));

        // Directional terrain hillshading from height_coast_tex
        vec2 duv = 1.8 / tex_size;
        float h_r = texture(height_coast_tex, UV + vec2(duv.x, 0.0)).r;
        float h_d = texture(height_coast_tex, UV + vec2(0.0, duv.y)).r;
        vec3 nrm = normalize(vec3((elev - h_r) * 12.0, (elev - h_d) * 12.0, 1.0));
        float light = clamp(dot(nrm, normalize(vec3(-0.58, -0.68, 0.45))), 0.30, 1.38);

        vec3 terrain_col = terrain_biome_color(terrain_id, elev, UV) * light;
        vec3 parchment_tint = mix(lens_col.rgb, lens_col.rgb * vec3(1.06, 0.99, 0.88), 0.32);
        parchment_tint *= (0.85 + grain * 0.22) * mix(0.84, 1.16, light * 0.78);

        // Political rim shadow inside nation borders (classic Paradox map depth)
        float inner_rim = (diff_nation_1 || diff_nation_2) ? 0.72 : 1.0;
        vec3 base_col;
        if (is_political_lens) {
            vec3 terrain_blended = mix(terrain_col, lens_col.rgb * (0.64 + light * 0.40), 0.28);
            base_col = mix(parchment_tint, terrain_blended, detail_mix) * inner_rim;
        } else {
            // Thematic data lenses (Economic, Ancestry, SoL, Diplomacy) keep strong legibility at all zooms
            base_col = mix(parchment_tint, mix(terrain_col, lens_col.rgb, 0.68), detail_mix * 0.65) * inner_rim;
        }

        // Diagonal occupation stripes when province is held by wartime enemy
        if (meta.b > 0.5) {
            float stripe = mod(FRAGCOORD.x + FRAGCOORD.y, 11.0);
            if (stripe < 4.5) {
                vec3 occ_col = mix(vec3(0.75, 0.16, 0.14), vec3(0.95, 0.74, 0.25), meta.a * 0.45);
                base_col = mix(base_col, occ_col, 0.72);
            }
        }

        // State & Province selection + Hover feedback
        int my_state_tag = int(round(meta.g * 255.0));
        if (selected_state_tag > 0 && my_state_tag == selected_state_tag && prov_idx != selected_province_idx) {
            base_col = mix(base_col, vec3(0.96, 0.87, 0.58), 0.14);
        }
        if (prov_idx == selected_province_idx) {
            base_col = mix(base_col, vec3(1.0, 0.90, 0.52), 0.26);
        }
        if (prov_idx == hovered_province_idx) {
            base_col += vec3(0.11, 0.10, 0.07);
        }

        // Crisp 3-tier cartographic borders + dark shoreline
        if (is_shore) {
            base_col = mix(base_col, vec3(0.08, 0.07, 0.06), 0.88);
        } else if (diff_nation_1) {
            base_col = mix(base_col, vec3(0.11, 0.08, 0.06), 0.92);
        } else if (diff_nation_2) {
            base_col = mix(base_col, lens_col.rgb * 0.35 + vec3(0.05), 0.65);
        } else if (diff_state) {
            base_col = mix(base_col, vec3(0.18, 0.15, 0.12), 0.62);
        } else if (diff_prov) {
            float prov_alpha = mix(0.24, 0.48, smoothstep(0.60, 1.50, camera_zoom));
            base_col = mix(base_col, vec3(0.22, 0.18, 0.14), prov_alpha);
        }

        // Selected province crisp gold perimeter
        if (prov_idx == selected_province_idx && (n_r1 != prov_idx || n_l1 != prov_idx || n_u1 != prov_idx || n_d1 != prov_idx)) {
            base_col = vec3(0.99, 0.85, 0.34);
        } else if (prov_idx == hovered_province_idx && (n_r1 != prov_idx || n_l1 != prov_idx || n_u1 != prov_idx || n_d1 != prov_idx)) {
            base_col = vec3(0.98, 0.93, 0.72);
        }

        COLOR = vec4(base_col, 1.0);
    }
}
