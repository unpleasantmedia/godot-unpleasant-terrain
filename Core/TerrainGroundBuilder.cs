using System.Collections.Generic;
using System.Linq;
using Godot;
using FloatOp = Godot.VisualShaderNodeFloatOp.OperatorEnum;
using FloatFunc = Godot.VisualShaderNodeFloatFunc.FunctionEnum;
using VectorOp = Godot.VisualShaderNodeVectorOp.OperatorEnum;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// Builds the ground's visual shader for a number of texture slots, all of nodes: a graph has a set
/// number of them, so it is built again when the slots change. Slots marked as paving are blended
/// apart from the rest. What it was built for is kept in its <see cref="SignatureMeta"/>.
/// </summary>
public sealed class TerrainGroundBuilder
{
    public const int MinSlots = 4;

    public const int MaxSlots = 32;

    public const string SignatureMeta = "terrain_ground_signature";

    /// <summary>What a graph is built from, to tell whether one is still right.</summary>
    public static string Signature(int slots, IEnumerable<int> paving, IReadOnlyDictionary<int, int> autoLayers) =>
        $"{slots}:{string.Join(",", paving)}:{string.Join(",", autoLayers.OrderBy(a => a.Key).Select(a => $"{a.Key}@{a.Value}"))}";

    public static VisualShader Build(int slots, IReadOnlyCollection<int> paving, IReadOnlyDictionary<int, int> autoLayers)
    {
        var builder = new TerrainGroundBuilder(slots, paving, autoLayers);
        builder._shader.Set("flags/skip_vertex_transform", true);
        builder._shader.AddVarying("v_world", VisualShader.VaryingMode.VertexToFragLight, VisualShader.VaryingType.Vector3D);
        builder._shader.AddVarying("v_normal", VisualShader.VaryingMode.VertexToFragLight, VisualShader.VaryingType.Vector3D);
        builder.BuildVertex();
        builder.BuildFragment();
        builder._shader.SetMeta(SignatureMeta, Signature(slots, paving, autoLayers));
        return builder._shader;
    }

    /// <summary>A node's output.</summary>
    private readonly record struct Out(int Node, int Port = 0)
    {
        public static implicit operator Out(int node) => new(node);
    }

    private readonly VisualShader _shader = new();
    private readonly int _slots;
    private readonly HashSet<int> _paving;
    private readonly IReadOnlyDictionary<int, int> _autoLayers;
    private VisualShader.Type _stage = VisualShader.Type.Vertex;
    private int _next = 10;
    private float _column;
    private float _row;

    private Out _size, _density, _map, _half, _halfCell, _zero, _one, _lookup, _heights;

    private TerrainGroundBuilder(int slots, IEnumerable<int> paving, IReadOnlyDictionary<int, int> autoLayers)
    {
        _slots = slots;
        _autoLayers = autoLayers;
        _paving = [.. paving];
    }

    private int Add(VisualShaderNode node)
    {
        var id = _next++;
        _shader.AddNode(_stage, node, new Vector2(_column, _row), id);
        _row += 170f;
        return id;
    }

    private void Wire(Out from, int to, int port)
    {
        var error = _shader.ConnectNodes(_stage, from.Node, from.Port, to, port);
        if (error != Error.Ok)
            GD.PushError($"Terrain shader: couldn't connect {from.Node}:{from.Port} to {to}:{port} ({error})");
    }

    private void Place(float column)
    {
        _column = column;
        _row = 0f;
    }

    private int K(float value) => Add(new VisualShaderNodeFloatConstant { Constant = value });

    private int Param(string name, float value, float low = 0f, float high = 0f)
    {
        var node = new VisualShaderNodeFloatParameter { ParameterName = name, DefaultValueEnabled = true, DefaultValue = value };
        if (high > low)
        {
            node.Hint = VisualShaderNodeFloatParameter.HintEnum.Range;
            node.Min = low;
            node.Max = high;
        }

        return Add(node);
    }

    private int Op(FloatOp kind, Out a, Out b)
    {
        var id = Add(new VisualShaderNodeFloatOp { Operator = kind });
        Wire(a, id, 0);
        Wire(b, id, 1);
        return id;
    }

    private int Plus(Out a, Out b) => Op(FloatOp.Add, a, b);
    private int Minus(Out a, Out b) => Op(FloatOp.Sub, a, b);
    private int Times(Out a, Out b) => Op(FloatOp.Mul, a, b);
    private int Over(Out a, Out b) => Op(FloatOp.Div, a, b);
    private int Max(Out a, Out b) => Op(FloatOp.Max, a, b);
    private int Min(Out a, Out b) => Op(FloatOp.Min, a, b);

    private int Fn(FloatFunc kind, Out a)
    {
        var id = Add(new VisualShaderNodeFloatFunc { Function = kind });
        Wire(a, id, 0);
        return id;
    }

    private int Smooth(Out low, Out high, Out value)
    {
        var id = Add(new VisualShaderNodeSmoothStep { OpType = VisualShaderNodeSmoothStep.OpTypeEnum.Scalar });
        Wire(low, id, 0);
        Wire(high, id, 1);
        Wire(value, id, 2);
        return id;
    }

    private int Mix(VisualShaderNodeMix.OpTypeEnum type, Out a, Out b, Out amount)
    {
        var id = Add(new VisualShaderNodeMix { OpType = type });
        Wire(a, id, 0);
        Wire(b, id, 1);
        Wire(amount, id, 2);
        return id;
    }

    private int MixF(Out a, Out b, Out amount) => Mix(VisualShaderNodeMix.OpTypeEnum.Scalar, a, b, amount);
    private int Mix3(Out a, Out b, Out amount) => Mix(VisualShaderNodeMix.OpTypeEnum.Vector3DScalar, a, b, amount);
    private int Mix4(Out a, Out b, Out amount) => Mix(VisualShaderNodeMix.OpTypeEnum.Vector4DScalar, a, b, amount);

    private int Compose3(Out x, Out y, Out z)
    {
        var id = Add(new VisualShaderNodeVectorCompose { OpType = VisualShaderNodeVectorBase.OpTypeEnum.Vector3D });
        Wire(x, id, 0);
        Wire(y, id, 1);
        Wire(z, id, 2);
        return id;
    }

    private int Uv2(Out x, Out z)
    {
        var id = Add(new VisualShaderNodeVectorCompose { OpType = VisualShaderNodeVectorBase.OpTypeEnum.Vector2D });
        Wire(x, id, 0);
        Wire(z, id, 1);
        return id;
    }

    private int Decompose3(Out a)
    {
        var id = Add(new VisualShaderNodeVectorDecompose { OpType = VisualShaderNodeVectorBase.OpTypeEnum.Vector3D });
        Wire(a, id, 0);
        return id;
    }

    private int Decompose4(Out a)
    {
        var id = Add(new VisualShaderNodeVectorDecompose { OpType = VisualShaderNodeVectorBase.OpTypeEnum.Vector4D });
        Wire(a, id, 0);
        return id;
    }

    private int VOp(VectorOp kind, Out a, Out b)
    {
        var id = Add(new VisualShaderNodeVectorOp { OpType = VisualShaderNodeVectorBase.OpTypeEnum.Vector3D, Operator = kind });
        Wire(a, id, 0);
        Wire(b, id, 1);
        return id;
    }

    private int VPlus(Out a, Out b) => VOp(VectorOp.Add, a, b);
    private int VScale(Out a, Out amount) => VOp(VectorOp.Mul, a, Compose3(amount, amount, amount));

    private int VNorm(Out a)
    {
        var id = Add(new VisualShaderNodeVectorFunc { OpType = VisualShaderNodeVectorBase.OpTypeEnum.Vector3D, Function = VisualShaderNodeVectorFunc.FunctionEnum.Normalize });
        Wire(a, id, 0);
        return id;
    }

    private int C3(Vector3 value) => Add(new VisualShaderNodeVec3Constant { Constant = value });

    private int Input(string name) => Add(new VisualShaderNodeInput { InputName = name });

    private int Vec3Param(string name) => Add(new VisualShaderNodeVec3Parameter { ParameterName = name });

    private int ColorParam(string name, Color value) =>
        Add(new VisualShaderNodeColorParameter { ParameterName = name, DefaultValueEnabled = true, DefaultValue = value });

    private int ArrayParam(string name, bool color, bool linear) => Add(new VisualShaderNodeTexture2DArrayParameter
    {
        ParameterName = name,
        TextureType = color ? VisualShaderNodeTextureParameter.TextureTypeEnum.Color : VisualShaderNodeTextureParameter.TextureTypeEnum.Data,
        TextureFilter = color ? VisualShaderNodeTextureParameter.TextureFilterEnum.LinearMipmapAnisotropic
            : linear ? VisualShaderNodeTextureParameter.TextureFilterEnum.Linear : VisualShaderNodeTextureParameter.TextureFilterEnum.Nearest,
        TextureRepeat = color ? VisualShaderNodeTextureParameter.TextureRepeatEnum.Enabled : VisualShaderNodeTextureParameter.TextureRepeatEnum.Disabled,
    });

    private int SampleArray(Out sampler, Out uv, Out? lod = null)
    {
        var id = Add(new VisualShaderNodeTexture2DArray { Source = VisualShaderNodeSample3D.SourceEnum.Port });
        Wire(uv, id, 0);
        if (lod is { } level)
            Wire(level, id, 1);
        Wire(sampler, id, 2);
        return id;
    }

    private (Out Layer, Out X, Out Z) Region(Out x, Out z)
    {
        var cellX = Fn(FloatFunc.Floor, Over(x, _size));
        var cellZ = Fn(FloatFunc.Floor, Over(z, _size));
        var mapX = Over(Plus(Plus(cellX, _half), _halfCell), _map);
        var mapZ = Over(Plus(Plus(cellZ, _half), _halfCell), _map);
        var lookup = Add(new VisualShaderNodeTexture { Source = VisualShaderNodeTexture.SourceEnum.Port });
        Wire(Uv2(mapX, mapZ), lookup, 0);
        Wire(_zero, lookup, 1);
        Wire(_lookup, lookup, 2);
        var layer = Decompose4(lookup);
        var localX = Over(Plus(Minus(x, Times(cellX, _size)), _halfCell), _size);
        var localZ = Over(Plus(Minus(z, Times(cellZ, _size)), _halfCell), _size);
        return (new Out(layer, 0), localX, localZ);
    }

    private int LayerUv((Out Layer, Out X, Out Z) region) => Compose3(region.X, region.Z, Minus(region.Layer, _one));

    private Out HeightAt(Out x, Out z)
    {
        var region = Region(Fn(FloatFunc.Round, x), Fn(FloatFunc.Round, z));
        return new Out(Decompose4(SampleArray(_heights, LayerUv(region), _zero)), 0);
    }

    private void Common()
    {
        Place(-6000f);
        _size = Param("terrain_region_size", 1024f);
        _density = Param("terrain_vertex_density", 1f);
        _map = K(32f);
        _half = K(16f);
        _halfCell = K(0.5f);
        _zero = K(0f);
        _one = K(1f);
        _lookup = Add(new VisualShaderNodeTexture2DParameter
        {
            ParameterName = "terrain_region_lookup",
            TextureFilter = VisualShaderNodeTextureParameter.TextureFilterEnum.Nearest,
            TextureRepeat = VisualShaderNodeTextureParameter.TextureRepeatEnum.Disabled,
        });
    }

    private void BuildVertex()
    {
        _stage = VisualShader.Type.Vertex;
        Common();
        _heights = ArrayParam("terrain_height_maps", false, false);
        var flags = ArrayParam("terrain_flag_maps", false, true);
        var ring = Param("terrain_ring_cells", 80f);
        var camera = Decompose3(Vec3Param("terrain_camera_pos"));
        var spacing = Param("terrain_vertex_spacing", 1f);

        Place(-5000f);
        var model = Input("model_matrix");
        var view = Input("view_matrix");
        var world = Add(new VisualShaderNodeTransformVecMult());
        Wire(model, world, 0);
        Wire(Input("vertex"), world, 1);
        var worldParts = Decompose3(world);
        var parts = Add(new VisualShaderNodeTransformDecompose());
        Wire(model, parts, 0);
        var scale = new Out(Decompose3(new Out(parts, 0)), 0);
        var wx = new Out(worldParts, 0);
        var wz = new Out(worldParts, 2);

        Place(-4000f);
        var cellX = Fn(FloatFunc.Round, Over(wx, scale));
        var cellZ = Fn(FloatFunc.Round, Over(wz, scale));
        var dx = Minus(wx, new Out(camera, 0));
        var dz = Minus(wz, new Out(camera, 2));
        var away = Over(Fn(FloatFunc.Sqrt, Plus(Times(dx, dx), Times(dz, dz))), scale);
        var morph = Smooth(Times(ring, K(0.7625f)), Times(ring, K(0.9625f)), away);
        var two = K(2f);
        var oddX = Op(FloatOp.Mod, cellX, two);
        var oddZ = Op(FloatOp.Mod, cellZ, two);
        var sx = Times(wx, _density);
        var sz = Times(wz, _density);
        var ex = Times(Times(Minus(cellX, oddX), scale), _density);
        var ez = Times(Times(Minus(cellZ, oddZ), scale), _density);
        var fx = Minus(wx, Times(Times(oddX, scale), morph));
        var fz = Minus(wz, Times(Times(oddZ, scale), morph));

        Place(-3000f);
        var h = MixF(HeightAt(sx, sz), HeightAt(ex, ez), morph);
        Place(-2500f);
        var u = MixF(HeightAt(Plus(sx, _one), sz), HeightAt(Plus(ex, _one), ez), morph);
        Place(-2000f);
        var v = MixF(HeightAt(sx, Plus(sz, _one)), HeightAt(ex, Plus(ez, _one)), morph);

        Place(-1500f);
        var normal = VNorm(Compose3(Minus(h, u), spacing, Minus(h, v)));
        var region = Region(Fn(FloatFunc.Round, sx), Fn(FloatFunc.Round, sz));
        var flag = Decompose4(SampleArray(flags, LayerUv(region), _zero));
        // No ground in holes, or outside the regions: not a number, which draws nothing.
        var gone = Max(new Out(flag, 1), Minus(_one, Smooth(K(0f), K(0.5f), region.Layer)));
        var nan = Over(K(0f), K(0f));
        var placed = Add(new VisualShaderNodeIf());
        Wire(gone, placed, 0);
        Wire(K(0.5f), placed, 1);
        var position = Compose3(fx, h, fz);
        Wire(position, placed, 3);
        Wire(Compose3(nan, h, fz), placed, 4);
        Wire(position, placed, 5);

        Place(-800f);
        var setWorld = Add(new VisualShaderNodeVaryingSetter { VaryingName = "v_world", VaryingType = VisualShader.VaryingType.Vector3D });
        Wire(placed, setWorld, 0);
        var setNormal = Add(new VisualShaderNodeVaryingSetter { VaryingName = "v_normal", VaryingType = VisualShader.VaryingType.Vector3D });
        Wire(normal, setNormal, 0);
        var viewPosition = Add(new VisualShaderNodeTransformVecMult());
        Wire(view, viewPosition, 0);
        Wire(placed, viewPosition, 1);
        var tangent = VNorm(VOp(VectorOp.Cross, normal, C3(new Vector3(0f, 0f, 1f))));
        var binormal = VNorm(VOp(VectorOp.Cross, normal, tangent));
        _shader.SetNodePosition(_stage, 0, Vector2.Zero);
        Wire(viewPosition, 0, 0);
        Wire(VNorm(ViewDirection(view, normal)), 0, 1);
        Wire(VNorm(ViewDirection(view, binormal)), 0, 2);
        Wire(VNorm(ViewDirection(view, tangent)), 0, 3);
    }

    private int ViewDirection(Out view, Out direction)
    {
        var id = Add(new VisualShaderNodeTransformVecMult { Operator = VisualShaderNodeTransformVecMult.OperatorEnum.Op3X3Axb });
        Wire(view, id, 0);
        Wire(direction, id, 1);
        return id;
    }

    /// <summary>A group of slots' sums, blended among themselves.</summary>
    private sealed class Group
    {
        public Out? Albedo, Plain, Normal, Rough, Depth, Total, Weight;
    }

    private void BuildFragment()
    {
        _stage = VisualShader.Type.Fragment;
        _next = 10;
        Common();
        var getWorld = Add(new VisualShaderNodeVaryingGetter { VaryingName = "v_world", VaryingType = VisualShader.VaryingType.Vector3D });
        var getNormal = Add(new VisualShaderNodeVaryingGetter { VaryingName = "v_normal", VaryingType = VisualShader.VaryingType.Vector3D });
        var worldParts = Decompose3(getWorld);
        var normalParts = Decompose3(getNormal);
        var wx = new Out(worldParts, 0);
        var wy = new Out(worldParts, 1);
        var wz = new Out(worldParts, 2);
        var ny = new Out(normalParts, 1);
        var flags = ArrayParam("terrain_flag_maps", false, true);
        var maps = TerrainBake.MapsFor(_slots);
        var weightParams = Enumerable.Range(0, maps).Select(i => ArrayParam($"terrain_weight_maps_{i}", false, true)).ToList();
        var colours = ArrayParam("terrain_color_maps", true, true);
        var albedos = ArrayParam("terrain_texture_array_albedo", true, true);
        var normals = ArrayParam("terrain_texture_array_normal", false, true);

        Place(-5400f);
        var region = Region(Times(wx, _density), Times(wz, _density));
        var uv = LayerUv(region);
        var flag = Decompose4(SampleArray(flags, uv, _zero));
        var inside = Smooth(K(0f), K(0.5f), region.Layer);
        var autoAmount = Max(new Out(flag, 0), Minus(_one, inside));
        var tint = SampleArray(colours, uv, _zero);
        var weights = weightParams.Select(p => Decompose4(SampleArray(p, uv, _zero))).ToList();

        // ---- The auto-shader, by height and slope ----
        Place(-4800f);
        var noise = new NoiseTexture2D { Noise = new FastNoiseLite { Frequency = 0.02f, Seed = 7 }, Seamless = true, Width = 512, Height = 512 };
        var noiseScale = Param("edge_noise_size", 25f, 1f, 200f);
        var noiseAt = Uv2(Over(wx, Times(noiseScale, K(20f))), Over(wz, Times(noiseScale, K(20f))));
        var noiseSample = Add(new VisualShaderNodeTexture { Source = VisualShaderNodeTexture.SourceEnum.Texture, Texture = noise });
        Wire(noiseAt, noiseSample, 0);
        var n = Minus(Times(new Out(Decompose4(noiseSample), 0), K(2f)), _one);
        var angle = Times(Fn(FloatFunc.Acos, Min(ny, _one)), K(57.29578f));
        var height = Plus(wy, Times(n, Param("edge_height_noise", 1.5f, 0f, 20f)));
        var slope = Plus(angle, Times(n, Param("edge_slope_noise", 4f, 0f, 20f)));
        Place(-4300f);
        // From the top layer down, each takes what is left. Whatever no rule claims is the first slot's.
        var autos = new Dictionary<int, Out>();
        Out remaining = _one;
        foreach (var layer in _autoLayers.GroupBy(a => a.Value).OrderByDescending(g => g.Key))
        {
            var claims = layer.Select(a => (Slot: a.Key, Claim: Claim(a.Key, height, slope))).ToList();
            Out total = claims[0].Claim;
            foreach (var other in claims.Skip(1))
                total = Plus(total, other.Claim);
            var share = Max(total, _one);
            foreach (var (slot, claim) in claims)
                autos[slot] = Times(Over(claim, share), remaining);
            remaining = Times(remaining, Max(Minus(_one, total), K(0f)));
        }

        autos[0] = autos.TryGetValue(0, out var first) ? Plus(first, remaining) : remaining;

        // ---- Shared for every slot ----
        Place(-3300f);
        var flatToggle = Add(new VisualShaderNodeBooleanParameter { ParameterName = "use_flat_colours", DefaultValueEnabled = true, DefaultValue = false });
        var flatAmount = Max(Param("terrain_flat_amount", 0f), flatToggle);
        var sharp = Plus(Times(Param("blend_sharpness", 0.5f, 0f, 1f), K(56f)), K(8f));
        var ground = Compose3(wx, wz, K(0f));
        var halfUv = K(0.5f);
        var camera = Decompose3(Vec3Param("terrain_camera_pos"));
        var toCameraX = Minus(wx, new Out(camera, 0));
        var toCameraY = Minus(wy, new Out(camera, 1));
        var toCameraZ = Minus(wz, new Out(camera, 2));
        var distance = Fn(FloatFunc.Sqrt, Plus(Plus(Times(toCameraX, toCameraX), Times(toCameraY, toCameraY)), Times(toCameraZ, toCameraZ)));
        var farMix = Times(Param("far_blend_amount", 1f, 0f, 1f), Smooth(Param("far_blend_start", 15f, 0f, 500f), Param("far_blend_end", 90f, 0f, 2000f), distance));
        var lastMix = Times(Param("farthest_blend_amount", 1f, 0f, 1f), Smooth(Param("farthest_blend_start", 150f, 0f, 2000f), Param("farthest_blend_end", 600f, 0f, 4000f), distance));
        var farGround = Turned(wx, wz, Param("far_rotation", 0.7f, 0f, 6.283f));
        var lastGround = Turned(wx, wz, Param("farthest_rotation", 2.1f, 0f, 6.283f));
        var farScale = Param("far_uv_scale", 0.2f, 0.02f, 1f);
        var lastScale = Param("farthest_uv_scale", 0.04f, 0.005f, 1f);
        var hardness = Param("paving_edge_hardness", 1f, 0f, 1f);

        // ---- Each texture slot, summed ----
        var natural = new Group();
        var paved = new Group();
        for (var i = 0; i < _slots; i++)
        {
            Place(-2800f + i * 700f);
            Out w = Times(new Out(weights[i / 4], i % 4), Minus(_one, autoAmount));
            if (autos.TryGetValue(i, out var auto))
                w = Plus(w, Times(auto, autoAmount));
            var slot = K(i);
            var scale = Times(Param($"terrain_slot_{i}_uv_scale", 0.5f), halfUv);
            var nearUv = VPlus(VScale(ground, scale), Compose3(halfUv, halfUv, slot));
            var farUv = VPlus(VScale(farGround, Times(scale, farScale)), Compose3(halfUv, halfUv, slot));
            var lastUv = VPlus(VScale(lastGround, Times(scale, lastScale)), Compose3(halfUv, halfUv, slot));
            var albedo = Mix4(Mix4(SampleArray(albedos, nearUv), SampleArray(albedos, farUv), farMix), SampleArray(albedos, lastUv), lastMix);
            var normal = Mix4(Mix4(SampleArray(normals, nearUv), SampleArray(normals, farUv), farMix), SampleArray(normals, lastUv), lastMix);
            var a = Decompose4(albedo);
            var o = Decompose4(normal);
            var albedoRgb = Compose3(new Out(a, 0), new Out(a, 1), new Out(a, 2));
            var tintColor = ColorParam($"terrain_slot_{i}_tint", Colors.White);
            var flatColor = ColorParam($"terrain_slot_{i}_flat_colour", new Color(0.5f, 0.5f, 0.5f));
            var tinted = Mix3(VOp(VectorOp.Mul, albedoRgb, tintColor), flatColor, flatAmount);
            var heightA = MixF(new Out(a, 3), K(0.5f), flatAmount);
            var rough = MixF(Plus(new Out(o, 3), Param($"terrain_slot_{i}_roughness_mod", 0f)), Param($"terrain_slot_{i}_flat_roughness", 0.9f), flatAmount);
            // Unpacked: x and y lie along the ground, z is up; packed x, z, y as the textures are.
            var unpacked = VOp(VectorOp.Sub, Compose3(Times(new Out(o, 0), K(2f)), Times(new Out(o, 2), K(2f)), Times(new Out(o, 1), K(2f))), C3(Vector3.One));
            var normalMap = Mix3(unpacked, C3(Vector3.Up), flatAmount);
            var up = Max(Min(ny, _one), K(0f));
            var strengthBase = Max(Plus(w, Times(heightA, up)), K(0f));
            var strength = Times(Op(FloatOp.Pow, strengthBase, sharp), Min(Times(Max(w, K(0f)), K(1000f)), _one));
            var depth = Param($"terrain_slot_{i}_normal_depth", 1f);

            var group = _paving.Contains(i) ? paved : natural;
            group.Albedo = SumV(group.Albedo, VScale(tinted, strength));
            group.Plain = SumV(group.Plain, VScale(tinted, w));
            group.Normal = SumV(group.Normal, VScale(normalMap, strength));
            group.Rough = Sum(group.Rough, Times(rough, strength));
            group.Depth = Sum(group.Depth, Times(depth, strength));
            group.Total = Sum(group.Total, strength);
            group.Weight = Sum(group.Weight, w);
        }

        // ---- Out ----
        Place(-2800f + _slots * 700f);
        // (Multiplied up from a larger number: a float constant node is written with only a few decimal places.)
        var small = K(0.00001f);
        var tiny = Times(Times(small, small), Times(Times(small, small), Times(small, small)));
        var result = Resolve(natural, tiny);
        Place(_column + 500f);
        var pavedResult = Resolve(paved, tiny);
        Place(_column + 500f);
        if (result is null)
        {
            result = pavedResult;
        }
        else if (pavedResult is { } other)
        {
            // Paving where it is the more of what is here. At full hardness that is a line; lower, a widening blend.
            var share = Over(paved.Weight!.Value, Max(Plus(natural.Weight!.Value, paved.Weight!.Value), tiny));
            var spread = Max(Times(Minus(_one, hardness), halfUv), K(0.001f));
            var amount = Smooth(Minus(halfUv, spread), Plus(halfUv, spread), share);
            var mine = result.Value;
            result = (Mix3(mine.Albedo, other.Albedo, amount), Mix3(mine.Normal, other.Normal, amount), MixF(mine.Rough, other.Rough, amount), MixF(mine.Depth, other.Depth, amount));
        }

        var final = result!.Value;
        var tintParts = Decompose4(tint);
        var finalAlbedo = VOp(VectorOp.Mul, final.Albedo, Compose3(new Out(tintParts, 0), new Out(tintParts, 1), new Out(tintParts, 2)));
        var finalNormal = Decompose3(VNorm(VPlus(final.Normal, C3(new Vector3(0f, 0.001f, 0f)))));
        var packed = VPlus(VScale(Compose3(new Out(finalNormal, 0), new Out(finalNormal, 2), new Out(finalNormal, 1)), halfUv), C3(new Vector3(0.5f, 0.5f, 0.5f)));
        _shader.SetNodePosition(_stage, 0, new Vector2(_column + 900f, 0f));
        Wire(finalAlbedo, 0, 0);
        Wire(final.Rough, 0, 3);
        Wire(Minus(_one, final.Rough), 0, 4);
        Wire(packed, 0, 9);
        Wire(final.Depth, 0, 10);
    }

    /// <summary>How much a slot's auto-shader rules claim the ground here.</summary>
    private int Claim(int slot, Out height, Out slope)
    {
        var inHeight = InRange(height, Param($"terrain_slot_{slot}_auto_min_height", -10000f), Param($"terrain_slot_{slot}_auto_max_height", 10000f), Param($"terrain_slot_{slot}_auto_height_blend", 1f));
        var inSlope = InRange(slope, Param($"terrain_slot_{slot}_auto_min_slope", -90f), Param($"terrain_slot_{slot}_auto_max_slope", 180f), Param($"terrain_slot_{slot}_auto_slope_blend", 4f));
        return Times(inHeight, inSlope);
    }

    private int InRange(Out value, Out low, Out high, Out blend)
    {
        var fade = Max(blend, K(0.001f));
        return Times(Smooth(Minus(low, fade), Plus(low, fade), value), Minus(_one, Smooth(Minus(high, fade), Plus(high, fade), value)));
    }

    private Out Sum(Out? sum, Out add) => sum is { } value ? Plus(value, add) : add;

    private Out SumV(Out? sum, Out add) => sum is { } value ? VPlus(value, add) : add;

    /// <summary>One group's slots averaged by their blend strengths, if it has any.</summary>
    private (Out Albedo, Out Normal, Out Rough, Out Depth)? Resolve(Group group, Out tiny)
    {
        if (group.Total is not { } total)
            return null;
        var inverse = Over(_one, Max(total, tiny));
        // The sharpened blend can run out of numbers where nothing outweighs the rest: the plain blend there.
        var usable = Smooth(K(0f), tiny, total);
        var plain = VScale(group.Plain!.Value, Over(_one, Max(group.Weight!.Value, tiny)));
        return (
            Mix3(plain, VScale(group.Albedo!.Value, inverse), usable),
            VScale(group.Normal!.Value, inverse),
            MixF(K(0.9f), Min(Max(Times(group.Rough!.Value, inverse), K(0f)), _one), usable),
            Times(group.Depth!.Value, inverse));
    }

    private int Turned(Out x, Out z, Out angle)
    {
        var cos = Fn(FloatFunc.Cos, angle);
        var sin = Fn(FloatFunc.Sin, angle);
        return Compose3(Minus(Times(cos, x), Times(sin, z)), Plus(Times(sin, x), Times(cos, z)), K(0f));
    }
}
