// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch, MIT), as it is there, except where marked "(FNAPatches)".
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Terraria.Tile as a 14-byte value, and every Tile in the game's code a pointer to its record in
// TrPatches.Tiles' native block (the Android build's tiles are such handles): no object a tile, no
// lookup, nothing to allocate or collect.
static partial class TilePtr
{
    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        var stack = new Stack<TypeDefinition>(m.Types);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            // (FNAPatches) the runtime (TrPatches.Tiles, TileStore) is woven into Terraria.exe itself, compiled with plain
            // pointers: it is not converted
            if (t.Namespace == "TrPatches")
                continue;
            yield return t;
            foreach (var n in t.NestedTypes) stack.Push(n);
        }
    }

    static bool IsGridCall(Instruction ins, string name) =>
        ins.Operand is MethodReference mr && mr.Name == name && mr.DeclaringType is ArrayType at && TileSim.IsTile(at.ElementType);

    // does the type mention Tile other than as itself, a reference to it or the two-dimensional array
    static bool Odd(TypeReference t)
    {
        switch (t)
        {
            case null: return false;
            case GenericInstanceType g: return g.GenericArguments.Any(a => TileSim.IsTile(a) || Odd(a));
            case ArrayType a: return TileSim.IsTile(a.ElementType) ? a.Rank != 2 : Odd(a.ElementType);
            case TypeSpecification s: return !TileSim.IsTile(s.ElementType) && Odd(s.ElementType);
            default: return false;
        }
    }

    // trpatch tile-survey: what the conversion will meet, on the unpatched game
    public static int Survey(ModuleDefinition m)
    {
        int nulls = 0, indirect = 0, casts = 0, news = 0, fresh = 0, methods = 0;
        var notes = new List<string>();
        var grids = new Dictionary<string, int>();
        foreach (var type in AllTypes(m))
        {
            foreach (var f in type.Fields)
            {
                if (TileSim.IsTile(f.FieldType)) notes.Add($"field {f.FullName}");
                if (Odd(f.FieldType)) notes.Add($"odd field {f.FullName}");
            }
            foreach (var method in type.Methods)
            {
                if (TileSim.IsTile(method.ReturnType)) notes.Add($"returns Tile: {method.FullName}");
                if (Odd(method.ReturnType) || method.Parameters.Any(p => Odd(p.ParameterType))) notes.Add($"odd signature {method.FullName}");
                if (!method.HasBody)
                    continue;
                if (method.Body.Variables.Any(v => Odd(v.VariableType))) notes.Add($"odd local in {method.FullName}");
                method.Body.SimplifyMacros();
                var sim = new TileSim(method);
                methods++;
                nulls += sim.NullTiles.Count;
                indirect += sim.Indirect.Count;
                casts += sim.Casts.Count;
                notes.AddRange(sim.Notes.Select(x => sim.Where(x.Key) + ": " + x.Value));
                int n = 0, direct = 0;
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Newobj && TileSim.IsTile(((MethodReference)ins.Operand).DeclaringType))
                    {
                        n++;
                        if (((MethodReference)ins.Operand).Parameters.Count != 0) notes.Add($"{method.FullName}: new Tile with arguments");
                    }
                    if (ins.Operand is MemberReference r && !(r is TypeReference tr0 && TileSim.IsTile(tr0)) &&
                        (Odd(r as TypeReference) || Odd(r.DeclaringType) || (r is GenericInstanceMethod gm && gm.GenericArguments.Any(a => TileSim.IsTile(a) || Odd(a)))))
                        notes.Add($"{method.FullName} IL_{ins.Offset:x4}: odd operand {ins.OpCode} {r.FullName}");
                    if (IsGridCall(ins, ".ctor")) notes.Add($"{method.FullName} IL_{ins.Offset:x4}: a new Tile array");
                    if ((IsGridCall(ins, "Get") || IsGridCall(ins, "Set")) && sim.In.TryGetValue(ins, out var st))
                    {
                        var g = st[st.Length - (IsGridCall(ins, "Get") ? 3 : 4)];
                        grids[g.From == null ? "several" : sim.Takers[g.From].Count != 1 ? "shared " + g.From.OpCode : g.From.OpCode + " " + (g.From.Operand as FieldReference)?.FullName] =
                            grids.TryGetValue(g.From == null ? "several" : sim.Takers[g.From].Count != 1 ? "shared " + g.From.OpCode : g.From.OpCode + " " + (g.From.Operand as FieldReference)?.FullName, out int c) ? c + 1 : 1;
                    }
                    if (IsGridCall(ins, "Address")) notes.Add($"{method.FullName} IL_{ins.Offset:x4}: the address of a grid cell");
                    if (!IsGridCall(ins, "Set") || !sim.In.TryGetValue(ins, out var stack))
                        continue;
                    var v = stack[stack.Length - 1];
                    if (v.New && sim.Takers[v.From].Count == 1) direct++;
                    else notes.Add($"{method.FullName} IL_{ins.Offset:x4}: the grid gets {(v.Nulls != null ? "null" : v.New ? "a new tile that something else takes too" : "a tile from " + (v.From?.OpCode.ToString() ?? "several places"))}");
                }
                news += n;
                fresh += direct;
                if (n > direct) notes.Add($"{method.FullName}: {n} new Tile, {direct} of them straight into the grid");
            }
        }
        foreach (string line in notes.Distinct().OrderBy(x => x, StringComparer.Ordinal))
            Console.WriteLine(line);
        foreach (var g in grids.OrderByDescending(x => x.Value))
            Console.WriteLine($"grid from {g.Key}: {g.Value}");
        Console.WriteLine($"{methods} methods; ldnull as a Tile {nulls}; ldind/stind on a Tile& {indirect}; casts of a Tile {casts}; new Tile {news}, {fresh} straight into the grid");
        return 0;
    }
}
