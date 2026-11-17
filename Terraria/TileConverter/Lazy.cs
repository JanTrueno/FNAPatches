using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The IL edits behind lazy textures (see ReLogic.Lazy.mm.cs and Terraria.Lazy.mm.cs), run from the MonoMod rules of those files.
namespace TerrariaPatches.TileConvert
{
	public static class LazyRules
	{

		private static MethodReference Own(ModuleDefinition m, string type, string method)
		{
			return m.ImportReference(m.GetType(type).Methods.Single(x => x.Name == method));
		}

		public static int Relogic(ModuleDefinition m)
		{
			int n = 0;
			MethodReference ensure = Own(m, "TrPatches.Assets", "Ensure");
			MethodReference ensureDeferred = Own(m, "TrPatches.Assets", "EnsureDeferred");
			MethodReference setRepository = Own(m, "TrPatches.Assets", "SetRepository");
			MethodReference loadedHook = Own(m, "TrPatches.Assets", "Loaded");
			TypeDefinition asset = m.GetType("ReLogic.Content.Asset`1");
			// the field Evict reads and writes
			FieldDefinition used = asset.Fields.FirstOrDefault(f => f.Name == "trUsed");
			if (used == null) { used = new FieldDefinition("trUsed", Mono.Cecil.FieldAttributes.Public, m.TypeSystem.Int32); asset.Fields.Add(used); }
			GenericInstanceType self = new GenericInstanceType(asset);
			self.GenericArguments.Add(asset.GenericParameters[0]);
			FieldReference usedRef = new FieldReference(used.Name, m.TypeSystem.Int32, self);
			FieldReference now = new FieldReference("Now", m.TypeSystem.Int32, m.ImportReference(m.GetType("TrPatches.Evict")));
			MethodDefinition getState = asset.Methods.Single(x => x.Name == "get_State");
			MethodReference getStateRef = new MethodReference(getState.Name, getState.ReturnType, self) { HasThis = true };

			// get_Value loads a texture that has not been loaded, and marks it used; get_IsLoaded loads one that was deferred
			foreach (var pair in new[] { new { Name = "get_Value", Call = ensure }, new { Name = "get_IsLoaded", Call = ensureDeferred } })
			{
				Mono.Cecil.Cil.MethodBody body = asset.Methods.Single(x => x.Name == pair.Name).Body;
				ILProcessor il = body.GetILProcessor();
				Instruction first = body.Instructions[0];
				il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
				il.InsertBefore(first, il.Create(OpCodes.Call, getStateRef));
				il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
				il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
				il.InsertBefore(first, il.Create(OpCodes.Call, pair.Call));
				if (pair.Name == "get_Value")
				{
					Instruction f2 = body.Instructions[0];
					il.InsertBefore(f2, il.Create(OpCodes.Ldarg_0));
					il.InsertBefore(f2, il.Create(OpCodes.Ldsfld, now));
					il.InsertBefore(f2, il.Create(OpCodes.Stfld, usedRef));
				}
				n++;
			}
			// SubmitLoadedContent tells the runtime
			MethodDefinition submit = asset.Methods.Single(x => x.Name == "SubmitLoadedContent");
			ILProcessor sil = submit.Body.GetILProcessor();
			Instruction s0 = submit.Body.Instructions[0];
			sil.InsertBefore(s0, sil.Create(OpCodes.Ldarg_0));
			sil.InsertBefore(s0, sil.Create(OpCodes.Ldarg_1));
			sil.InsertBefore(s0, sil.Create(OpCodes.Box, asset.GenericParameters[0]));
			sil.InsertBefore(s0, sil.Create(OpCodes.Call, loadedHook));
			n++;
			// everywhere else in Asset<T>, "Value" is the field: Unload, Dispose and SubmitLoadedContent must not load what they are about to drop
			FieldDefinition backing = asset.Fields.Single(f => f.Name == "<Value>k__BackingField");
			FieldReference backingRef = new FieldReference(backing.Name, backing.FieldType, self);
			foreach (MethodDefinition method in asset.Methods.Where(x => x.HasBody && x.Name != "get_Value" && x.Name != "op_Explicit"))
			{
				foreach (Instruction ins in method.Body.Instructions)
				{
					MethodReference mr = ins.Operand as MethodReference;
					if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && mr != null && mr.Name == "get_Value")
					{
						ins.OpCode = OpCodes.Ldfld;
						ins.Operand = backingRef;
						n++;
					}
				}
			}
			// the repository the runtime asks to load
			foreach (MethodDefinition ctor in m.GetType("ReLogic.Content.AssetRepository").Methods.Where(x => x.IsConstructor && !x.IsStatic))
			{
				ILProcessor cil = ctor.Body.GetILProcessor();
				foreach (Instruction ret in ctor.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
				{
					Instruction ld = cil.Create(OpCodes.Ldarg_0);
					cil.InsertBefore(ret, ld);
					cil.InsertBefore(ret, cil.Create(OpCodes.Call, setRepository));
					foreach (Instruction j in ctor.Body.Instructions)
					{
						if (j.Operand == ret) j.Operand = ld;
					}
					n++;
				}
			}
			// the texture pack: the content source answers a packed name with an eight-byte stand-in, the XNB reader makes the texture
			// of a stand-in (PortMaster port, trpatch Install.cs)
			MethodReference packOpen = Own(m, "TrPatches.Assets", "PackOpenHook");
			MethodReference packFrom = Own(m, "TrPatches.Assets", "PackFromHook");
			MethodDefinition openStream = m.GetType("ReLogic.Content.Sources.XnaContentSource").Methods.Single(x => x.Name == "OpenStream");
			ILProcessor oil = openStream.Body.GetILProcessor();
			Instruction o0 = openStream.Body.Instructions[0];
			Instruction pop = oil.Create(OpCodes.Pop);
			oil.InsertBefore(o0, oil.Create(OpCodes.Ldarg_1));
			oil.InsertBefore(o0, oil.Create(OpCodes.Call, packOpen));
			oil.InsertBefore(o0, oil.Create(OpCodes.Dup));
			oil.InsertBefore(o0, oil.Create(OpCodes.Brfalse, pop));
			oil.InsertBefore(o0, oil.Create(OpCodes.Ret));
			oil.InsertBefore(o0, pop);
			MethodDefinition fromStream = m.GetType("ReLogic.Content.Readers.XnbReader").Methods.Single(x => x.Name == "FromStream");
			ILProcessor fil2 = fromStream.Body.GetILProcessor();
			Instruction f0 = fromStream.Body.Instructions[0];
			Instruction pop2 = fil2.Create(OpCodes.Pop);
			fil2.InsertBefore(f0, fil2.Create(OpCodes.Ldarg_1));
			fil2.InsertBefore(f0, fil2.Create(OpCodes.Call, packFrom));
			fil2.InsertBefore(f0, fil2.Create(OpCodes.Dup));
			fil2.InsertBefore(f0, fil2.Create(OpCodes.Brfalse, pop2));
			fil2.InsertBefore(f0, fil2.Create(OpCodes.Unbox_Any, fromStream.GenericParameters[0]));
			fil2.InsertBefore(f0, fil2.Create(OpCodes.Ret));
			fil2.InsertBefore(f0, pop2);
			n += 2;
			return n;
		}
	

			static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
			{
				return new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));
			}

			static MethodReference Static(ModuleDefinition m, AssemblyNameReference asm, string type, string name, TypeReference returns, params TypeReference[] args)
			{
				int dot = type.LastIndexOf('.');
				TypeReference t = new TypeReference(type.Substring(0, dot), type.Substring(dot + 1), m, asm);
				MethodReference r = new MethodReference(name, returns, t);
				foreach (TypeReference a in args) r.Parameters.Add(new ParameterDefinition(a));
				return r;
			}

			public static int Terraria(ModuleDefinition m)
			{
				int n = 0;
				AssemblyNameReference relogic = m.AssemblyReferences.First(a => a.Name == "ReLogic");
				TypeReference obj = m.TypeSystem.Object, voidT = m.TypeSystem.Void;
				TypeReference assetState = new TypeReference("ReLogic.Content", "AssetState", m, relogic, true);
				TypeReference texture2D = m.GetTypeReferences().First(t => t.FullName == "Microsoft.Xna.Framework.Graphics.Texture2D");
				GenericInstanceType assetOfTexture = new GenericInstanceType(m.GetTypeReferences().First(t => t.FullName == "ReLogic.Content.Asset`1"));
				assetOfTexture.GenericArguments.Add(texture2D);
				MethodReference defer = Static(m, relogic, "TrPatches.Assets", "Defer", voidT, obj);
				MethodReference state = Static(m, relogic, "TrPatches.Evict", "State", assetState, assetOfTexture);
				MethodReference tick = Static(m, relogic, "TrPatches.Evict", "Tick", voidT);
				MethodReference drawn = Static(m, relogic, "TrPatches.Evict", "Drawn", voidT, texture2D);

				// startup requests: made without loading, and noted as deferred
				MethodDefinition loadTextures = m.GetType("Terraria.Initializers.AssetInitializer").Methods.Single(x => x.Name == "LoadTextures");
				ILProcessor il = loadTextures.Body.GetILProcessor();
				foreach (Instruction ld in loadTextures.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldarg_0).ToList())
				{
					Instruction next = ld.Next;
					while (next.OpCode != OpCodes.Call && next.OpCode != OpCodes.Callvirt) next = next.Next;
					MethodReference called = next.Operand as MethodReference;
					if (called != null && called.Name == "LoadAsset")
					{
						ld.OpCode = OpCodes.Ldc_I4_0;
						il.InsertAfter(next, il.Create(OpCodes.Call, defer));
						il.InsertAfter(next, il.Create(OpCodes.Dup));
						n++;
					}
				}
				// the fonts load at once
				MethodDefinition loadFonts = m.GetType("Terraria.Initializers.AssetInitializer").Methods.Single(x => x.Name == "LoadFonts");
				ILProcessor fil = loadFonts.Body.GetILProcessor();
				Instruction f0 = loadFonts.Body.Instructions[0];
				fil.InsertBefore(f0, fil.Create(OpCodes.Ldc_I4_1));
				fil.InsertBefore(f0, fil.Create(OpCodes.Starg, loadFonts.Parameters[0]));
				n++;
				// sound effects asked for with AsyncLoad (2) are not loaded either
				foreach (MethodDefinition method in m.GetType("Terraria.Audio.LegacySoundPlayer").Methods.Where(x => x.HasBody))
				{
					ILProcessor sil = method.Body.GetILProcessor();
					foreach (Instruction ins in method.Body.Instructions.ToList())
					{
						GenericInstanceMethod gim = ins.Operand as GenericInstanceMethod;
						if (gim != null && gim.ElementMethod.Name == "Request" && gim.GenericArguments[0].Name == "SoundEffect" && ins.Previous != null && ins.Previous.OpCode == OpCodes.Ldc_I4_2)
						{
							ins.Previous.OpCode = OpCodes.Ldc_I4_0;
							sil.InsertAfter(ins, sil.Create(OpCodes.Call, defer));
							sil.InsertAfter(ins, sil.Create(OpCodes.Dup));
							n++;
						}
					}
				}
				// texture requests stored in fields and arrays elsewhere (outside the UI, which loads at once)
				foreach (TypeDefinition type in m.Types.SelectMany(Nested))
				{
					if (type.FullName.Contains("UI") || type.FullName == "Terraria.Initializers.AssetInitializer") continue;
					foreach (MethodDefinition method in type.Methods.Where(x => x.HasBody))
					{
						ILProcessor ril = method.Body.GetILProcessor();
						foreach (Instruction ins in method.Body.Instructions.ToList())
						{
							GenericInstanceMethod gim = ins.Operand as GenericInstanceMethod;
							if (gim != null && gim.ElementMethod.Name == "Request" && gim.GenericArguments[0].Name == "Texture2D"
								&& ins.Previous != null && ins.Previous.OpCode == OpCodes.Ldc_I4_1
								&& ins.Next != null && (ins.Next.OpCode == OpCodes.Stfld || ins.Next.OpCode == OpCodes.Stsfld || ins.Next.OpCode == OpCodes.Stelem_Ref))
							{
								ins.Previous.OpCode = OpCodes.Ldc_I4_0;
								ril.InsertAfter(ins, ril.Create(OpCodes.Call, defer));
								ril.InsertAfter(ins, ril.Create(OpCodes.Dup));
								n++;
							}
						}
					}
				}
				// asking a texture's state counts as using it (the draw hook counts the rest)
				int reads = 0;
				foreach (MethodDefinition method in m.Types.SelectMany(Nested).SelectMany(t => t.Methods).Where(x => x.HasBody))
				{
					foreach (Instruction ins in method.Body.Instructions)
					{
						GenericInstanceType decl = (ins.Operand as MethodReference) != null ? ((MethodReference) ins.Operand).DeclaringType as GenericInstanceType : null;
						if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && decl != null && ((MethodReference) ins.Operand).Name == "get_State"
							&& decl.ElementType.FullName == "ReLogic.Content.Asset`1" && decl.GenericArguments[0].FullName == "Microsoft.Xna.Framework.Graphics.Texture2D")
						{
							ins.OpCode = OpCodes.Call;
							ins.Operand = state;
							reads++;
						}
					}
				}
				Console.WriteLine("[lazy] " + reads + " texture state reads");
				n += reads;
				// the tile batch draws without a SpriteBatch
				foreach (string name in new[] { "RenderBatch", "SwitchBatch" })
				{
					MethodDefinition batch = m.GetType("Terraria.Graphics.TileBatch").Methods.Single(x => x.Name == name);
					if (batch.Parameters[0].ParameterType.Name != "Texture2D") throw new InvalidOperationException("TileBatch." + name + " does not take its texture first");
					ILProcessor bil = batch.Body.GetILProcessor();
					Instruction b0 = batch.Body.Instructions[0];
					bil.InsertBefore(b0, bil.Create(OpCodes.Ldarg_1));
					bil.InsertBefore(b0, bil.Create(OpCodes.Call, drawn));
					n++;
				}
				n += Sizes(m, relogic);
				// the frame loop: loads queued by other threads, the unloading
				MethodDefinition update = m.GetType("Terraria.Main").Methods.Single(x => x.Name == "Update" && x.Parameters.Count == 1);
				ILProcessor uil = update.Body.GetILProcessor();
				uil.InsertBefore(update.Body.Instructions[0], uil.Create(OpCodes.Call, tick));
				n++;
				return n;
			}
		

		// fonts: each page read as a stand-in, its texture loaded when first drawn (PortMaster port, trpatch Install.cs)
		public static int Fonts(ModuleDefinition m)
		{
			int changes = 0;
			MethodReference readPage = m.ImportReference(m.GetType("TrPatches.Fonts").Methods.Single(x => x.Name == "ReadPage"));
			MethodReference resolve = m.ImportReference(m.GetType("TrPatches.Fonts").Methods.Single(x => x.Name == "Resolve"));
			foreach (Instruction ins in m.GetType("ReLogic.Graphics.DynamicSpriteFontReader").Methods.Single(x => x.Name == "Read").Body.Instructions)
			{
				GenericInstanceMethod g = ins.Operand as GenericInstanceMethod;
				if (g != null && g.ElementMethod.Name == "ReadObject" && g.GenericArguments[0].Name == "Texture2D")
				{
					ins.OpCode = OpCodes.Call;
					ins.Operand = readPage;
					changes++;
				}
			}
			TypeDefinition font = m.GetType("ReLogic.Graphics.DynamicSpriteFont");
			foreach (MethodDefinition method in font.Methods.Concat(font.NestedTypes.SelectMany(t => t.Methods)).Where(x => x.HasBody))
			{
				ILProcessor il = method.Body.GetILProcessor();
				foreach (Instruction ins in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference
					&& ((FieldReference) i.Operand).Name == "Texture" && ((FieldReference) i.Operand).DeclaringType.Name == "SpriteCharacterData").ToList())
				{
					il.InsertAfter(ins, il.Create(OpCodes.Call, resolve));
					changes++;
				}
			}
			return changes;
		}

		// Utils.Width/Height/Size/Frame of an Asset<Texture2D> answer from TrPatches.Sizes (the deferred textures' sizes without
		// loading them); PortMaster port, trpatch Install.cs PatchSizes
		static int Sizes(ModuleDefinition m, AssemblyNameReference relogic)
		{
			int changes = 0;
			TypeDefinition utils = m.GetType("Terraria.Utils");
			TypeReference sizes = new TypeReference("TrPatches", "Sizes", m, relogic);
			foreach (string name in new[] { "Width", "Height", "Size", "Frame" })
			{
				MethodDefinition method = utils.Methods.Single(x => x.Name == name && x.Parameters.Count > 0 && x.Parameters[0].ParameterType.Name == "Asset`1");
				MethodReference target = new MethodReference(name, method.ReturnType, sizes);
				foreach (ParameterDefinition p in method.Parameters) target.Parameters.Add(new ParameterDefinition(p.ParameterType));
				Mono.Cecil.Cil.MethodBody body = method.Body;
				body.Instructions.Clear();
				body.ExceptionHandlers.Clear();
				body.Variables.Clear();
				ILProcessor il = body.GetILProcessor();
				for (int i = 0; i < method.Parameters.Count; i++) il.Append(il.Create(OpCodes.Ldarg, method.Parameters[i]));
				il.Append(il.Create(OpCodes.Call, target));
				il.Append(il.Create(OpCodes.Ret));
				changes++;
			}
			return changes;
		}

		// Main.Initialize hands the archive reader (TrPatches.Pack) to ReLogic's loaders before the first asset is read
		public static int Pack(ModuleDefinition m)
		{
			MethodDefinition init = m.GetType("Terraria.Main").Methods.Single(x => x.Name == "Initialize" && x.Parameters.Count == 0 && !x.IsStatic);
			ILProcessor il = init.Body.GetILProcessor();
			il.InsertBefore(init.Body.Instructions[0], il.Create(OpCodes.Call, m.ImportReference(m.GetType("TrPatches.Pack").Methods.Single(x => x.Name == "Register"))));
			return 1;
		}
	}
}
