using Mono.Cecil;

namespace TerrariaPatches.TileConvert
{
	// The PortMaster port's speed passes (wild1walker, MIT; Split.cs, Inits.cs, Scans.cs, DrawCuts.cs), for the MonoModRules
	// in Terraria.Speed.mm.cs. The runtime they call (TrPatches.Scan, TrPatches.Stars) is woven into Terraria.exe itself.
	public static class Speed
	{
		public static int DrawCuts(ModuleDefinition m) { return global::DrawCuts.Apply(m, m); }
		public static int Scans(ModuleDefinition m) { return global::Scans.Apply(m, m); }
		public static int Split(ModuleDefinition m) { return global::Split.Apply(m); }
		public static int Inits(ModuleDefinition m) { return global::Inits.Apply(m); }
	}
}
