using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using MonoMod;
using MonoMod.InlineRT;
using Terraria;

#pragma warning disable 1591, 0649, 0169, 0414

// The PortMaster port's speed passes (wild1walker, portmaster-terraria, MIT), over the woven game (TileConverter: Split.cs,
// Inits.cs, Scans.cs, DrawCuts.cs; their order in TileConverter/Passes.cs):
//   - the longest routines split into parts and their locals no longer cleared at every call (the creatures' and
//     projectiles' AI cleared up to 85 KB of stack per call: on the H700 the Destroyer went from about 49 to 60 fps);
//   - draw cuts: one TileDrawInfo instead of one per tile, the zoom asked once a pass, only the queued sprites uploaded, the
//     pylon layer's search left out with no pylon, the stars drawn texture by texture (TrPatches.Stars, TR_STARS=0: off);
//   - the fixed lists' "inactive" remembered (TrPatches.Scan, TR_SCAN=0: off, TR_SCAN_AUDIT=1: checked), loops passing the
//     slots known inactive, the dust list's inactive end, the search for a free dust, the lying items' merge (TR_SAME=0: off).
// TERRARIA_SPEED=0 leaves all of it out; TERRARIA_SPLIT=0, TERRARIA_INITS=0, TERRARIA_SCANS=0, TERRARIA_DRAWCUTS=0 one pass.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_SPEED") == "0")
			{
				return;
			}
			bool hook = false;
			if (Environment.GetEnvironmentVariable("TERRARIA_DRAWCUTS") != "0")
				hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.DrawCuts, "drawcuts", m => TerrariaPatches.TileConvert.Speed.DrawCuts(m));
			if (Environment.GetEnvironmentVariable("TERRARIA_SCANS") != "0")
				hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Scans, "scans", m => TerrariaPatches.TileConvert.Speed.Scans(m));
			if (Environment.GetEnvironmentVariable("TERRARIA_SPLIT") != "0")
				hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Split, "split", m => TerrariaPatches.TileConvert.Speed.Split(m));
			if (Environment.GetEnvironmentVariable("TERRARIA_INITS") != "0")
				hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Inits, "inits", m => TerrariaPatches.TileConvert.Speed.Inits(m));
			if (hook)
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace Terraria
{
	// the index + 1 of the world item a marked inner item belongs to (TrPatches.Scan)
	class patch_Item : Item
	{
		public int trSlot;
	}
}

// From the PortMaster port by wild1walker (tools/trpatch/runtime/Scan.cs and Stars.cs, MIT), except where marked: his
// interface cuts (Ui) are not taken, so their marks are left out here (FNAPatches).
namespace TrPatches
{

    // The game keeps its projectiles, NPCs, players and dust in fixed lists (1000, 200, 255 and 6000
    // slots) and asks every slot "active?" in dozens of loops a frame. An object is hundreds of bytes
    // and the lists are more than the H700's cache holds, so every question is a read from memory:
    // 85 to 240 ns a slot, 1 ms for one pass over the dust, about 3 ms of a 16.7 ms frame with next
    // to nothing in the lists (2026-11-11).
    // Here a slot seen inactive is remembered in two small arrays: idle[k] is set and seen[k] is the
    // object only while that object is inactive and k is its own index (whoAmI, dustIndex). Every write
    // of an "active" field and of an index field in the game goes through here (trpatch, Scans), so a
    // write of true, or of another index, takes the object's mark away at its index, where alone it can
    // be. A question about a marked object is answered false without touching it; any other question
    // goes to the object. The index that comes with a question is only where to look: the answer is
    // the object's field for any object and any index, and the game runs the same.
    // TR_SCAN=0: nothing is remembered. TR_SCAN_AUDIT=1 (tests): every mark is checked against its
    // object every update.
    public static class Scan
    {
        static readonly bool on = Environment.GetEnvironmentVariable("TR_SCAN") != "0";
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_SCAN_AUDIT") == "1";
        public static bool Stop;        // tests (Parts.Sweep, "@scan"): every question goes to the object

        static readonly object[] seenP = new object[1001], seenN = new object[201], seenL = new object[256], seenD = new object[6001];
        static readonly byte[] idleP = new byte[1001], idleN = new byte[201], idleL = new byte[256], idleD = new byte[6001];
        static readonly object[] seenI = new object[401];
        static readonly byte[] idleI = new byte[401];
        static int wrong;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool Idle(object[] seen, byte[] idle, object o, int k) =>
            (uint)k < (uint)idle.Length && idle[k] != 0 && seen[k] == o && !Stop;

        // o was read inactive with k its index: marked. The caller reads it again after: another thread
        // may have woken it between (the world's loader sets entities up beside the menu).
        static void Rest(object[] seen, byte[] idle, object o, int k)
        {
            idle[k] = 0;
            seen[k] = o;
            idle[k] = 1;
            Thread.MemoryBarrier();
        }

        // o is active now, or k is no more its index
        static void Woke(object[] seen, byte[] idle, object o, int k)
        {
            Thread.MemoryBarrier();
            if ((uint)k < (uint)idle.Length && seen[k] == o)
                idle[k] = 0;
        }

        // ---- projectiles
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Active(Projectile o, int k) => !Idle(seenP, idleP, o, k) && Look(o, k);

        static bool Look(Projectile o, int k)
        {
            bool active = o.active;
            if (!active && on && !Stop && (uint)k < (uint)idleP.Length && o.whoAmI == k)
            {
                Rest(seenP, idleP, o, k);
                if (o.active || o.whoAmI != k) idleP[k] = 0;
            }
            return active;
        }

        public static void Off(Projectile o, int k)
        {
            if (Idle(seenP, idleP, o, k)) return;
            o.active = false;
            Look(o, k);
        }

        public static void Set(Projectile o, bool active)
        {
            o.active = active;
            if (active) Woke(seenP, idleP, o, o.whoAmI);
        }

        // Projectile.Update returns at once for an inactive projectile
        public static void Update(Projectile o, int i)
        {
            if (!Idle(seenP, idleP, o, i)) o.Update(i);
        }

        // ---- NPCs
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Active(NPC o, int k) => !Idle(seenN, idleN, o, k) && Look(o, k);

        static bool Look(NPC o, int k)
        {
            bool active = o.active;
            if (!active && on && !Stop && (uint)k < (uint)idleN.Length && o.whoAmI == k)
            {
                Rest(seenN, idleN, o, k);
                if (o.active || o.whoAmI != k) idleN[k] = 0;
            }
            return active;
        }

        public static void Off(NPC o, int k)
        {
            if (Idle(seenN, idleN, o, k)) return;
            o.active = false;
            Look(o, k);
        }

        public static void Set(NPC o, bool active)
        {
            o.active = active;
            if (active) Woke(seenN, idleN, o, o.whoAmI);
        }

        // NPC.UpdateNPC sets whoAmI to i and returns for an inactive NPC: a marked one has that index
        public static void Update(NPC o, int i)
        {
            if (!Idle(seenN, idleN, o, i)) o.UpdateNPC(i);
        }

        // ---- players
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Active(Player o, int k) => !Idle(seenL, idleL, o, k) && Look(o, k);

        static bool Look(Player o, int k)
        {
            bool active = o.active;
            if (!active && on && !Stop && (uint)k < (uint)idleL.Length && o.whoAmI == k)
            {
                Rest(seenL, idleL, o, k);
                if (o.active || o.whoAmI != k) idleL[k] = 0;
            }
            return active;
        }

        public static void Off(Player o, int k)
        {
            if (Idle(seenL, idleL, o, k)) return;
            o.active = false;
            Look(o, k);
        }

        public static void Set(Player o, bool active)
        {
            o.active = active;
            if (active) Woke(seenL, idleL, o, o.whoAmI);
        }

        // ---- dust
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Active(Dust o, int k) => !Idle(seenD, idleD, o, k) && Look(o, k);

        static bool Look(Dust o, int k)
        {
            bool active = o.active;
            if (!active && on && !Stop && (uint)k < (uint)idleD.Length && o.dustIndex == k)
            {
                Interlocked.Increment(ref busyD);
                if (k >= Volatile.Read(ref tailD)) Interlocked.Increment(ref versionD);
                Rest(seenD, idleD, o, k);
                if (o.active || o.dustIndex != k) idleD[k] = 0;
                Interlocked.Decrement(ref busyD);
            }
            return active;
        }

        public static void Off(Dust o, int k)
        {
            if (Idle(seenD, idleD, o, k)) return;
            bool was = o.active;
            o.active = false;
            if (was) Lower(o.dustIndex);
            Look(o, k);
        }

        public static void Set(Dust o, bool active)
        {
            if (!active)
            {
                bool was = o.active;
                o.active = false;
                if (was) Lower(o.dustIndex);
                return;
            }
            Interlocked.Increment(ref busyD);
            int k = o.dustIndex;
            if (k >= Volatile.Read(ref tailD)) Interlocked.Increment(ref versionD);
            o.active = true;
            Woke(seenD, idleD, o, k);
            Interlocked.Decrement(ref busyD);
        }

        // ---- the end of the dust list
        // Dust.UpdateDust writes "inactive" to every dust past the number the game draws (1000 to
        // 6000 of the 6000, by the quality setting) every update: thousands of questions for nothing.
        // tailD is where the marked end of the list starts: from there on every slot of Main.dust
        // holds its own marked object, so the rest of the game's loop has nothing to do and the loop
        // is left there (trpatch, Scans.Tail). What could make that untrue says so first: a mark made
        // or taken away from tailD on and an object put in the list change versionD, and busyD counts
        // the changes under way (on any thread); the end is then worked out again at the next update.
        static int tailD = int.MaxValue, lastD = int.MaxValue, knownD, versionD, busyD;
        static bool againD = true;
        public static long Tails;       // updates whose loop was left early (tests)

        public static bool Tail(int i)
        {
            if (!on || Stop) return false;
            if (i <= lastD && againD) Mend();
            lastD = i;
            if (i >= tailD && Volatile.Read(ref busyD) == 0 && Volatile.Read(ref versionD) == knownD)
            {
                Tails++;
                return true;
            }
            againD = true;
            return false;
        }

        static void Mend()
        {
            againD = false;
            Volatile.Write(ref tailD, int.MaxValue);
            int v = Volatile.Read(ref versionD);
            if (Volatile.Read(ref busyD) != 0)
            {
                againD = true;
                return;
            }
            Dust[] list = Main.dust;
            int t = Math.Min(Main.maxDust, list.Length);
            while (t > 0 && idleD[t - 1] != 0 && seenD[t - 1] == list[t - 1]) t--;
            knownD = v;
            Volatile.Write(ref tailD, t);
            Thread.MemoryBarrier();
            if (Volatile.Read(ref busyD) != 0 || Volatile.Read(ref versionD) != v)
            {
                Volatile.Write(ref tailD, int.MaxValue);
                againD = true;
            }
        }

        // an object was put in Main.dust, or the list itself changed
        public static void Moved()
        {
            Interlocked.Increment(ref versionD);
            Lower(0);
        }

        // ---- the first free dust
        // Dust.NewDust looks for the first inactive dust from slot 0, past every active one: 700 of
        // them in a fight, a hundred times a frame. Every slot below lowD holds its own active
        // object, so the search starts there (trpatch, Scans.First). A write of "inactive" and a
        // change of index lower it to the object's index, an object put in the list to 0; First
        // raises it past the active dust it reads, unless something was lowered meanwhile (stepD).
        static int lowD, stepD;
        public static long Firsts, Skipped;     // searches, and the slots they did not ask (tests)

        static void Lower(int k)
        {
            if (k < 0) k = 0;
            Interlocked.Increment(ref stepD);
            int now;
            while (k < (now = Volatile.Read(ref lowD)) && Interlocked.CompareExchange(ref lowD, k, now) != now) { }
        }

        public static int First()
        {
            if (!on || Stop) return 0;
            int step = Volatile.Read(ref stepD), from = Volatile.Read(ref lowD), k = from;
            Dust[] list = Main.dust;
            int n = Math.Min(list.Length, 6000);
            Dust o;
            while (k < n && (o = list[k]) != null && o.active && o.dustIndex == k) k++;
            if (k != from && Interlocked.CompareExchange(ref lowD, k, from) == from && Volatile.Read(ref stepD) != step)
                Lower(from);
            Firsts++;
            Skipped += from;
            if (audit)
                for (int j = 0; j < from; j++)
                    if (list[j] == null || !list[j].active)
                    {
                        wrong++;
                        Console.Error.WriteLine($"[trpatch] scan: dust {j} is free below {from}, where the search was to start");
                        return 0;
                    }
            return k;
        }

        // ---- the index fields: in place of a write of Entity.whoAmI, of Dust.dustIndex
        public static void Who(Entity o, int index)
        {
            int was = o.whoAmI;
            o.whoAmI = index;
            if (was == index) return;
            Woke(seenP, idleP, o, was);
            Woke(seenN, idleN, o, was);
            Woke(seenL, idleL, o, was);
            Woke(seenI, idleI, o, was);
        }

        // ---- world items: one is inactive (WorldItem.active) and air (IsAir) alike while its inner
        // item's type is 0. Marked so at its index, the inner item keeping the index + 1 (Item.trSlot):
        // every write of Item.type in the game comes through Type, a new inner item through Inner, the
        // item's index through Who, and a new object at the index is not the marked one.
        static bool Look(WorldItem o, int k)
        {
            var it = o.inner;
            bool air = it.type == 0;
            if (air && on && !Stop && (uint)k < (uint)idleI.Length && o.whoAmI == k)
            {
                ((Terraria.patch_Item)(object)it).trSlot = k + 1;
                Rest(seenI, idleI, o, k);
                if (it.type != 0 || o.inner != it || o.whoAmI != k) idleI[k] = 0;
            }
            return !air;
        }

        // every "item.type = value" of the game
        public static void Type(Item it, int value)
        {
            it.type = value;
            Interlocked.Increment(ref versionS);
            int s = ((Terraria.patch_Item)(object)it).trSlot;
            if (s != 0)
            {
                ((Terraria.patch_Item)(object)it).trSlot = 0;
                Thread.MemoryBarrier();
                if (s - 1 < idleI.Length)
                {
                    idleI[s - 1] = 0;
                }
            }
        }

        // WorldItem.inner's setter, after the store
        public static void Inner(WorldItem o)
        {
            Interlocked.Increment(ref versionS);
            Woke(seenI, idleI, o, o.whoAmI);
        }

        // Ui: player o is marked inactive at k
        internal static bool IdlePlayer(Player o, int k) => Idle(seenL, idleL, o, k);

        // Ui: o is marked air at k; o asked, and marked when air
        internal static bool Air(WorldItem o, int k) => Idle(seenI, idleI, o, k);
        internal static bool LookItem(WorldItem o, int k) => Look(o, k);

        public static void Index(Dust o, int index)
        {
            int was = o.dustIndex;
            if (was == index) return;
            Interlocked.Increment(ref busyD);
            if (was >= Volatile.Read(ref tailD)) Interlocked.Increment(ref versionD);
            o.dustIndex = index;
            Lower(was);
            Woke(seenD, idleD, o, was);
            Interlocked.Decrement(ref busyD);
        }

        // ---- loops (trpatch, Scans.Skip): a loop over a list whose every round begins with the
        // question and goes to the loop's step on "inactive". From slot i, the first slot below n
        // whose object does not answer "inactive" (that round is the game's to do), or -1 with none
        // left; the questions are the loop's own, in its order, the marked slots passed in a loop
        // of two small arrays and the list.
        public static long Loops, Passed;       // loops' heads, and the rounds they did not make (tests)

        public static int NextP(int i, int n)
        {
            var list = Main.projectile;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleP.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleP[j] != 0 && seenP[j] == o)
                {
                    if (audit && o.active) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        public static int NextN(int i, int n)
        {
            var list = Main.npc;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleN.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleN[j] != 0 && seenN[j] == o)
                {
                    if (audit && o.active) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        public static int NextL(int i, int n)
        {
            var list = Main.player;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleL.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleL[j] != 0 && seenL[j] == o)
                {
                    if (audit && o.active) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        public static int NextI(int i, int n)
        {
            var list = Main.item;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleI.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleI[j] != 0 && seenI[j] == o)
                {
                    if (audit && o.inner.type != 0) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        public static int NextD(int i, int n)
        {
            var list = Main.dust;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleD.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleD[j] != 0 && seenD[j] == o)
                {
                    if (audit && o.active) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        // ---- the merge of lying items (WorldItem.TryCombiningIntoNearbyItems): for each item, every
        // item after it, until its stack is full. Only one of the same type can be merged into it
        // (Item.CanStack: the same type first), and with 300-400 items lying each round reads two
        // objects of another item: 6 ms an update on the RK3326. So each slot's inner type is kept
        // with the count of writes it was read at (versionS: every write of an item's type, every new
        // inner item and every store into Main.item counts one, through Type, Inner and Stored), and
        // NextSame passes the slots of another type while nothing was written since. A round passed
        // is one whose first question would have been false (air, or CanStack false): it does nothing.
        static volatile int versionS = 1;
        static readonly WorldItem[] holdS = new WorldItem[401];
        static readonly int[] typeS = new int[401], stampS = new int[401];
        public static long SameRounds, SamePassed, SameRead;

        // Every slot's type is read again in one pass when the count of writes moved (Chain), and each
        // slot points to the next slot of its type, or of none known (nextS): the merge's walk from
        // the item's own slot is a walk along its type. A new object put in the list counts as a
        // write too (Stored, after the game's four stores into Main.item).
        static readonly int[] nextS = new int[401];
        static int[] lastOf = new int[0];
        static int builtS;

        public static void Stored() => Interlocked.Increment(ref versionS);

        static void Chain(WorldItem[] list, int n)
        {
            int v = versionS;
            int top = 0;
            for (int j = 0; j < n; j++)
            {
                var o = list[j];
                var inner = o?.inner;
                holdS[j] = o;
                typeS[j] = inner == null ? -1 : inner.type;
                stampS[j] = v;
                if (typeS[j] > top) top = typeS[j];
            }
            SameRead += n;
            if (lastOf.Length <= top)
                lastOf = new int[top + 64];
            for (int t = 0; t < lastOf.Length; t++)
                lastOf[t] = n;
            int unknown = n;
            for (int j = n - 1; j >= 0; j--)
            {
                int t = typeS[j];
                if (t < 0)
                {
                    nextS[j] = unknown;
                    unknown = j;
                    continue;
                }
                nextS[j] = Math.Min(lastOf[t], unknown);
                lastOf[t] = j;
            }
            builtS = v;
        }

        public static int NextSame(int i, int n, WorldItem self)
        {
            var list = Main.item;
            if (!on || Stop || !same || i < 1 || i >= n || n > list.Length || n >= holdS.Length || self.inner == null)
                return NextI(i, n);
            int t = self.inner.type;
            if (builtS != versionS)
                Chain(list, n);
            int c;
            if (typeS[i - 1] == t)
                c = nextS[i - 1];     // from the item's own slot, or from the one it last merged with
            else
            {
                // after a merge emptied the slot before (the chain read again): every slot of its type or unknown
                c = i;
                while (c < n && typeS[c] != t && typeS[c] >= 0)
                    c++;
            }
            if (audit)
                for (int j = i; j < c && j < n; j++)
                    if (list[j]?.inner != null && list[j].inner.type == t)
                        wrong++;
            SameRounds++;
            SamePassed += Math.Min(c, n) - i;
            return c < n ? c : -1;
        }

        static readonly bool same = Environment.GetEnvironmentVariable("TR_SAME") != "0";

        // ---- tests
        static int Count(byte[] idle) { int n = 0; foreach (byte b in idle) n += b; return n; }

        // marked slots: projectiles, NPCs, players, dust (and marks found wrong by the audit)
        public static string Line() =>
            $"idle {Count(idleP)} {Count(idleN)} {Count(idleL)} {Count(idleD)} {Count(idleI)} tail {(tailD == int.MaxValue ? -1 : tailD)} left {Tails} first {lowD} of {Firsts} skipping {Skipped / Math.Max(1, Firsts)} loops {Loops} passing {Passed} same {SameRounds} passing {SamePassed} read {SameRead}" + (audit ? $" wrong {wrong}" : "");

        public static void Audit()
        {
            if (!audit) return;
            for (int k = 0; k < idleP.Length; k++)
                if (idleP[k] != 0 && (((Projectile)seenP[k]).active || ((Projectile)seenP[k]).whoAmI != k)) wrong++;
            for (int k = 0; k < idleN.Length; k++)
                if (idleN[k] != 0 && (((NPC)seenN[k]).active || ((NPC)seenN[k]).whoAmI != k)) wrong++;
            for (int k = 0; k < idleL.Length; k++)
                if (idleL[k] != 0 && (((Player)seenL[k]).active || ((Player)seenL[k]).whoAmI != k)) wrong++;
            for (int k = 0; k < idleD.Length; k++)
                if (idleD[k] != 0 && (((Dust)seenD[k]).active || ((Dust)seenD[k]).dustIndex != k)) wrong++;
            for (int k = 0; k < idleI.Length; k++)
                if (idleI[k] != 0 && Main.item[k] == seenI[k] && (((WorldItem)seenI[k]).inner.type != 0 || ((WorldItem)seenI[k]).whoAmI != k)) wrong++;
        }
    }


    // The sky's stars are sprites of four textures, drawn in the order of the game's list, and FNA's
    // batch ends a draw call wherever the texture changes: about 150 calls for 150 stars, 1.5 ms of a
    // frame on the H700 from dusk to dawn. A star's colour has alpha 0 and the batch blends one,
    // 1 - alpha with the stock shader: every star is added to what is there, and a sum (with its clamp
    // at white) is the same in any order. So the stars of one Main.DrawStarsInBackground are held and
    // drawn texture by texture: four calls. The game's code runs in its order; only the draws move
    // (DrawCuts.Stars). A sprite that is not such a sum is drawn where it stood. TR_STARS=0: as the
    // game draws.
    public static class Stars
    {
        static readonly bool on = Environment.GetEnvironmentVariable("TR_STARS") != "0";
        public static bool Off;         // tests (Parts.Sweep, "@stars")
        public static int Held;

        struct Sprite
        {
            public Texture2D Texture;
            public Vector2 Position, Origin;
            public Color Color;
            public float Rotation, Scale;
            public SpriteEffects Effects;
        }

        const BindingFlags Own = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo blend = typeof(SpriteBatch).GetField("blendState", Own),
            sort = typeof(SpriteBatch).GetField("sortMode", Own), effect = typeof(SpriteBatch).GetField("customEffect", Own);
        // the held stars, each with the place of its texture in kinds; drawn texture by texture, in
        // the order a texture first came and, within one, in the order of the draws
        static Sprite[] held = new Sprite[256];
        static int[] kindOf = new int[256], order = new int[256];
        static int count;
        static readonly List<Texture2D> kinds = new List<Texture2D>();
        static int[] start = new int[8];
        static bool holding;

        // as DrawStarsInBackground starts
        public static void Begin(SpriteBatch batch)
        {
            holding = on && !Off && blend != null && sort != null && effect != null
                && blend.GetValue(batch) == BlendState.AlphaBlend && effect.GetValue(batch) == null
                && (SpriteSortMode)sort.GetValue(batch) == SpriteSortMode.Deferred;
        }

        // in place of DrawStar's SpriteBatch.Draw
        public static void Draw(SpriteBatch batch, Texture2D texture, Vector2 position, Rectangle? source, Color color,
            float rotation, Vector2 origin, float scale, SpriteEffects effects, float depth)
        {
            if (!holding || color.A != 0 || source.HasValue || depth != 0f)
            {
                End(batch, holding);
                batch.Draw(texture, position, source, color, rotation, origin, scale, effects, depth);
                return;
            }
            int kind = kinds.IndexOf(texture);
            if (kind < 0)
            {
                kind = kinds.Count;
                kinds.Add(texture);
            }
            if (count == held.Length)
            {
                Array.Resize(ref held, count * 2);
                Array.Resize(ref kindOf, count * 2);
                Array.Resize(ref order, count * 2);
            }
            held[count] = new Sprite { Texture = texture, Position = position, Origin = origin, Color = color,
                Rotation = rotation, Scale = scale, Effects = effects };
            kindOf[count++] = kind;
        }

        // as DrawStarsInBackground returns
        public static void End(SpriteBatch batch) => End(batch, false);

        static void End(SpriteBatch batch, bool more)
        {
            int n = count, k = kinds.Count;
            if (start.Length < k + 1)
                start = new int[k * 2 + 1];
            for (int i = 0; i <= k; i++)
                start[i] = 0;
            for (int i = 0; i < n; i++)
                start[kindOf[i] + 1]++;
            for (int i = 1; i <= k; i++)
                start[i] += start[i - 1];
            for (int i = 0; i < n; i++)
                order[start[kindOf[i]]++] = i;
            for (int j = 0; j < n; j++)
            {
                ref Sprite s = ref held[order[j]];
                batch.Draw(s.Texture, s.Position, null, s.Color, s.Rotation, s.Origin, s.Scale, s.Effects, 0f);
                s.Texture = null;
            }
            Held += n;
            count = 0;
            kinds.Clear();
            holding = more;
        }
    }
}
