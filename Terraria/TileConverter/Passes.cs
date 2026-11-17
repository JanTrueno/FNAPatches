using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace TerrariaPatches.TileConvert
{
	// The order the Cecil passes run in over the woven Terraria.exe. MonoMod runs its post-processors in the order the patch
	// assemblies happen to load, and some passes need the others' work done first (the tile conversion before the draw cuts
	// and the scans; the routines split and their locals' clearing dropped last, over everything). Each patch's MonoModRules
	// adds its pass here; the first one also hooks Run, which runs them all by their order.
	public static class Passes
	{
		public const int Lazy = 10, Pack = 20, View = 30, Tiles = 40, DrawCuts = 50, Scans = 60, Split = 90, Inits = 95;

		static readonly List<Tuple<int, string, Func<ModuleDefinition, int>>> passes = new List<Tuple<int, string, Func<ModuleDefinition, int>>>();

		// true for the first pass added: the caller hooks Run into MonoMod's post-processors
		public static bool Add(int order, string name, Func<ModuleDefinition, int> pass)
		{
			passes.Add(Tuple.Create(order, name, pass));
			return passes.Count == 1;
		}

		public static void Run(ModuleDefinition module)
		{
			foreach (var p in passes.OrderBy(x => x.Item1).ToList())
			{
				Console.WriteLine("[" + p.Item2 + "] " + p.Item3(module) + " changes");
			}
		}

		// the game's own types: not the port's runtime woven in beside them (namespace TrPatches)
		public static bool Game(TypeDefinition t)
		{
			while (t.DeclaringType != null)
			{
				t = t.DeclaringType;
			}
			return t.Namespace != "TrPatches";
		}
	}
}
