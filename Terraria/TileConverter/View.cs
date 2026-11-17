using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Main.minScreenW/H (800 x 600, set in Main's static constructor) from Terraria.Patches.View, so that a 640 x 480 handheld
// draws at its own size instead of an 800 x 600 frame the screen cuts off (PortMaster port by wild1walker, MIT: Install.cs
// PatchView). Run from the MonoMod rules of Terraria.TerrariaPatches.mm.cs.
namespace TerrariaPatches.TileConvert
{
	public static class View
	{
		public static int Apply(ModuleDefinition m)
		{
			MethodDefinition cctor = m.GetType("Terraria.Main").Methods.Single(x => x.IsConstructor && x.IsStatic);
			TypeDefinition view = m.GetType("Terraria.Patches.View");
			int changes = 0;
			foreach (Instruction ins in cctor.Body.Instructions)
			{
				if (ins.OpCode == OpCodes.Stsfld && ins.Operand is FieldReference f && (f.Name == "minScreenW" || f.Name == "minScreenH")
					&& (ins.Previous.OpCode == OpCodes.Ldc_I4 || ins.Previous.OpCode == OpCodes.Ldc_I4_S))
				{
					ins.Previous.OpCode = OpCodes.Call;
					ins.Previous.Operand = view.Methods.Single(x => x.Name == (f.Name == "minScreenW" ? "MinWidth" : "MinHeight"));
					changes++;
				}
			}
			if (changes != 2)
				throw new InvalidOperationException("Main.minScreenW/H: " + changes + " of 2 found");
			return changes;
		}
	}
}
