// Furniture and other players (a second draw call per chunk): their own colours, merged into the
// surfaces they touch, half-rendered streaming edges. See level.gdshaderinc.
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;

#define PROP
#include "res://shaders/level.gdshaderinc"
