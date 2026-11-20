using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace TerrariaPatches.TileConvert
{
	// The bosses' cuts (Terraria.Bosses.mm.cs, where each one's reason is): the routines of the game they were written for,
	// and the places where three of those routines call a cut (the fourth cut, wet, is a plain MonoMod patch).
	public static class Bosses
	{
		// A cut holds for the code it was written for: these routines of Terraria 1.4.5.8, by the sum of their instructions
		// (Guard). After a game update the patch log names each routine that changed and leaves its cut out, until someone
		// has read the new routine, seen that the cut's reason still holds, and put the new sum here.
		static readonly (string cut, string type, string name, string first, ulong sum)[] Written =
		{
			("wet", "Terraria.Collision", "WetCollision", null, 0x0c1e9a015bc45e82),
			("tiles", "Terraria.NPC", "AI_037_Destroyer", null, 0x45c624c1065efab0),
			("immune", "Terraria.NPC", "UpdateNPC", null, 0x34534d589ee98574),
			("sight", "Terraria.Main", "DrawInterface_14_EntityHealthBars", null, 0x315aa7ce2b29cc2e),
			("sight", "Terraria.Collision", "CanHit", "Vector2", 0xb403f30d741fe971),
			("sight", "Terraria.Collision", "CanHit", "Point", 0x13000cef07f92b8e),
		};
		static readonly HashSet<string> left = new HashSet<string>();

		public static bool Known(string cut)
		{
			return !left.Contains(cut);
		}

		// On the game as shipped: called from the rules' static constructor, before MonoMod weaves anything.
		public static void Look(ModuleDefinition m)
		{
			foreach (var w in Written)
			{
				var found = m.GetType(w.type)?.Methods.Where(x => x.Name == w.name && x.HasBody
					&& (w.first == null || x.Parameters.Count > 0 && x.Parameters[0].ParameterType.Name == w.first)).ToList();
				ulong sum = 14695981039346656037;
				if (found?.Count == 1)
					foreach (var i in found[0].Body.Instructions)
						sum = Guard.Sum(sum, i);
				if (sum != w.sum && left.Add(w.cut))
					Console.WriteLine($"[bosses] {w.type}::{w.name} is not the routine the {w.cut} cut was written for (it sums to {sum:x16}): left out");
			}
		}

		// A routine of the game as MonoMod left it: under its own name, or orig_<name> when a patch wraps it.
		static MethodDefinition Routine(ModuleDefinition m, string type, string name)
		{
			return m.GetType(type)?.Methods.Where(x => (x.Name == "orig_" + name || x.Name == name) && x.HasBody).OrderBy(x => x.Name == name).FirstOrDefault();
		}

		// what a call takes and gives
		static string Shape(MethodReference r)
		{
			return r.ReturnType.FullName + "(" + string.Join(",", r.Parameters.Select(p => p.ParameterType.FullName)) + ")";
		}

		// tiles: AI_037_Destroyer's look begins "found = false; if (!found) for (k = x0; k < x1; k++) for (l = y0; l < y1; l++)".
		// Its "k = x0" becomes "k = this.InBlocks(x0, x1, y0, y1, ref found)" (the bounds are the ones its two tests read),
		// and where the look ends, the answer just loaded is shown to Boss.Looked (the audit).
		static bool Tiles(MethodDefinition ai, MethodDefinition to, MethodDefinition looked)
		{
			var code = ai.Body.Instructions;
			var il = ai.Body.GetILProcessor();
			var starts = Enumerable.Range(0, code.Count - 10).Where(i => string.Join(" ", code.Skip(i).Take(10).Select(x => x.OpCode.Name))
				== "ldc.i4 stloc ldloc brtrue ldloc stloc br ldloc stloc br" && (int)code[i].Operand == 0 && code[i + 1].Operand == code[i + 2].Operand).ToList();
			if (starts.Count != 1)
				return false;
			int at = starts[0];
			Instruction first = code[at + 4], end = (Instruction)code[at + 3].Operand, outer = (Instruction)code[at + 6].Operand, inner = (Instruction)code[at + 9].Operand;
			if (end.OpCode != OpCodes.Ldloc || end.Operand != code[at + 1].Operand || outer.Next.OpCode != OpCodes.Ldloc || inner.Next.OpCode != OpCodes.Ldloc)
				return false;
			var load = new[] { first.Operand, outer.Next.Operand, code[at + 7].Operand, inner.Next.Operand }.Select(v => il.Create(OpCodes.Ldloc, (VariableDefinition)v))
				.Concat(new[] { il.Create(OpCodes.Ldloca, (VariableDefinition)code[at + 1].Operand), il.Create(OpCodes.Call, to) }).ToList();
			first.OpCode = OpCodes.Ldarg;
			first.Operand = ai.Body.ThisParameter;
			foreach (var x in Enumerable.Reverse(load))
				il.InsertAfter(first, x);
			il.InsertAfter(end, il.Create(OpCodes.Call, looked));
			il.InsertAfter(end, il.Create(OpCodes.Dup));
			return true;
		}

		// immune: the countdown's "j = 0" becomes "j = Boss.Countdown(this)". The instruction that loads the 0 stays (the
		// code before the loop jumps to it) and is made to load the NPC; the call follows it.
		static bool Countdown(MethodDefinition update, MethodDefinition to)
		{
			var code = update.Body.Instructions;
			int at = code.IndexOf(code.FirstOrDefault(i => i.OpCode == OpCodes.Ldfld && ((FieldReference)i.Operand).FullName == "System.Int32[] Terraria.NPC::immune")) - 4;
			if (at < 0 || string.Join(" ", code.Skip(at).Take(4).Select(i => i.OpCode.Name)) != "ldc.i4 stloc br ldarg" || (int)code[at].Operand != 0)
				return false;
			code[at].OpCode = OpCodes.Ldarg;
			code[at].Operand = update.Body.ThisParameter;
			update.Body.GetILProcessor().InsertAfter(code[at], Instruction.Create(OpCodes.Call, to));
			return true;
		}

		// sight: the health bars' search starts by setting its best distance to 999999 and asks Collision.CanHit in one place.
		// That call goes to Boss.Sight, which takes and gives the same, and Boss.NewSearch is called where the search starts:
		// the instruction there stays where it is, for whatever jumps to it, and becomes the call; a copy of it follows.
		static bool Sight(MethodDefinition bars, MethodDefinition to, MethodDefinition begin)
		{
			var code = bars.Body.Instructions;
			var calls = code.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "CanHit"
				&& r.DeclaringType.FullName == "Terraria.Collision" && Shape(r) == Shape(to)).ToList();
			var starts = code.Where(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 999999f).ToList();
			if (calls.Count != 1 || starts.Count != 1 || code.IndexOf(starts[0]) > code.IndexOf(calls[0]))
				return false;
			calls[0].Operand = to;
			bars.Body.GetILProcessor().InsertAfter(starts[0], Instruction.Create(OpCodes.Ldc_R4, 999999f));
			starts[0].OpCode = OpCodes.Call;
			starts[0].Operand = begin;
			return true;
		}

		public static int Apply(ModuleDefinition m)
		{
			var boss = m.GetType("TrPatches.Boss");
			Func<string, MethodDefinition> cut = name => boss.Methods.Single(x => x.Name == name);
			var places = new (string cut, string type, string name, Func<MethodDefinition, bool> put)[]
			{
				("tiles", "Terraria.NPC", "AI_037_Destroyer", r => Tiles(r, m.GetType("Terraria.NPC").Methods.Single(x => x.Name == "InBlocks"), cut("Looked"))),
				("immune", "Terraria.NPC", "UpdateNPC", r => Countdown(r, cut("Countdown"))),
				("sight", "Terraria.Main", "DrawInterface_14_EntityHealthBars", r => Sight(r, cut("Sight"), cut("NewSearch"))),
			};
			foreach (var place in places.Where(x => Known(x.cut)))
			{
				// (the long forms of every instruction while the routine is changed, and the short ones again after)
				var routine = Routine(m, place.type, place.name);
				routine.Body.SimplifyMacros();
				if (!place.put(routine) && left.Add(place.cut))
					Console.WriteLine($"[bosses] the {place.cut} cut's place in {place.name} was not found: left out");
				routine.Body.OptimizeMacros();
			}
			var cuts = new[] { "wet", "tiles", "immune", "sight" }.Where(Known).ToList();
			Console.WriteLine("[bosses] cuts: " + string.Join(" ", cuts));
			return cuts.Count;
		}
	}
}
