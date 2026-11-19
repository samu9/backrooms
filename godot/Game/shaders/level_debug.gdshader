// Placeholder look for M2: flat colours per surface kind, a 0.5 m grid so motion reads, soft
// contact darkening from the chunk's occupancy texture, and distance fog. M3 replaces it with the
// port of the web level shader (same vertex attributes and textures).
shader_type spatial;
render_mode unshaded, cull_back;

uniform sampler2D occ : filter_linear, repeat_disable; // R wall, G ceiling height / 8, B chair rail
uniform vec2 chunk_origin;  // world x, z of the chunk corner
uniform vec3 fog_color = vec3(0.25, 0.21, 0.09);
uniform float fog_density = 0.022;

varying vec3 world_pos;
varying flat float kind; // 0 floor, 1 ceiling, 2 wall (CUSTOM0 = aA: kind, mode, u, v)

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	kind = CUSTOM0.x;
}

float wall_near(vec2 xz) {
	// occupancy texel centres: the chunk's 64 cells start 16 texels in, 96 texels across
	vec2 uv = ((xz - chunk_origin) / 0.5 + 16.0) / 96.0;
	return texture(occ, uv).r;
}

void fragment() {
	vec3 base = kind < 0.5 ? vec3(0.32, 0.24, 0.07) : kind < 1.5 ? vec3(0.62, 0.58, 0.42) : vec3(0.72, 0.6, 0.22);
	vec3 n = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	float light = 0.62 + 0.38 * dot(n, normalize(vec3(0.35, 0.8, 0.5)));
	// grid on floors and ceilings, seams every 0.5 m along walls
	vec2 g = kind < 1.5 ? world_pos.xz : vec2(world_pos.x + world_pos.z, world_pos.y);
	vec2 d = abs(fract(g * 2.0) - 0.5) / fwidth(g * 2.0);
	float line = 1.0 - clamp(min(d.x, d.y) - 0.5, 0.0, 1.0);
	// darken floor and ceiling next to walls (the linear filter of the occupancy gives a soft edge)
	float contact = kind < 1.5 ? wall_near(world_pos.xz) : 0.0;
	vec3 c = base * light * (1.0 - 0.18 * line) * (1.0 - 0.45 * contact);
	float dist = length(VERTEX);
	ALBEDO = mix(fog_color, c, exp(-dist * fog_density));
}
