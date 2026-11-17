using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using MonoMod.InlineRT;

#pragma warning disable 1591, 0649, 0169

// The textures from GPU-ASTC's archive (below). The rules make Main.Initialize hand the reader to ReLogic's loaders first thing.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Pack, "pack", m => TerrariaPatches.TileConvert.LazyRules.Pack(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    // Textures from Content/Images.astcpak, which GPU-ASTC (`gpuastc -pack Content/Images`) writes at setup: every texture of
    // Content/Images as ASTC blocks (4x4, 5x5 or 6x6, premultiplied colour as in the game's files), one file, an index sorted by
    // name. The layout is GPU-ASTC's include/astcpak.h. The file is mapped and a texture's blocks go to the driver from the mapping.
    //
    // The game's loaders are left as they are (as in the PortMaster port's pack, wild1walker, MIT): the content source's OpenStream
    // answers a packed name with an 8-byte stand-in (Open), and the XNB reader, given that stand-in, gets its texture here (From).
    // A texture not in the archive is read from its file as before. TR_PACK=0: not used.
    public static unsafe class Pack
    {
        const uint Mark = 0x4B505254;       // "TRPK"
        const int HeaderSize = 64, EntrySize = 52;

        struct Entry
        {
            public long Offset, Size;
            public int Width, Height, Block;
        }

        static readonly bool on = Environment.GetEnvironmentVariable("TR_PACK") != "0";
        static bool tried;
        static byte* map;
        static Dictionary<string, int> names;
        static Entry[] entries;
        public static int Count;
        public static long Bytes;

        [DllImport("libc.so.6", SetLastError = true)]
        static extern IntPtr mmap(IntPtr start, UIntPtr length, int protection, int flags, int file, long offset);
        [DllImport("libc.so.6")]
        static extern int madvise(IntPtr start, UIntPtr length, int advice);    // 3: will be needed

        static void Load()
        {
            tried = true;
            string path = Path.Combine("Content", "Images.astcpak");
            try
            {
                if (!on || !File.Exists(path))
                    return;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
                {
                    long length = f.Length;
                    IntPtr m = mmap(IntPtr.Zero, (UIntPtr) (ulong) length, 1, 2, f.SafeFileHandle.DangerousGetHandle().ToInt32(), 0);   // to read, private
                    if (m == (IntPtr) (-1))
                        throw new IOException("mmap: error " + Marshal.GetLastWin32Error());
                    byte* d = (byte*) m;
                    if (length < HeaderSize || Encoding.ASCII.GetString(d, 8) != "ASTCPAK1" || *(uint*) (d + 8) != 1)
                        throw new InvalidDataException("not an astcpak archive");
                    int count = *(int*) (d + 12);
                    long indexAt = *(long*) (d + 16), indexSize = *(long*) (d + 24);
                    if (indexAt < HeaderSize || indexAt + indexSize > length || (long) count * EntrySize > indexSize)
                        throw new InvalidDataException("damaged index");
                    byte* index = d + indexAt, text = index + (long) count * EntrySize;
                    long textSize = indexSize - (long) count * EntrySize;
                    var list = new Entry[count];
                    var map2 = new Dictionary<string, int>(count, StringComparer.Ordinal);
                    for (int i = 0; i < count; i++)
                    {
                        byte* r = index + (long) i * EntrySize;
                        uint at = *(uint*) r, len = *(uint*) (r + 4);
                        var e = new Entry { Width = *(int*) (r + 8), Height = *(int*) (r + 12), Block = r[16], Offset = *(long*) (r + 20), Size = *(long*) (r + 28) };
                        if ((long) at + len > textSize || e.Offset < HeaderSize || e.Offset + e.Size > length || e.Width <= 0 || e.Height <= 0)
                            throw new InvalidDataException("entry " + i);
                        list[i] = e;
                        // entry names are relative to Content/Images; the game asks for "Images/<name>"
                        map2["Images/" + Encoding.UTF8.GetString(text + at, (int) len)] = i;
                    }
                    map = d;
                    entries = list;
                    names = map2;
                    Console.Error.WriteLine("[pack] Content/Images.astcpak: " + count + " textures, " + (length >> 20) + " MB, mapped");
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[pack] Content/Images.astcpak not used: " + e.Message);
            }
        }

        // ReLogic.dll's content source and XNB reader call these through TrPatches.Assets.PackOpen / PackFrom (ReLogic cannot name
        // this assembly); they are handed over by reflection at Main.Initialize, before the first asset is read
        public static void Register()
        {
            Type assets = Type.GetType("TrPatches.Assets, ReLogic");
            assets.GetField("PackOpen").SetValue(null, (Func<string, Stream>) Open);
            assets.GetField("PackFrom").SetValue(null, (Func<Stream, object>) From);
        }

        // XnaContentSource.OpenStream, first thing: the stand-in, or null (the file is opened)
        public static Stream Open(string assetName)
        {
            if (!tried)
                lock (typeof(Pack))
                    if (!tried)
                        Load();
            if (entries == null)
                return null;
            if (assetName.IndexOf('\\') >= 0)
                assetName = assetName.Replace('\\', '/');
            int id;
            if (!names.TryGetValue(assetName, out id))
                return null;
            var stand = new byte[8];
            fixed (byte* p = stand)
            {
                *(uint*) p = Mark;
                *(int*) (p + 4) = id;
            }
            return new MemoryStream(stand, 0, 8, false);
        }

        // XnbReader.FromStream<T>, first thing: the texture of a stand-in, or null (FNA reads the stream)
        public static object From(Stream stream)
        {
            var m = stream as MemoryStream;
            if (entries == null || m == null || m.Length != 8)
                return null;
            var stand = new byte[8];
            m.Position = 0;
            m.Read(stand, 0, 8);
            int id = BitConverter.ToInt32(stand, 4);
            if (BitConverter.ToUInt32(stand, 0) != Mark || (uint) id >= (uint) entries.Length)
            {
                m.Position = 0;
                return null;
            }
            return Make(id);
        }

        static Texture2D Make(int id)
        {
            Entry e = entries[id];
            SurfaceFormat format = e.Block == 6 ? SurfaceFormat.Astc6x6EXT : e.Block == 5 ? SurfaceFormat.Astc5x5EXT : SurfaceFormat.Astc4x4EXT;
            var texture = new Texture2D(Main.instance.GraphicsDevice, e.Width, e.Height, false, format);
            long page = e.Offset >> 12 << 12;
            madvise((IntPtr) (map + page), (UIntPtr) (ulong) (e.Offset + e.Size - page), 3);
            texture.SetDataPointerEXT(0, null, (IntPtr) (map + e.Offset), (int) e.Size);
            Interlocked.Increment(ref Count);
            Interlocked.Add(ref Bytes, e.Size);
            return texture;
        }
    }
}
