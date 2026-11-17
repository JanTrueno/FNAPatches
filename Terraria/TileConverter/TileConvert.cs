// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch, MIT), as it is there, except where marked "(FNAPatches)".
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

public static partial class TilePtr  // (FNAPatches) public: called from the MonoMod rules
{
    // Tile's methods of at most this many bytes of code are marked to be inlined
    const int InlineTileMethodsUpTo = 72;

    // The conversion. In every method of the game:
    //   Main.tile[x, y]                Tiles.At(x, y): the cell's record, or null where the game's grid
    //                                  holds null (outside the world; a client's sections not yet sent)
    //   Main.tile[x, y] = new Tile()   Tiles.PutFresh(x, y): the cell zeroed, as a new tile is
    //   Main.tile[x, y] = tile         tile = Tiles.PutNew(x, y, tile): the cell takes the tile's content
    //                                  and the local goes on with the cell, as the object was the cell's
    //   new Tile()                     Tiles.New(): a zeroed record of its own
    //   null where a Tile goes         a null pointer
    //   lock (tile)                    Tiles.Enter, Tiles.Exit
    //   Main.maxTilesX = ...           and Tiles.Sized(): the block follows the world's size
    // then every Tile of a signature is a Tile*, and Tile the 14-byte value its nine fields make
    // (Retype). Returns the number of changes.
    public static int Convert(ModuleDefinition m, TypeDefinition tiles)
    {
        var tile = m.GetType("Terraria.Tile");
        if (tile.IsValueType)
            throw new InvalidOperationException("Terraria.Tile is a value already");
        var main = m.GetType("Terraria.Main");
        var ptr = new PointerType(tile);
        // Main.tile's own type: C# writes Tile[,] with lower bounds 0, and .NET (unlike Mono) matches
        // signatures exactly
        var grid = main.Fields.Single(f => f.Name == "tile").FieldType;
        TypeReference i32 = m.TypeSystem.Int32, nothing = m.TypeSystem.Void;
        // (FNAPatches) TrPatches.Tiles is in this module, its methods taking void* (Tile is still a class when it is compiled):
        // the calls go to its definitions, found by name and number of parameters
        MethodReference Static(string name, TypeReference ret, params TypeReference[] args)
        {
            var found = tiles.Methods.Where(x => x.Name == name && x.Parameters.Count == args.Length).ToArray();
            if (found.Length != 1)
                throw new InvalidOperationException($"TrPatches.Tiles.{name}/{args.Length}: {found.Length} matching methods");
            return found[0];
        }
        MethodReference at = Static("At", ptr, i32, i32), atGrid = Static("At", ptr, grid, i32, i32);
        MethodReference putNew = Static("PutNew", ptr, i32, i32, ptr), putNewGrid = Static("PutNew", ptr, grid, i32, i32, ptr);
        MethodReference putFresh = Static("PutFresh", nothing, i32, i32), putFreshGrid = Static("PutFresh", nothing, grid, i32, i32);
        var fresh = Static("New", ptr);
        var newGrid = Static("NewGrid", grid, i32, i32);
        var enter = Static("Enter", nothing, ptr, new ByReferenceType(m.TypeSystem.Boolean));
        var exit = Static("Exit", nothing, ptr);
        var sized = Static("Sized", nothing);

        int changes = 0;
        var errors = new List<string>();

        // a value has no object under it: Tile's constructors lose their call of Object's, and Clone
        // (MemberwiseClone; nothing in the game calls it) its body
        foreach (var ctor in tile.Methods.Where(x => x.IsConstructor && !x.IsStatic))
        {
            var call = ctor.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r
                && r.Name == ".ctor" && r.DeclaringType.FullName == "System.Object");
            if (call == null || call.Previous == null || call.Previous.OpCode != OpCodes.Ldarg_0)
                throw new InvalidOperationException($"{ctor.FullName}: no call of Object's constructor");
            call.Previous.OpCode = OpCodes.Nop;
            call.OpCode = OpCodes.Nop;
            call.Operand = null;
            changes++;
        }
        foreach (var clone in tile.Methods.Where(x => x.Name == "Clone" && x.Parameters.Count == 0))
        {
            clone.Body.Instructions.Clear();
            clone.Body.Variables.Clear();
            var il = clone.Body.GetILProcessor();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            changes++;
        }

        // WorldUtils.DebugRotate moves the grid's objects from cell to cell with one kept in a local,
        // which records cannot do. Nothing in the game calls it: it loses its body.
        foreach (var type in AllTypes(m))
            foreach (var method in type.Methods.Where(x => x.HasBody))
                foreach (var ins in method.Body.Instructions)
                    if (ins.Operand is MethodReference r && r.Name == "DebugRotate" && r.DeclaringType.Name == "WorldUtils")
                        throw new InvalidOperationException($"{method.FullName} uses WorldUtils.DebugRotate");
        foreach (var rotate in m.GetType("Terraria.WorldBuilding.WorldUtils").Methods.Where(x => x.Name == "DebugRotate"))
        {
            rotate.Body.Instructions.Clear();
            rotate.Body.Variables.Clear();
            rotate.Body.ExceptionHandlers.Clear();
            rotate.Body.GetILProcessor().Emit(OpCodes.Ret);
            changes++;
        }

        foreach (var type in AllTypes(m))
            foreach (var method in type.Methods.Where(x => x.HasBody))
            {
                var body = method.Body;
                body.SimplifyMacros();
                var sim = new TileSim(method);
                var done = new HashSet<Instruction>();            // the simulation's notes that are dealt with here
                var drop = new HashSet<Instruction>();            // loads whose value nothing takes any more
                var plain = new HashSet<Instruction>();           // grid calls whose grid is dropped
                var freshInto = new HashSet<Instruction>();       // stores of a new Tile nothing else takes
                var back = new Dictionary<Instruction, VariableDefinition>();    // stores whose tile a local keeps

                // what each grid call takes, before anything changes
                foreach (var ins in body.Instructions)
                {
                    bool get = IsGridCall(ins, "Get"), set = IsGridCall(ins, "Set");
                    if (!(get || set) || !sim.In.TryGetValue(ins, out var st))
                        continue;
                    var list = new List<Instruction>();
                    if (Droppable(sim, st[st.Length - (get ? 3 : 4)], ins, list))
                    {
                        drop.UnionWith(list);
                        plain.Add(ins);
                    }
                    if (get)
                        continue;
                    var v = st[st.Length - 1];
                    if (v.From == null)
                        errors.Add($"{sim.Where(ins)}: the grid gets a tile from several places");
                    else if (v.New && sim.OnlyTaker(v.From, ins))
                    {
                        drop.Add(v.From);
                        freshInto.Add(ins);
                    }
                    else if (v.New && !sim.Merged.Contains(v.From))
                    {
                        // new Tile(), dup, stloc, Set: the local goes on with the cell
                        var takers = sim.Takers[v.From];
                        var stores = takers.Where(t => t.OpCode.Code == Code.Stloc).ToList();
                        if (stores.Count == 1 && takers.All(t => t == ins || t == stores[0] || t.OpCode.Code == Code.Dup))
                            back[ins] = (VariableDefinition)stores[0].Operand;
                        else
                            errors.Add($"{sim.Where(ins)}: the grid gets a new tile that {string.Join(", ", takers.Select(t => t.OpCode.ToString()))} take too");
                    }
                    else if (v.From.OpCode.Code == Code.Ldloc)
                        back[ins] = (VariableDefinition)v.From.Operand;
                    else if (!(v.Nulls != null && v.Type == null))
                        errors.Add($"{sim.Where(ins)}: the grid gets a tile from {v.From.OpCode}");
                }

                {
                    var il = body.GetILProcessor();
                    foreach (var ins in body.Instructions.ToArray())
                    {
                        var mr = ins.Operand as MethodReference;
                        bool known = sim.In.TryGetValue(ins, out var st);
                        if (drop.Contains(ins))
                        {
                            ins.OpCode = OpCodes.Nop;
                            ins.Operand = null;
                        }
                        else if (sim.NullTiles.Contains(ins))
                        {
                            ins.OpCode = OpCodes.Ldc_I4;
                            ins.Operand = 0;
                            il.InsertAfter(ins, il.Create(OpCodes.Conv_U));
                        }
                        else if (sim.Indirect.Contains(ins))
                            ins.OpCode = ins.OpCode.Code == Code.Ldind_Ref ? OpCodes.Ldind_I : OpCodes.Stind_I;
                        else if (IsGridCall(ins, "Get"))
                        {
                            ins.OpCode = OpCodes.Call;
                            ins.Operand = plain.Contains(ins) ? at : atGrid;
                        }
                        else if (IsGridCall(ins, "Set"))
                        {
                            ins.OpCode = OpCodes.Call;
                            if (freshInto.Contains(ins))
                                ins.Operand = plain.Contains(ins) ? putFresh : putFreshGrid;
                            else
                            {
                                ins.Operand = plain.Contains(ins) ? putNew : putNewGrid;
                                il.InsertAfter(ins, back.TryGetValue(ins, out var local) ? il.Create(OpCodes.Stloc, local) : il.Create(OpCodes.Pop));
                            }
                        }
                        else if (IsGridCall(ins, ".ctor") && ins.OpCode == OpCodes.Newobj)
                        {
                            ins.OpCode = OpCodes.Call;
                            ins.Operand = newGrid;
                        }
                        else if (mr != null && mr.DeclaringType is ArrayType ga && TileSim.IsTile(ga.ElementType))
                        {
                            errors.Add($"{sim.Where(ins)}: {ins.OpCode} {mr.FullName}");
                            continue;
                        }
                        else if (ins.OpCode == OpCodes.Newobj && TileSim.IsTile(mr.DeclaringType))
                        {
                            if (mr.Parameters.Count != 0)
                                errors.Add($"{sim.Where(ins)}: new Tile with arguments");
                            ins.OpCode = OpCodes.Call;
                            ins.Operand = fresh;
                        }
                        else if (ins.OpCode == OpCodes.Callvirt && TileSim.IsTile(mr.DeclaringType))
                            ins.OpCode = OpCodes.Call;        // (a null tile faults at the method's first field)
                        else if (known && ins.OpCode == OpCodes.Call && mr.DeclaringType.FullName == "System.Threading.Monitor"
                            && mr.Name == "Enter" && mr.Parameters.Count == 2 && TileSim.IsTile(st[st.Length - 2].Type))
                        {
                            ins.Operand = enter;
                            done.Add(ins);
                        }
                        else if (known && ins.OpCode == OpCodes.Call && mr.DeclaringType.FullName == "System.Threading.Monitor"
                            && mr.Name == "Exit" && TileSim.IsTile(st[st.Length - 1].Type))
                        {
                            ins.Operand = exit;
                            done.Add(ins);
                        }
                        else if (ins.OpCode == OpCodes.Stsfld && ins.Operand is FieldReference size && size.DeclaringType.FullName == "Terraria.Main"
                            && (size.Name == "maxTilesX" || size.Name == "maxTilesY"))
                            il.InsertAfter(ins, il.Create(OpCodes.Call, sized));
                        else
                            continue;
                        changes++;
                    }
                }
                foreach (var note in sim.Notes)
                    if (!done.Contains(note.Key))
                        errors.Add($"{sim.Where(note.Key)}: {note.Value}");
                body.OptimizeMacros();
            }
        if (errors.Count > 0)
            throw new InvalidOperationException("the tile conversion met code it does not know:\n  " + string.Join("\n  ", errors.Distinct()));

        changes += Retype(m, tile);

        // Tile's own small methods (active(), slope(), color()...) are a field and a mask: inlined, a
        // tile test in the game's loops is a load and no call
        foreach (var method in tile.Methods.Where(x => x.HasBody && !x.IsConstructor && !x.Body.HasExceptionHandlers
            && x.Body.CodeSize <= InlineTileMethodsUpTo))
        {
            method.ImplAttributes |= MethodImplAttributes.AggressiveInlining;
            changes++;
        }
        if (Environment.GetEnvironmentVariable("TRPATCH_VERBOSE") == "1")
            foreach (var method in tile.Methods.Where(x => x.HasBody).OrderBy(x => x.Body.CodeSize))
                Console.Error.WriteLine($"tile method: {method.Body.CodeSize,5} {method.Name}/{method.Parameters.Count}");
        {
            // Player.IsTileTypeInInteractionRange (is a tile of this type in reach: the map's pylon layer
            // asks every frame, over 121 x 124 cells): TrPatches.Tiles.TypeInReach, from the records
            var player = m.GetType("Terraria.Player");
            var inReach = player.Methods.Single(x => x.Name == "IsTileTypeInInteractionRange");
            var to = Static("TypeInReach", m.TypeSystem.Boolean, player, i32, inReach.Parameters[1].ParameterType);
            var body = inReach.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, to);
            il.Emit(OpCodes.Ret);
            changes++;
        }
        return changes;
    }

    // can the load of the grid go: only this call takes it, and loading it does nothing else
    static bool Droppable(TileSim sim, TileSim.Val g, Instruction taker, List<Instruction> drop)
    {
        var from = g.From;
        if (!sim.OnlyTaker(from, taker))
            return false;
        switch (from.OpCode.Code)
        {
            case Code.Ldsfld: case Code.Ldloc: case Code.Ldarg:
                drop.Add(from);
                return true;
            case Code.Ldfld:
                var st = sim.In[from];
                var of = st[st.Length - 1].From;
                if (!sim.OnlyTaker(of, from) || (of.OpCode.Code != Code.Ldarg && of.OpCode.Code != Code.Ldloc))
                    return false;
                drop.Add(of);
                drop.Add(from);
                return true;
            default:
                return false;
        }
    }
}
