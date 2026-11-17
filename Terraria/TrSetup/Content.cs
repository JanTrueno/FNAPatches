// From wild1walker's PortMaster Terraria port (portmaster-terraria, MIT: tools/trpatch/Install.cs): its `content` command
// (Content, WriteSizes, Uncompress, SamePath), copied as they are.
using System;
using System.Collections.Generic;
using System.IO;

static class Install
{
    public static int Content(string game, string output)
    {
        // (the textures' LZX is read with the game's FNA.dll)
        if (Environment.GetEnvironmentVariable("FNA_DLL") == null)
            Environment.SetEnvironmentVariable("FNA_DLL", Environment.GetEnvironmentVariable("TR_FNA") ?? Path.Combine(Path.GetFullPath(game), "FNA.dll"));
        Console.WriteLine("Content/trpatch-sizes.txt: " + WriteSizes(Path.Combine(game, "Content"), Path.Combine(output, "Content", "trpatch-sizes.txt")) + " textures");

        string fonts = Path.Combine(game, "Content", "Fonts");
        if (Directory.Exists(fonts))
            foreach (string f in Directory.GetFiles(fonts, "*.xnb"))
                Console.WriteLine($"{Path.GetFileName(f)}: " + Uncompress(f, Path.Combine(output, "Content", "Fonts", Path.GetFileName(f))));
        return 0;
    }

    static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('/') == Path.GetFullPath(b).TrimEnd('/');

    // every texture's size, "Images/NPC_1 40 224" a line, from the XNB headers (TrPatches.Sizes)
    static int WriteSizes(string content, string output)
    {
        var lines = new List<string>();
        string images = Path.Combine(content, "Images");
        foreach (string path in Directory.EnumerateFiles(images, "*.xnb", SearchOption.AllDirectories))
        {
            var r = new BinaryReader(new MemoryStream(Xnb.ReadHead(path, 256)));
            string[] readers = Xnb.ReadHeader(r);
            int type = Xnb.Read7Bit(r);
            if (type == 0 || !readers[type - 1].StartsWith("Microsoft.Xna.Framework.Content.Texture2DReader"))
                continue;
            r.ReadInt32();
            int w = r.ReadInt32(), h = r.ReadInt32();
            string name = path.Substring(content.Length + 1, path.Length - content.Length - 5).Replace('\\', '/');
            lines.Add($"{name} {w} {h}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllLines(output, lines);
        return lines.Count;
    }


    // an XNB stored uncompressed (the same content, LZX undone)
    static string Uncompress(string from, string to)
    {
        byte[] data = Xnb.Read(from, out byte platform, out byte flags);
        byte version;
        using (var f = File.OpenRead(from)) { f.Position = 4; version = (byte)f.ReadByte(); }
        Directory.CreateDirectory(Path.GetDirectoryName(to));
        if ((flags & 0x80) == 0 && SamePath(from, to))
            return "already uncompressed";
        string tmp = to + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            w.Write(new[] { (byte)'X', (byte)'N', (byte)'B', platform, version, (byte)(flags & 0x7F) });
            w.Write(10 + data.Length);
            w.Write(data);
        }
        File.Copy(tmp, to, true);
        File.Delete(tmp);
        return $"uncompressed ({data.Length >> 20} MB)";
    }

}
