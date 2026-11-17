// From wild1walker's PortMaster Terraria port (portmaster-terraria, MIT: tools/trpatch/Xnb.cs), copied as it is.
using System;
using System.IO;
using System.Reflection;
using System.Text;

// XNB files: the header, LZX decompression through FNA's own LzxDecoder (the game's FNA.dll, found by
// reflection), and the 7-bit integers and strings of the content stream.
static class Xnb
{
    // the first bytes of the content (at least <head> of them; the whole file when uncompressed)
    public static byte[] ReadHead(string path, int head)
    {
        byte[] f = File.ReadAllBytes(path);
        if (f.Length < 10 || f[0] != 'X' || f[1] != 'N' || f[2] != 'B') throw new InvalidDataException(path + ": not an XNB file");
        int fileSize = BitConverter.ToInt32(f, 6);
        if ((f[5] & 0x80) == 0) { var raw = new byte[fileSize - 10]; Array.Copy(f, 10, raw, 0, raw.Length); return raw; }
        return Lzx(f, 14, fileSize - 14, BitConverter.ToInt32(f, 10), head);
    }

    public static byte[] Read(string path, out byte platform, out byte flags)
    {
        byte[] f = File.ReadAllBytes(path);
        if (f.Length < 10 || f[0] != 'X' || f[1] != 'N' || f[2] != 'B') throw new InvalidDataException(path + ": not an XNB file");
        platform = f[3]; flags = f[5];
        int fileSize = BitConverter.ToInt32(f, 6);
        if ((flags & 0x80) == 0) { var raw = new byte[fileSize - 10]; Array.Copy(f, 10, raw, 0, raw.Length); return raw; }
        int outSize = BitConverter.ToInt32(f, 10);
        return Lzx(f, 14, fileSize - 14, outSize);
    }

    static Type lzxType;
    static byte[] Lzx(byte[] f, int start, int compressed, int outSize, int stopAfter = int.MaxValue)
    {
        lock (typeof(Xnb))
            if (lzxType == null)
            {
                string fna = Environment.GetEnvironmentVariable("FNA_DLL") ?? "FNA.dll";
                lzxType = Assembly.LoadFrom(fna).GetType("Microsoft.Xna.Framework.Content.LzxDecoder", true);
            }
        object dec = Activator.CreateInstance(lzxType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { 16 }, null);
        MethodInfo decompress = lzxType.GetMethod("Decompress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var input = new MemoryStream(f, 0, start + compressed);
        input.Position = start;
        var output = new MemoryStream(outSize);
        long pos = start;
        while (pos - start < compressed)
        {
            int hi = input.ReadByte(), lo = input.ReadByte();
            int block = (hi << 8) | lo, frame = 0x8000;
            if (hi == 0xFF)
            {
                hi = lo; lo = input.ReadByte(); frame = (hi << 8) | lo;
                hi = input.ReadByte(); lo = input.ReadByte(); block = (hi << 8) | lo;
                pos += 5;
            }
            else pos += 2;
            if (block == 0 || frame == 0) break;
            decompress.Invoke(dec, new object[] { input, block, output, frame });
            pos += block;
            input.Position = pos;
            if (output.Length >= stopAfter)
                return output.ToArray();
        }
        if (output.Length != outSize) throw new InvalidDataException("LZX: " + output.Length + " bytes, expected " + outSize);
        return output.ToArray();
    }

    public static int Read7Bit(BinaryReader r)
    {
        int v = 0, shift = 0;
        while (true) { byte b = r.ReadByte(); v |= (b & 0x7F) << shift; if ((b & 0x80) == 0) return v; shift += 7; }
    }

    // the type reader table, then the shared resource count; returns the reader names
    public static string[] ReadHeader(BinaryReader r)
    {
        int n = Read7Bit(r);
        var names = new string[n];
        for (int i = 0; i < n; i++) { names[i] = r.ReadString(); r.ReadInt32(); }
        Read7Bit(r);
        return names;
    }
}
