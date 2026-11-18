using System;
using Microsoft.Xna.Framework;
using MonoMod;
using MonoMod.InlineRT;
using Terraria;
using Terraria.Graphics.Light;

#pragma warning disable 1591, 0649, 0169, 0414

// Bosses of many parts (wild1walker, portmaster-terraria, MIT: tools/trpatch/Bosses.cs and runtime/Boss.cs): work the game
// does for every part of the Destroyer, the Twins and Skeletron Prime, or for every NPC, whose answer is known without it.
// The same values, the same writes in the same order; the pass (TileConverter/Bosses.cs) matches each place on the game's
// code and sums the routines the cuts rely on, and leaves a cut out when anything differs:
//   sight   the Destroyer's health bar: the nearest segment in sight asked first, the rest not asked
//   dark    an NPC drawn behind the tiles: the first dark tile decides it
//   immune  an NPC's 256 hit counters: the countdown passed while none is above 0
//   tiles   a Destroyer segment in the blocks: the squares first, and the first tile ends it
//   wet     the look for liquid at an entity's centre: the squares that cannot meet it passed
//   buffs   an NPC's walks over its buff slots, passed while none holds a buff
// On the RK3326 (G350) the Destroyer went from 41.1 to 47.3 frames a second with them. TERRARIA_BOSSES=0 leaves the pass out
// when patching; at run time TR_BOSS_SIGHT=0, TR_BOSS_DARK=0, TR_BOSS_IMMUNE=0, TR_BOSS_TILES=0, TR_BOSS_WET=0,
// TR_BOSS_BUFFS=0 leave one out, and TR_BOSS_AUDIT=1 has the game do its own work and compares.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_SPEED") == "0" || Environment.GetEnvironmentVariable("TERRARIA_BOSSES") == "0")
				return;
			// the sums are taken of the game's code as shipped: here, before MonoMod weaves anything (the weave rewrites the
			// bodies' branches, and the sums take each instruction's offset); the cuts are matched before the tile conversion
			TerrariaPatches.TileConvert.BossesPass.Look(MonoModRule.Modder.Module);
			bool hook = TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Bosses, "bosses", m => TerrariaPatches.TileConvert.BossesPass.Apply(m));
			if (hook)
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    // Fights with bosses of many parts (the Destroyer's 80-odd segments, the Twins, Skeletron Prime and
    // its arms): work the game does for every part, or every NPC, whose answer is known without it.
    // Each cut gives the values the game's own code gives and makes the same writes in the same order;
    // the patcher (trpatch Bosses.cs) checks on the game's code that the places it changes are the ones
    // this was written for.
    //   sight   the Destroyer's health bar: the nearest segment the player can see
    //   dark    an NPC drawn behind the tiles is drawn black when a tile around it is dark
    //   immune  the countdown of an NPC's 256 hit counters, passed while all are at 0 or below
    //   tiles   whether a Destroyer segment is in the blocks
    //   wet     the look for liquid at an entity's centre (Collision.WetCollision): squares that cannot meet it passed
    //   buffs   an NPC's walks over its buff slots, passed while no slot holds a buff
    // TR_BOSS_SIGHT=0, TR_BOSS_DARK=0, TR_BOSS_IMMUNE=0, TR_BOSS_TILES=0, TR_BOSS_WET=0, TR_BOSS_BUFFS=0 leave
    // one out; TR_BOSS_AUDIT=1 has the game do its own work for all six and compares what the cut would
    // have given.
    public static unsafe class Boss
    {
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_BOSS_AUDIT") == "1";
        static readonly bool sightOn = Environment.GetEnvironmentVariable("TR_BOSS_SIGHT") != "0";
        static readonly bool immuneOn = Environment.GetEnvironmentVariable("TR_BOSS_IMMUNE") != "0";
        // read by the patched routines (a branch around the game's own loop)
        public static readonly bool DarkOn = Environment.GetEnvironmentVariable("TR_BOSS_DARK") != "0" && !audit;
        public static readonly bool TilesOn = Environment.GetEnvironmentVariable("TR_BOSS_TILES") != "0" && !audit;
        // the guards in Collision.WetCollision: worked out (WetGuard), and followed (WetOn)
        public static readonly bool WetGuard = Environment.GetEnvironmentVariable("TR_BOSS_WET") != "0";
        public static readonly bool WetOn = WetGuard && !audit;
        static readonly bool buffsOn = Environment.GetEnvironmentVariable("TR_BOSS_BUFFS") != "0";
        // (the counts: BossCount, which the stats line reads without starting this class on its thread)

        static void Wrong(string what)
        {
            if (BossCount.wrong++ < 20)
                Console.Error.WriteLine("[trpatch] boss: " + what);
        }

        // ---- sight. The game's search goes through the segments in order and asks Collision.CanHit
        // (a walk over the tiles between) of every one nearer than the best so far; it keeps the nearest
        // segment in sight, the lowest index of those as near. Asked in order of distance, then index,
        // the first in sight is that one, and the game's loop is given true for it alone: every segment
        // asked before it in that order is out of sight, and the loop takes the one it is given whatever
        // it takes before. CanHit only reads, so asking fewer changes nothing else.
        static bool sightNew;
        static int sightBest = -1, gameBest = -1;
        static float[] sightD = new float[256];
        static int[] sightJ = new int[256];

        // where the game's search begins
        public static void SightBegin()
        {
            if (audit && !sightNew && BossCount.searches > 0)
            {
                BossCount.compared++;
                if (gameBest != sightBest)
                    Wrong($"the Destroyer's nearest segment in sight is {gameBest} for the game, {sightBest} here");
            }
            sightNew = true;
            gameBest = -1;
        }

        // in place of Collision.CanHit(a, w1, h1, b, w2, h2) in the search, for segment j
        public static bool Sight(Vector2 a, int w1, int h1, Vector2 b, int w2, int h2, int j)
        {
            if (!sightOn)
                return Collision.CanHit(a, w1, h1, b, w2, h2);
            if (sightNew)
            {
                sightNew = false;
                BossCount.searches++;
                sightBest = Nearest(a);
            }
            if (audit)
            {
                bool hit = Collision.CanHit(a, w1, h1, b, w2, h2);
                if (hit)
                    gameBest = j;
                return hit;
            }
            return j == sightBest;
        }

        // the segment the game's search ends with (a: the player's centre), or -1
        static int Nearest(Vector2 a)
        {
            int max = Main.maxNPCs, n = 0;
            if (sightD.Length < max)
            {
                sightD = new float[max];
                sightJ = new int[max];
            }
            for (int j = 0; j < max; j++)
            {
                NPC npc = Main.npc[j];
                if (npc.active && npc.type >= 134 && npc.type <= 136)
                {
                    Vector2 d = a - npc.Center;
                    float length = d.Length();
                    if (length < 999999f)
                    {
                        sightD[n] = length;
                        sightJ[n] = j;
                        n++;
                    }
                }
            }
            while (n > 0)
            {
                // the nearest left; of those as near, the first (they are in index order)
                int k = 0;
                for (int q = 1; q < n; q++)
                    if (sightD[q] < sightD[k])
                        k = q;
                int j = sightJ[k];
                BossCount.asked++;
                if (Collision.CanHit(a, 1, 1, Main.npc[j].Center, 1, 1))
                    return j;
                n--;
                for (int q = k; q < n; q++)
                {
                    sightD[q] = sightD[q + 1];
                    sightJ[q] = sightJ[q + 1];
                }
            }
            return -1;
        }

        // ---- dark. The game asks Lighting.Brightness of every tile in the rectangle around the NPC
        // (x0..x1, y0..y1, ends included; some 30 for a Destroyer segment) and sets the colour black at
        // each dark one: the first dark tile decides it, and Brightness only reads. With the legacy
        // lighting (the port's) the brightness is worked out here from the same light values, with the
        // same operations in the same order.
        static int darkMine = -1;
        static bool darkSeen;

        public static bool Dark(int x0, int x1, int y0, int y1)
        {
            BossCount.darkAsked++;
            var engine = Lighting._activeEngine;
            if (engine != null && engine.GetType() == typeof(LegacyLighting))
            {
                BossCount.darkFast++;
                return DarkLegacy((LegacyLighting)engine, x0, x1, y0, y1);
            }
            for (int l = x0; l <= x1; l++)
                for (int m = y0; m <= y1; m++)
                    if (Lighting.Brightness(l, m) == 0f)
                        return true;
            return false;
        }

        // Lighting.Brightness: GlobalBrightness * (r + g + b) / 3f of LegacyLighting.GetColor's colour,
        // which is the tile's light state inside the lit rectangle and 0, 0, 0 outside it
        static bool Zero(float global, float r, float g, float b) => global * (r + g + b) / 3f == 0f;

        static bool DarkLegacy(LegacyLighting e, int x0, int x1, int y0, int y1)
        {
            float global = Lighting.GlobalBrightness;
            int left = e._expandedRectLeft, right = e._expandedRectRight, top = e._expandedRectTop, bottom = e._expandedRectBottom;
            for (int l = x0; l <= x1; l++)
            {
                if (l < left || l >= right)
                {
                    if (y0 <= y1 && Zero(global, 0f, 0f, 0f))
                        return true;
                    continue;
                }
                // (the column read where the game's GetColor first reads it)
                LegacyLighting.LightingState[] column = null;
                for (int m = y0; m <= y1; m++)
                {
                    if (m < top || m >= bottom)
                    {
                        if (Zero(global, 0f, 0f, 0f))
                            return true;
                        continue;
                    }
                    if (column == null)
                        column = e._states[l - left];
                    var s = column[m - top];
                    if (Zero(global, s.R, s.G, s.B))
                        return true;
                }
            }
            return false;
        }

        // the game's own loop runs (the cut left out, or audited): what the cut would say, kept
        public static void DarkBefore(int x0, int x1, int y0, int y1)
        {
            if (!audit)
                return;
            darkMine = Dark(x0, x1, y0, y1) ? 1 : 0;
            darkSeen = false;
        }

        // the game's loop found a dark tile
        public static void DarkSeen()
        {
            if (audit)
                darkSeen = true;
        }

        public static void DarkAfter()
        {
            if (!audit || darkMine < 0)
                return;
            BossCount.compared++;
            if ((darkMine == 1) != darkSeen)
                Wrong($"an NPC behind the tiles is {(darkSeen ? "" : "not ")}dark for the game, {(darkMine == 1 ? "" : "not ")}here");
            darkMine = -1;
        }

        // ---- immune. Every update the game counts down each of an NPC's 256 counters that is above 0
        // (one a player, one the world): 1 KB read for every NPC, mostly zeros, which on the RK3326
        // comes from memory. With every counter at 0 or below the countdown changes nothing, and stays
        // so until a counter is written: every write of one outside the countdown goes through
        // ImmuneStore (the patcher), which drops the mark of the NPCs whose counters those are.
        // A mark: the NPC object at its index, and the very array it was seen with.
        const int Slots = 256;
        static readonly object[] immuneWho = new object[Slots];
        static readonly int[][] immuneArr = new int[Slots][];
        static int[] immuneLast;

        // before the countdown: true to pass it
        public static bool ImmuneSkip(NPC n)
        {
            if (!immuneOn)
                return false;
            int w = n.whoAmI;
            if ((uint)w >= Slots || immuneWho[w] != n || immuneArr[w] != n.immune)
            {
                BossCount.immuneRan++;
                return false;
            }
            if (audit)
            {
                int[] a = n.immune;
                BossCount.compared++;
                for (int k = 0; k < 256; k++)
                    if (a[k] > 0)
                    {
                        Wrong($"NPC {w} ({n.type}) was taken to have no hit counter above 0, and counter {k} is {a[k]}");
                        break;
                    }
                BossCount.immuneRan++;
                return false;
            }
            BossCount.immuneSkipped++;
            return true;
        }

        // after the countdown: marked when no counter it counts is above 0
        public static void ImmuneDone(NPC n)
        {
            if (!immuneOn)
                return;
            int w = n.whoAmI;
            if ((uint)w >= Slots)
                return;
            int[] a = n.immune;
            if (a != null && a.Length >= 256)
            {
                int k = 0;
                while (k < 256 && a[k] <= 0)
                    k++;
                if (k == 256)
                {
                    immuneWho[w] = n;
                    immuneArr[w] = a;
                    immuneLast = null;
                    return;
                }
            }
            immuneWho[w] = null;
            immuneArr[w] = null;
        }

        // in place of a store of one of an NPC's counters (a[i] = v)
        public static void ImmuneStore(int[] a, int i, int v)
        {
            a[i] = v;
            BossCount.immuneWrites++;
            if (a == immuneLast)
                return;
            for (int w = 0; w < Slots; w++)
                if (immuneArr[w] == a)
                {
                    immuneArr[w] = null;
                    immuneWho[w] = null;
                }
            // no mark has this array now, until ImmuneDone makes one
            immuneLast = a;
        }

        // ---- tiles. The game looks at every tile from a column left of the segment to two right of it,
        // and a row above to two below (some 30), for one that blocks or holds liquid, and then whether
        // that tile's square meets the segment's; once it has one it goes on through the next columns
        // all the same. The squares' test only reads the segment and the numbers, the tiles' only the
        // tiles, so the square is asked first, and the first tile that answers ends it.

        public static bool InTiles(NPC n, int x0, int x1, int y0, int y1)
        {
            BossCount.tilesAsked++;
            for (int k = x0; k < x1; k++)
            {
                float x = k * 16;
                if (!(n.position.X + (float)n.width > x) || !(n.position.X < x + 16f))
                    continue;
                for (int l = y0; l < y1; l++)
                {
                    float y = l * 16;
                    if (!(n.position.Y + (float)n.height > y) || !(n.position.Y < y + 16f))
                        continue;
                    // the tile's record (TileRecords): type at 0, liquid at 4, sTileHeader at 5, frameY at 12 (FNAPatches)
                    byte* t = (byte*)Tiles.At(k, l);
                    if (t == null)
                        continue;
                    ushort type = *(ushort*)t;
                    bool nactive = (*(ushort*)(t + 5) & 0x60) == 0x20;
                    if ((nactive && (Main.tileSolid[type] || (Main.tileSolidTop[type] && *(short*)(t + 12) == 0))) || t[4] > 64)
                        return true;
                }
            }
            return false;
        }

        // after the game's own look (the cut left out, or audited)
        public static void TilesCheck(NPC n, int x0, int x1, int y0, int y1, bool game)
        {
            if (!audit)
                return;
            BossCount.compared++;
            if (InTiles(n, x0, x1, y0, y1) != game)
                Wrong($"Destroyer segment {n.whoAmI} is {(game ? "" : "not ")}in the blocks for the game, {(game ? "not " : "")}here");
        }

        // ---- buffs. Every update two routines walk an NPC's 20 buff slots (the flags each buff sets, the
        // buffs run out), and each round of both begins "no buff in this slot: next". With no buff in any
        // slot both walks do nothing, until a slot is written: every write of one (adding a buff, taking one
        // away, a buff from the network) goes through BuffStore, which drops the mark of that array. A
        // slot cleared (Array.Clear) holds no buff. The mark: as the immune counters' (the NPC object at its
        // index and the very array), made after the first walk.
        static readonly object[] buffWho = new object[Slots];
        static readonly int[][] buffArr = new int[Slots][];
        static int[] buffLast;

        // before a walk: true to pass it
        public static bool BuffsSkip(NPC n)
        {
            if (!buffsOn)
                return false;
            int w = n.whoAmI;
            if ((uint)w >= Slots || buffWho[w] != n || buffArr[w] != n.buffType)
            {
                BossCount.buffRan++;
                return false;
            }
            if (audit)
            {
                int[] a = n.buffType;
                BossCount.compared++;
                for (int k = 0; k < NPC.maxBuffs; k++)
                    if (a[k] > 0)
                    {
                        Wrong($"NPC {w} ({n.type}) was taken to have no buff, and slot {k} holds {a[k]}");
                        break;
                    }
                BossCount.buffRan++;
                return false;
            }
            BossCount.buffSkipped++;
            return true;
        }

        // after the first walk: marked when no slot it walks holds a buff
        public static void BuffsDone(NPC n)
        {
            if (!buffsOn)
                return;
            int w = n.whoAmI;
            if ((uint)w >= Slots)
                return;
            int[] a = n.buffType;
            int max = NPC.maxBuffs;
            if (a != null && a.Length >= max)
            {
                int k = 0;
                while (k < max && a[k] <= 0)
                    k++;
                if (k == max)
                {
                    buffWho[w] = n;
                    buffArr[w] = a;
                    buffLast = null;
                    return;
                }
            }
            buffWho[w] = null;
            buffArr[w] = null;
        }

        // in place of a store of one of an NPC's buff slots (a[i] = v)
        public static void BuffStore(int[] a, int i, int v)
        {
            a[i] = v;
            BossCount.buffWrites++;
            if (a == buffLast)
                return;
            for (int w = 0; w < Slots; w++)
                if (buffArr[w] == a)
                {
                    buffArr[w] = null;
                    buffWho[w] = null;
                }
            buffLast = a;
        }

        // ---- wet. Collision.WetCollision looks at every tile from a column left of the entity to two right of
        // it, and a row above to two below, for liquid (or a slope under liquid) whose square meets a small
        // probe at the entity's centre (10 wide, half the entity high), and returns at the first. Only the
        // squares' sums decide whether a tile can be the one; the patcher has the routine pass a column or
        // a row whose square cannot meet the probe, by the same sums (with a row's liquid reaching less than
        // 17 below its top), before it reads the tiles.

        // the routine returns true at a square: the guards had it as one that can meet the probe
        public static void WetHit(bool column, bool row)
        {
            BossCount.wetHits++;
            if (!audit)
                return;
            BossCount.compared++;
            if (!column || !row)
                Wrong($"the look for liquid returned at a square the guards would pass ({(column ? "" : "column")} {(row ? "" : "row")})");
        }
    }

    // Boss's counts, in a class with no static constructor: the stats thread reads them (and the switch
    // for the audit) without starting Boss there. Boss's statics are set up on the game thread when
    // the game first reaches a cut; started on the stats thread while the game was still starting up
    // the two threads waited on each other.
    public static class BossCount
    {
        public static long searches, asked, darkAsked, darkFast, immuneSkipped, immuneRan, immuneWrites, tilesAsked, wetHits,
            buffSkipped, buffRan, buffWrites, compared, wrong;

        public static string Line() =>
            $"boss sight {searches} searches {asked} asked, dark {darkAsked} ({darkFast} lit here), immune {immuneSkipped} passed {immuneRan} counted {immuneWrites} writes, tiles {tilesAsked}, wet {wetHits} found, buffs {buffSkipped} passed {buffRan} walked {buffWrites} writes"
            + (Environment.GetEnvironmentVariable("TR_BOSS_AUDIT") == "1" ? $", compared {compared} wrong {wrong}" : "");
    }
}
