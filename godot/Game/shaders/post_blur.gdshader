// Separable 9-tap gaussian in 5 linear taps (web/src/post.ts blurFrag). dir = 0 is a plain copy
// (used to downsample to the second bloom level).
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D t_src : filter_linear, repeat_disable;
uniform vec2 dir;

void fragment() {
	vec3 c = texture(t_src, UV).rgb * 0.227027;
	c += texture(t_src, UV + dir * 1.3846).rgb * 0.316216;
	c += texture(t_src, UV - dir * 1.3846).rgb * 0.316216;
	c += texture(t_src, UV + dir * 3.2308).rgb * 0.070270;
	c += texture(t_src, UV - dir * 3.2308).rgb * 0.070270;
	COLOR = vec4(c, 1.0);
}
