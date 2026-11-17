using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BF = System.Reflection.BindingFlags;
using MethodInfo = System.Reflection.MethodInfo;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;

using MonoMod.InlineRT;
using ReLogic.Content;

#pragma warning disable 1591, 0649, 0169

// Lazy textures and eviction. Terraria loads some 2,300 textures when it starts and keeps every one in memory and on the GPU,
// though a world on screen draws a few hundred. Here a texture is loaded the first time something reads it and unloaded when it
// has not been drawn for a while or when free memory runs low. Follows the PortMaster port by wild1walker (portmaster-terraria,
// MIT: TrPatches/Assets.cs and Evict.cs), reimplemented: the runtime below is added to ReLogic.dll, and the rules at the end make
// the few edits inside ReLogic's Asset<T> that the runtime hangs on. Terraria.Lazy.mm.cs makes the matching edits in Terraria.exe.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_LAZY") == "0")
			{
				Console.WriteLine("[lazy] TERRARIA_LAZY=0: lazy textures left out");
				return;
			}
			MonoModRule.Modder.PostProcessors += delegate (MonoModder modder)
			{
				Console.WriteLine("[lazy] ReLogic: " + TerrariaPatches.TileConvert.LazyRules.Relogic(modder.Module) + " changes");
			};
		}
	}
}

namespace ReLogic.Content
{
	// adds the field Evict keeps the time of last use in; MonoMod relinks this class to Asset<T>
	class patch_Asset<T> where T : class
	{
		public int trUsed;
	}
}

namespace TrPatches
{
	public static class Assets
	{
		private static IAssetRepository repository;
		private static readonly Dictionary<Type, MethodInfo> requests = new Dictionary<Type, MethodInfo>();
		private static readonly HashSet<string> failed = new HashSet<string>();
		[ThreadStatic] private static bool inside;
		public static int Loads;
		public static readonly bool Verbose = Environment.GetEnvironmentVariable("TR_STATS_VERBOSE") == "1";
		private static readonly HashSet<object> deferred = new HashSet<object>();
		private static int mainThread;
		private static readonly Queue<KeyValuePair<object, ManualResetEventSlim>> queued = new Queue<KeyValuePair<object, ManualResetEventSlim>>();

		// the texture pack (TrPatches.Pack, in Terraria.exe's patch) registers itself here
		public static Func<string, Stream> PackOpen;
		public static Func<Stream, object> PackFrom;

		public static Stream PackOpenHook(string name)
		{
			return PackOpen == null ? null : PackOpen(name);
		}

		public static object PackFromHook(Stream stream)
		{
			return PackFrom == null ? null : PackFrom(stream);
		}

		// an asset that was requested without loading it
		public static void Defer(object asset)
		{
			lock (deferred) { deferred.Add(asset); }
		}

		public static bool IsDeferred(object asset)
		{
			lock (deferred) { return deferred.Contains(asset); }
		}

		public static void EnsureDeferred(object asset)
		{
			if (IsDeferred(asset)) Ensure(asset);
		}

		// Asset<T>.SubmitLoadedContent
		public static void Loaded(object asset, object value)
		{
			Texture2D texture = value as Texture2D;
			if (texture != null && asset is Asset<Texture2D>)
			{
				texture.Tag = asset;
				Evict.Loaded((Asset<Texture2D>) asset);
			}
			if (Verbose) Console.Error.WriteLine("[lazy] loaded: " + ((IAsset) asset).Name + (texture != null ? " " + texture.Width + "x" + texture.Height : ""));
		}

		// AssetRepository's constructor
		public static void SetRepository(object repo)
		{
			if (repository == null)
			{
				repository = (IAssetRepository) repo;
				mainThread = Thread.CurrentThread.ManagedThreadId;
				SpriteBatch.TextureDrawnEXT = Evict.Drawn;
			}
		}

		public static void Pump()
		{
			while (true)
			{
				KeyValuePair<object, ManualResetEventSlim> item;
				lock (queued)
				{
					if (queued.Count == 0) break;
					item = queued.Dequeue();
				}
				try { Ensure(item.Key); }
				finally { item.Value.Set(); }
			}
		}

		// Asset<T>.Value on an asset that is not loaded: load it now. Textures are created on the main thread (the GL context is
		// there); another thread asks the main thread, which answers from Evict.Tick.
		public static void Ensure(object asset)
		{
			IAsset a = (IAsset) asset;
			string name = a.Name;
			if (inside || repository == null || string.IsNullOrEmpty(name) || a.IsDisposed) return;
			if (asset is Asset<Texture2D> && Thread.CurrentThread.ManagedThreadId != mainThread)
			{
				using (ManualResetEventSlim done = new ManualResetEventSlim())
				{
					lock (queued) { queued.Enqueue(new KeyValuePair<object, ManualResetEventSlim>(asset, done)); }
					if (!done.Wait(10000)) Console.Error.WriteLine("[lazy] the main thread did not load " + name + " in 10 s");
				}
				return;
			}
			lock (failed) { if (failed.Contains(name)) return; }
			Type type = asset.GetType().GetGenericArguments()[0];
			MethodInfo request;
			lock (requests)
			{
				if (!requests.TryGetValue(type, out request))
				{
					request = requests[type] = typeof(IAssetRepository).GetMethod("Request").MakeGenericMethod(type);
				}
			}
			inside = true;
			try
			{
				request.Invoke(repository, new object[] { name, AssetRequestMode.ImmediateLoad });
				Interlocked.Increment(ref Loads);
			}
			catch (Exception ex)
			{
				lock (failed) { failed.Add(name); }
				Console.Error.WriteLine("[lazy] cannot load " + name + ": " + (ex.InnerException ?? ex).Message);
			}
			finally { inside = false; }
		}
	}

	public static class Evict
	{
		public static int Now;
		private static readonly Stopwatch clock = Stopwatch.StartNew();
		private static readonly int after = int.Parse(Environment.GetEnvironmentVariable("TR_EVICT") ?? "30");
		private static readonly int mark = int.Parse(Environment.GetEnvironmentVariable("TR_EVICT_FREE") ?? "140");
		private static int nextSweep = 5;
		private static MethodInfo unload;
		public static int Count;
		public static long Bytes;
		public static volatile int Free;
		private static Thread watch;
		private static readonly List<Asset<Texture2D>> line = new List<Asset<Texture2D>>();
		private static int lineAt;
		private static int lineAge;
		private static long owed;
		private static readonly HashSet<Asset<Texture2D>> loaded = new HashSet<Asset<Texture2D>>();
		private static readonly Dictionary<object, int> unloadedAt = new Dictionary<object, int>();
		private static readonly HashSet<object> kept = new HashSet<object>();

		private static ref int Used(Asset<Texture2D> asset)
		{
			return ref ((ReLogic.Content.patch_Asset<Texture2D>) (object) asset).trUsed;
		}

		// SpriteBatch.TextureDrawnEXT
		public static void Drawn(Texture2D texture)
		{
			Asset<Texture2D> a = texture != null ? texture.Tag as Asset<Texture2D> : null;
			if (a != null) Used(a) = Now;
		}

		// replaces Terraria's reads of Asset<Texture2D>.State: asking is using
		public static AssetState State(Asset<Texture2D> asset)
		{
			Used(asset) = Now;
			return asset.State;
		}

		public static void Loaded(Asset<Texture2D> asset)
		{
			Used(asset) = Now;
			lock (loaded) { loaded.Add(asset); }
			lock (unloadedAt)
			{
				int at;
				if (!unloadedAt.TryGetValue(asset, out at)) return;
				unloadedAt.Remove(asset);
				// loaded again within 2 s of being unloaded: it is in use, keep it from now on
				if (Now - at <= 2) kept.Add(asset);
			}
		}

		// once a frame, on the main thread
		public static void Tick()
		{
			Now = (int) clock.Elapsed.TotalSeconds;
			Assets.Pump();
			if (after <= 0) return;
			try
			{
				if (lineAt < line.Count) Unload(2);
				if (Now >= nextSweep)
				{
					nextSweep = Now + 5;
					Sweep();
				}
			}
			catch (Exception ex) { Console.Error.WriteLine("[lazy] eviction: " + ex.Message); }
		}

		private static void Watch()
		{
			while (true)
			{
				try
				{
					foreach (string l in File.ReadLines("/proc/meminfo"))
					{
						if (l.StartsWith("MemAvailable:", StringComparison.Ordinal))
						{
							Free = (int) (long.Parse(l.Substring(13).Replace("kB", "").Trim()) >> 10);
							break;
						}
					}
				}
				catch { Free = 0; break; }
				Thread.Sleep(2000);
			}
		}

		private static void Sweep()
		{
			if (watch == null && mark > 0)
			{
				watch = new Thread(Watch) { IsBackground = true, Name = "lazy memory" };
				watch.Start();
				return;
			}
			int free = Free;
			bool known = mark > 0 && free > 0;
			if (known && free >= mark) return;
			line.Clear();
			lineAt = 0;
			lineAge = (known && free < mark * 3 / 4) ? Math.Min(after, 10) : after;
			owed = known ? ((long) (mark - free) << 20) : long.MaxValue;
			lock (loaded)
			{
				loaded.RemoveWhere(delegate (Asset<Texture2D> a) { return (int) a.State != 2; });
				foreach (Asset<Texture2D> a in loaded)
				{
					if (a.Source == null || Now - Used(a) < lineAge) continue;
					string name = a.Name;
					if (name.StartsWith("Images/UI/", StringComparison.Ordinal) || name.StartsWith("Images\\UI\\", StringComparison.Ordinal)) continue;
					lock (unloadedAt) { if (kept.Contains(a)) continue; }
					line.Add(a);
				}
			}
			line.Sort(delegate (Asset<Texture2D> x, Asset<Texture2D> y) { return Used(x).CompareTo(Used(y)); });
		}

		private static void Unload(int most)
		{
			while (most > 0 && lineAt < line.Count && owed > 0)
			{
				Asset<Texture2D> a = line[lineAt];
				int unused = Now - Used(a);
				if ((int) a.State == 2 && unused >= lineAge)
				{
					Texture2D value = a.Value;
					Used(a) = Now - unused;
					if (value != null && !(value is RenderTarget2D))
					{
						long size = (long) value.Width * value.Height * 4;
						if (unload == null) unload = typeof(Asset<Texture2D>).GetMethod("Unload", BF.Instance | BF.NonPublic);
						unload.Invoke(a, null);
						lock (unloadedAt) { unloadedAt[a] = Now; }
						Count++;
						Bytes += size;
						owed -= size;
						most--;
						if (Assets.Verbose) Console.Error.WriteLine("[lazy] unloaded: " + a.Name + " (" + (size >> 10) + " KB, unused " + unused + " s)");
					}
				}
				lineAt++;
			}
			if (lineAt >= line.Count || owed <= 0)
			{
				line.Clear();
				lineAt = 0;
			}
		}
	}
}

// TrPatches.Sizes: the PortMaster port's (wild1walker, portmaster-terraria, MIT: tools/trpatch/runtime/Sizes.cs), copied as it is.
namespace TrPatches
{
    // Terraria's Utils.Width/Height/Frame/Size(Asset<Texture2D>) read a texture's size; the game asks
    // them of an asset that is not loaded and gets 0 (or an empty rectangle). For the textures trpatch
    // defers (the game loaded them at start-up, so the game always saw their sizes), the size comes from
    // Content/trpatch-sizes.txt, which trpatch install writes from the XNB headers: no texture is loaded
    // only to be measured (the mounts', the credits', the world load's own reads), and none from the
    // world-loading thread. Every other asset answers as the game's own helpers do.
    public static class Sizes
    {
        static Dictionary<string, Point> table;

        static bool Lookup(Asset<Texture2D> asset, out Point size)
        {
            if (table == null)
                Load();
            return table.TryGetValue(asset.Name.Replace('\\', '/'), out size);
        }

        static void Load()
        {
            var t = new Dictionary<string, Point>(16384);
            string path = Path.Combine("Content", "trpatch-sizes.txt");
            if (File.Exists(path))
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] f = line.Split(' ');
                    if (f.Length == 3)
                        t[f[0]] = new Point(int.Parse(f[1]), int.Parse(f[2]));
                }
            table = t;
        }

        static bool Known(Asset<Texture2D> asset, out Point size)
        {
            size = default;
            if (asset.State == AssetState.Loaded)
            {
                Texture2D t = asset.Value;
                size = new Point(t.Width, t.Height);
                return true;
            }
            if (Assets.IsDeferred(asset) && Lookup(asset, out size))
                return true;
            if (!asset.IsLoaded)                 // the deferred ones not in the table load here
                return false;
            Texture2D v = asset.Value;
            size = new Point(v.Width, v.Height);
            return true;
        }

        public static int Width(Asset<Texture2D> asset) => Known(asset, out Point s) ? s.X : 0;

        public static int Height(Asset<Texture2D> asset) => Known(asset, out Point s) ? s.Y : 0;

        public static Vector2 Size(Asset<Texture2D> asset) => Known(asset, out Point s) ? new Vector2(s.X, s.Y) : Vector2.Zero;

        // as Utils.Frame(Texture2D, ...)
        public static Rectangle Frame(Asset<Texture2D> asset, int horizontalFrames, int verticalFrames, int frameX, int frameY, int sizeOffsetX, int sizeOffsetY)
        {
            if (!Known(asset, out Point s))
                return Rectangle.Empty;
            int w = s.X / horizontalFrames, h = s.Y / verticalFrames;
            return new Rectangle(w * frameX, h * frameY, w + sizeOffsetX, h + sizeOffsetY);
        }
    }
}
