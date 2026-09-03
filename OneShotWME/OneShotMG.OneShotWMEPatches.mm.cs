using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

#pragma warning disable CS0626

// Fixes FMOD_ERR_HEADER_MISMATCH on Linux builds of libfmod.so.
//
// FMOD.Factory.System_Create calls the game's own [DllImport] declaration of
// FMOD5_System_Create(out IntPtr system) -- one argument. But FMOD5_System_Create is
// (confirmed by disassembling a real Linux libfmod.so.13 aarch64 build) a one-instruction
// tail-call trampoline straight into the real, documented FMOD_System_Create(FMOD_SYSTEM**,
// unsigned int headerversion) -- which takes *two* arguments. Since a naked branch doesn't
// touch registers, whatever garbage happens to be sitting in the second argument register
// at that moment gets passed as headerversion, which essentially never matches by chance --
// producing FMOD_ERR_HEADER_MISMATCH regardless of platform or which FMOD build is used.
//
// The version check itself is lenient: tested empirically (cross-compiled a native aarch64
// test binary, ran it under qemu-aarch64-static against the real .so) that this specific
// library accepts *any* header version in the whole 2.02.x line, not just an exact build
// match. So the fix isn't "find the exact matching FMOD release" -- it's "actually pass the
// argument the real function needs," with any reasonable 2.02.x constant.
namespace OneShotMG
{
    class patch_Program
    {
        private static readonly List<ILHook> Hooks = new List<ILHook>();

        // The real entry point (confirmed via ikdasm) is a parameterless static void
        // Main() -- not Main(string[] args). MonoMod's patch_ convention merges by exact
        // signature match, so this has to match precisely or the patch silently becomes a
        // dead, never-called overload sitting next to the untouched original -- exactly the
        // failure mode this one hit the first time around: no exception, no diagnostic
        // output, merge reports success, and the original unpatched behavior just persists.
        extern static void orig_Main();

        private static void Main()
        {
            Assembly game = Assembly.GetExecutingAssembly();
            AddHook(game, "FMOD.Factory", "System_Create", Hook_Factory_System_Create);

            // Removes the game's only two calls into System.Windows.Forms/GDI+, both
            // incidental rather than core functionality on Linux/Mono handhelds.
            // libgdiplus.so.0 itself depends on a full desktop GUI stack -- libX11,
            // libcairo, libfontconfig, libglib-2.0, libjpeg, libpng, libtiff, libgif,
            // libfreetype, libexif -- which a minimal embedded gaming OS plausibly doesn't
            // ship in whole or in part. Sourcing and bundling that entire dependency tree
            // just to keep two incidental calls working is the wrong shape of fix.
            AddHook(game, "OneShotMG.src.LogManager", "Log", Hook_LogManager_Log);
            AddHook(game, "OneShotMG.src.EngineSpecificCode.GraphicsManager", "generateAvailableResolutions", Hook_GenerateAvailableResolutions);

            // Makes the game fill a handheld's screen instead of opening a fixed 1280x720
            // window that overflows it. See each hook for the details.
            AddCtorHook(game, "OneShotMG.src.EngineSpecificCode.GraphicsManager", Hook_GraphicsManagerCtor);
            AddHook(game, "OneShotMG.src.EngineSpecificCode.GraphicsManager", "SetFullscreen", Hook_SetFullscreen);

            orig_Main();
        }

        private static void AddHook(Assembly game, string typeName, string name, ILContext.Manipulator manipulator)
        {
            try
            {
                Type type = game.GetType(typeName);
                if (type == null)
                {
                    Console.Out.WriteLine($"OneShotWMEPatches: type {typeName} not found, skipping");
                    return;
                }

                MethodInfo method = type.GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static);

                if (method == null)
                {
                    Console.Out.WriteLine($"OneShotWMEPatches: {typeName}.{name} not found, skipping");
                    return;
                }

                Hooks.Add(new ILHook(method, manipulator));
            }
            catch (Exception e)
            {
                Console.Out.WriteLine($"OneShotWMEPatches: failed to hook {typeName}.{name}: {e.Message}");
            }
        }

        private static void AddCtorHook(Assembly game, string typeName, ILContext.Manipulator manipulator)
        {
            try
            {
                Type type = game.GetType(typeName);
                if (type == null)
                {
                    Console.Out.WriteLine($"OneShotWMEPatches: type {typeName} not found, skipping");
                    return;
                }

                ConstructorInfo[] ctors = type.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (ctors.Length != 1)
                {
                    Console.Out.WriteLine($"OneShotWMEPatches: expected exactly one {typeName} constructor, found {ctors.Length}, skipping");
                    return;
                }

                Hooks.Add(new ILHook(ctors[0], manipulator));
            }
            catch (Exception e)
            {
                Console.Out.WriteLine($"OneShotWMEPatches: failed to hook {typeName}..ctor: {e.Message}");
            }
        }

        // Original body (4 instructions, confirmed via ikdasm on the real assembly):
        //   ldarg.0
        //   ldflda    native int FMOD.System::handle
        //   call      FMOD.RESULT FMOD.Factory::FMOD5_System_Create(native int&)
        //   ret
        // Same stack effect either side (one native int& in, one FMOD.RESULT/int32 out), so
        // this only needs to swap the call target -- nothing else in the method changes.
        private static void Hook_Factory_System_Create(ILContext il)
        {
            var instrs = il.Body.Instructions;
            ILProcessor ilp = il.Body.GetILProcessor();
            MethodInfo fixedCreate = typeof(patch_Program).GetMethod(
                nameof(FixedSystemCreate), BindingFlags.NonPublic | BindingFlags.Static);
            MethodReference fixedCreateRef = ilp.Import(fixedCreate);

            for (int i = 0; i < instrs.Count; i++)
            {
                var call = instrs[i].Operand as MethodReference;
                bool isBrokenCreate = instrs[i].OpCode == OpCodes.Call
                    && call != null && call.Name == "FMOD5_System_Create"
                    && call.DeclaringType.FullName == "FMOD.Factory";

                if (!isBrokenCreate)
                    continue;

                instrs[i].Operand = fixedCreateRef;
                Console.Out.WriteLine("OneShotWMEPatches: FMOD.Factory.System_Create -- redirected to a version-correct System_Create call");
                return;
            }

            Console.Out.WriteLine("OneShotWMEPatches: FMOD.Factory.System_Create -- FMOD5_System_Create call not found (already patched or game updated?)");
        }

        // FMOD_VERSION for the 2.02.x line. Confirmed empirically (not guessed) against a
        // real Linux libfmod.so.13 build that the header-version check only cares about
        // major.minor (2.02), not the exact patch/build number -- every value from
        // 0x00020202 through 0x00020218 succeeded in that test, only the 2.01.x line failed.
        // If a future libfmod.so build needs a different generation, this is the one
        // constant to change.
        private const uint FmodHeaderVersion = 0x00020202;

        [DllImport("x64/fmod", EntryPoint = "FMOD_System_Create")]
        private static extern int FMOD_System_Create_Real(out IntPtr system, uint headerversion);

        private static int FixedSystemCreate(ref IntPtr system)
        {
            return FMOD_System_Create_Real(out system, FmodHeaderVersion);
        }

        // LogManager.Log's Error case does:
        //   logFile.WriteLine(text); Console.WriteLine(text); MessageBox.Show(text);
        // The first two already surface the error message where it's actually useful (the
        // console and log.txt); MessageBox.Show is what needs GDI+ and used to crash on
        // this device. Removes exactly that call (push arg, call, pop return value) and
        // nothing else, so error logging keeps working -- including finally letting future
        // errors on this path actually be *seen* instead of being masked by a GDI+ crash,
        // the way the original Steam and FMOD failures both were before their real fixes.
        private static void Hook_LogManager_Log(ILContext il)
        {
            var instrs = il.Body.Instructions;

            for (int i = 0; i + 2 < instrs.Count; i++)
            {
                var call = instrs[i + 1].Operand as MethodReference;
                bool isMessageBoxShow = instrs[i + 1].OpCode == OpCodes.Call
                    && call != null && call.Name == "Show"
                    && call.DeclaringType.FullName == "System.Windows.Forms.MessageBox";

                if (!isMessageBoxShow)
                    continue;
                if (instrs[i + 2].OpCode != OpCodes.Pop)
                    continue;

                instrs.RemoveAt(i + 2); // pop
                instrs.RemoveAt(i + 1); // call MessageBox.Show
                instrs.RemoveAt(i);     // ldloc (the string argument)

                Console.Out.WriteLine("OneShotWMEPatches: removed MessageBox.Show from LogManager.Log");
                return;
            }

            Console.Out.WriteLine("OneShotWMEPatches: LogManager.Log -- MessageBox.Show call not found (already patched or game updated?)");
        }

        // generateAvailableResolutions() adds a handful of hardcoded resolutions (720p,
        // 900p, 1080p, plus 4:3 variants) and then extends that list with anything matching
        // a real connected monitor's size, via Screen.AllScreens. That extension is a
        // convenience for desktop players with unusual monitor sizes; it's not needed on a
        // single fixed-resolution handheld display, and every one of the base resolutions
        // is already in the list before this call ever runs.
        //
        // Redirects the property call to push an empty Screen[] instead: `ldc.i4.0; newarr
        // Screen`. That specifically avoids triggering System.Windows.Forms.Screen's own
        // static initializer (newarr does not run the element type's cctor, unlike calling
        // any of its members would), so nothing here touches GDI+ at all. The foreach loop
        // that follows simply doesn't execute on an empty array; the rest of the method
        // (the hardcoded resolutions) is untouched.
        private static void Hook_GenerateAvailableResolutions(ILContext il)
        {
            var instrs = il.Body.Instructions;
            ILProcessor ilp = il.Body.GetILProcessor();

            for (int i = 0; i < instrs.Count; i++)
            {
                var call = instrs[i].Operand as MethodReference;
                bool isAllScreens = instrs[i].OpCode == OpCodes.Call
                    && call != null && call.Name == "get_AllScreens"
                    && call.DeclaringType.FullName == "System.Windows.Forms.Screen";

                if (!isAllScreens)
                    continue;

                Instruction callInstr = instrs[i];
                TypeReference screenType = call.DeclaringType;

                ilp.InsertBefore(callInstr, ilp.Create(OpCodes.Ldc_I4_0));
                callInstr.OpCode = OpCodes.Newarr;
                callInstr.Operand = screenType;

                Console.Out.WriteLine("OneShotWMEPatches: GraphicsManager.generateAvailableResolutions -- Screen.AllScreens replaced with an empty array");
                return;
            }

            Console.Out.WriteLine("OneShotWMEPatches: generateAvailableResolutions -- Screen.AllScreens call not found (already patched or game updated?)");
        }

        // Makes the game start filling the screen on a handheld.
        //
        // GraphicsManager's constructor hardcodes a *windowed* 1280x720 backbuffer and only
        // ever calls SetFullscreen(true) on the Steam Deck path -- which never runs here,
        // since the Steamworks stub correctly reports "not a Steam Deck". On a panel smaller
        // than 720p the window is simply larger than the display and only its top-left
        // corner is visible (on a 640x480 device, almost exactly a quarter of the game).
        //
        // It's not visible over SSH because of a coincidence rather than anything working
        // as intended: launched with no display server, SDL drives KMSDRM directly and
        // clamps the oversized window down to a real panel mode, which fires
        // ClientSizeChanged -> GraphicsManager.OnResize, and that handler snaps the
        // backbuffer back to something that fits. Launched from the frontend the window is
        // created at its literal requested size, no resize event ever fires, and nothing
        // corrects it. OnResize is also explicitly a no-op while fullscreen -- so going
        // fullscreen doesn't just paper over the symptom, it removes the dependency on that
        // accidental correction entirely.
        //
        // Appends to the end of the constructor:
        //   1. SetFullscreen(true) -- with HardwareModeSwitch already false, this is
        //      borderless-fullscreen-at-desktop-size, so it needs no modeset and works even
        //      where the frontend still owns the display.
        //   2. SetDrawResolution(GetDrawResolutionForOneshotMaximized()) -- the game's own
        //      helper, which derives an aspect-correct draw size from the now-correct output
        //      size. Worth using rather than picking a resolution here: the final blit
        //      (EndDrawCycle) stretches draw size to output size with no letterboxing, so a
        //      mismatched aspect would visibly distort, and this is exactly the routine the
        //      game already trusts to avoid that.
        private static void Hook_GraphicsManagerCtor(ILContext il)
        {
            var instrs = il.Body.Instructions;
            ILProcessor ilp = il.Body.GetILProcessor();
            MethodInfo helper = typeof(patch_Program).GetMethod(
                nameof(ApplyHandheldDisplayDefaults), BindingFlags.NonPublic | BindingFlags.Static);
            MethodReference helperRef = ilp.Import(helper);

            Instruction last = instrs.Count > 0 ? instrs[instrs.Count - 1] : null;
            if (last == null || last.OpCode != OpCodes.Ret)
            {
                Console.Out.WriteLine("OneShotWMEPatches: GraphicsManager..ctor -- unexpected final instruction, skipping");
                return;
            }

            // Rewrites the existing trailing `ret` in place into `ldarg.0` and appends the
            // call plus a fresh `ret` after it, rather than inserting ahead of it. The
            // constructor ends in `if (IsOnSteamDeck) { ... }`, which compiles to a branch
            // straight to this `ret` -- and since the stub makes that condition false, that
            // branch is always taken. Inserting before the `ret` would leave that branch
            // pointing past the new code, so it would never run. Mutating the instruction
            // the branch already targets means every path reaches it.
            last.OpCode = OpCodes.Ldarg_0;
            last.Operand = null;
            ilp.Append(ilp.Create(OpCodes.Call, helperRef));
            ilp.Append(ilp.Create(OpCodes.Ret));

            Console.Out.WriteLine("OneShotWMEPatches: GraphicsManager..ctor -- start fullscreen at the display's own resolution");
        }

        // Called by reflection rather than against typed references so this patch keeps
        // needing no compile-time reference to the game assembly or to MonoGame.
        private static void ApplyHandheldDisplayDefaults(object graphicsManager)
        {
            try
            {
                Type gm = graphicsManager.GetType();
                const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                MethodInfo setFullscreen = gm.GetMethod("SetFullscreen", Instance);
                MethodInfo getMaximizedRes = gm.GetMethod("GetDrawResolutionForOneshotMaximized", Instance);
                MethodInfo setDrawResolution = gm.GetMethod("SetDrawResolution", Instance);

                if (setFullscreen == null)
                {
                    Console.Out.WriteLine("OneShotWMEPatches: GraphicsManager.SetFullscreen not found, leaving display mode alone");
                    return;
                }

                setFullscreen.Invoke(graphicsManager, new object[] { true });

                // Only meaningful once fullscreen has updated the output size above, since
                // the draw resolution is derived from it -- hence the ordering.
                if (getMaximizedRes != null && setDrawResolution != null)
                {
                    object res = getMaximizedRes.Invoke(graphicsManager, null);
                    setDrawResolution.Invoke(graphicsManager, new object[] { res });
                    Console.Out.WriteLine($"OneShotWMEPatches: output {Describe(gm, graphicsManager, "OutputScreenSize")}, draw resolution {Describe(res)}");
                }
            }
            catch (Exception e)
            {
                Console.Out.WriteLine($"OneShotWMEPatches: failed to apply handheld display defaults: {e.Message}");
            }
        }

        // Vec2 has no ToString of its own, so logging one directly just prints the type name.
        // Reading X/Y out by hand is worth the few lines: whether the game picked up the
        // panel's real size is the single most useful thing to be able to read back off a
        // device log if a display ever comes out wrong again.
        private static string Describe(object vec2)
        {
            if (vec2 == null)
                return "<null>";

            try
            {
                Type type = vec2.GetType();
                FieldInfo x = type.GetField("X");
                FieldInfo y = type.GetField("Y");

                if (x == null || y == null)
                    return vec2.ToString();

                return $"{x.GetValue(vec2)}x{y.GetValue(vec2)}";
            }
            catch
            {
                return vec2.ToString();
            }
        }

        private static string Describe(Type ownerType, object owner, string propertyName)
        {
            try
            {
                PropertyInfo property = ownerType.GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                return property == null ? "<unknown>" : Describe(property.GetValue(owner, null));
            }
            catch
            {
                return "<unknown>";
            }
        }

        // Keeps the game from ever dropping back into the broken windowed path.
        //
        // Fixing only the constructor would still regress on any device that already has a
        // desktop save: loading one calls SetFullscreen(data.isFullscreen), and a save
        // written while the display was misbehaving stores false -- so the game would come
        // up correctly and then put itself back into an oversized window a moment later.
        // Something else does the same on focus loss.
        //
        // Forces the argument to true (`ldarg.1` -> `ldc.i4.1`) rather than emptying the
        // method, so the "enter fullscreen" direction still works normally and only the
        // request to leave it is ignored. Windowed mode has no sensible meaning on a fixed
        // single-display handheld in the first place.
        private static void Hook_SetFullscreen(ILContext il)
        {
            var instrs = il.Body.Instructions;
            bool patched = false;

            for (int i = 0; i < instrs.Count; i++)
            {
                if (!LoadsFullscreenArgument(instrs[i]))
                    continue;

                instrs[i].OpCode = OpCodes.Ldc_I4_1;
                instrs[i].Operand = null;
                patched = true;
            }

            Console.Out.WriteLine(patched
                ? "OneShotWMEPatches: GraphicsManager.SetFullscreen -- pinned to fullscreen"
                : "OneShotWMEPatches: GraphicsManager.SetFullscreen -- argument load not found (already patched or game updated?)");
        }

        // The method as compiled loads its argument with the compact `ldarg.1` macro form,
        // but a manipulator isn't guaranteed to see instructions exactly as they were
        // compiled -- macro forms may have been expanded to the long `ldarg`/`ldarg.s`
        // encoding before this runs. Matching only `ldarg.1` would then quietly find
        // nothing and leave the method unpatched, so both encodings are accepted.
        //
        // In an instance method argument 1 is the first declared parameter (argument 0 being
        // `this`), which Cecil exposes as index 0 of the parameter list -- hence the
        // apparent off-by-one when checking the long form's operand.
        private static bool LoadsFullscreenArgument(Instruction instr)
        {
            if (instr.OpCode == OpCodes.Ldarg_1)
                return true;

            if (instr.OpCode != OpCodes.Ldarg && instr.OpCode != OpCodes.Ldarg_S)
                return false;

            ParameterDefinition parameter = instr.Operand as ParameterDefinition;
            return parameter != null && parameter.Index == 0;
        }
    }
}
