// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Scans.cs, MIT), except where marked (FNAPatches).
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// The game's fixed lists (projectiles, NPCs, players, dust) and their "active" fields: TrPatches.Scan
// remembers the slots seen inactive and has the reason the game runs the same. Here every write of
// an "active" field and of an index field (Entity.whoAmI, Dust.dustIndex) is sent through Scan, a
// read of "active" asks Scan where the object was taken from its list with a plain index, and the
// calls of Projectile.Update and NPC.UpdateNPC go through Scan. What Scan answers does not depend
// on the index handed to it, so the index only has to be an int that can be loaded there.
static class Scans
{
    sealed class Kind
    {
        public FieldDefinition List, Active;
        public MethodReference Read, Off, Set;
    }

    static bool Is(object operand, FieldDefinition field) =>
        operand is FieldReference r && r.Name == field.Name && r.DeclaringType.FullName == field.DeclaringType.FullName;

    static bool Is(object operand, MethodDefinition method) =>
        operand is MethodReference r && r.Name == method.Name && r.DeclaringType.FullName == method.DeclaringType.FullName
        && r.Parameters.Count == method.Parameters.Count;

    static IEnumerable<TypeDefinition> Nested(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));

    // Item.trSlot: the index + 1 of the world item a marked inner item belongs to (TrPatches.Scan),
    // in the install and in the reference the runtime is built against
    public static void Open(ModuleDefinition m)
    {
        var item = m.GetType("Terraria.Item");
        if (item != null && !item.Fields.Any(f => f.Name == "trSlot"))
            item.Fields.Add(new FieldDefinition("trSlot", FieldAttributes.Public, m.TypeSystem.Int32));
    }

    public static int Apply(ModuleDefinition m, ModuleDefinition tr)
    {
        Open(m);
        var main = m.GetType("Terraria.Main");
        var scan = tr.GetType("TrPatches.Scan");
        MethodReference Helper(string name, TypeDefinition first) => m.ImportReference(scan.Methods.Single(x =>
            x.Name == name && x.IsPublic && x.Parameters.Count == 2 && x.Parameters[0].ParameterType.FullName == first.FullName));
        var kinds = new[] { ("projectile", "Terraria.Projectile"), ("npc", "Terraria.NPC"), ("player", "Terraria.Player"), ("dust", "Terraria.Dust") }
            .Select(k =>
            {
                var type = m.GetType(k.Item2);
                return new Kind
                {
                    List = main.Fields.Single(f => f.Name == k.Item1 && f.IsStatic), Active = type.Fields.Single(f => f.Name == "active"),
                    Read = Helper("Active", type), Off = Helper("Off", type), Set = Helper("Set", type),
                };
            }).ToList();
        var entity = m.GetType("Terraria.Entity");
        var dust = m.GetType("Terraria.Dust");
        var indexes = new[]
        {
            (entity.Fields.Single(f => f.Name == "whoAmI"), Helper("Who", entity)),
            (dust.Fields.Single(f => f.Name == "dustIndex"), Helper("Index", dust)),
        };
        var projectile = m.GetType("Terraria.Projectile");
        var npc = m.GetType("Terraria.NPC");
        var update = projectile.Methods.Single(x => x.Name == "Update" && x.Parameters.Count == 1);
        var updateNpc = npc.Methods.Single(x => x.Name == "UpdateNPC" && x.Parameters.Count == 1);
        // the two updates as Scan knows them: return at once when inactive (the NPC's index set first)
        Starts(update, kinds[0].Active, null);
        Starts(updateNpc, kinds[1].Active, indexes[0].Item1);
        var updates = new[] { (update, Helper("Update", projectile)), (updateNpc, Helper("Update", npc)) };

        int n = 0, hinted = 0;
        foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
        {
            if (!method.Body.Instructions.Any(i => kinds.Any(k => Is(i.Operand, k.Active)) || indexes.Any(x => Is(i.Operand, x.Item1))
                || updates.Any(u => Is(i.Operand, u.Item1))))
                continue;
            n += Body(method, kinds, indexes, updates, ref hinted);
        }
        Console.Error.WriteLine($"scans: {n} reads, writes and calls through TrPatches.Scan, {hinted} with their index");
        bool items = Items(m, scan);
        n += Tail(m, scan, main, dust, kinds[3]);
        n += First(m, scan, dust);
        n += Skip(m, scan, kinds, items);
        if (items)
            n += Same(m, scan);
        return n;
    }

    // WorldItem.TryCombiningIntoNearbyItems: its loop over the items after it, passed by NextI above,
    // passes by Scan.NextSame instead the items of another type too (Scan.cs)
    static int Same(ModuleDefinition m, TypeDefinition scan)
    {
        var merge = m.GetType("Terraria.WorldItem")?.Methods.SingleOrDefault(x => x.Name == "TryCombiningIntoNearbyItems" && !x.IsStatic && x.HasBody);
        var calls = merge?.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r
            && r.Name == "NextI" && r.DeclaringType.FullName == "TrPatches.Scan").ToList();
        if (calls == null || calls.Count != 1)
        {
            Console.Error.WriteLine($"scans: the items' merge is not as expected ({calls?.Count ?? -1} loops passed); left as NextI passes it");
            return 0;
        }
        // every store into Main.item tells (Scan.Stored), after it: in the methods that store into it,
        // after each of their stores of a reference into an array (one into another array only reads
        // the slots again)
        var itemList = m.GetType("Terraria.Main").Fields.Single(f => f.Name == "item" && f.IsStatic);
        var stored = m.ImportReference(scan.Methods.Single(x => x.Name == "Stored"));
        int stores = 0, places = 0;
        foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
        {
            var ins = method.Body.Instructions;
            if (!ins.Any(x => x.OpCode == OpCodes.Ldsfld && Is(x.Operand, itemList)) || !ins.Any(x => x.OpCode == OpCodes.Stelem_Ref))
                continue;
            var sil = method.Body.GetILProcessor();
            foreach (var x in ins.Where(x => x.OpCode == OpCodes.Stelem_Ref).ToList())
            {
                sil.InsertAfter(x, sil.Create(OpCodes.Call, stored));
                stores++;
            }
            places++;
        }
        if (stores < 4)
        {
            Console.Error.WriteLine($"scans: {stores} stores into Main.item found, 4 at least expected; the items' merge left as NextI passes it");
            return 0;
        }
        var il = merge.Body.GetILProcessor();
        il.InsertBefore(calls[0], il.Create(OpCodes.Ldarg_0));
        calls[0].Operand = m.ImportReference(scan.Methods.Single(x => x.Name == "NextSame"));
        Console.Error.WriteLine($"scans: the items' merge passes the items of another type; {stores} stores in {places} methods tell");
        return 1 + stores;
    }

    // Dust.UpdateDust asks every one of the 6000 dust, and writes "inactive" to each past the number
    // drawn. An inactive dust is nothing to either branch of its loop, so where Scan knows every dust
    // from here on inactive (Scan.Tail) the loop is left. An object put in the list tells Scan.
    // World items: every "item.type = value" through Scan.Type, and WorldItem.inner's setter tells
    // Scan.Inner. False (no item loop is passed) where an item's type is written through its address.
    static bool Items(ModuleDefinition m, TypeDefinition scan)
    {
        var type = m.GetType("Terraria.Item").Fields.Single(f => f.Name == "type");
        var hook = m.ImportReference(scan.Methods.Single(x => x.Name == "Type"));
        int typed = 0, addressed = 0;
        foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
            foreach (var i in method.Body.Instructions)
                if (i.OpCode == OpCodes.Stfld && Is(i.Operand, type))
                {
                    i.OpCode = OpCodes.Call;
                    i.Operand = hook;
                    typed++;
                }
                else if (i.OpCode == OpCodes.Ldflda && Is(i.Operand, type))
                {
                    var all = method.Body.Instructions.ToArray();
                    int j = Array.IndexOf(all, i);
                    if (!Reads(all, Taker(all, j)))
                    {
                        addressed++;
                        if (Environment.GetEnvironmentVariable("TRPATCH_DEBUG") != null)
                            Console.Error.WriteLine($"scans: an item's type by address: {method.DeclaringType.FullName}::{method.Name} at {j}: "
                                + string.Join(" | ", all.Skip(j + 1).Take(4).Select(x => x.OpCode + " " + (x.Operand is MethodReference mr ? mr.DeclaringType.Name + "::" + mr.Name : ""))));
                    }
                }
        var set = m.GetType("Terraria.WorldItem").Methods.Single(x => x.Name == "set_inner").Body;
        var inner = m.ImportReference(scan.Methods.Single(x => x.Name == "Inner"));
        var il = set.GetILProcessor();
        foreach (var ret in set.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList())
        {
            var first = il.Create(OpCodes.Ldarg_0);
            il.InsertBefore(ret, first);
            il.InsertBefore(ret, il.Create(OpCodes.Call, inner));
            foreach (var x in set.Instructions)
                if (x.Operand == ret && x != first)
                    x.Operand = first;
        }
        Console.Error.WriteLine($"scans: {typed} writes of an item's type through Scan" + (addressed > 0 ? $"; {addressed} through its address: item loops left as they were" : ""));
        return addressed == 0;
    }

    static int Tail(ModuleDefinition m, TypeDefinition scan, TypeDefinition main, TypeDefinition dust, Kind kind)
    {
        var update = dust.Methods.Single(x => x.Name == "UpdateDust" && x.Parameters.Count == 0);
        var limit = main.Fields.Single(f => f.Name == "maxDustToDraw");
        var body = update.Body;
        body.SimplifyMacros();
        var all = body.Instructions.ToArray();
        int n = 0;
        // the loop's last statement: the write followed by the loop's step
        var offs = Enumerable.Range(0, all.Length).Where(x => all[x].OpCode == OpCodes.Call && all[x].Operand == kind.Off
            && x + 7 < all.Length && all[x + 3].OpCode == OpCodes.Add && all[x + 7].OpCode == OpCodes.Blt).ToList();
        int q = offs.Count == 1 ? offs[0] : -1;
        // the loop as written: for (i = 0; i < 6000; i++) { d = dust[i]; if (i < limit) { if (!d.active) continue; ... } else d.active = false; }
        bool ok = q >= 2 && q + 7 < all.Length && !body.HasExceptionHandlers && all[q - 1].OpCode == OpCodes.Ldloc && all[q - 2].OpCode == OpCodes.Ldloc;
        VariableDefinition i = ok ? (VariableDefinition)all[q - 1].Operand : null, d = ok ? (VariableDefinition)all[q - 2].Operand : null;
        bool Loc(int at, VariableDefinition v) => all[at].OpCode == OpCodes.Ldloc && all[at].Operand == v;
        ok = ok && Loc(q + 1, i) && all[q + 2].OpCode == OpCodes.Ldc_I4 && (int)all[q + 2].Operand == 1 && all[q + 3].OpCode == OpCodes.Add
            && all[q + 4].OpCode == OpCodes.Stloc && all[q + 4].Operand == i
            && Loc(q + 5, i) && all[q + 6].OpCode == OpCodes.Ldc_I4 && (int)all[q + 6].Operand == 6000 && all[q + 7].OpCode == OpCodes.Blt
            && q + 8 < all.Length;
        if (ok)
        {
            // the loop's head: the dust taken, the index against the limit, the question and the "continue"
            int h = Array.IndexOf(all, (Instruction)all[q + 7].Operand);
            ok = h >= 0 && h + 10 < q && all[h].OpCode == OpCodes.Ldsfld && Is(all[h].Operand, kind.List) && Loc(h + 1, i)
                && all[h + 2].OpCode == OpCodes.Ldelem_Ref && all[h + 3].OpCode == OpCodes.Stloc && all[h + 3].Operand == d
                && Loc(h + 4, i) && all[h + 5].OpCode == OpCodes.Ldsfld && Is(all[h + 5].Operand, limit)
                && all[h + 6].OpCode == OpCodes.Bge && all[h + 6].Operand == all[q - 2]
                && Loc(h + 7, d) && Loc(h + 8, i) && all[h + 9].OpCode == OpCodes.Call && all[h + 9].Operand == kind.Read
                && (all[h + 10].OpCode == OpCodes.Brfalse && all[h + 10].Operand == all[q + 1]
                    || all[h + 10].OpCode == OpCodes.Brtrue && all[h + 11].OpCode == OpCodes.Br && all[h + 11].Operand == all[q + 1]);
            // the two locals are the loop's alone
            ok = ok && all.Count(x => x.OpCode == OpCodes.Stloc && x.Operand == d) == 1
                && all.Count(x => (x.OpCode == OpCodes.Stloc || x.OpCode == OpCodes.Ldloca) && x.Operand == i) == 2
                && !all.Skip(q + 8).Any(x => x.Operand == i || x.Operand == d);
        }
        if (ok)
        {
            var il = body.GetILProcessor();
            var first = all[q - 2];
            var head = il.Create(OpCodes.Ldloc, i);
            il.InsertBefore(first, head);
            il.InsertBefore(first, il.Create(OpCodes.Call, m.ImportReference(scan.Methods.Single(x => x.Name == "Tail"))));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue, all[q + 8]));
            foreach (var x in all)
                if (x.Operand == first)
                    x.Operand = head;
            n++;
        }
        if (!ok && Environment.GetEnvironmentVariable("TRPATCH_DEBUG") != null)
        {
            Console.Error.WriteLine($"scans: {offs.Count} Off calls, at {q}");
            if (q >= 2)
            {
                int h = q + 7 < all.Length && all[q + 7].Operand is Instruction to ? Array.IndexOf(all, to) : -1;
                for (int x = Math.Max(0, h); h >= 0 && x < h + 13; x++) Console.Error.WriteLine($"  head {x}: {all[x]}");
                for (int x = q - 3; x < Math.Min(all.Length, q + 10); x++) Console.Error.WriteLine($"  tail {x}: {all[x]}");
            }
        }
        body.OptimizeMacros();
        if (!ok)
        {
            Console.Error.WriteLine("scans: Dust.UpdateDust's loop is not as expected; left as it is");
            return 0;
        }
        // stores to the list
        var moved = m.ImportReference(scan.Methods.Single(x => x.Name == "Moved"));
        foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
        {
            var code = method.Body.Instructions;
            bool list = code.Any(x => Is(x.Operand, kind.List) || x.Operand is MethodReference r && r.Name == "get__dust");
            var stores = code.Where(x => x.OpCode == OpCodes.Stsfld && Is(x.Operand, kind.List) || list && x.OpCode == OpCodes.Stelem_Ref).ToList();
            if (list && code.Any(x => x.OpCode == OpCodes.Ldelema && x.Operand is TypeReference t && t.FullName == dust.FullName))
                throw new InvalidOperationException($"{method.FullName}: the address of a place in the dust list is taken");
            if (stores.Count == 0)
                continue;
            method.Body.SimplifyMacros();
            var edit = method.Body.GetILProcessor();
            foreach (var store in stores)
                edit.InsertAfter(store, edit.Create(OpCodes.Call, moved));
            method.Body.OptimizeMacros();
            n += stores.Count;
        }
        Console.Error.WriteLine($"scans: the dust loop is left at the list's inactive end; {n - 1} stores to the list tell");
        return n;
    }

    // Dust.NewDust looks for the first inactive dust from slot 0: "for (i = 0; i < 6000; i++)", every
    // active one passed over. Scan.First knows a slot below which all are active, and the loop
    // starts there.
    static int First(ModuleDefinition m, TypeDefinition scan, TypeDefinition dust)
    {
        var found = new List<(MethodDefinition, Instruction)>();
        foreach (var method in dust.Methods.Where(x => x.HasBody))
        {
            method.Body.SimplifyMacros();
            if (method.Name != "NewDust")
                continue;
            foreach (var x in method.Body.Instructions)
                if (x.OpCode == OpCodes.Ldc_I4 && (int)x.Operand == 0 && x.Next?.OpCode == OpCodes.Stloc && x.Next.Next?.OpCode == OpCodes.Br
                    && x.Next.Next.Operand is Instruction t && t.OpCode == OpCodes.Ldloc && t.Operand == x.Next.Operand
                    && t.Next?.OpCode == OpCodes.Ldc_I4 && (int)t.Next.Operand == 6000 && t.Next.Next?.OpCode == OpCodes.Blt)
                    found.Add((method, x));
        }
        bool ok = found.Count == 1;
        if (ok)
        {
            found[0].Item2.OpCode = OpCodes.Call;
            found[0].Item2.Operand = m.ImportReference(scan.Methods.Single(x => x.Name == "First"));
        }
        foreach (var method in dust.Methods.Where(x => x.HasBody))
            method.Body.OptimizeMacros();
        Console.Error.WriteLine(ok ? "scans: the search for a free dust starts where the active ones end"
            : $"scans: {found.Count} searches for a free dust, expected one in Dust.NewDust; left as they are");
        return ok ? 1 : 0;
    }

    static void Starts(MethodDefinition method, FieldDefinition active, FieldDefinition index)
    {
        var i = method.Body.Instructions;
        int at = 0;
        bool ok = true;
        if (index != null)
        {
            ok = i[0].OpCode == OpCodes.Ldarg_0 && i[1].OpCode == OpCodes.Ldarg_1 && i[2].OpCode == OpCodes.Stfld && Is(i[2].Operand, index);
            at = 3;
        }
        ok = ok && i[at].OpCode == OpCodes.Ldarg_0 && i[at + 1].OpCode == OpCodes.Ldfld && Is(i[at + 1].Operand, active)
            && (((i[at + 2].OpCode == OpCodes.Brtrue || i[at + 2].OpCode == OpCodes.Brtrue_S) && i[at + 3].OpCode == OpCodes.Ret)
                // or a jump to the method's return
                || ((i[at + 2].OpCode == OpCodes.Brfalse || i[at + 2].OpCode == OpCodes.Brfalse_S)
                    && ((Instruction)i[at + 2].Operand).OpCode == OpCodes.Ret));
        if (!ok)
            throw new InvalidOperationException($"{method.FullName} does not start as expected: " + string.Join(" | ", i.Take(9).Select(x => x.ToString())));
    }

    // A loop over a list, "for (...; i < n; i++)", whose every round begins with the question about
    // the list's element i and goes to the loop's step on "inactive": such a round does nothing, and
    // Scan's Next walks over the marked slots in a loop of its own. Where it comes to the list's end
    // the loop is left with what its last round would have left: i at n, the element's local (where
    // the loop keeps one) on the last element.
    static int Skip(ModuleDefinition m, TypeDefinition scan, List<Kind> kinds, bool items)
    {
        var next = new[] { "NextP", "NextN", "NextL", "NextD", "NextI" }.Select(x => m.ImportReference(scan.Methods.Single(y => y.Name == x))).ToArray();
        var debug = Environment.GetEnvironmentVariable("TRPATCH_DEBUG") != null;
        var done = new int[kinds.Count + 1];
        // world items (k == kinds.Count): the list Main.item, the question WorldItem.active (false: to
        // the step) or IsAir (true: to the step), maybe after the game's assertion of the index
        var itemList = m.GetType("Terraria.Main").Fields.Single(f => f.Name == "item" && f.IsStatic);
        var whoAmI = m.GetType("Terraria.Entity").Fields.Single(f => f.Name == "whoAmI");
        bool Item(Instruction x, string name) => x.OpCode == OpCodes.Callvirt && x.Operand is MethodReference r
            && r.Name == name && r.DeclaringType.FullName == "Terraria.WorldItem";
        int near = 0;
        foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
        {
            var body = method.Body;
            if (!body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld && (kinds.Any(k => Is(i.Operand, k.List)) || items && Is(i.Operand, itemList))))
                continue;
            body.SimplifyMacros();
            VariableDefinition temp = null;
            var all = body.Instructions.ToArray();
            for (int h = 0; h < all.Length; h++)
            {
                int k = kinds.FindIndex(x => all[h].OpCode == OpCodes.Ldsfld && Is(all[h].Operand, x.List));
                if (k < 0 && items && all[h].OpCode == OpCodes.Ldsfld && Is(all[h].Operand, itemList))
                    k = kinds.Count;
                if (k < 0 || h + 8 >= all.Length || all[h + 1].OpCode != OpCodes.Ldloc || all[h + 2].OpCode != OpCodes.Ldelem_Ref)
                    continue;
                var i = (VariableDefinition)all[h + 1].Operand;
                if (i.VariableType.MetadataType != MetadataType.Int32)
                    continue;
                // the element kept in a local, or asked as it is
                int q = h + 3;
                VariableDefinition one = null;
                if (all[q].OpCode == OpCodes.Stloc && all[q + 1].OpCode == OpCodes.Ldloc && all[q + 1].Operand == all[q].Operand)
                {
                    one = (VariableDefinition)all[q].Operand;
                    q += 2;
                }
                // the question, with its index or without
                bool air = false;
                if (k == kinds.Count)
                {
                    // Invariant.Assert(item.whoAmI == i, ...): true of a marked item (Scan keeps that)
                    if (one != null && q + 6 < all.Length && all[q].OpCode == OpCodes.Ldfld && Is(all[q].Operand, whoAmI)
                        && all[q + 1].OpCode == OpCodes.Ldloc && all[q + 1].Operand == i && all[q + 2].OpCode == OpCodes.Ceq
                        && all[q + 3].OpCode == OpCodes.Ldstr && all[q + 4].OpCode == OpCodes.Call && all[q + 4].Operand is MethodReference a
                        && a.Name == "Assert" && a.DeclaringType.FullName == "Terraria.Testing.Invariant"
                        && all[q + 5].OpCode == OpCodes.Ldloc && all[q + 5].Operand == one)
                        q += 6;
                    if (Item(all[q], "get_active"))
                        q++;
                    else if (Item(all[q], "get_IsAir"))
                    {
                        q++;
                        air = true;
                    }
                    else
                        continue;
                }
                else if (all[q].OpCode == OpCodes.Ldfld && Is(all[q].Operand, kinds[k].Active))
                    q++;
                else if (all[q].OpCode == OpCodes.Ldloc && all[q].Operand == i && all[q + 1].OpCode == OpCodes.Call && all[q + 1].Operand == kinds[k].Read)
                    q += 2;
                else
                    continue;
                // "inactive": to the step
                var no = air ? OpCodes.Brtrue : OpCodes.Brfalse;
                var yes = air ? OpCodes.Brfalse : OpCodes.Brtrue;
                var step = q + 1 >= all.Length ? null
                    : all[q].OpCode == no ? all[q].Operand as Instruction
                    : all[q].OpCode == yes && all[q + 1].OpCode == OpCodes.Br ? all[q + 1].Operand as Instruction : null;
                int s = step == null ? -1 : Array.IndexOf(all, step);
                // i++; i < n; back to this head, n a number or a static's value
                bool ok = s > q && s + 7 < all.Length
                    && all[s].OpCode == OpCodes.Ldloc && all[s].Operand == i && all[s + 1].OpCode == OpCodes.Ldc_I4 && (int)all[s + 1].Operand == 1
                    && all[s + 2].OpCode == OpCodes.Add && all[s + 3].OpCode == OpCodes.Stloc && all[s + 3].Operand == i
                    && all[s + 4].OpCode == OpCodes.Ldloc && all[s + 4].Operand == i
                    && (all[s + 5].OpCode == OpCodes.Ldc_I4 || all[s + 5].OpCode == OpCodes.Ldsfld
                        && ((FieldReference)all[s + 5].Operand).FieldType.MetadataType == MetadataType.Int32)
                    && all[s + 6].OpCode == OpCodes.Blt && all[s + 6].Operand == all[h];
                // no edge of a guarded block within the loop
                ok = ok && !body.ExceptionHandlers.SelectMany(x => new[] { x.TryStart, x.TryEnd, x.HandlerStart, x.HandlerEnd, x.FilterStart })
                    .Any(x => x != null && Array.IndexOf(all, x) >= h && Array.IndexOf(all, x) <= s + 7);
                if (!ok)
                {
                    if (debug && near++ < 40)
                        Console.Error.WriteLine($"scans: not passed: {method.DeclaringType.Name}.{method.Name} at {h}: step {s}"
                            + (s > 0 && s + 6 < all.Length ? $" {all[s].OpCode} {all[s + 5].OpCode} {all[s + 6].OpCode}" : ""));
                    continue;
                }
                var il = body.GetILProcessor();
                if (temp == null)
                    body.Variables.Add(temp = new VariableDefinition(m.TypeSystem.Int32));
                Instruction head = all[h], bound = all[s + 5], exit = all[s + 7];
                var first = il.Create(OpCodes.Ldloc, i);
                var go = il.Create(OpCodes.Ldloc, temp);
                var code = new List<Instruction>
                {
                    first, Copy(bound), il.Create(OpCodes.Call, next[k]), il.Create(OpCodes.Stloc, temp),
                    il.Create(OpCodes.Ldloc, temp), il.Create(OpCodes.Ldc_I4, 0), il.Create(OpCodes.Bge, go),
                    Copy(bound), il.Create(OpCodes.Stloc, i),
                };
                if (one != null)
                    code.AddRange(new[]
                    {
                        Copy(head), Copy(bound), il.Create(OpCodes.Ldc_I4, 1), il.Create(OpCodes.Sub),
                        il.Create(OpCodes.Ldelem_Ref), il.Create(OpCodes.Stloc, one),
                    });
                code.AddRange(new[] { il.Create(OpCodes.Br, exit), go, il.Create(OpCodes.Stloc, i) });
                foreach (var x in code)
                    il.InsertBefore(head, x);
                foreach (var x in all)
                {
                    if (x.Operand == head)
                        x.Operand = first;
                    else if (x.Operand is Instruction[] to)
                        for (int j = 0; j < to.Length; j++)
                            if (to[j] == head) to[j] = first;
                }
                done[k]++;
                all = body.Instructions.ToArray();
                h += code.Count;
            }
            body.OptimizeMacros();
        }
        Console.Error.WriteLine($"scans: {done.Sum()} loops pass their marked slots ({done[0]} over projectiles, {done[1]} NPCs, {done[2]} players, {done[3]} dust, {done[4]} items)");
        return done.Sum();
    }

    static bool Int(TypeReference t)
    {
        switch (t.MetadataType)
        {
            case MetadataType.Int32: case MetadataType.Byte: case MetadataType.SByte: case MetadataType.Int16: case MetadataType.UInt16:
                return true;
        }
        return false;
    }

    // an index that can be loaded again where it is wanted: a local, an argument, a constant
    // (a static field too where the load follows at once)
    static bool Index(Instruction i, bool near) =>
        i.OpCode == OpCodes.Ldloc ? Int(((VariableDefinition)i.Operand).VariableType)
        : i.OpCode == OpCodes.Ldarg ? Int(((ParameterDefinition)i.Operand).ParameterType)
        : i.OpCode == OpCodes.Ldc_I4 || near && i.OpCode == OpCodes.Ldsfld && Int(((FieldReference)i.Operand).FieldType);

    // the index of the list's element that instruction j puts on the stack, where it shows
    static Instruction From(Instruction[] all, int j, Kind kind)
    {
        if (j >= 2 && all[j].OpCode == OpCodes.Ldelem_Ref && Index(all[j - 1], true)
            && all[j - 2].OpCode == OpCodes.Ldsfld && Is(all[j - 2].Operand, kind.List))
            return all[j - 1];
        if (all[j].OpCode != OpCodes.Ldloc)
            return null;
        // a local: its last store above
        for (int p = j - 1; p >= 3; p--)
            if (all[p].OpCode == OpCodes.Stloc && all[p].Operand == all[j].Operand)
                return all[p - 1].OpCode == OpCodes.Ldelem_Ref && Index(all[p - 2], false)
                    && all[p - 3].OpCode == OpCodes.Ldsfld && Is(all[p - 3].Operand, kind.List) ? all[p - 2] : null;
        return null;
    }

    // the instruction that takes the value instruction j puts on the stack (in its block), or the end
    static int Taker(Instruction[] all, int j)
    {
        int depth = 1;
        for (int q = j + 1; q < all.Length; q++)
        {
            int pops = Rests.Pops(all[q]);
            if (pops >= depth)
                return q;
            if (all[q].OpCode.FlowControl != FlowControl.Next && all[q].OpCode.FlowControl != FlowControl.Call)
                break;
            depth += Rests.Pushes(all[q]) - pops;
        }
        return all.Length;
    }

    // the address of a field handed to a method of bool or int (ToString, CompareTo, ...): a read
    static bool Reads(Instruction[] all, int j)
    {
        if (j < all.Length && all[j].OpCode == OpCodes.Constrained) j++;
        return j < all.Length && (all[j].OpCode == OpCodes.Call || all[j].OpCode == OpCodes.Callvirt)
            && all[j].Operand is MethodReference r && r.HasThis
            && (r.DeclaringType.FullName == "System.Boolean" || r.DeclaringType.FullName == "System.Int32"
                || r.DeclaringType.FullName == "System.Object");
    }

    static int Body(MethodDefinition method, List<Kind> kinds, (FieldDefinition, MethodReference)[] indexes,
        (MethodDefinition, MethodReference)[] updates, ref int hinted)
    {
        var body = method.Body;
        body.SimplifyMacros();
        var all = body.Instructions.ToArray();
        var il = body.GetILProcessor();
        // where a branch may land, the stack is not known to be what the instruction above left
        var aimed = new HashSet<Instruction>();
        foreach (var i in all)
        {
            if (i.Operand is Instruction to) aimed.Add(to);
            else if (i.Operand is Instruction[] tos) foreach (var t in tos) aimed.Add(t);
        }
        foreach (var h in body.ExceptionHandlers)
            foreach (var edge in new[] { h.TryStart, h.TryEnd, h.HandlerStart, h.HandlerEnd, h.FilterStart })
                if (edge != null) aimed.Add(edge);
        int n = 0;
        for (int q = 0; q < all.Length; q++)
        {
            var i = all[q];
            if (i.OpCode == OpCodes.Ldflda && (kinds.Any(k => Is(i.Operand, k.Active)) || indexes.Any(x => Is(i.Operand, x.Item1)))
                && !Reads(all, q + 1))
                throw new InvalidOperationException($"{method.FullName}: the address of {((FieldReference)i.Operand).FullName} is taken");
            if (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            {
                foreach (var (from, to) in updates)
                    if (Is(i.Operand, from))
                    {
                        i.OpCode = OpCodes.Call;
                        i.Operand = to;
                        n++;
                    }
                continue;
            }
            if (i.OpCode == OpCodes.Stfld)
            {
                foreach (var (field, to) in indexes)
                    if (Is(i.Operand, field))
                    {
                        i.OpCode = OpCodes.Call;
                        i.Operand = to;
                        n++;
                    }
            }
            if (i.OpCode != OpCodes.Ldfld && i.OpCode != OpCodes.Stfld)
                continue;
            var kind = kinds.FirstOrDefault(k => Is(i.Operand, k.Active));
            if (kind == null)
                continue;
            if (i.OpCode == OpCodes.Ldfld)
            {
                var index = q >= 1 && !aimed.Contains(i) ? From(all, q - 1, kind) : null;
                if (index == null)
                    continue;       // read from the object, as the game does
                il.InsertBefore(i, Copy(index));
                i.OpCode = OpCodes.Call;
                i.Operand = kind.Read;
                hinted++;
            }
            else
            {
                // "= false" on an element of the list: nothing to write when it is known inactive
                var zero = q >= 2 && all[q - 1].OpCode == OpCodes.Ldc_I4 && (int)all[q - 1].Operand == 0 && !aimed.Contains(i);
                var index = zero ? From(all, q - 2, kind) : null;
                if (index != null)
                {
                    var copy = Copy(index);
                    all[q - 1].OpCode = copy.OpCode;
                    all[q - 1].Operand = copy.Operand;
                    hinted++;
                }
                i.OpCode = OpCodes.Call;
                i.Operand = index != null ? kind.Off : kind.Set;
            }
            n++;
        }
        body.OptimizeMacros();
        return n;
    }

    static Instruction Copy(Instruction i) =>
        i.Operand is VariableDefinition v ? Instruction.Create(i.OpCode, v)
        : i.Operand is ParameterDefinition p ? Instruction.Create(i.OpCode, p)
        : i.Operand is FieldReference f ? Instruction.Create(i.OpCode, f)
        : Instruction.Create(i.OpCode, (int)i.Operand);
}
