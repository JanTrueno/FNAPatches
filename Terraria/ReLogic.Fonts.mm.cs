using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.InlineRT;

#pragma warning disable 1591, 0649, 0169

// Lazy font pages. Terraria's five fonts carry about 160 pages each (a page per script the game is translated into: 206 MB of
// DXT3 on the GPU). TrPatches.Fonts below is the PortMaster port's (wild1walker, portmaster-terraria, MIT: tools/trpatch/runtime/
// Fonts.cs), copied as it is: a page is read as a stand-in and its texture is made when a glyph of it is first drawn. The font
// files have to be stored uncompressed for that (`trpatch content` at setup writes them so). The rules edit ReLogic.dll's font
// reader and drawing methods to call it.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_FONTS") == "0")
			{
				return;
			}
			MonoModRule.Modder.PostProcessors += delegate (MonoModder modder)
			{
				Console.WriteLine("[fonts] ReLogic: " + TerrariaPatches.TileConvert.LazyRules.Fonts(modder.Module) + " changes");
			};
		}
	}
}

namespace TrPatches
{
    // A font's pages, loaded when a glyph of the page is first drawn. ReLogic's DynamicSpriteFontReader
    // calls ReadPage instead of ReadObject<Texture2D>; DynamicSpriteFont's draw methods pass each
    // glyph's texture through Resolve. Terraria's five fonts carry 159 - 160 pages each (every script
    // the game is translated into: 206 MB of DXT3, about 820 MB as the RGBA that a GPU without S3TC
    // gets), and a language draws from a few of them.
    // A page is read again from its file when it is first drawn, so the font's XNB must be stored
    // uncompressed (trpatch install writes them so); from a compressed XNB, or with TR_FONTS_EAGER=1,
    // pages load at once as before.
    public static class Fonts
    {
        sealed class Page
        {
            public string File;
            public long Offset;          // the first mip level's size field
            public SurfaceFormat Format;
            public int Width, Height, Levels;
            public GraphicsDevice Device;
            public Texture2D Real;
        }

        // stand-in -> page; fonts live as long as the game, so a plain dictionary by reference (the
        // draws run on the main thread; the reader may run elsewhere, under the lock)
        static readonly Dictionary<Texture2D, Page> pages = new Dictionary<Texture2D, Page>(new ByReference());
        static Texture2D lastStand, lastReal;

        sealed class ByReference : IEqualityComparer<Texture2D>
        {
            public bool Equals(Texture2D a, Texture2D b) => ReferenceEquals(a, b);
            public int GetHashCode(Texture2D t) => RuntimeHelpers.GetHashCode(t);
        }
        static readonly bool eager = Environment.GetEnvironmentVariable("TR_FONTS_EAGER") == "1";
        public static int PagesRead, PagesLoaded;
        public static long BytesLoaded;

        public static Texture2D ReadPage(ContentReader input)
        {
            if (eager || !(input.BaseStream is FileStream file))
                return input.ReadObject<Texture2D>();
            long start = input.BaseStream.Position;
            int type = Read7Bit(input);
            if (type == 0)
                return null;
            var page = new Page
            {
                File = file.Name,
                Format = (SurfaceFormat)input.ReadInt32(),
                Width = input.ReadInt32(),
                Height = input.ReadInt32(),
                Levels = input.ReadInt32(),
                Device = ((IGraphicsDeviceService)input.ContentManager.ServiceProvider.GetService(typeof(IGraphicsDeviceService))).GraphicsDevice,
            };
            page.Offset = input.BaseStream.Position;
            for (int l = 0; l < page.Levels; l++)
                input.BaseStream.Seek(input.ReadInt32(), SeekOrigin.Current);
            // a stand-in with no GPU texture behind it: the key Resolve looks the page up by
            var stand = (Texture2D)FormatterServices.GetUninitializedObject(typeof(Texture2D));
            GC.SuppressFinalize(stand);
            lock (pages)
                pages.Add(stand, page);
            PagesRead++;
            return stand;
        }

        // every glyph drawn: the last page's texture at once (a string's glyphs mostly share a page)
        public static Texture2D Resolve(Texture2D texture)
        {
            if (ReferenceEquals(texture, lastStand))
                return lastReal;
            Page page;
            lock (pages)
                if (texture == null || !pages.TryGetValue(texture, out page))
                    return texture;
            if (page.Real == null)
                lock (page)
                    if (page.Real == null)
                        page.Real = Load(page);
            lastReal = page.Real;
            lastStand = texture;
            return page.Real;
        }

        static Texture2D Load(Page page)
        {
            // as FNA's Texture2DReader: DXT the GPU cannot sample is unpacked to Color on the CPU
            SurfaceFormat format = page.Format;
            if ((format == SurfaceFormat.Dxt1 && !Supports(page.Device, "FNA3D_SupportsDXT1")) ||
                ((format == SurfaceFormat.Dxt3 || format == SurfaceFormat.Dxt5) && !Supports(page.Device, "FNA3D_SupportsS3TC")))
                format = SurfaceFormat.Color;
            var texture = new Texture2D(page.Device, page.Width, page.Height, page.Levels > 1, format);
            using (var stream = new FileStream(page.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                stream.Position = page.Offset;
                var reader = new BinaryReader(stream);
                for (int l = 0; l < page.Levels; l++)
                {
                    byte[] data = reader.ReadBytes(reader.ReadInt32());
                    if (format != page.Format)
                        data = Unpack(page.Format, data, page.Width >> l, page.Height >> l);
                    texture.SetData(l, null, data, 0, data.Length);
                    BytesLoaded += data.Length;
                }
            }
            PagesLoaded++;
            return texture;
        }

        static int Read7Bit(BinaryReader r)
        {
            int v = 0, shift = 0;
            while (true) { byte b = r.ReadByte(); v |= (b & 0x7F) << shift; if ((b & 0x80) == 0) return v; shift += 7; }
        }

        // FNA's internals, as its Texture2DReader uses them
        static readonly Assembly fna = typeof(Texture2D).Assembly;
        static bool Supports(GraphicsDevice device, string function)
        {
            object gl = typeof(GraphicsDevice).GetField("GLDevice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(device);
            MethodInfo m = fna.GetType("Microsoft.Xna.Framework.Graphics.FNA3D").GetMethod(function, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return Convert.ToInt32(m.Invoke(null, new[] { gl })) != 0;
        }

        static byte[] Unpack(SurfaceFormat format, byte[] data, int width, int height)
        {
            string name = format == SurfaceFormat.Dxt1 ? "DecompressDxt1" : format == SurfaceFormat.Dxt3 ? "DecompressDxt3" : "DecompressDxt5";
            MethodInfo m = fna.GetType("Microsoft.Xna.Framework.Graphics.DxtUtil").GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(byte[]), typeof(int), typeof(int) }, null);
            return (byte[])m.Invoke(null, new object[] { data, width, height });
        }
    }
}
