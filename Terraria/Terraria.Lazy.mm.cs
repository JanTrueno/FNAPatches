using System;
using System.Collections.Generic;
using System.Linq;
using MonoMod.InlineRT;

#pragma warning disable 1591

// Terraria.exe's side of the lazy textures (ReLogic.Lazy.mm.cs has the runtime and the edits in ReLogic.dll). Follows the
// PortMaster port by wild1walker (trpatch Install.cs, MIT): the texture and sound requests made at startup no longer load, the
// reads of a texture's state count as use, and the frame loop ticks the loader and the eviction.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_LAZY") == "0")
			{
				return;
			}
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Lazy, "lazy", m => TerrariaPatches.TileConvert.LazyRules.Terraria(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}

	}
}
