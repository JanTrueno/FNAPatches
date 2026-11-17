using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// .NET (CoreCLR) enforces the access modifiers that Mono ignores: the patched game reaches into FNA's internals and into its own
// non-public members from patch code. CoreCLR lets an assembly through when it carries IgnoresAccessChecksToAttribute naming the
// other assembly; the attribute's type is declared in the assembly itself (CoreCLR matches it by name). Run from the MonoMod rules
// of Terraria.NetAccess.mm.cs and ReLogic.NetAccess.mm.cs.
namespace TerrariaPatches.TileConvert
{
	public static class NetAccess
	{
		public static void Apply(ModuleDefinition module)
		{
			const string ns = "System.Runtime.CompilerServices";
			TypeDefinition attr = module.GetType(ns + ".IgnoresAccessChecksToAttribute");
			if (attr == null)
			{
				// System.Attribute from the game's own core library (mscorlib), as the rest of the game refers to it
				var baseType = new TypeReference("System", "Attribute", module, module.TypeSystem.CoreLibrary);
				attr = new TypeDefinition(ns, "IgnoresAccessChecksToAttribute", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, baseType);
				var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
					MethodAttributes.RTSpecialName, module.TypeSystem.Void);
				ctor.Parameters.Add(new ParameterDefinition("assemblyName", ParameterAttributes.None, module.TypeSystem.String));
				ILProcessor il = ctor.Body.GetILProcessor();
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Call, new MethodReference(".ctor", module.TypeSystem.Void, baseType) { HasThis = true });
				il.Emit(OpCodes.Ret);
				attr.Methods.Add(ctor);
				module.Types.Add(attr);
			}
			MethodDefinition use = attr.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 1);
			foreach (string name in new[] { "FNA", "ReLogic", "Terraria" })
			{
				var ca = new CustomAttribute(use);
				ca.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, name));
				module.Assembly.CustomAttributes.Add(ca);
			}
			Console.WriteLine("[net] IgnoresAccessChecksTo added");
		}
	}
}
