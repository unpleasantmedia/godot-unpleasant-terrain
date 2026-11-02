shader_type spatial;
render_mode blend_mix,depth_draw_opaque,cull_back,diffuse_burley,specular_schlick_ggx,skip_vertex_transform;

/* The terrain's ground, as drawn by TerrainNode (addons/unpleasant_terrain/Core/TerrainNode.cs): ported from
 * the shader used under Terrain3D, with the same textures, auto-shader and colours.
 * Only the vertex stage is new: the terrain is a clipmap of square rings round the camera, each
 * twice as coarse as the one inside it, and each ring's vertices morph into the next one's towards
 * its outer edge, so there are no seams or popping. The private uniforms (named as Terrain3D's,
 * so the fragment stage is unchanged) are set by the terrain node, not saved with the material.
 *
 * The fragment stage started as Terrain3D's lightweight example shader: Base/Over/Blend/Holes/Colormap. */

// Defined Constants
#define SKIP_PASS 0
#define VERTEX_PASS 1
#define FRAGMENT_PASS 2
#define COLOR_MAP vec4(1.0, 1.0, 1.0, 0.5)
#define DIV_255 0.003921568627450 // 1. / 255.

// Inline Functions
#define DECODE_BLEND(control) float(control >> 14u & 0xFFu) * DIV_255
#define DECODE_AUTO(control) bool(control & 0x1u)
#define DECODE_BASE(control) int(control >> 27u & 0x1Fu)
#define DECODE_OVER(control) int(control >> 22u & 0x1Fu)
#define DECODE_HOLE(control) bool(control >>2u & 0x1u)

#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
    #define fma(a, b, c) ((a) * (b) + (c))
    #define dFdxCoarse(a) dFdx(a)
    #define dFdyCoarse(a) dFdy(a)
#endif

// Private uniforms, set by the terrain node
uniform vec3 _camera_pos = vec3(0.f);
uniform float _ring_cells = 80.0; // how many of its own cells a ring reaches out from its middle
uniform uint _background_mode = 0u; // no ground outside the regions
uniform float _vertex_spacing = 1.0;
uniform float _vertex_density = 1.0; // = 1./_vertex_spacing
uniform float _region_size = 1024.0;
uniform float _region_texel_size = 0.0009765625; // = 1./region_size
uniform int _region_map_size = 32;
uniform int _region_map[1024];
uniform vec2 _region_locations[1024];
uniform float _texture_normal_depth_array[32];
uniform float _texture_ao_strength_array[32];
uniform float _texture_roughness_mod_array[32];
uniform float _texture_uv_scale_array[32];
uniform vec4 _texture_color_array[32];
uniform highp sampler2DArray _height_maps : repeat_disable;
uniform highp sampler2DArray _control_maps : repeat_disable;
uniform highp sampler2DArray _color_maps : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform highp sampler2DArray _texture_array_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform highp sampler2DArray _texture_array_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;

// Public uniforms
uniform bool enable_texturing = true;
uniform float blend_sharpness : hint_range(0, 1) = 0.5;
uniform bool flat_terrain_normals = false;
// Autoshader: which texture is which, by its place in the terrain's asset list.
group_uniforms auto_textures;
uniform int sand_texture : hint_range(0, 31) = 2;
uniform int grass_texture : hint_range(0, 31) = 0;
uniform int dirt_texture : hint_range(0, 31) = 3;
uniform int stone_texture : hint_range(0, 31) = 1;
group_uniforms;
// Autoshader: sand, then grass, then dirt, going up; each up to a height, in metres above sea level.
group_uniforms auto_heights;
uniform float sand_height : hint_range(-10, 50, 0.1) = 3.5;
uniform float grass_height : hint_range(0, 600, 1) = 200.0;
uniform float height_blend : hint_range(0, 50, 0.1) = 1.5;
uniform float grass_to_dirt_blend : hint_range(0, 200, 1) = 30.0;
group_uniforms;
// Autoshader: dirt from one slope, stone from a steeper one, in degrees.
group_uniforms auto_slopes;
uniform float dirt_slope : hint_range(0, 90, 0.5) = 26.0;
uniform float stone_slope : hint_range(0, 90, 0.5) = 38.0;
uniform float slope_blend : hint_range(0, 30, 0.5) = 4.0;
group_uniforms;
// Autoshader: how ragged the edges between them are, and how big the rags.
group_uniforms auto_edges;
uniform float edge_height_noise : hint_range(0, 20, 0.1) = 1.5;
uniform float edge_slope_noise : hint_range(0, 20, 0.1) = 4.0;
uniform float edge_noise_size : hint_range(1, 200, 1) = 25.0;
group_uniforms;
// Distance tiling: further off, each texture blends into itself tiled larger (and turned, so the two
// never line up), so its repeat is not seen across a field or a mountainside; close by, it is as it is.
group_uniforms distance_tiling;
uniform float far_uv_scale : hint_range(0.02, 1.0, 0.01) = 0.2; // the far tiling, against the near: 0.2 tiles five times as big
uniform float far_blend_start : hint_range(0, 500, 1) = 15.0;   // metres from the camera where the far tiling starts coming in
uniform float far_blend_end : hint_range(0, 2000, 1) = 90.0;    // and where it has come in all the way
uniform float far_blend_amount : hint_range(0, 1, 0.01) = 1.0;  // how much of it there is at the end; 0 turns it off
uniform float far_rotation : hint_range(0, 6.283) = 0.7;        // how far the far tiling is turned, in radians
// A third level, further still: the far tiling blends in turn into one larger again.
uniform float farthest_uv_scale : hint_range(0.005, 1.0, 0.005) = 0.04; // against the near: 0.04 tiles twenty-five times as big
uniform float farthest_blend_start : hint_range(0, 2000, 1) = 150.0;
uniform float farthest_blend_end : hint_range(0, 4000, 1) = 600.0;
uniform float farthest_blend_amount : hint_range(0, 1, 0.01) = 1.0;  // 0 turns the third level off
uniform float farthest_rotation : hint_range(0, 6.283) = 2.1;
group_uniforms;

// Flat colours: instead of the textures, each terrain texture is one colour and one roughness, the
// colour varying over broad patches so a field is not all one shade. Slots follow the terrain's
// texture list (its ids): 0 Grass, 1 Rock, 2 Sand, 3 Dry Grass, 4 Asphalt, 5 Sidewalk. The paved ones
// (4 and 5) are the roads' own colours, even: no patches, ragged edges or macro variation.
group_uniforms flat_colours;
uniform bool use_flat_colours = false;
uniform vec3 colour_0_grass : source_color = vec3(0.29, 0.37, 0.18);
uniform vec3 colour_1_rock : source_color = vec3(0.37, 0.36, 0.34);
uniform vec3 colour_2_sand : source_color = vec3(0.70, 0.62, 0.47);
uniform vec3 colour_3_dry_grass : source_color = vec3(0.52, 0.47, 0.30);
uniform vec3 colour_4_asphalt : source_color = vec3(0.20, 0.19, 0.18);  // as Assets/Materials/Asphalt.tres
uniform vec3 colour_5_sidewalk : source_color = vec3(0.62, 0.61, 0.58); // as Assets/Materials/Sidewalk.tres
uniform vec4 roughness_0_to_3 = vec4(0.90, 0.80, 0.92, 0.90);   // Grass, Rock, Sand, Dry Grass
uniform vec2 roughness_4_to_5 = vec2(0.95, 0.9);                // Asphalt, Sidewalk
uniform float sidewalk_slab_shade : hint_range(0, 2, 0.01) = 0.55; // how light the painted sidewalk's slabs are, against its flat colour
uniform float paved_specular : hint_range(0, 1, 0.01) = 0.15;      // as the roads' Asphalt and Sidewalk materials
uniform float patch_size : hint_range(5, 500, 1) = 60.0;        // metres across a patch of lighter or darker colour
uniform float patch_brightness : hint_range(0, 0.5, 0.01) = 0.12; // how much lighter or darker a patch gets
uniform float patch_warmth : hint_range(0, 0.5, 0.01) = 0.10;   // how much a patch leans yellower
uniform float edge_raggedness : hint_range(0, 1, 0.01) = 0.6;   // how uneven the edges between colours are
group_uniforms;

// Macro Variation
uniform bool enable_macro_variation = true;
uniform vec3 macro_variation1 : source_color = vec3(1.);
uniform vec3 macro_variation2 : source_color = vec3(1.);
uniform float macro_variation_slope : hint_range(0., 1.)  = 0.333;
uniform highp sampler2D noise_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float noise1_scale : hint_range(0.001, 1.) = 0.04; // Used for macro variation 1. Scaled up 10x
uniform float noise1_angle : hint_range(0, 6.283) = 0.;
uniform vec2 noise1_offset = vec2(0.5);
uniform float noise2_scale : hint_range(0.001, 1.) = 0.076; // Used for macro variation 2. Scaled up 10x

// Varyings & Types
varying vec3 v_normal;
varying vec3 v_vertex;
varying mat3 TBN;

////////////////////////
// Vertex
////////////////////////

// Takes in world space XZ (UV) coordinates & search depth (only applicable for background mode none)
// Returns ivec3 with:
// XY: (0 to _region_size - 1) coordinates within a region
// Z: layer index used for texturearrays, -1 if not in a region
ivec3 get_index_coord(const vec2 uv, const int search) {
	vec2 r_uv = round(uv);
	vec2 o_uv = mod(r_uv,_region_size);
	ivec2 pos;
	int bounds, layer_index = -1;
	for (int i = -1; i < clamp(search, SKIP_PASS, FRAGMENT_PASS); i++) {
		if ((layer_index == -1 && _background_mode == 0u ) || i < 0) {
			r_uv -= i == -1 ? vec2(0.0) : vec2(float(o_uv.x <= o_uv.y), float(o_uv.y <= o_uv.x));
			pos = ivec2(floor((r_uv) * _region_texel_size)) + (_region_map_size / 2);
			bounds = int(uint(pos.x | pos.y) < uint(_region_map_size));
			layer_index = (_region_map[ pos.y * _region_map_size + pos.x ] * bounds - 1);
		}
	}
	return ivec3(ivec2(mod(r_uv,_region_size)), layer_index);
}

// Takes in descaled (world_space / region_size) world to region space XZ (UV2) coordinates, returns vec3 with:
// XY: (0. to 1.) coordinates within a region
// Z: layer index used for texturearrays, -1 if not in a region
vec3 get_index_uv(const vec2 uv2) {
	ivec2 pos = ivec2(floor(uv2)) + (_region_map_size / 2);
	int bounds = int(uint(pos.x | pos.y) < uint(_region_map_size));
	int layer_index = _region_map[ pos.y * _region_map_size + pos.x ] * bounds - 1;
	return vec3(uv2 - _region_locations[layer_index], float(layer_index));
}

void vertex() {
	// The ring's vertex, in world space, and in its ring's cells (the ring's scale is its cell size).
	v_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float scale = MODEL_MATRIX[0][0];
	vec2 cell = round(v_vertex.xz / scale);

	// Towards its ring's outer edge, a vertex on an odd line morphs onto the even one before it, so
	// by the edge the ring is the next one's grid: it ends inside 61 cells of the camera (as far as
	// the inner edge reaches) and is done by 77 (before the outer edge), as fractions of the ring.
	float cells_away = length(v_vertex.xz - _camera_pos.xz) / scale;
	float vertex_lerp = smoothstep(_ring_cells * 0.7625, _ring_cells * 0.9625, cells_away);
	vec2 odd = mod(cell, 2.0);
	vec2 start_pos = v_vertex.xz * _vertex_density;
	vec2 end_pos = (cell - odd) * scale * _vertex_density;
	v_vertex.xz -= odd * scale * vertex_lerp;

	// UV coordinates in world space. Values are 0 to _region_size within regions
	UV = v_vertex.xz * _vertex_density;

	// UV coordinates in region space + texel offset. Values are 0 to 1 within regions
	UV2 = fma(UV, vec2(_region_texel_size), vec2(0.5 * _region_texel_size));

	// No ground in holes, or outside the regions.
	ivec3 v_region = get_index_coord(start_pos, VERTEX_PASS);
	uint control = floatBitsToUint(texelFetch(_control_maps, v_region, 0)).r;
	bool hole = DECODE_HOLE(control);

	if (hole || v_region.z == -1) {
		v_vertex.x = 0. / 0.;
	} else {
		// Set final vertex height & calculate vertex normals. 3 lookups
		ivec3 uv_a = get_index_coord(start_pos, VERTEX_PASS);
		ivec3 uv_b = get_index_coord(end_pos, VERTEX_PASS);
		float h = mix(texelFetch(_height_maps, uv_a, 0).r,texelFetch(_height_maps, uv_b, 0).r,vertex_lerp);
		v_vertex.y = h;

		// Vertex normals
		float u = mix(texelFetch(_height_maps, get_index_coord(start_pos + vec2(1,0), VERTEX_PASS), 0).r,
			texelFetch(_height_maps, get_index_coord(end_pos + vec2(1,0), VERTEX_PASS), 0).r, vertex_lerp);
		float v = mix(texelFetch(_height_maps, get_index_coord(start_pos + vec2(0,1), VERTEX_PASS), 0).r,
			texelFetch(_height_maps, get_index_coord(end_pos + vec2(0,1), VERTEX_PASS), 0).r, vertex_lerp);

		v_normal = normalize(vec3(h - u, _vertex_spacing, h - v));
	}

	// Convert model space to view space w/ skip_vertex_transform render mode
	VERTEX = (VIEW_MATRIX * vec4(v_vertex, 1.0)).xyz;

	// Apply terrain normals
	vec3 w_tangent = normalize(cross(v_normal, vec3(0.0, 0.0, 1.0)));
	vec3 w_binormal = normalize(cross(v_normal, w_tangent));
	TBN = mat3(w_tangent, w_binormal, v_normal);

	NORMAL = normalize((VIEW_MATRIX * vec4(v_normal, 0.0)).xyz);
	BINORMAL = normalize((VIEW_MATRIX * vec4(w_binormal, 0.0)).xyz);
	TANGENT = normalize((VIEW_MATRIX * vec4(w_tangent, 0.0)).xyz);
}

////////////////////////
// Fragment
////////////////////////

// Smooth value noise over the ground, about -1 to 1, for ragged edges between textures.
float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float value_noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = hash(i), b = hash(i + vec2(1, 0)), c = hash(i + vec2(0, 1)), d = hash(i + vec2(1, 1));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 2.0 - 1.0;
}

// The auto-shader: which two textures, and how far from the first to the second, as a
// control value. Sand, grass and dirt by height; dirt and then stone by slope, over them.
uint terrain_auto(vec3 world, vec3 normal) {
	vec2 at = world.xz / edge_noise_size;
	float n = value_noise(at) * 0.65 + value_noise(at * 2.7 + 17.0) * 0.35;
	float h = world.y + n * edge_height_noise;
	float angle = degrees(acos(clamp(normal.y, 0.0, 1.0))) + n * edge_slope_noise;

	float to_grass = smoothstep(sand_height - height_blend, sand_height + height_blend, h);
	float high_dirt = smoothstep(grass_height - grass_to_dirt_blend, grass_height + grass_to_dirt_blend, h);
	float slope_dirt = smoothstep(dirt_slope - slope_blend, dirt_slope + slope_blend, angle);
	float slope_stone = smoothstep(stone_slope - slope_blend, stone_slope + slope_blend, angle);

	// By height: sand to grass on the shore, grass to dirt up high.
	int first = to_grass < 1.0 ? sand_texture : grass_texture;
	int second = to_grass < 1.0 ? grass_texture : dirt_texture;
	float blend = to_grass < 1.0 ? to_grass : high_dirt;

	// A slope brings in dirt over whatever the height gave, keeping whichever of that showed most.
	if (second == dirt_texture) {
		blend = max(blend, slope_dirt);
	} else if (slope_dirt > 0.0) {
		first = blend < 0.5 ? first : second;
		second = dirt_texture;
		blend = slope_dirt;
	}

	// Steeper still, stone over dirt.
	if (slope_stone > 0.0) {
		first = dirt_texture;
		second = stone_texture;
		blend = slope_stone;
	}

	return ((uint(first) & 0x1Fu) << 27u) | ((uint(second) & 0x1Fu) << 22u) |
		((uint(fma(clamp(blend, 0.0, 1.0), 255.0, 0.5)) & 0xFFu) << 14u);
}

mat2 rotate_plane(float angle) {
	float c = cos(angle), s = sin(angle);
	return mat2(vec2(c, s), vec2(-s, c));
}

// A terrain texture's flat colour and roughness, by its id.
vec3 flat_colour(int id) {
	switch (id) {
		case 0: return colour_0_grass;
		case 1: return colour_1_rock;
		case 2: return colour_2_sand;
		case 3: return colour_3_dry_grass;
		case 4: return colour_4_asphalt;
		case 5: return colour_5_sidewalk;
	}
	return vec3(0.5);
}

// Whether a terrain texture shows its own texture even among the flat colours: the sidewalk, for its slabs.
bool textured_when_flat(int id) {
	return id == 5;
}

// The colour the sidewalk texture was made in (Tools: its slabs at Assets/Materials/Sidewalk.tres's colour),
// so it can be shown in its flat colour's shade instead.
// In linear colour, as the texture and the flat colours are read.
const vec3 sidewalk_texture_colour = vec3(0.3419, 0.3325, 0.2961);

// Whether a terrain texture is paving (asphalt, sidewalk): even, and edged cleanly.
bool paved(int id) {
	return id == 4 || id == 5;
}

float flat_roughness(int id) {
	return id < 4 ? roughness_0_to_3[id] : (id < 6 ? roughness_4_to_5[id - 4] : 0.9);
}

void fragment() {
	// Recover UVs
	vec2 uv = UV;
	vec2 uv2 = UV2;

	// Lookup offsets, ID and blend weight
	vec3 region_uv = get_index_uv(uv2);
	const vec3 offsets = vec3(0, 1, 2);
	vec2 index_id = floor(uv);
	vec2 weight = fract(uv);
	vec2 invert = 1.0 - weight;
	vec4 weights = vec4(
		invert.x * weight.y, // 0
		weight.x * weight.y, // 1
		weight.x * invert.y, // 2
		invert.x * invert.y  // 3
	);

	ivec3 index[4];
	// control map lookups, used for some normal lookups as well
	index[0] = get_index_coord(index_id + offsets.xy, FRAGMENT_PASS);
	index[1] = get_index_coord(index_id + offsets.yy, FRAGMENT_PASS);
	index[2] = get_index_coord(index_id + offsets.yx, FRAGMENT_PASS);
	index[3] = get_index_coord(index_id + offsets.xx, FRAGMENT_PASS);

	vec3 base_ddx = dFdxCoarse(v_vertex);
	vec3 base_ddy = dFdyCoarse(v_vertex);
	vec4 base_dd = vec4(base_ddx.xz, base_ddy.xz);
	// Calculate the effective mipmap for regionspace
	float region_mip = log2(max(length(base_ddx.xz), length(base_ddy.xz)) * _vertex_density);

	// Color map
	vec4 color_map = region_uv.z > -1.0 ? textureLod(_color_maps, region_uv, region_mip) : COLOR_MAP;

	// The smooth normal, for paving: flat (faceted) normals would show the terrain's triangles on it.
	vec3 smooth_normal = NORMAL;
	if (flat_terrain_normals) {
		NORMAL = normalize(cross(dFdyCoarse(VERTEX),dFdxCoarse(VERTEX)));
		TANGENT = normalize(cross(NORMAL, VIEW_MATRIX[2].xyz));
		BINORMAL = normalize(cross(NORMAL, TANGENT));
	}

	// defaults
	vec4 normal_rough = vec4(0., 1., 0., 0.7);
	vec4 albedo_height = vec4(1.);
	float normal_map_depth = 1.;
	float ao_strength = 0.;
	// How much of what shows here is paving.
	float paved_weight = 0.;

	if (enable_texturing) {
		// set to zero before accumulation
		albedo_height = vec4(0.);
		normal_rough = vec4(0.);
		normal_map_depth = 0.;
		ao_strength = 0.;
		float total_weight = 0.;
		// How much is painted paving and natural ground here, as painted (not by their heights, which
		// would make the edge between them ragged).
		float paved_painted = 0.;
		float natural_painted = 0.;
		vec4 paved_albedo = vec4(0.);
		vec4 paved_normal = vec4(0.);
		float paved_depth = 0.;
		float paved_ao = 0.;
		float sharpness = fma(56., blend_sharpness, 8.);

		// Get index control data
		// 1 - 4 lookups
		uvec4 control = floatBitsToUint(vec4(
			texelFetch(_control_maps, index[0], 0).r,
			texelFetch(_control_maps, index[1], 0).r,
			texelFetch(_control_maps, index[2], 0).r,
			texelFetch(_control_maps, index[3], 0).r));

		{
			// Enable Autoshader if outside regions or painted in regions, otherwise manual painted
			uvec4 is_auto = (control & uvec4(0x1u)) | uvec4(uint(region_uv.z < 0.0));
			uint u_auto = terrain_auto(v_vertex, v_normal);
			control = control * (1u - is_auto) + u_auto * is_auto;
		}


		// Texture weights
		// Vectorised Deocode of all texture IDs, then swizzle to per index mapping.
		ivec4 t_id[2] = {ivec4(control >> uvec4(27u) & uvec4(0x1Fu)),
			ivec4(control >> uvec4(22u) & uvec4(0x1Fu))};
		ivec2 texture_ids[4] = ivec2[4](
			ivec2(t_id[0].x, t_id[1].x),
			ivec2(t_id[0].y, t_id[1].y),
			ivec2(t_id[0].z, t_id[1].z),
			ivec2(t_id[0].w, t_id[1].w));

		// interpolated weights.
		vec4 weights_id_1 = vec4(control >> uvec4(14u) & uvec4(0xFFu)) * DIV_255 * weights;
		vec4 weights_id_0 = weights - weights_id_1;
		vec2 t_weights[4] = {vec2(0), vec2(0), vec2(0), vec2(0)};
		for (int i = 0; i < 4; i++) {
				vec2 w_0 = vec2(weights_id_0[i]);
				vec2 w_1 = vec2(weights_id_1[i]);
				ivec2 id_0 = texture_ids[i].xx;
				ivec2 id_1 = texture_ids[i].yy;
				t_weights[0] += fma(w_0, vec2(equal(texture_ids[0], id_0)), w_1 * vec2(equal(texture_ids[0], id_1)));
				t_weights[1] += fma(w_0, vec2(equal(texture_ids[1], id_0)), w_1 * vec2(equal(texture_ids[1], id_1)));
				t_weights[2] += fma(w_0, vec2(equal(texture_ids[2], id_0)), w_1 * vec2(equal(texture_ids[2], id_1)));
				t_weights[3] += fma(w_0, vec2(equal(texture_ids[3], id_0)), w_1 * vec2(equal(texture_ids[3], id_1)));
		}


		// Process control data to determine each texture ID present, so that only
		// a single sample will be needed later, as all id are contiguous when features
		// like detiling, scale, rotation, and projection are not present.
		// 2 to 16 lookups
		uint id_read = 0u; // 1 bit per possible ID
		// world normal adjustment requires acess to previous id during next iteration
		vec4 nrm = vec4(0.0, 1.0, 0.0, 1.0);
		// adjust uv scale to account for vertex spacing
		uv *= _vertex_spacing;
		// How much of the far tiling shows here, by the distance from the camera; and its turn.
		float camera_distance = length(v_vertex - _camera_pos);
		float far_mix = far_blend_amount * smoothstep(far_blend_start, max(far_blend_end, far_blend_start + 0.01), camera_distance);
		mat2 far_turn = rotate_plane(far_rotation);
		float farthest_mix = farthest_blend_amount * smoothstep(farthest_blend_start, max(farthest_blend_end, farthest_blend_start + 0.01), camera_distance);
		mat2 farthest_turn = rotate_plane(farthest_rotation);
		// Flat colours: the patches (lighter or darker, warmer or not) and the noise their edges follow.
		vec3 patches = vec3(1.0);
		float edge_height = 0.5;
		if (use_flat_colours) {
			vec2 ground = v_vertex.xz;
			float light = texture(noise_texture, ground / patch_size).r;
			float warm = texture(noise_texture, ground / (patch_size * 1.7) * rotate_plane(1.3) + vec2(0.37)).r;
			patches = vec3(1.0 + (light - 0.5) * 2.0 * patch_brightness);
			patches *= mix(vec3(1.0), vec3(1.10, 1.04, 0.86), clamp(warm * 2.0 - 0.5, 0.0, 1.0) * patch_warmth * 4.0);
			edge_height = texture(noise_texture, ground / 7.0).r;
		}
		for (int i = 0; i < 4; i++) {
			for (int t = 0; t < 2; t++) {
				int id = texture_ids[i][t];
				uint mask = 1u << uint(id);
				if ((id_read & mask) == 0u) {
					// Set this id bit
					id_read |= mask;
					float id_w = t_weights[i][t];
					float id_scale = _texture_uv_scale_array[id] * 0.5;
					vec2 id_uv = fma(uv, vec2(id_scale), vec2(0.5));
					vec4 i_dd = base_dd * id_scale;
					float world_normal = clamp(fma(TBN[0], vec3(nrm.x), fma(TBN[1], vec3(nrm.z), v_normal * vec3(nrm.y))).y, 0., 1.);
					vec4 alb;
					if (use_flat_colours) {
						// The colour, in patches; the "height" that sets where colours meet is noise, so edges are ragged.
						// Paving is even, and sits over what is round it, so its edge is clean.
						alb = paved(id) ? vec4(flat_colour(id), 1.0) : vec4(flat_colour(id) * patches, mix(0.5, edge_height, edge_raggedness));
						// A flat normal (packed as the textures are: x, then y along the ground, then up).
						nrm = vec4(0.5, 0.5, 1.0, flat_roughness(id));
						// The sidewalk's slabs, from its texture, at its own tiling only (the far tilings
						// are turned, which would turn its joints), in its flat colour's shade.
						if (textured_when_flat(id)) {
							vec4 slabs = textureGrad(_texture_array_albedo, vec3(id_uv, float(id)), i_dd.xy, i_dd.zw);
							alb.rgb = slabs.rgb * flat_colour(id) / sidewalk_texture_colour * sidewalk_slab_shade;
							nrm = vec4(textureGrad(_texture_array_normal, vec3(id_uv, float(id)), i_dd.xy, i_dd.zw).xyz, flat_roughness(id));
						}
					} else {
					alb = textureGrad(_texture_array_albedo, vec3(id_uv, float(id)), i_dd.xy, i_dd.zw);
					nrm = textureGrad(_texture_array_normal, vec3(id_uv, float(id)), i_dd.xy, i_dd.zw);
					// Further off, blended into the same texture tiled larger and turned. Its normal is
					// turned back with it, so its bumps still face the way the ground does.
					if (far_mix > 0.001) {
						float far_scale = id_scale * far_uv_scale;
						vec2 far_uv = far_turn * (uv * far_scale) + vec2(0.5);
						vec2 far_dx = far_turn * (base_dd.xy * far_scale);
						vec2 far_dy = far_turn * (base_dd.zw * far_scale);
						vec4 far_alb = textureGrad(_texture_array_albedo, vec3(far_uv, float(id)), far_dx, far_dy);
						vec4 far_nrm = textureGrad(_texture_array_normal, vec3(far_uv, float(id)), far_dx, far_dy);
						// The normal's x and y lie along the turned texture: turn them back to the ground's.
						vec2 back = (far_nrm.xy * 2.0 - 1.0) * far_turn;
						far_nrm.xy = back * 0.5 + 0.5;
						alb = mix(alb, far_alb, far_mix);
						nrm = mix(nrm, far_nrm, far_mix);
					}
					// The third level, the same way, on top of whatever the first two made.
					if (farthest_mix > 0.001) {
						float farthest_scale = id_scale * farthest_uv_scale;
						vec2 farthest_uv = farthest_turn * (uv * farthest_scale) + vec2(0.5);
						vec2 farthest_dx = farthest_turn * (base_dd.xy * farthest_scale);
						vec2 farthest_dy = farthest_turn * (base_dd.zw * farthest_scale);
						vec4 farthest_alb = textureGrad(_texture_array_albedo, vec3(farthest_uv, float(id)), farthest_dx, farthest_dy);
						vec4 farthest_nrm = textureGrad(_texture_array_normal, vec3(farthest_uv, float(id)), farthest_dx, farthest_dy);
						vec2 back = (farthest_nrm.xy * 2.0 - 1.0) * farthest_turn;
						farthest_nrm.xy = back * 0.5 + 0.5;
						alb = mix(alb, farthest_alb, farthest_mix);
						nrm = mix(nrm, farthest_nrm, farthest_mix);
					}
					}
					alb.rgb *= _texture_color_array[id].rgb;
					nrm.a = clamp(nrm.a + _texture_roughness_mod_array[id], 0., 1.);
					// Unpack normal map for blending.
					nrm.xyz = fma(nrm.xzy, vec3(2.0), vec3(-1.0));
					// height weight modifier.
					float id_weight = exp2(sharpness * log2(id_w + alb.a * world_normal));
					// Paving and the natural ground are summed apart, to choose between after: a hard edge.
					if (paved(id)) {
						paved_albedo += alb * id_weight;
						paved_normal += nrm * id_weight;
						paved_depth += _texture_normal_depth_array[id] * id_weight;
						paved_ao += _texture_ao_strength_array[id] * id_weight;
						paved_weight += id_weight;
						paved_painted += id_w;
					} else {
						albedo_height += alb * id_weight;
						normal_rough += nrm * id_weight;
						normal_map_depth += _texture_normal_depth_array[id] * id_weight;
						ao_strength += _texture_ao_strength_array[id] * id_weight;
						total_weight += id_weight;
						natural_painted += id_w;
					}
				}
			}
		}
		// normalize accumulated values back to 0.0 - 1.0 range.
		// Paving where it is the more of what is here, the natural ground elsewhere: no blend between
		// the two, so a sidewalk or road painted on the ground has a hard edge.
		if (paved_weight > 0.0 && (paved_painted >= natural_painted || total_weight <= 0.0)) {
			float paved_inv = 1.0 / paved_weight;
			albedo_height = paved_albedo * paved_inv;
			normal_rough = paved_normal * paved_inv;
			normal_map_depth = paved_depth * paved_inv;
			ao_strength = paved_ao * paved_inv;
			paved_weight = 1.0;
		} else {
			float weight_inv = 1.0 / total_weight;
			albedo_height *= weight_inv;
			normal_rough *= weight_inv;
			normal_map_depth *= weight_inv;
			ao_strength *= weight_inv;
			paved_weight = 0.0;
		}
	}

	// Macro variation. 2 lookups
	vec3 macrov = vec3(1.);
	if (enable_macro_variation) {
		float noise1 = texture(noise_texture, (uv * noise1_scale * .1 + noise1_offset) * rotate_plane(noise1_angle)).r;
		float noise2 = texture(noise_texture, uv * noise2_scale * .1).r;
		macrov = mix(macro_variation1, vec3(1.), noise1);
		macrov *= mix(macro_variation2, vec3(1.), noise2);
		macrov = mix(vec3(1.0), macrov, clamp(v_normal.y + macro_variation_slope, 0., 1.));
		// None on paving.
		macrov = mix(macrov, vec3(1.0), paved_weight);
	}

	// Wetness/roughness modifier, converting 0 - 1 range to -1 to 1 range, clamped to Godot roughness values
	float roughness = clamp(fma(color_map.a - 0.5, 2.0, normal_rough.a), 0., 1.);

	// Paving is smooth, like the roads' own sidewalks, however the rest is shaded.
	if (flat_terrain_normals && paved_weight > 0.0) {
		NORMAL = normalize(mix(NORMAL, smooth_normal, paved_weight));
		TANGENT = normalize(cross(NORMAL, VIEW_MATRIX[2].xyz));
		BINORMAL = normalize(cross(NORMAL, TANGENT));
	}

	// Apply PBR
	ALBEDO = albedo_height.rgb * color_map.rgb * macrov;
	ROUGHNESS = roughness;
	SPECULAR = 1. - normal_rough.a;
	// Paving as shiny as the roads' own materials.
	SPECULAR = mix(SPECULAR, paved_specular, paved_weight);
	// Repack final normal map value.
	NORMAL_MAP = fma(normalize(normal_rough.xzy), vec3(0.5), vec3(0.5));
	NORMAL_MAP_DEPTH = normal_map_depth;

	// Higher and/or facing up, less occluded.
	float ao = (1. - (albedo_height.a * log(2.1 - ao_strength))) * (1. - normal_rough.y);
	AO = clamp(1. - ao * ao_strength, albedo_height.a, 1.0);
	AO_LIGHT_AFFECT = (1.0 - albedo_height.a) * clamp(normal_rough.y, 0., 1.);

}
