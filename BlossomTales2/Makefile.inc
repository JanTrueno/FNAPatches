SRC := $(MAKECMDGOALS)/BlossomTales2.BlossomTales2Patches.mm.cs
ASM := $(SRC:%.cs=out/%.dll)
DEPS_FOLDER ?= deps/$(MAKECMDGOALS)
# The game assembly is deliberately NOT referenced. The patch hooks by reflection through
# MonoMod.RuntimeDetour.ILHook and looks its types up by name inside the assembly it is
# merged into, so one build applies to both BlossomTales2.exe (Linux) and
# "Blossom Tales 2.exe" (Windows). MonoMod resolves an assembly reference by searching for
# a file named after it, so referencing the game here would tie the patch to whichever
# build it was compiled against -- verified by it failing on exactly that.
#
# FNA.dll is the port's copy (dlls/), which is what actually gets loaded: the launcher
# deletes the game's own bundled FNA so MONO_PATH wins.
DEPS := \
        $(DEPS_FOLDER)/FNA.dll \
        deps/Cecil/*.dll \
        deps/MonoMod/MonoMod.exe \
        deps/MonoMod/MonoMod.Utils.dll \
        deps/MonoMod/MonoMod.RuntimeDetour.dll
