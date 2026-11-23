// Bloom bright pass (web/src/post.ts brightFrag): 4 taps of the HDR scene, keep what is very bright.
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D t_src : filter_linear, repeat_disable;
uniform vec2 texel;

void fragment() {
	vec3 c = vec3(0.0);
	c += texture(t_src, UV + texel * vec2(-1.0, -1.0)).rgb;
	c += texture(t_src, UV + texel * vec2(1.0, -1.0)).rgb;
	c += texture(t_src, UV + texel * vec2(-1.0, 1.0)).rgb;
	c += texture(t_src, UV + texel * vec2(1.0, 1.0)).rgb;
	c *= 0.25;
	float l = dot(c, vec3(0.299, 0.587, 0.114));
	COLOR = vec4(c * smoothstep(1.2, 3.5, l), 1.0);
}
