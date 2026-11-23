// Port of the level/prop shader in web/src/shaders.ts. All surfaces are procedural. Lighting is
// evaluated analytically from the ceiling-panel lattice, with soft wall occlusion ray-marched
// through the chunk's occupancy texture. Included by level.gdshader and prop.gdshader (which
// defines PROP). Keep it in step with the GLSL: hash3 must match shared/gen.ts.
//
// Differences from the GLSL, all mechanical:
//   - per-frame values are global uniforms (project.godot [shader_globals]), set by Main.cs;
//   - Godot has no mutable globals, so the pixel footprint (gFw) is passed as `fw`;
//   - uEnt[4] is br_ent0..3 (global uniforms can't be arrays).

global uniform float br_time;
global uniform int br_seed;
global uniform vec3 br_fog_color;
global uniform float br_fog_density;
global uniform float br_quality;   // 0 low, 1 high
global uniform vec4 br_ent0;       // entities in the hunt: x, z, active
global uniform vec4 br_ent1;
global uniform vec4 br_ent2;
global uniform vec4 br_ent3;

uniform sampler2D occ_tex : filter_linear, repeat_disable;     // R occupancy, G ceiling height / 8, B chair rail
uniform sampler2D lights_tex : filter_nearest, repeat_disable; // R kind/3, G ceiling height / 8, B tint hash
uniform vec2 chunk_origin;
uniform vec2 light_base;

varying vec3 v_world;
varying vec3 v_normal;
varying vec3 v_col;
varying vec4 v_a; // kind, mode, u, v
varying vec4 v_b; // sizeU, sizeV, seed, dissolve

#define CELL 0.5
#define BORDER 16.0
#define TEXN 96.0
#define LP 3.0

float hash3(int x, int y, int z) {
	uint h = uint(x) * 374761393u + uint(y) * 668265263u + uint(z + br_seed * 1013) * 2246822519u;
	h = (h ^ (h >> 13u)) * 1274126177u;
	h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}
float vnoise(vec2 p, int s) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	ivec2 a = ivec2(i);
	float v00 = hash3(a.x, a.y, s);
	float v10 = hash3(a.x + 1, a.y, s);
	float v01 = hash3(a.x, a.y + 1, s);
	float v11 = hash3(a.x + 1, a.y + 1, s);
	return mix(mix(v00, v10, f.x), mix(v01, v11, f.x), f.y);
}
float fbm(vec2 p, int s) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 4; i++) {
		v += a * vnoise(p, s + i * 17);
		p = p * 2.03 + 7.1;
		a *= 0.5;
	}
	return v / 0.9375;
}

float occ(vec2 w) {
	vec2 c = (w - chunk_origin) / CELL + BORDER;
	return texture(occ_tex, c / TEXN).r;
}
float ceil_h(vec2 w) {
	vec2 c = floor((w - chunk_origin) / CELL + BORDER) + 0.5;
	return texture(occ_tex, c / TEXN).g * 8.0;
}
vec4 light_tex(ivec2 li) {
	return texelFetch(lights_tex, clamp(li - ivec2(light_base), ivec2(0), ivec2(15)), 0);
}

// Lights stutter and die around an entity: the darkness travels with it.
float ent_dim(vec2 lp, ivec2 li) {
	vec4 ents[4] = vec4[4](br_ent0, br_ent1, br_ent2, br_ent3);
	float k = 1.0;
	for (int i = 0; i < 4; i++) {
		if (ents[i].z < 0.5) continue;
		float d = distance(lp, ents[i].xy);
		if (d > 9.0) continue;
		float near = smoothstep(9.0, 3.5, d);
		float s = hash3(li.x, li.y, 90 + int(br_time * 9.0));
		k *= (s > near * 0.85 ? 1.0 : 0.04) * smoothstep(2.2, 3.6, d);
	}
	return k;
}

// stuttering fluorescent tube
float flicker(ivec2 li) {
	float s = hash3(li.x, li.y, 60) * 100.0;
	float t = br_time * (6.0 + 10.0 * hash3(li.x, li.y, 61)) + s;
	float a = step(0.35, vnoise(vec2(t, s), 62));
	float burst = step(0.6, vnoise(vec2(br_time * 0.4 + s, 3.0), 63)); // periods of calm vs. stutter
	return mix(1.0, a * 0.85 + 0.05, burst);
}

// Soft occlusion along the segment a -> b through the occupancy texture. Samples are spaced in
// metres, closer than the 0.5 m wall thickness, so no wall can fall between two samples.
float shadow_march(vec2 a, vec2 b) {
	vec2 d = b - a;
	float len = length(d);
	float step_l = br_quality > 0.5 ? 0.3 : 0.42;
	int n = int(clamp(ceil(len / step_l), 2.0, 24.0));
	float v = 1.0;
	for (int i = 0; i < 24; i++) {
		if (i >= n) break;
		v *= 1.0 - smoothstep(0.15, 0.45, occ(a + d * ((float(i) + 0.5) / float(n))));
		if (v < 0.01) return 0.0;
	}
	return v;
}

// --- materials (authored in sRGB, converted to linear) ---
// fw: world-space size of this pixel, used to fade out detail before it aliases
float detail(float size, float fw) { return 1.0 - smoothstep(size * 0.12, size * 0.45, fw); }
vec3 to_lin(vec3 c) { return pow(c, vec3(2.2)); }

vec3 wallpaper(float u, float y, float rail, float fw) {
	vec3 base = vec3(0.80, 0.745, 0.44);
	float col = u / 0.165;
	float fc = fract(col);
	float edge = min(fc, 1.0 - fc) * 0.165;
	float line = (1.0 - smoothstep(0.0, 0.006, edge)) * detail(0.012, fw);
	// faint stacked chevrons inside each column
	float cv = fract(y / 0.15 + abs(fc - 0.5) * 0.85);
	float chev = (1.0 - smoothstep(0.0, 0.08, abs(cv - 0.5) - 0.03)) * detail(0.03, fw);
	base *= 1.0 - 0.045 * line - 0.03 * chev * (1.0 - line);
	// paper roll seams
	float seam = (1.0 - smoothstep(0.0, 0.003, abs(fract(u / 0.53 + 0.13) - 0.5) * 0.53 - 0.262)) * detail(0.006, fw);
	base *= 1.0 - 0.06 * seam;
	float n = fbm(vec2(u * 0.6, y * 0.8), 5);
	base *= 0.9 + 0.17 * n;
	float fine = mix(0.5, vnoise(vec2(u, y) * 90.0, 8), detail(0.02, fw));
	base *= 0.97 + 0.06 * fine;
	// grime creeping up from the carpet, and damp stains running down from the ceiling
	float gr = fbm(vec2(u * 2.0, y * 1.5), 9);
	base = mix(base * vec3(0.74, 0.68, 0.5), base, smoothstep(0.0, 0.45, y - 0.15 * gr + 0.05));
	float st = fbm(vec2(u * 0.9, y * 0.18 + 3.0), 21);
	float stain = smoothstep(0.66, 0.72, st);
	float ring = (smoothstep(0.66, 0.68, st) - smoothstep(0.69, 0.72, st)) * detail(0.02, fw);
	base = mix(base, base * vec3(0.8, 0.7, 0.5), stain * 0.5);
	base *= 1.0 - ring * 0.1;
	if (rail > 0.5 && abs(y - 0.88) < 0.028) base = vec3(0.47, 0.37, 0.22) * (abs(y - 0.88) > 0.022 ? 0.72 : 1.0) * (0.92 + 0.12 * n);
	if (y < 0.075) base = vec3(0.5, 0.44, 0.28) * (0.9 + 0.2 * n) * (y < 0.072 ? 1.0 : 0.75);
	return to_lin(base);
}

vec3 carpet(vec2 p, float fw) {
	vec3 base = vec3(0.75, 0.69, 0.48);
	float fine = vnoise(p * 70.0, 3) * 0.55 + vnoise(p * 160.0, 4) * 0.45;
	base *= 1.0 + 0.16 * (fine - 0.5) * detail(0.012, fw);
	float mid = fbm(p * 0.7, 7);
	base *= 0.9 + 0.18 * mid;
	float damp = smoothstep(0.58, 0.74, fbm(p * 0.16 + 11.0, 13));
	base = mix(base, base * vec3(0.7, 0.64, 0.5), damp * 0.8);
	// faint walk-worn tracks
	float track = smoothstep(0.62, 0.8, fbm(vec2(p.x * 0.08, p.y * 0.9), 31));
	base *= 1.0 - 0.05 * track;
	return to_lin(base);
}

// 2 x 4 ft acoustic tiles; the grid is offset so each light panel fills exactly one tile
vec3 ceiling_tile(vec2 p, out float is_grid, float fw) {
	vec2 sz = vec2(1.2, 0.6);
	vec2 t = (p - vec2(0.3, 0.0)) / sz;
	vec2 f = fract(t);
	vec2 ed = min(f, 1.0 - f) * sz;
	float e = min(ed.x, ed.y);
	float gw = max(0.012, fw * 0.7);
	is_grid = (1.0 - smoothstep(gw * 0.6, gw, e)) * mix(0.5, 1.0, detail(0.03, fw));
	vec3 base = vec3(0.87, 0.83, 0.64);
	float pits = vnoise(p * 110.0, 5);
	base *= 1.0 - 0.1 * smoothstep(0.6, 0.85, pits) * detail(0.01, fw);
	base *= 0.95 + 0.07 * vnoise(p * 9.0, 6);
	ivec2 ti = ivec2(floor(t));
	float hs = hash3(ti.x, ti.y, 70);
	if (hs < 0.06) {
		vec2 c = vec2(hash3(ti.x, ti.y, 71), hash3(ti.x, ti.y, 72)) * 0.6 + 0.2;
		float r = length((f - c) * sz) / 0.6 + 0.15 * vnoise(p * 6.0, 73);
		float stain = 1.0 - smoothstep(0.14, 0.26, r);
		base = mix(base, base * vec3(0.8, 0.68, 0.46), stain);
		base *= 1.0 - 0.15 * (smoothstep(0.2, 0.25, r) - smoothstep(0.25, 0.29, r));
	} else if (hs > 0.997) {
		base = vec3(0.05, 0.045, 0.035); // missing tile: darkness above
	}
	base = mix(base, vec3(0.74, 0.72, 0.62), is_grid);
	return to_lin(base);
}

vec3 light_tint(float h) {
	return mix(vec3(1.0, 0.96, 0.84), vec3(0.94, 1.0, 0.86), h);
}

// ceiling panel: sets emission (linear HDR), and the albedo override when inside a panel
bool panel(vec3 p, inout vec3 alb, out vec3 emis, float fw) {
	emis = vec3(0.0);
	ivec2 li = ivec2(floor(p.xz / LP));
	vec2 q = p.xz - (vec2(li) + 0.5) * LP;
	if (abs(q.x) > 0.6 || abs(q.y) > 0.3) return false;
	vec4 L = light_tex(li);
	float k = floor(L.r * 3.0 + 0.5);
	if (k < 0.5 || abs(p.y - L.g * 8.0) > 0.08) return false;
	float fr = 0.035;
	if (abs(q.x) > 0.6 - fr || abs(q.y) > 0.3 - fr) {
		alb = to_lin(vec3(0.8, 0.79, 0.72));
		return true;
	}
	// prismatic diffuser over three tubes
	vec2 g = fract(q * 60.0);
	float prism = mix(1.0, 0.94 + 0.06 * smoothstep(0.25, 0.5, abs(g.x - 0.5)), detail(0.006, fw));
	float tubes = 0.0;
	for (int i = -1; i <= 1; i++) tubes += exp(-pow((q.y - float(i) * 0.085) / 0.035, 2.0));
	if (k < 1.5) { // dead panel: grey, tubes just visible
		alb = to_lin(vec3(0.6, 0.6, 0.56)) * (0.88 + 0.15 * tubes) * prism;
		return true;
	}
	float inten = (k > 2.5 ? flicker(li) : 1.0) * ent_dim((vec2(li) + 0.5) * LP, li);
	float ends = mix(0.75, 1.0, smoothstep(0.58, 0.48, abs(q.x)));
	alb = vec3(0.0);
	emis = light_tint(L.b) * (3.2 + 0.9 * tubes) * ends * prism * inten + vec3(0.08) * inten;
	return true;
}

// direct light from the 4x4 nearest panels, windowed so the set can change without seams
vec3 lighting(vec3 p, vec3 n, float kind, out float on_sum) {
	vec3 sum = vec3(0.0);
	on_sum = 0.0;
	ivec2 c = ivec2(floor(p.xz / LP - 0.5));
	vec2 from = p.xz + n.xz * 0.26;
	for (int dz = -1; dz <= 2; dz++)
	for (int dx = -1; dx <= 2; dx++) {
		ivec2 li = c + ivec2(dx, dz);
		vec4 L = light_tex(li);
		float k = floor(L.r * 3.0 + 0.5);
		if (k < 1.5) continue;
		vec3 lp = vec3((float(li.x) + 0.5) * LP, L.g * 8.0 - 0.03, (float(li.y) + 0.5) * LP);
		vec2 hd = abs(lp.xz - p.xz);
		float win = smoothstep(1.5 * LP, 0.9 * LP, max(hd.x, hd.y));
		if (win <= 0.0) continue;
		float inten = (k > 2.5 ? flicker(li) : 1.0) * ent_dim(lp.xz, li);
		vec3 d = lp - p;
		float dist = length(d);
		vec3 l = d / dist;
		float nl = kind > 0.5 && kind < 1.5 ? 0.0 : clamp(dot(n, l) * 0.85 + 0.15, 0.0, 1.0);
		float down = pow(clamp(l.y, 0.0, 1.0), 0.7) * 0.85 + 0.15;
		float att = 1.0 / (1.0 + dist * dist * 0.42);
		if (att * win * inten < 0.004) continue; // too faint to bother tracing
		float vis = shadow_march(from, lp.xz);
		sum += light_tint(L.b) * inten * nl * down * att * vis * win;
		on_sum += inten * vis * win * (1.0 / (1.0 + dist * 0.25));
	}
	return sum * 2.4;
}

void vertex() {
	vec3 p = VERTEX;
#ifdef PROP
	// half-rendered props occasionally snap to a coarse vertex grid, like geometry still streaming in
	float g = fract(br_time * 0.37 + CUSTOM1.z * 7.31);
	if (CUSTOM0.y > 0.5 && CUSTOM0.y < 1.5 && g > 0.93) p = floor(p / 0.12 + 0.5) * 0.12;
	VERTEX = p;
#endif
	v_world = (MODEL_MATRIX * vec4(p, 1.0)).xyz;
	v_normal = normalize(mat3(MODEL_MATRIX) * NORMAL);
	v_col = COLOR.rgb;
	v_a = CUSTOM0;
	v_b = CUSTOM1;
}

void fragment() {
	vec3 p = v_world;
	vec3 n = normalize(v_normal);
#ifdef PROP
	if (!FRONT_FACING) n = -n;
#endif
	float kind = v_a.x;
	float fw = length(fwidth(p));
	float rail = texture(occ_tex, (floor((p.xz + n.xz * 0.26 - chunk_origin) / CELL + BORDER) + 0.5) / TEXN).b;
	vec3 alb;
	vec3 emis = vec3(0.0);
	float wire = 0.0; // half-rendered wireframe edges glow faintly, but only where the lights are on
	float ao = 1.0;
	float H = ceil_h(p.xz + n.xz * 0.26);

#ifndef PROP
	if (kind < 0.5) {
		alb = carpet(p.xz, fw);
		float a = occ(p.xz + vec2(0.3, 0.0)) + occ(p.xz - vec2(0.3, 0.0)) + occ(p.xz + vec2(0.0, 0.3)) + occ(p.xz - vec2(0.0, 0.3));
		float b = occ(p.xz + vec2(0.12, 0.0)) + occ(p.xz - vec2(0.12, 0.0)) + occ(p.xz + vec2(0.0, 0.12)) + occ(p.xz - vec2(0.0, 0.12));
		ao = 1.0 - 0.32 * smoothstep(0.0, 1.0, a * 0.5) - 0.3 * smoothstep(0.0, 1.0, b * 0.5);
	} else if (kind < 1.5) {
		float grid;
		alb = ceiling_tile(p.xz, grid, fw);
		panel(p, alb, emis, fw);
		float a = occ(p.xz + vec2(0.3, 0.0)) + occ(p.xz - vec2(0.3, 0.0)) + occ(p.xz + vec2(0.0, 0.3)) + occ(p.xz - vec2(0.0, 0.3));
		ao = 1.0 - 0.35 * smoothstep(0.0, 1.0, a * 0.5);
	} else {
		float u = abs(n.x) > 0.5 ? p.z * sign(n.x) : -p.x * sign(n.z);
		alb = wallpaper(u, p.y, rail, fw);
		vec3 t = vec3(-n.z, 0.0, n.x);
		vec2 o = p.xz + n.xz * 0.26;
		float corner = occ(o + t.xz * 0.3) + occ(o - t.xz * 0.3);
		ao = mix(0.62, 1.0, smoothstep(0.0, 0.45, p.y)) * mix(0.72, 1.0, smoothstep(0.0, 0.4, H - p.y));
		ao *= 1.0 - 0.35 * smoothstep(0.2, 1.0, corner);
	}
#else
	// props: own colour, then merged into whatever surface they intersect
	alb = to_lin(v_col);
	float mode = v_a.y;
	float seed = v_b.z;
	vec2 fuv = v_a.zw;
	vec2 fsz = v_b.xy;
	float edge_d = min(min(fuv.x, fsz.x - fuv.x), min(fuv.y, fsz.y - fuv.y));
	float ew = max(0.008, fw * 1.2);
	float edge = 1.0 - smoothstep(ew * 0.6, ew, edge_d);
	alb *= 1.0 - 0.25 * edge;
	float nz = vnoise(p.xz * 6.0 + p.y * 3.0, 90);
	if (mode > 3.5) {
		emis = to_lin(v_col) * (2.5 + 1.5 * edge);
		alb *= 0.2;
	} else if (mode > 2.5) {
		alb = wallpaper(fuv.x * 0.6 + seed * 10.0, fuv.y * 1.7, 0.0, fw) * 0.95;
	}
	if (mode > 0.5 && mode < 1.5) {
		float T = 0.3 + 0.45 * fract(seed * 13.7) + 0.06 * sin(br_time * 0.25 + seed * 40.0);
		float d = v_b.w;
		if (d > T + 0.08) {
			if (edge < 0.5) discard;
			alb = to_lin(vec3(0.9, 0.86, 0.7)) * 0.5;
			wire = 1.0;
		} else if (d > T) {
			float th = (d - T) / 0.08;
			float r = hash3(int(FRAGCOORD.x), int(FRAGCOORD.y), int(br_time * 12.0));
			if (r < th) discard;
			alb = mix(alb, to_lin(vec3(0.75, 0.73, 0.68)), th);
		} else if (d > T - 0.25) {
			// untextured "clay" band just below the streaming edge
			alb = mix(alb, to_lin(vec3(0.62, 0.61, 0.58)), smoothstep(T - 0.25, T - 0.05, d));
		}
	}
	float reach = mode > 1.5 && mode < 2.5 ? 0.6 : 0.16;
	float mF = smoothstep(reach, 0.0, p.y + (nz - 0.5) * reach * 0.8);
	float mC = smoothstep(reach, 0.0, (H - p.y) + (nz - 0.5) * reach * 0.8);
	float wo = occ(p.xz);
	float mW = mode > 1.5 && mode < 2.5 ? smoothstep(0.1, 0.4, wo + (nz - 0.5) * 0.25) : smoothstep(0.32, 0.5, wo + (nz - 0.5) * 0.12);
	float grid;
	vec3 wall_alb = wallpaper(p.x + p.z, p.y, rail, fw);
	if (mW > max(mF, mC)) alb = mix(alb, wall_alb, mW);
	else if (mF > mC) alb = mix(alb, carpet(p.xz, fw), mF);
	else alb = mix(alb, ceiling_tile(p.xz, grid, fw), mC);
	ao = mix(0.75, 1.0, smoothstep(0.0, 0.3, p.y));
#endif

	float on_sum;
	vec3 direct = lighting(p, n, kind, on_sum);
	float on = clamp(on_sum / 3.0, 0.0, 1.0);
	// bounce: the whole place is soaked in diffuse light where lamps are on
	vec3 amb = mix(vec3(0.04, 0.034, 0.018), vec3(0.34, 0.31, 0.19), on);
	// the ceiling is lit by the bright carpet below
	if (kind > 0.5 && kind < 1.5) amb *= 1.75 + 0.5 * on;
	vec3 col = alb * (direct + amb) * ao + emis + wire * vec3(0.09, 0.085, 0.06) * on;

	float dist = length(p - CAMERA_POSITION_WORLD);
	float fog = 1.0 - exp(-dist * br_fog_density);
	vec3 fog_c = br_fog_color * mix(0.12, 1.0, on);
	col = mix(col, fog_c, fog);
	ALBEDO = col;
}
