using System;
using MonoMod;
using MonoMod.InlineRT;

#pragma warning disable 1591, 0649, 0169, 0414

// Throttled work (wild1walker, portmaster-terraria, MIT: tools/trpatch/Install.cs PatchThrottle and runtime/Throttle.cs): work
// the game redoes every frame that a handheld cannot afford (TileConverter/ThrottlePass.cs). As built the picture is the
// game's in every frame: only the frame statistics are throttled; the two map settings change the picture and are opt-in:
//   - TimeLogger's frame statistics (the debug overlay's quantiles over the last 300 frames, ~120 series: 0.9 ms a frame on
//     an RG34XX-SP) worked out only while the overlay or the game's frame log reads them (TR_GAME_STATS=1: always);
//   - TR_MAPHEADS_EVERY=<n> (default 1, the game's): the map's player heads rendered into their target every nth frame,
//     drawn from the last render in between (a head then turns up to n-1 frames late on the minimap);
//   - TR_MAP_BUDGET_MS=<ms> (default 5, the game's): the map's section drawing budget a frame after a world loads (less:
//     the map fills in more slowly).
// TERRARIA_THROTTLE=0 leaves the pass out when patching.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_SPEED") == "0" || Environment.GetEnvironmentVariable("TERRARIA_THROTTLE") == "0")
				return;
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Throttle, "throttle", m => TerrariaPatches.TileConvert.ThrottlePass.Apply(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    // Two settings that change the picture, both the game's own values unless asked for:
    //   the map's player heads (Main.MapPlayerRenderer: each head drawn again into its render target
    //   every frame; render target changes are costly on Mali's tiled GPUs): every nth frame with
    //   TR_MAPHEADS_EVERY=<n>, the heads drawn from the last render in between (default 1: every frame)
    //   the map's section drawing after a world loads: TR_MAP_BUDGET_MS=<ms> a frame (default 5, the
    //   game's; less and the map fills in more slowly)
    public static class Throttle
    {
        static readonly int headsEvery = int.TryParse(Environment.GetEnvironmentVariable("TR_MAPHEADS_EVERY"), out int n) && n > 0 ? n : 1;
        public static readonly double MapBudget = double.TryParse(Environment.GetEnvironmentVariable("TR_MAP_BUDGET_MS"), out double b) && b > 0 ? b : 5.0;
        static int heads;

        public static bool MapHeads() => heads++ % headsEvery == 0;

        // TimeLogger's frame statistics (the debug overlay's median, 90th percentile and maximum of the
        // last 300 frames, two series for each of its ~60 entries: 0.9 ms a frame on an RG34XX-SP) are
        // read by that overlay and by the game's own frame log: worked out only while one of them is
        // on (or with TR_GAME_STATS=1), from the same ring of values, so they are the game's numbers
        // from the overlay's second frame on
        static readonly bool stats = Environment.GetEnvironmentVariable("TR_GAME_STATS") == "1";
        public static bool Stats(bool logging) => logging || stats || Terraria.Main.drawDiag != 0;
    }
}
