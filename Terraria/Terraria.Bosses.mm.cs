using System;
using Microsoft.Xna.Framework;
using MonoMod;
using MonoMod.InlineRT;
using Terraria;

#pragma warning disable 1591, 0649, 0169, 0414, 0626

// Bosses of many parts (after wild1walker's portmaster-terraria, MIT). The Destroyer is 87 NPCs, and the game does some work
// for every one of them, every update or every frame, whose answer is known without it. Four cuts, each exact: the same
// values, the same writes, no draw on Main.rand added or left out.
//   wet     Collision.WetCollision: no liquid in the few tiles its probe can reach, so no look at the rest
//   tiles   a Destroyer part's look for a block it is in: the squares that can meet it tried first, the first tile ends it
//   immune  NPC.UpdateNPC's countdown of an NPC's 256 hit counters: eight at a time while they are all zero
//   sight   the Destroyer's health bar: the nearest part in sight asked first, the rest not asked
// Each is tied to the routines it was written for (TileConverter/Bosses.cs sums the game's code): another version of the
// game leaves that cut out and says so in the patch log. TERRARIA_BOSSES=0 leaves all four out when patching. At run time
// TR_BOSS_WET=0, TR_BOSS_TILES=0, TR_BOSS_IMMUNE=0 and TR_BOSS_SIGHT=0 turn one off, and TR_BOSS_AUDIT=1 works the answers
// out the game's way as well and reports any difference.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			bool on = Environment.GetEnvironmentVariable("TERRARIA_SPEED") != "0" && Environment.GetEnvironmentVariable("TERRARIA_BOSSES") != "0";
			// here, before MonoMod weaves anything, the game's code is still as shipped
			if (on)
				TerrariaPatches.TileConvert.Bosses.Look(MonoModRule.Modder.Module);
			MonoModRule.Flag.Set("Bosses.Wet", on && TerrariaPatches.TileConvert.Bosses.Known("wet"));
			if (on && TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Bosses, "bosses", m => TerrariaPatches.TileConvert.Bosses.Apply(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace Terraria
{
	// wet. The game asks this of every NPC, projectile, item and player every update. It looks at every tile from a column
	// left of the entity to two right of it and a row above to two below (some 30 for a Destroyer part) for liquid, or a slope
	// under liquid, that meets a small probe at the entity's centre: 10 wide (or the entity's width), half its height high.
	// A tile's liquid lies in the tile's square and at most a pixel below it, and a slope takes its liquid from the tile
	// above: an answer of true needs liquid in a tile the probe reaches or in the tile above one. No liquid there: the
	// answer is false, and the two flags the routine clears first are cleared.
	[MonoModIfFlag("Bosses.Wet")]
	class patch_Collision : Collision
	{
		public static extern bool orig_WetCollision(Vector2 Position, int Width, int Height);
		public static bool WetCollision(Vector2 Position, int Width, int Height)
		{
			bool dry = TrPatches.Boss.wet && Dry(Position, Width, Height);
			if (dry && !TrPatches.Boss.audit)
			{
				honey = false;
				shimmer = false;
				return false;
			}
			bool found = orig_WetCollision(Position, Width, Height);
			if (dry)
				TrPatches.Boss.Audited(0, !found, "liquid met an entity where the look ahead saw none");
			return found;
		}

		// No liquid where the probe can reach: its columns, its rows and the row above them, with one more column and row on
		// every side, which is more than any rounding of the probe's place could move it.
		static bool Dry(Vector2 position, int width, int height)
		{
			int w = Math.Min(10, width), h = Math.Min(height / 2, height);
			float x = position.X + (float)(width / 2) - (float)(w / 2), y = position.Y + (float)(height / 2) - (float)(h / 2);
			int x0 = Math.Max((int)Math.Floor(x / 16f) - 1, 0), x1 = Math.Min((int)Math.Floor((x + w) / 16f) + 1, Main.maxTilesX - 1);
			int y0 = Math.Max((int)Math.Floor(y / 16f) - 2, 0), y1 = Math.Min((int)Math.Floor((y + h) / 16f) + 1, Main.maxTilesY - 1);
			for (int i = x0; i <= x1; i++)
			{
				for (int j = y0; j <= y1; j++)
				{
					Tile tile = Main.tile[i, j];
					if (tile != null && tile.liquid > 0)
					{
						return false;
					}
				}
			}
			return true;
		}
	}

	// tiles. Every update a part of the Destroyer asks whether it is in the blocks. The game looks at every tile from a
	// column left of the part to two right of it and a row above to two below (some 30) for one that blocks or holds liquid, and
	// only then whether that tile's square meets the part's; when it has found one it still goes through the columns that
	// are left. The squares' test reads only the part and the numbers, the tiles' only the tiles: here the squares are
	// tried first (9 to 16 of them can meet the part) and the first tile that answers ends it. The answer is "any" either way.
	class patch_NPC : NPC
	{
		// Called in place of the first column the game's loop starts from, with the look's own bounds (columns x0 to x1,
		// rows y0 to y1, ends left out) and the answer it has just set to false. The look done here: x1 has the game's loop
		// end at once. The cut off, or audited: x0 has it run as it was.
		public int InBlocks(int x0, int x1, int y0, int y1, ref bool inBlocks)
		{
			if (!TrPatches.Boss.tiles)
				return x0;
			bool found = false;
			for (int k = x0; k < x1 && !found; k++)
			{
				float x = k * 16;
				if (!(position.X + (float)width > x && position.X < x + 16f))
					continue;
				for (int l = y0; l < y1 && !found; l++)
				{
					float y = l * 16;
					if (!(position.Y + (float)height > y && position.Y < y + 16f))
						continue;
					Tile tile = Main.tile[k, l];
					found = tile != null && ((tile.nactive() && (Main.tileSolid[tile.type] || (Main.tileSolidTop[tile.type] && tile.frameY == 0))) || tile.liquid > 64);
				}
			}
			if (TrPatches.Boss.audit)
			{
				TrPatches.Boss.looked = found;
				return x0;
			}
			inBlocks = found;
			return x1;
		}
	}
}

namespace TrPatches
{
	public static unsafe class Boss
	{
		public static bool wet = Environment.GetEnvironmentVariable("TR_BOSS_WET") != "0";
		public static bool immune = Environment.GetEnvironmentVariable("TR_BOSS_IMMUNE") != "0";
		public static bool sight = Environment.GetEnvironmentVariable("TR_BOSS_SIGHT") != "0";
		public static bool tiles = Environment.GetEnvironmentVariable("TR_BOSS_TILES") != "0";
		public static bool audit = Environment.GetEnvironmentVariable("TR_BOSS_AUDIT") == "1";
		public static bool looked;
		static readonly long[] compared = new long[4];
		static long wrong;

		// TR_BOSS_AUDIT=1: the game's own answer was worked out beside a cut's (0 wet, 1 tiles, 2 immune, 3 sight)
		public static void Audited(int cut, bool same, string what)
		{
			if (!same && wrong++ < 20)
				Console.Error.WriteLine("[bosses] WRONG: " + what);
			if (++compared[cut] % 100000 == 0 || !same)
				Console.Error.WriteLine("[bosses] audit: wet " + compared[0] + ", tiles " + compared[1] + ", immune " + compared[2] + ", sight " + compared[3] + " compared; " + wrong + " wrong");
		}

		// tiles, audited: after the game's own look, with its answer (NPC.InBlocks kept its own in looked)
		public static void Looked(bool inBlocks)
		{
			if (audit && tiles)
				Audited(1, inBlocks == looked, "a Destroyer part in the blocks for the game and not here, or the other way");
		}

		// immune. Every update the game counts down each of an NPC's 256 hit counters that is above 0 (one per player, one
		// for the world): 1 KB read for every NPC, nearly always all zeros. Here the same countdown reads eight counters at
		// a time and goes through them one by one only where the eight are not all zero. Called in place of the 0 the game's
		// loop starts from: 256 has that loop end at once; 0 (the cut off, or an array that is not 256 long) has it run as it was.
		public static int Countdown(NPC npc)
		{
			int[] counters = npc.immune;
			if (!immune || counters == null || counters.Length != 256)
				return 0;
			int[] before = audit ? (int[])counters.Clone() : null;
			fixed (int* p = counters)
			{
				long* q = (long*)p;
				for (int k = 0; k < 128; k += 4)
					if ((q[k] | q[k + 1] | q[k + 2] | q[k + 3]) != 0)
						for (int j = k * 2; j < k * 2 + 8; j++)
							if (p[j] > 0)
								p[j]--;
			}
			if (before != null)
			{
				bool same = true;
				for (int j = 0; j < 256; j++)
					same &= counters[j] == (before[j] > 0 ? before[j] - 1 : before[j]);
				Audited(2, same, "an NPC's hit counters were not counted down as the game counts them");
			}
			return 256;
		}

		// sight. For the Destroyer's health bar the game goes through the parts in the order of the NPC list and asks
		// Collision.CanHit (a walk over the tiles between) of every part nearer than the nearest in sight so far; it ends
		// with the nearest part in sight, the first in the list of those as near. Here the parts are asked in the order of
		// their distance, then of the list, until one is in sight, once a search; the game's loop is then told "in sight"
		// for that part alone. It takes the part it is told whatever it was told before, so it ends with the same part and
		// the same distance. CanHit only reads, so asking it of fewer parts changes nothing else.
		static bool searched, found, gameFound;
		static Vector2 nearest, gameNearest;
		static float[] distance;
		static int[] part;

		// a search begins
		public static void NewSearch()
		{
			if (audit && searched)
				Audited(3, found == gameFound && (!found || nearest == gameNearest), "the Destroyer's nearest part in sight is not the game's");
			searched = false;
		}

		// in place of the search's Collision.CanHit(a, w1, h1, b, w2, h2): a is the player's centre, b a part's
		public static bool Sight(Vector2 a, int w1, int h1, Vector2 b, int w2, int h2)
		{
			if (!sight)
				return Collision.CanHit(a, w1, h1, b, w2, h2);
			if (!searched)
			{
				searched = true;
				gameFound = false;
				found = Nearest(a, w1, h1, w2, h2);
			}
			if (!audit)
				return found && b == nearest;
			// audit: the game's own search goes on as it was, and the part it ends with is kept
			if (!Collision.CanHit(a, w1, h1, b, w2, h2))
				return false;
			gameFound = true;
			gameNearest = b;
			return true;
		}

		// the centre of the part the game's search ends with, into nearest; false when no part is in sight
		static bool Nearest(Vector2 a, int w1, int h1, int w2, int h2)
		{
			if (distance == null)
			{
				distance = new float[Main.maxNPCs];
				part = new int[Main.maxNPCs];
			}
			int n = 0;
			for (int j = 0; j < Main.maxNPCs; j++)
			{
				NPC npc = Main.npc[j];
				if (npc.active && npc.type >= 134 && npc.type <= 136)
				{
					float length = (a - npc.Center).Length();
					if (length < 999999f)
					{
						distance[n] = length;
						part[n++] = j;
					}
				}
			}
			while (n > 0)
			{
				int k = 0;
				for (int q = 1; q < n; q++)
					if (distance[q] < distance[k] || (distance[q] == distance[k] && part[q] < part[k]))
						k = q;
				Vector2 centre = Main.npc[part[k]].Center;
				if (Collision.CanHit(a, w1, h1, centre, w2, h2))
				{
					nearest = centre;
					return true;
				}
				n--;
				distance[k] = distance[n];
				part[k] = part[n];
			}
			return false;
		}
	}
}
