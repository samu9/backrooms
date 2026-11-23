// The camcorder (web/src/post.ts finalFrag): barrel, tape jitter and tearing, chromatic aberration,
// composite chroma bleed, bloom, ACES, the yellow grade, the entity's view, vignette, grain,
// scanlines, gamma. Drawn on the root viewport, which shows it as is (sRGB, no conversion).
//
// The GLSL works in GL conventions (uv and gl_FragCoord with y up); Godot's UV and FRAGCOORD have
// y down. `uv` and `fc` below are converted to GL ones and every lookup converts back, so the
// tape effects roll the same way as on the web.
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D t_scene : filter_linear, repeat_disable;
uniform sampler2D t_b1 : filter_linear, repeat_disable;
uniform sampler2D t_b2 : filter_linear, repeat_disable;
uniform vec2 res;             // scene render size (pixels)
uniform float time;
uniform float vhs = 1.0;      // camcorder effects on/off (V)
uniform float exposure = 1.0;
uniform float fade = 1.0;
uniform float dread = 0.0;    // 0..1: an entity is near, the tape starts to fail
uniform float hunter = 0.0;   // 0..1: we are an entity: see the place drained and red
uniform float glitch = 0.0;   // 0..1: just got caught

float h21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}
vec3 aces(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }
vec3 tex(sampler2D t, vec2 glUv) { return texture(t, vec2(glUv.x, 1.0 - glUv.y)).rgb; }
vec3 scene_ca(vec2 uv, float amt) {
	vec2 d = uv - 0.5;
	vec2 o = d * dot(d, d) * amt;
	return vec3(tex(t_scene, uv + o).r, tex(t_scene, uv).g, tex(t_scene, uv - o).b);
}

void fragment() {
	vec2 uv = vec2(UV.x, 1.0 - UV.y);
	vec2 screen = 1.0 / SCREEN_PIXEL_SIZE;
	vec2 fc = vec2(FRAGCOORD.x, screen.y - FRAGCOORD.y);
	vec2 d = uv - 0.5;
	uv = 0.5 + d * (1.0 - 0.02 * vhs + 0.04 * vhs * dot(d, d)); // slight camcorder barrel
	// tape: per-line horizontal jitter and a slow tracking wobble
	float line = floor(uv.y * res.y / 2.0);
	float tf = floor(time * 30.0);
	float fail = max(dread * dread, glitch);
	uv.x += (h21(vec2(line, tf)) - 0.5) * (0.0009 * vhs + 0.006 * fail);
	// tearing bands that roll through the frame as it closes in
	float tear = step(1.0 - 0.2 * fail, h21(vec2(floor(uv.y * 24.0), tf))) * fail;
	uv.x += tear * (h21(vec2(tf, floor(uv.y * 24.0))) - 0.5) * 0.05;
	uv.y += glitch * (h21(vec2(tf, 1.0)) - 0.5) * 0.05;
	uv.x += sin(uv.y * 9.0 + time * 1.3) * 0.0006 * vhs;
	float band_on = step(0.8, h21(vec2(floor(time / 6.0), 7.0)));
	float band = smoothstep(0.025, 0.0, abs(uv.y - fract(time / 6.0) * 1.3 + 0.15)) * vhs * band_on;
	uv.x += band * (h21(vec2(line, tf + 3.0)) - 0.5) * 0.01;

	vec3 c = scene_ca(uv, 0.03 * vhs + 0.004 + 0.12 * fail);
	// chroma bleed: luma sharp, chroma smeared to the right like composite video
	vec2 px = vec2(1.0 / res.x, 0.0);
	vec3 sm = (tex(t_scene, uv - px * 2.0) + tex(t_scene, uv - px * 5.0) + tex(t_scene, uv - px * 9.0) + c) * 0.25;
	float Y = dot(c, vec3(0.299, 0.587, 0.114));
	float Ys = dot(sm, vec3(0.299, 0.587, 0.114));
	c = mix(c, sm - Ys + Y, 0.65 * vhs);

	vec3 bloom = tex(t_b1, uv) * 0.7 + tex(t_b2, uv) * 1.1;
	c += bloom * 0.35;
	c *= exposure;
	c = aces(c);
	// grade: warm/yellow cast, crushed blue, lifted blacks, softened whites
	c = pow(c, vec3(0.95, 0.97, 1.2));
	float l = dot(c, vec3(0.299, 0.587, 0.114));
	c = mix(vec3(l), c, 0.98);
	c = c * vec3(1.02, 1.0, 0.88) * 0.95 + vec3(0.025, 0.022, 0.01);
	// the entity's view: drained, rusty, darker at the edges
	float lh = dot(c, vec3(0.299, 0.587, 0.114));
	c = mix(c, vec3(lh * 1.15, lh * 0.82, lh * 0.7), 0.75 * hunter);
	c = mix(c, vec3(dot(c, vec3(0.333))), 0.35 * fail);
	float v = smoothstep(0.95 - 0.25 * max(hunter, dread), 0.25, length(d * vec2(1.0, 0.85)));
	c *= mix(0.62, 1.0, v);
	// grain (stronger in the darks) and faint scanlines
	float g = h21(fc + fract(time * 7.31) * 100.0) - 0.5;
	c += g * (mix(0.045, 0.025, l) + 0.14 * fail);
	c += tear * 0.12;
	c *= 1.0 - 0.035 * vhs * step(0.5, fract(fc.y * 0.5));
	c += band * 0.03;
	c = pow(max(c, 0.0), vec3(1.0 / 2.2));
	c *= fade;
	COLOR = vec4(c, 1.0);
}
