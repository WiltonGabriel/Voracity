shader_type canvas_item;

// Batched 1-Draw-Call Shared-Arc Border Shader:
// Evaluates dynamic ownership & diplomatic borders directly from province_lookup_tex (LeftFaceId, RightFaceId)
// passed in UV so territorial transfers never require rebuilding border geometry!
uniform sampler2D province_lookup_tex : filter_nearest, repeat_disable;
uniform float camera_zoom = 1.0;
uniform int hovered_province_idx = 0;
uniform int selected_province_idx = 0;
uniform bool show_borders = true;
uniform bool show_province_borders = true;

varying float v_left_face;
varying float v_right_face;
varying float v_is_coast;
varying float v_edge_side;

int decode_u16(float lo, float hi) {
    return int(round(lo * 255.0)) + int(round(hi * 255.0)) * 256;
}

vec4 fetch_tags(int prov_idx) {
    int p = clamp(prov_idx, 0, 65535);
    return texelFetch(province_lookup_tex, ivec2(p & 255, (p >> 8) * 3 + 1), 0);
}

void vertex() {
    v_left_face = UV.x;
    v_right_face = UV.y;
    v_is_coast = (UV.x < 0.5 || UV.y < 0.5 || COLOR.b > 0.5) ? 1.0 : 0.0;
    v_edge_side = COLOR.a; // 0 = -normal, 1 = +normal

    float inv_zoom = clamp(1.0 / sqrt(max(0.35, camera_zoom)), 0.45, 1.45);
    float half_w = (v_is_coast > 0.5 ? 1.45 : 1.15) * inv_zoom;
    vec2 normal_off = (COLOR.rg - vec2(0.5)) * 4.0;
    VERTEX.xy += normal_off * half_w;
}

void fragment() {
    if (!show_borders) {
        discard;
    }

    int left_id = int(round(v_left_face));
    int right_id = int(round(v_right_face));
    bool highlight = (hovered_province_idx > 0 && (left_id == hovered_province_idx || right_id == hovered_province_idx))
                  || (selected_province_idx > 0 && (left_id == selected_province_idx || right_id == selected_province_idx));

    if (v_is_coast > 0.5) {
        vec3 col = highlight ? vec3(0.99, 0.87, 0.40) : vec3(0.08, 0.07, 0.06);
        COLOR = vec4(col, highlight ? 0.92 : 0.84);
    } else {
        vec4 tl = fetch_tags(left_id);
        vec4 tr = fetch_tags(right_id);

        int n_l = decode_u16(tl.r, tl.g);
        int n_r = decode_u16(tr.r, tr.g);
        int s_l = decode_u16(tl.b, tl.a);
        int s_r = decode_u16(tr.b, tr.a);

        float aa = 1.0 - abs(v_edge_side * 2.0 - 1.0);
        if (highlight) {
            bool is_sel = (left_id == selected_province_idx || right_id == selected_province_idx);
            COLOR = vec4(is_sel ? vec3(0.99, 0.85, 0.34) : vec3(0.98, 0.94, 0.72), smoothstep(0.0, 0.45, aa) * 0.95);
        } else if (n_l != n_r) {
            COLOR = vec4(0.10, 0.07, 0.05, smoothstep(0.0, 0.35, aa) * 0.92);
        } else if (s_l != s_r) {
            COLOR = vec4(0.18, 0.15, 0.12, smoothstep(0.0, 0.40, aa) * 0.65);
        } else if (show_province_borders && camera_zoom >= 0.72) {
            float prov_alpha = mix(0.16, 0.48, smoothstep(0.72, 1.80, camera_zoom));
            COLOR = vec4(0.22, 0.18, 0.14, smoothstep(0.0, 0.45, aa) * prov_alpha);
        } else {
            discard;
        }
    }
}
