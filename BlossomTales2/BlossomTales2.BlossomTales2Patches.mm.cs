using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

#pragma warning disable CS0626

// Performance patches for Blossom Tales II: The Minotaur Prince, targeting handhelds.
//
// Same engine lineage as the first game, and the same fixed per-frame overhead, so these
// mirror BlossomTalesPatches. Blossom Tales I went 15-20fps -> 60fps; this one starts
// higher (35-40fps) and carries slightly more render target work, having five targets
// where the first game has four.
//
// None of this changes what the game looks like.
namespace BlossomTales2
{
    class patch_Program
    {
        // Hooks are held here because ILHook stops applying once collected.
        private static readonly List<ILHook> Hooks = new List<ILHook>();

        extern static void orig_Main(string[] args);

        private static void Main(string[] args)
        {
            // Hooked by reflection through ILHook rather than through generated MMHOOK
            // events, and the types are looked up by name in this very assembly rather
            // than referenced at compile time. Both matter for portability: the Linux build
            // ships as BlossomTales2.exe and the Windows build as "Blossom Tales 2.exe",
            // and MonoMod resolves an assembly reference by looking for a file named after
            // it -- so a patch that referenced the game assembly would only apply to the
            // build it was compiled against. With no reference at all, one patch fits both.
            //
            // After MonoMod merges this in, the executing assembly *is* the game.
            Assembly game = Assembly.GetExecutingAssembly();

            AddHook(game, "BlossomTales2.Game1", "LoadContent", Hook_Game1_LoadContent);

            // These own the finalTarget round-trip as well as their sprite batches.
            AddHook(game, "BlossomTales2.GameState_Menu", "Draw", Hook_DrawPath);
            AddHook(game, "BlossomTales2.GameState_Playing", "Draw", Hook_DrawPath);

            // Only needs the batching fix. (Lighting.Draw has no Immediate batches here,
            // unlike the first game, so it is not hooked.)
            AddHook(game, "BlossomTales2.Game1", "Draw", Hook_SortModesOnly);

            orig_Main(args);
        }

        private static void AddHook(Assembly game, string typeName, string name, ILContext.Manipulator manipulator)
        {
            try
            {
                Type type = game.GetType(typeName);
                if (type == null)
                {
                    Console.Out.WriteLine($"BlossomTales2Patches: type {typeName} not found, skipping");
                    return;
                }

                MethodInfo method = type.GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static);

                if (method == null)
                {
                    Console.Out.WriteLine($"BlossomTales2Patches: {typeName}.{name} not found, skipping");
                    return;
                }

                Hooks.Add(new ILHook(method, manipulator));
            }
            catch (Exception e)
            {
                // A failed hook costs performance, not correctness -- let the game run.
                Console.Out.WriteLine($"BlossomTales2Patches: failed to hook {typeName}.{name}: {e.Message}");
            }
        }

        // ---- Patch 1: oversized render targets ----------------------------------
        //
        // LoadContent allocates lightsTarget, satTarget, mainTarget and finalTarget at
        // 2048x2048 while the game only ever renders into a 1280x720 corner of them
        // (ScreenWidth/ScreenHeight are hardcoded to those values), and the final blit
        // reads back a 1280x720 source rectangle. On a tile-based GPU that is 4.19M pixels
        // cleared and resolved per target per frame to show 0.92M.
        //
        // mapTarget (1024x1024) is deliberately left alone -- it holds the world map.
        private static void Hook_Game1_LoadContent(ILContext il)
        {
            var instrs = il.Body.Instructions;
            int patched = 0;

            for (int i = 0; i + 2 < instrs.Count; i++)
            {
                if (!IsLdcI4(instrs[i], 2048) || !IsLdcI4(instrs[i + 1], 2048))
                    continue;
                if (instrs[i + 2].OpCode != OpCodes.Newobj)
                    continue;

                var ctor = instrs[i + 2].Operand as MethodReference;
                if (ctor == null || ctor.DeclaringType.Name != "RenderTarget2D")
                    continue;

                instrs[i].Operand = 1280;
                instrs[i + 1].Operand = 720;
                patched++;
            }

            Console.Out.WriteLine($"BlossomTales2Patches: resized {patched} render targets to 1280x720");
        }

        // ---- The two draw paths --------------------------------------------------

        private static void Hook_DrawPath(ILContext il)
        {
            int deferred = RetargetSortModes(il);
            int redirects = ApplyFinalTargetBypass(il);
            Console.Out.WriteLine($"BlossomTales2Patches: {il.Method.DeclaringType.Name}.Draw -- {deferred} batches deferred, {redirects} finalTarget binds redirected");
        }

        private static void Hook_SortModesOnly(ILContext il)
        {
            int deferred = RetargetSortModes(il);
            Console.Out.WriteLine($"BlossomTales2Patches: {il.Method.DeclaringType.Name}.Draw -- {deferred} batches deferred");
        }

        // ---- Patch 2: unbatched sprite draws -------------------------------------
        //
        // SpriteSortMode.Immediate flushes a draw call per sprite. Deferred batches them
        // and preserves submission order, so the output is identical.
        //
        // The lighting and saturation composites call EffectPass.Apply() between Begin and
        // Draw, which only takes effect in Immediate mode. All of them use the 2-argument
        // Begin(SpriteSortMode, BlendState) overload, and that parameter count is what
        // distinguishes them from every other batch here.
        private static int RetargetSortModes(ILContext il)
        {
            var instrs = il.Body.Instructions;
            int changed = 0;

            for (int i = 1; i < instrs.Count; i++)
            {
                // SpriteSortMode.Immediate == 1, pushed straight after the SpriteBatch instance.
                if (instrs[i].OpCode != OpCodes.Ldc_I4_1)
                    continue;
                if (!LoadsSpriteBatch(instrs[i - 1]))
                    continue;

                MethodReference begin = FindFollowingBegin(instrs, i);
                if (begin == null)
                    continue;
                if (begin.Parameters.Count == 2)
                    continue; // effect composite -- genuinely needs Immediate

                instrs[i].OpCode = OpCodes.Ldc_I4_0; // SpriteSortMode.Deferred
                changed++;
            }

            return changed;
        }

        private static bool LoadsSpriteBatch(Instruction instr)
        {
            if (instr.OpCode == OpCodes.Ldsfld)
            {
                var field = instr.Operand as FieldReference;
                return field != null && field.FieldType.Name == "SpriteBatch";
            }

            // Draw(SpriteBatch spriteBatch) -- the parameter is arg 1 on an instance method.
            return instr.OpCode == OpCodes.Ldarg_1;
        }

        private static MethodReference FindFollowingBegin(Mono.Collections.Generic.Collection<Instruction> instrs, int from)
        {
            for (int j = from + 1; j < instrs.Count && j < from + 48; j++)
            {
                if (instrs[j].OpCode != OpCodes.Callvirt && instrs[j].OpCode != OpCodes.Call)
                    continue;

                var method = instrs[j].Operand as MethodReference;
                if (method != null && method.Name == "Begin" && method.DeclaringType.Name == "SpriteBatch")
                    return method;
            }
            return null;
        }

        // ---- Patch 3: redundant finalTarget round-trip ----------------------------
        //
        // Both draw paths render into finalTarget and then blit it to the backbuffer at
        // AdditionalSCALE, which is backbufferWidth / 1280f. At the default resolution
        // (index 1 = 1280x720) that is exactly 1.0, making the round-trip a pure 1:1 copy
        // costing a render target bind, a full-screen clear, a full-screen blit and a tile
        // resolve for nothing.
        //
        // Unlike the first game, GameState_Menu.Draw binds finalTarget *twice* -- once for
        // the lighting composite and again, without clearing, for the fade and splash
        // overlays. Every bind is redirected, not just the first: redirecting only the first
        // would leave the overlay layer drawing into a finalTarget that is never presented,
        // silently losing fades and the splash.
        //
        // Both edits are guarded at runtime on AdditionalSCALE == 1f, so any other
        // resolution keeps the original, correctly-scaled path. The guard is emitted as
        // inline IL reading the live field rather than being decided at patch time --
        // AdditionalSCALE is reassigned on resolution changes and fullscreen toggles.
        private static int ApplyFinalTargetBypass(ILContext il)
        {
            FieldReference additionalScale = null;
            FieldReference finalTarget = null;

            // Resolve the fields out of the module being patched, so this never depends on
            // the patch assembly's own view of the game's types.
            foreach (TypeDefinition type in il.Method.Module.Types)
            {
                if (type.Name != "Game1")
                    continue;
                foreach (FieldDefinition field in type.Fields)
                {
                    if (field.Name == "AdditionalSCALE") additionalScale = field;
                    else if (field.Name == "finalTarget") finalTarget = field;
                }
            }

            if (additionalScale == null || finalTarget == null)
                return 0;

            var instrs = il.Body.Instructions;
            ILProcessor ilp = il.Body.GetILProcessor();

            // (a) Every bind that renders into finalTarget becomes a bind to null when the
            //     scale is 1. Emitted as:
            //         ldsfld finalTarget            (kept)
            //         ldsfld AdditionalSCALE
            //         ldc.r4 1.0
            //         bne.un keep                   -> scale != 1, use finalTarget as before
            //         pop                           -> otherwise swap it for null
            //         ldnull
            //     keep: callvirt SetRenderTarget
            int redirected = 0;
            for (int i = 0; i + 1 < instrs.Count; i++)
            {
                if (instrs[i].OpCode != OpCodes.Ldsfld || instrs[i].Operand != finalTarget)
                    continue;

                var call = instrs[i + 1].Operand as MethodReference;
                if (instrs[i + 1].OpCode != OpCodes.Callvirt || call == null || call.Name != "SetRenderTarget")
                    continue;

                Instruction keep = instrs[i + 1];
                ilp.InsertBefore(keep, ilp.Create(OpCodes.Ldsfld, additionalScale));
                ilp.InsertBefore(keep, ilp.Create(OpCodes.Ldc_R4, 1f));
                ilp.InsertBefore(keep, ilp.Create(OpCodes.Bne_Un, keep));
                ilp.InsertBefore(keep, ilp.Create(OpCodes.Pop));
                ilp.InsertBefore(keep, ilp.Create(OpCodes.Ldnull));
                redirected++;

                // The instruction list just grew; resume after the sequence we inserted.
                i = instrs.IndexOf(keep);
            }
            if (redirected == 0)
                return 0;

            // (b) Skip the trailing copy-back, which is the last statement of the method.
            //     Skipping it matters for correctness rather than just speed: it re-binds
            //     null and clears, which would wipe what was drawn straight to the backbuffer.
            Instruction ret = instrs[instrs.Count - 1];
            if (ret.OpCode != OpCodes.Ret)
                return redirected;

            int blit = -1;
            for (int i = instrs.Count - 1; i >= 1; i--)
            {
                var call = instrs[i].Operand as MethodReference;
                if (instrs[i].OpCode == OpCodes.Callvirt && call != null && call.Name == "SetRenderTarget"
                    && instrs[i - 1].OpCode == OpCodes.Ldnull)
                {
                    blit = i;
                    break;
                }
            }
            if (blit < 0)
                return redirected;

            // Walk back to the start of that statement (the `this` load that begins
            // game.GraphicsDevice), so the guard covers the whole block.
            int start = -1;
            for (int i = blit; i >= 0 && i > blit - 8; i--)
            {
                if (instrs[i].OpCode == OpCodes.Ldarg_0)
                {
                    start = i;
                    break;
                }
            }
            if (start < 0)
                return redirected;

            Instruction blockStart = instrs[start];
            ilp.InsertBefore(blockStart, ilp.Create(OpCodes.Ldsfld, additionalScale));
            ilp.InsertBefore(blockStart, ilp.Create(OpCodes.Ldc_R4, 1f));
            ilp.InsertBefore(blockStart, ilp.Create(OpCodes.Beq, ret));

            return redirected;
        }

        private static bool IsLdcI4(Instruction instr, int value)
        {
            return instr.OpCode == OpCodes.Ldc_I4 && instr.Operand is int && (int)instr.Operand == value;
        }
    }
}
