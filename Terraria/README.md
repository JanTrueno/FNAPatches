# Terraria patches

MonoMod patches for Terraria 1.4.5.8 (GOG Linux), for the PortMaster port on 1 GB ARM handhelds. The game runs on .NET 10 with our SDL2 builds of FNA, FNA3D and FAudio (their `terraria` branches). The port itself lives in the `jantrueno` branch of wild1walker's portmaster-terraria.

Most of the memory and speed work follows wild1walker's port (MIT), redone here as MonoMod patches. Each file says at the top what it takes from it.

## The patches

- **TerrariaPatches** (Terraria.exe, ReLogic.dll): the game calls SDL3 directly in a few places. Those calls go to SDL2 instead.
- **NetAccess**: lets the patched game reach private members under .NET.
- **Lazy**: textures and sounds load the first time they're used. Textures that haven't been drawn for a while are unloaded.
- **Fonts**: a font page gets its texture only when one of its glyphs is first drawn. Without this the fonts take about 200 MB.
- **TileRecords**: the world's tiles become 14-byte structs in one flat array instead of objects. This is the biggest memory saving.
- **Pack**: reads the textures from one ASTC archive made by [GPU-ASTC](https://github.com/JanTrueno/GPU-ASTC).
- **Speed**: wild1walker's trpatch passes:
  - draw cuts;
  - field and loop scans;
  - splitting the huge routines;
  - no zeroed locals.

`TileConverter/` holds the Cecil passes the patches' rules run, and `Passes.cs` runs them in a fixed order. They take effect when the game is patched. At that point `TERRARIA_SPEED=0` leaves out all the speed passes, and `TERRARIA_DRAWCUTS`, `TERRARIA_SCANS`, `TERRARIA_SPLIT` or `TERRARIA_INITS` set to `0` leave out one of them.

## Building

```bash
make Terraria
Terraria/make-port.sh
```

`make-port.sh` copies the build, MonoMod, FNA, FNA3D, FAudio and gpuastc into the port folder. With `PACK=1` it also writes `terraria.zip`. The script lists what it needs at the top.

To try the game on a PC: `test-net.sh` builds and runs it under .NET the way the handheld does, and `test-pc.sh` runs it under Mono.
