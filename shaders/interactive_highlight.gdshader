shader_type canvas_item;

// This game's point-and-click objects are already painted into the background.
// Their overlay textures supply a mask only: never redraw unlit image copies
// over the scene when the pointer is elsewhere.
uniform vec4 modulate_color : source_color = vec4(0.0);

void fragment() {
    float mask_alpha = texture(TEXTURE, UV).a;
    COLOR = vec4(modulate_color.rgb, mask_alpha * modulate_color.a);
}
