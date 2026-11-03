namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// A terrain sample's control word, packed as Terrain3D packed it (and as the terrain's shader
/// decodes it): the base texture in bits 27-31, the overlay texture in 22-26, how far the overlay
/// is blended in in 14-21, whether it is a hole in bit 2, and whether the auto-shader picks its
/// textures in bit 0, with how far it is held back from doing so in bits 3-6 (0: not at all, so
/// words with only bit 0 are all auto).
/// </summary>
public static class TerrainControl
{
    public const uint AutoBit = 1u;
    public const uint HoleBit = 1u << 2;

    public static int Base(uint control) => (int)(control >> 27 & 0x1Fu);

    public static int Overlay(uint control) => (int)(control >> 22 & 0x1Fu);

    public static float Blend(uint control) => (control >> 14 & 0xFFu) / 255f;

    public static bool IsHole(uint control) => (control & HoleBit) != 0;

    public static bool IsAuto(uint control) => (control & AutoBit) != 0;

    /// <summary>How much the auto-shader picks the textures, in steps from none to <see cref="FullAuto"/>.</summary>
    public static int AutoLevel(uint control) => IsAuto(control) ? FullAuto - (int)(control >> 3 & 0xFu) : 0;

    public const int FullAuto = 15;

    /// <summary>A control word with this much of the auto-shader, keeping its other bits.</summary>
    public static uint WithAutoLevel(uint control, int level)
    {
        var cleared = control & ~(AutoBit | 0xFu << 3);
        return level <= 0 ? cleared : cleared | AutoBit | (uint)(FullAuto - System.Math.Min(level, FullAuto)) << 3;
    }

    /// <summary>A control word with these textures, keeping its other bits (holes, the auto bit and the rest).</summary>
    public static uint WithTextures(uint control, int first, int second, float blend) =>
        control & ~(0x1Fu << 27 | 0x1Fu << 22 | 0xFFu << 14)
        | ((uint)first & 0x1Fu) << 27
        | ((uint)second & 0x1Fu) << 22
        | ((uint)(System.Math.Clamp(blend, 0f, 1f) * 255f + 0.5f) & 0xFFu) << 14;

    public static uint With(uint control, uint bit, bool on) => on ? control | bit : control & ~bit;
}
