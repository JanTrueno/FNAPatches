SRC := $(MAKECMDGOALS)/BlossomTales.BlossomTalesPatches.mm.cs $(MAKECMDGOALS)/SlimDX.DirectInput.cs
ASM := $(SRC:%.cs=out/%.dll)
DEPS_FOLDER ?= deps/$(MAKECMDGOALS)
# The game assembly is deliberately NOT referenced, and there is no MMHOOK step. The patch
# hooks by reflection through MonoMod.RuntimeDetour.ILHook and looks its types up by name
# inside the assembly it is merged into. That avoids having to carry the space in
# "Blossom Tales.exe" through make prerequisites, and means the patch does not care what the
# executable is called.
DEPS := deps/Cecil/*.dll \
        deps/MonoMod/MonoMod.exe \
        deps/MonoMod/MonoMod.Utils.dll \
        deps/MonoMod/MonoMod.RuntimeDetour.dll
