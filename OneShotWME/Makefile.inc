SRC := $(MAKECMDGOALS)/Steamworks.NET.cs $(MAKECMDGOALS)/OneShotMG.OneShotWMEPatches.mm.cs
ASM := $(SRC:%.cs=out/%.dll)
DEPS_FOLDER ?= deps/$(MAKECMDGOALS)
# Steamworks.NET.cs is a standalone drop-in replacement for the game's own Steamworks.NET.dll
# (see its header comment) and needs no deps of its own.
#
# OneShotMG.OneShotWMEPatches.mm.cs is a MonoMod patch, hooked by reflection with no
# reference to the game assembly (same reasoning as the BlossomTales2 patch: keeps one
# build working regardless of what the game's own exe happens to be named).
DEPS := deps/Cecil/*.dll \
        deps/MonoMod/MonoMod.exe \
        deps/MonoMod/MonoMod.Utils.dll \
        deps/MonoMod/MonoMod.RuntimeDetour.dll
