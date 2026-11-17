using System;
using MonoMod.InlineRT;

// IgnoresAccessChecksTo for .NET (TileConverter/NetAccess.cs). TERRARIA_NET_ACCESS=0 leaves it out (for Mono).
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_NET_ACCESS") != "0")
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.NetAccess.Apply(modder.Module); };
		}
	}
}
