// trsetup: the three setup steps of wild1walker's trpatch that our port runs (the patching itself is MonoMod's, in Terraria/).
// From portmaster-terraria (MIT: tools/trpatch/Program.cs, Extract copied as it is).
//   trsetup extract <Terraria.exe> <dir>               the libraries Terraria.exe carries as resources
//   trsetup stubs <dir> <framework dir> <file>...      stand-ins for the Windows types .NET lacks (TrStubs.dll)
//   trsetup content <game> <out>                       the textures' sizes and the fonts uncompressed
using System;
using System.IO;
using System.Linq;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "extract") return Extract(args[1], args[2]);
        if (args.Length >= 4 && args[0] == "stubs") return Stubs.Run(args[1], args[2], Enumerable.ToArray(Enumerable.Skip(args, 3)));
        if (args.Length == 3 && args[0] == "content") return Install.Content(args[1], args[2]);
        Console.Error.WriteLine("usage: trsetup extract <Terraria.exe> <dir> | stubs <dir> <framework dir> <file>... | content <game> <out>");
        return 2;
    }

    static int Extract(string exe, string dir)
    {
        Directory.CreateDirectory(dir);
        var m = Mono.Cecil.ModuleDefinition.ReadModule(exe);
        foreach (var r in m.Resources.OfType<Mono.Cecil.EmbeddedResource>().Where(r => r.Name.EndsWith(".dll")))
        {
            var a = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(r.GetResourceData()));
            File.WriteAllBytes(Path.Combine(dir, a.Name.Name + ".dll"), r.GetResourceData());
            Console.WriteLine(a.Name.Name + ".dll");
        }
        return 0;
    }
}
