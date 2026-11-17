using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Mono.Cecil;
using MonoMod.InlineRT;
using Terraria;
using Terraria.DataStructures;

#pragma warning disable 1591, 0649, 0169

// Tile records. Terraria keeps every world tile as an object (32 bytes each, plus an 8 byte reference in a Tile[,]); a medium
// world has 11.5 million of them. Here Tile becomes a 14-byte struct held in one flat array, and everything that handled a
// Tile reference (locals, arguments, fields, the Main.tile[x, y] accesses) handles a pointer into that array instead.
//
// The rewrite is done by TileConverter (a Cecil port of the PortMaster port's `trpatch` tile conversion, wild1walker, MIT) from
// the MonoModRules below, after all the other patches have been applied. The runtime it calls is in this file. Compiled with
// plain pointers, since Tile is still a class while this assembly is built.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_TILE_RECORDS") == "0")
			{
				Console.WriteLine("[tiles] TERRARIA_TILE_RECORDS=0: tile records left out");
				return;
			}
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Tiles, "tiles", delegate (ModuleDefinition m)
			{
				TypeDefinition tiles = m.GetType("TrPatches.Tiles");
				if (tiles == null)
				{
					throw new InvalidOperationException("TrPatches.Tiles was not added to the module");
				}
				return TilePtr.Convert(m, tiles);
			}))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
	public static unsafe class Tiles
	{
		// 14 bytes, same field order as Terraria.Tile: type, wall, liquid, sTileHeader, bTileHeader, bTileHeader2, bTileHeader3, frameX, frameY
		public const int RecordSize = 14;

		public static byte* data;
		public static int fastW;
		public static int fastH;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void* At(int x, int y)
		{
			int h = fastH;
			if ((uint) x < (uint) fastW && (uint) y < (uint) h)
			{
				return data + ((long) (x * h + y)) * RecordSize;
			}
			return TileStore.At(x, y);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void* At(Tile[,] grid, int x, int y)
		{
			return At(x, y);
		}

		public static void PutFresh(int x, int y)
		{
			int h = fastH;
			if ((uint) x < (uint) fastW && (uint) y < (uint) h)
			{
				Zero(data + ((long) (x * h + y)) * RecordSize);
			}
			else
			{
				TileStore.Put(x, y, null, true);
			}
		}

		public static void PutFresh(Tile[,] grid, int x, int y)
		{
			PutFresh(x, y);
		}

		public static void* PutNew(int x, int y, void* value)
		{
			int h = fastH;
			if (value == null || (uint) x >= (uint) fastW || (uint) y >= (uint) h)
			{
				return TileStore.Put(x, y, value, false);
			}
			byte* cell = data + ((long) (x * h + y)) * RecordSize;
			if (cell != value)
			{
				Copy(cell, (byte*) value);
			}
			return cell;
		}

		public static void* PutNew(Tile[,] grid, int x, int y, void* value)
		{
			return PutNew(x, y, value);
		}

		public static void* New()
		{
			return TileStore.New();
		}

		public static Tile[,] NewGrid(int w, int h)
		{
			RuntimeHelpers.RunClassConstructor(typeof(Tile).TypeHandle);
			return new Tile[1, 1];
		}

		public static void Enter(void* tile, ref bool taken)
		{
			Monitor.Enter(TileStore.TileLock, ref taken);
		}

		public static void Exit(void* tile)
		{
			Monitor.Exit(TileStore.TileLock);
		}

		public static void Sized()
		{
			TileStore.Sized();
		}

		internal static void Zero(byte* p)
		{
			*(long*) p = 0;
			*(int*) (p + 8) = 0;
			*(short*) (p + 12) = 0;
		}

		internal static void Copy(byte* to, byte* from)
		{
			*(long*) to = *(long*) from;
			*(int*) (to + 8) = *(int*) (from + 8);
			*(short*) (to + 12) = *(short*) (from + 12);
		}

		// Player.IsTileTypeInInteractionRange, which scans a rectangle of tiles for one type: the same scan on the flat array.
		public static bool TypeInReach(Player player, int type, TileReachCheckSettings settings)
		{
			int x1, y1, x2, y2;
			settings.GetTileRegion(player, out x1, out y1, out x2, out y2, 0);
			int maxX = Main.maxTilesX;
			int maxY = Main.maxTilesY;
			x1 = Math.Max(0, Math.Min(x1, maxX - 1));
			x2 = Math.Max(0, Math.Min(x2, maxX - 1));
			y1 = Math.Max(0, Math.Min(y1, maxY - 1));
			y2 = Math.Max(0, Math.Min(y2, maxY - 1));
			if (maxX == fastW && maxY == fastH)
			{
				for (int x = x1; x <= x2; x++)
				{
					byte* t = data + ((long) (x * maxY + y1)) * RecordSize;
					for (int n = y2 - y1; n >= 0; n--)
					{
						// type at offset 0, sTileHeader at offset 5, bit 0x20 is "active"
						if (*(ushort*) t == type && (*(ushort*) (t + 5) & 0x20) != 0)
						{
							return true;
						}
						t += RecordSize;
					}
				}
				return false;
			}
			for (int x = x1; x <= x2; x++)
			{
				for (int y = y1; y <= y2; y++)
				{
					byte* t = (byte*) TileStore.At(x, y);
					if (t != null && *(ushort*) t == type && (*(ushort*) (t + 5) & 0x20) != 0)
					{
						return true;
					}
				}
			}
			return false;
		}
	}

	// Where the records live. One reservation of address space for the largest world (8401 x 2401 x 14 bytes, 282 MB, not
	// counted against memory) of which only the current world's size is made usable, so a small world costs about 70 MB. A cell
	// can also be "absent" (the null a Tile[,] cell could hold); a bitmap says which are present, and once none are absent the
	// accessors skip it.
	public static unsafe class TileStore
	{
		private const int MaxWidth = 8401;
		private const int MaxHeight = 2401;
		private const int RingSize = 8192;
		private const long DataBytes = (long) MaxWidth * MaxHeight * Tiles.RecordSize;
		private const long PresentBytes = (((long) MaxWidth * MaxHeight + 63) / 64) * 8;

		public static readonly object TileLock = new object();
		private static readonly object gate = new object();
		private static int width;
		private static int height;
		private static bool stale;
		private static long usable;
		private static ulong* present;
		private static long absent;
		private static readonly Dictionary<int, IntPtr> outside = new Dictionary<int, IntPtr>();
		private static byte* ring;
		private static int next;

		private const int PROT_NONE = 0;
		private const int PROT_RW = 3;
		private const int MAP_PRIVATE_ANONYMOUS = 0x22;
		private const int MAP_NORESERVE = 0x4000;
		private const int MADV_DONTNEED = 4;

		[DllImport("libc.so.6", SetLastError = true)]
		private static extern IntPtr mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, IntPtr offset);

		[DllImport("libc.so.6", SetLastError = true)]
		private static extern int mprotect(IntPtr addr, UIntPtr length, int prot);

		[DllImport("libc.so.6", SetLastError = true)]
		private static extern int madvise(IntPtr addr, UIntPtr length, int advice);

		private static byte* Map(long bytes, int prot, int flags)
		{
			IntPtr p = mmap(IntPtr.Zero, (UIntPtr) (ulong) bytes, prot, MAP_PRIVATE_ANONYMOUS | flags, -1, IntPtr.Zero);
			if (p == new IntPtr(-1))
			{
				throw new OutOfMemoryException("tiles: no address space (" + (bytes >> 20) + " MB, errno " + Marshal.GetLastWin32Error() + ")");
			}
			return (byte*) p;
		}

		private static void Reserve()
		{
			if (Tiles.data == null)
			{
				present = (ulong*) Map(PresentBytes, PROT_RW, 0);
				ring = Map((long) RingSize * Tiles.RecordSize, PROT_RW, 0);
				Tiles.data = Map(DataBytes, PROT_NONE, MAP_NORESERVE);
			}
		}

		// a Tile standing in for "new Tile()": taken from a ring, so it is only good for a short while
		public static void* New()
		{
			if (ring == null)
			{
				lock (gate)
				{
					Reserve();
				}
			}
			byte* t = ring + ((long) (Interlocked.Increment(ref next) & (RingSize - 1))) * Tiles.RecordSize;
			Tiles.Zero(t);
			return t;
		}

		// called whenever Main.maxTilesX or Main.maxTilesY is assigned
		public static void Sized()
		{
			lock (gate)
			{
				if (Main.maxTilesX != width || Main.maxTilesY != height || stale)
				{
					Tiles.fastW = 0;
					Tiles.fastH = 0;
					stale = true;
				}
			}
		}

		private static void Sync()
		{
			if (!stale && Tiles.data != null)
			{
				return;
			}
			stale = false;
			Reserve();
			int w = Math.Max(0, Math.Min(Main.maxTilesX, MaxWidth));
			int h = Math.Max(0, Math.Min(Main.maxTilesY, MaxHeight));
			if (w != width || h != height)
			{
				long bytes = (long) w * h * Tiles.RecordSize;
				if (usable > 0)
				{
					madvise((IntPtr) Tiles.data, (UIntPtr) (ulong) usable, MADV_DONTNEED);
				}
				if (bytes > usable)
				{
					if (mprotect((IntPtr) Tiles.data, (UIntPtr) (ulong) bytes, PROT_RW) != 0)
					{
						throw new OutOfMemoryException("tiles: no memory (" + (bytes >> 20) + " MB, errno " + Marshal.GetLastWin32Error() + ")");
					}
					usable = bytes;
				}
				madvise((IntPtr) present, (UIntPtr) (ulong) PresentBytes, MADV_DONTNEED);
				List<int> drop = new List<int>();
				foreach (int key in outside.Keys)
				{
					if (key / MaxHeight < w && key % MaxHeight < h)
					{
						drop.Add(key);
					}
				}
				foreach (int key in drop)
				{
					outside.Remove(key);
				}
				width = w;
				height = h;
				absent = (long) w * h;
				if (Environment.GetEnvironmentVariable("TERRARIA_TILE_DEBUG") == "1")
				{
					Console.WriteLine("[tiles] " + w + " x " + h + ", " + (bytes >> 20) + " MB");
				}
			}
			if (absent == 0 && width > 0 && height > 0)
			{
				Fast();
			}
		}

		private static void Fast()
		{
			madvise((IntPtr) present, (UIntPtr) (ulong) PresentBytes, MADV_DONTNEED);
			Tiles.fastH = height;
			Tiles.fastW = width;
		}

		private static bool Present(long cell)
		{
			return absent == 0 || (present[cell >> 6] & (1UL << (int) (cell & 0x3F))) != 0;
		}

		public static void* At(int x, int y)
		{
			if ((uint) x >= MaxWidth || (uint) y >= MaxHeight)
			{
				throw new IndexOutOfRangeException();
			}
			lock (gate)
			{
				Sync();
				if (x < width && y < height)
				{
					long cell = (long) x * height + y;
					return Present(cell) ? Tiles.data + cell * Tiles.RecordSize : null;
				}
				IntPtr p;
				return outside.TryGetValue(x * MaxHeight + y, out p) ? (void*) p : null;
			}
		}

		public static void* Put(int x, int y, void* value, bool fresh)
		{
			if ((uint) x >= MaxWidth || (uint) y >= MaxHeight)
			{
				throw new IndexOutOfRangeException();
			}
			lock (gate)
			{
				Sync();
				if (x < width && y < height)
				{
					long cell = (long) x * height + y;
					byte* record = Tiles.data + cell * Tiles.RecordSize;
					ulong bit = 1UL << (int) (cell & 0x3F);
					if (value == null && !fresh)
					{
						if (!Present(cell))
						{
							return null;
						}
						if (absent == 0)
						{
							// the first absent cell: the bitmap starts out all present
							Tiles.fastW = 0;
							Tiles.fastH = 0;
							long words = ((long) width * height + 63) / 64;
							for (long i = 0; i < words; i++)
							{
								present[i] = ulong.MaxValue;
							}
						}
						present[cell >> 6] &= ~bit;
						absent++;
						return null;
					}
					if (fresh)
					{
						Tiles.Zero(record);
					}
					else if (record != value)
					{
						Tiles.Copy(record, (byte*) value);
					}
					if (!Present(cell))
					{
						present[cell >> 6] |= bit;
						if (--absent == 0)
						{
							Fast();
						}
					}
					return record;
				}
				int key = x * MaxHeight + y;
				if (value == null && !fresh)
				{
					outside.Remove(key);
					return null;
				}
				IntPtr old;
				if (!fresh && outside.TryGetValue(key, out old) && (void*) old == value)
				{
					return value;
				}
				byte* heap = (byte*) Marshal.AllocHGlobal(Tiles.RecordSize);
				if (fresh)
				{
					Tiles.Zero(heap);
				}
				else
				{
					Tiles.Copy(heap, (byte*) value);
				}
				outside[key] = (IntPtr) heap;
				return heap;
			}
		}
	}
}
