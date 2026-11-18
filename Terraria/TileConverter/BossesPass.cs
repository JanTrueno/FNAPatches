using Mono.Cecil;

namespace TerrariaPatches.TileConvert
{
	// The bosses' cuts (Bosses.cs, wild1walker, MIT), for the MonoModRules in Terraria.Bosses.mm.cs. The runtime they call
	// (TrPatches.Boss) is woven into Terraria.exe itself.
	public static class BossesPass
	{
		public static int Look(ModuleDefinition m) { global::Bosses.Look(m); return 0; }
		public static int Apply(ModuleDefinition m) { return global::Bosses.Apply(m, m); }
	}
}
