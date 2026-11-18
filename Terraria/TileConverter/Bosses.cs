// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Bosses.cs, MIT), except where marked (FNAPatches).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Bosses of many parts: four places where the game does, for every part or every NPC, work whose answer
// is known without it (TrPatches.Boss gives each one's reason):
//   sight   Main.DrawInterface_14_EntityHealthBars' search for the Destroyer's nearest segment in sight
//   dark    Main.DrawNPCDirect's look for a dark tile around an NPC drawn behind the tiles
//   immune  NPC.UpdateNPC's countdown of the NPC's hit counters, and every write of one
//   tiles   NPC.AI_037_Destroyer's look for a block or liquid in a segment's square
//   wet     Collision.WetCollision's look for liquid in the small square at an entity's centre
//   buffs   NPC.UpdateNPC_BuffSetFlags' and UpdateNPC_BuffClearExpiredBuffs' walks over the NPC's buffs
// Each place is matched here instruction by instruction with the code this was written for (before the
// tile conversion), the locals the cut leaves unwritten must be read nowhere else, and the routines a cut
// asks less often or works out itself are summed on the game's code as shipped: anything else leaves that
// cut out. The game's own code stays in place behind a switch (TR_BOSS_*=0, TR_BOSS_AUDIT=1).
static class Bosses
{
    // what the cuts rely on, as shipped: routines that only read, asked fewer times or in another order
    // (sight: CanHit and the centre; dark: Brightness and what it reads, which Boss.Dark works out the
    // same way; tiles: nactive)
    static readonly (string type, string name, string first, ulong sum)[] Relied =
    {
        ("Terraria.Collision", "CanHit", "Microsoft.Xna.Framework.Vector2", 0xb403f30d741fe971),
        ("Terraria.Collision", "CanHit", "Microsoft.Xna.Framework.Point", 0x13000cef07f92b8e),
        ("Terraria.Entity", "get_Center", null, 0x76dc1aca082fdb18),
        ("Terraria.Lighting", "Brightness", null, 0x6beb3dec7cd438d1),
        ("Terraria.Graphics.Light.LegacyLighting", "GetColor", null, 0x6e4584f896175c11),
        ("Terraria.Lighting", "get_GlobalBrightness", null, 0xd2bd3b69c41e0b37),
        ("Terraria.Tile", "nactive", null, 0xa845a7fbe97e2f5c),
        ("Terraria.Collision", "WetCollision", null, 0x0c1e9a015bc45e82),
    };
    static readonly HashSet<string> known = new HashSet<string>();

    static IEnumerable<TypeDefinition> Nested(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));

    // (Guard: a sum that Mono and .NET take alike)
    static ulong Sum(MethodDefinition method)
    {
        ulong h = 14695981039346656037;
        foreach (var i in method.Body.Instructions)
            h = Guard.Sum(h, i);
        return h;
    }

    // on the game's code as shipped, before any other patch
    public static void Look(ModuleDefinition m)
    {
        foreach (var r in Relied)
        {
            var found = m.GetType(r.type)?.Methods.Where(x => x.Name == r.name && x.HasBody
                && (r.first == null || x.Parameters.Count > 0 && x.Parameters[0].ParameterType.FullName == r.first)).ToList();
            ulong sum = found?.Count == 1 ? Sum(found[0]) : 0;
            if (sum == r.sum)
            {
                known.Add(r.type + "::" + r.name + "/" + r.first);
                if (r.name == "WetCollision")
                    wetShape = Shape(found[0]);     // (FNAPatches)
            }
            else
                Console.Error.WriteLine($"bosses: {r.type}::{r.name} sums to {sum:x16}, expected {r.sum:x16}");
        }
    }

    // (FNAPatches) a routine's instructions without their encoding: the long forms, branch targets by index, operands by name
    static ulong wetShape;
    static ulong Shape(MethodDefinition method)
    {
        method.Body.SimplifyMacros();
        var code = method.Body.Instructions;
        ulong h = 14695981039346656037;
        void Add(string text) { foreach (char c in text) h = (h ^ c) * 1099511628211; h = (h ^ '|') * 1099511628211; }
        foreach (var i in code)
            Add(Op(i) + " " + (i.Operand is Instruction t ? code.IndexOf(t).ToString(CultureInfo.InvariantCulture)
                : i.Operand is Instruction[] ts ? string.Join(",", ts.Select(x => code.IndexOf(x).ToString(CultureInfo.InvariantCulture)))
                : i.Operand is VariableDefinition v ? "v" + v.Index.ToString(CultureInfo.InvariantCulture)
                : i.Operand is IConvertible c ? c.ToString(CultureInfo.InvariantCulture)
                : Grid(Member(i.Operand)) ?? i.Operand?.ToString()));
        method.Body.OptimizeMacros();
        return h;
    }

    static bool Known(string type, string name, string first = null) => known.Contains(type + "::" + name + "/" + first);

    // what TrPatches.Boss reads of the legacy lighting (the reference assembly and the game alike)
    public static void Open(ModuleDefinition m)
    {
        var lighting = m.GetType("Terraria.Lighting");
        var legacy = m.GetType("Terraria.Graphics.Light.LegacyLighting");
        if (lighting == null || legacy == null)
            return;
        foreach (var f in lighting.Fields.Where(x => x.Name == "_activeEngine"))
            f.IsPublic = true;
        foreach (var f in legacy.Fields.Where(x => x.Name == "_states" || x.Name.StartsWith("_expandedRect")))
            f.IsPublic = true;
        var state = legacy.NestedTypes.FirstOrDefault(t => t.Name == "LightingState");
        if (state != null)
            state.IsNestedPublic = true;
    }

    public static int Apply(ModuleDefinition m, ModuleDefinition tr)
    {
        Open(m);
        var boss = tr.GetType("TrPatches.Boss");
        MethodReference To(string name) => m.ImportReference(boss.Methods.Single(x => x.Name == name));
        FieldReference Flag(string name) => m.ImportReference(boss.Fields.Single(x => x.Name == name));
        int n = 0;
        n += Sight(m, To);
        n += Dark(m, To, Flag);
        n += Immune(m, To);
        n += Tiles(m, To, Flag);
        n += Wet(m, To, Flag);
        n += Buffs(m, To);
        return n;
    }

    // ---- matching

    sealed class Match
    {
        public int Start, End;                                  // the code matched: [Start, End)
        public readonly Dictionary<string, VariableDefinition> Locals = new Dictionary<string, VariableDefinition>();
        public readonly Dictionary<string, int> Marks = new Dictionary<string, int>();
    }

    static string Member(object operand) =>
        operand is MemberReference r && !(operand is TypeReference) ? r.DeclaringType.FullName + "::" + r.Name
        : operand is TypeReference t ? t.FullName : null;

    // (FNAPatches) MonoMod's weave writes the tile grid's Get as callvirt (the game ships call); on an array they are the same
    static string Op(Instruction i) =>
        i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.DeclaringType is ArrayType ? "call" : i.OpCode.Name;

    // (FNAPatches) the tile grid's type as MonoMod's weave writes it (Tile[,]) or as the game ships it (Tile[0...,0...])
    static string Grid(string name) => name?.Replace("[0...,0...]", "[,]");

    // the one place in the routine where the pattern stands: a line an instruction ("opcode operand"), or
    // "name:" where a mark stands. Operands: $x a local (the same wherever $x stands), @x a mark, #n a
    // number, "this" the routine's object, * any, otherwise a member's full name (type::name) or a type's.
    static Match Find(MethodDefinition method, string[] pattern)
    {
        var code = method.Body.Instructions;
        Match found = null;
        for (int q = 0; q < code.Count; q++)
        {
            var at = Try(method, q, pattern);
            if (at == null)
                continue;
            if (found != null)
                return null;
            found = at;
        }
        return found;
    }

    static Match Try(MethodDefinition method, int q, string[] pattern)
    {
        var code = method.Body.Instructions;
        var r = new Match { Start = q };
        var jumps = new List<(Instruction from, string mark)>();
        int k = q;
        foreach (string line in pattern)
        {
            if (line.EndsWith(":"))
            {
                r.Marks[line.Substring(0, line.Length - 1)] = k;
                continue;
            }
            if (k >= code.Count)
                return null;
            var i = code[k];
            int space = line.IndexOf(' ');
            string op = space < 0 ? line : line.Substring(0, space), arg = space < 0 ? null : line.Substring(space + 1);
            if (Op(i) != op)
                return null;
            if (arg == null)
            {
                if (i.Operand != null && !(i.Operand is ParameterDefinition))
                    return null;
            }
            else if (arg == "*")
            {
                // any operand (the caller checks it)
            }
            else if (arg[0] == '$')
            {
                if (!(i.Operand is VariableDefinition v))
                    return null;
                if (r.Locals.TryGetValue(arg, out var bound))
                {
                    if (bound != v)
                        return null;
                }
                else
                {
                    if (r.Locals.ContainsValue(v))
                        return null;
                    r.Locals[arg] = v;
                }
            }
            else if (arg[0] == '@')
            {
                if (!(i.Operand is Instruction target))
                    return null;
                jumps.Add((target, arg.Substring(1)));
            }
            else if (arg[0] == '#')
            {
                double want = double.Parse(arg.Substring(1), CultureInfo.InvariantCulture);
                if (!(i.Operand is int iv && iv == want || i.Operand is float fv && fv == want))
                    return null;
            }
            else if (arg == "this")
            {
                if (!(i.Operand is ParameterDefinition p) || p != method.Body.ThisParameter)
                    return null;
            }
            else if (Grid(Member(i.Operand)) != Grid(arg))
                return null;
            k++;
        }
        r.End = k;
        foreach (var (from, mark) in jumps)
            if (!r.Marks.TryGetValue(mark, out int to) || to >= code.Count || code[to] != from)
                return null;
        return r;
    }

    // the locals named are read or written nowhere outside the match, and no jump from outside it lands
    // inside it but at its start (or at the marks given)
    static bool Private(MethodDefinition method, Match r, string[] locals, params string[] entries)
    {
        var code = method.Body.Instructions;
        var vars = locals.Select(x => r.Locals[x]).ToList();
        var ok = new HashSet<Instruction> { code[r.Start] };
        foreach (var e in entries)
            ok.Add(code[r.Marks[e]]);
        for (int q = 0; q < code.Count; q++)
        {
            if (q >= r.Start && q < r.End)
                continue;
            var i = code[q];
            if (i.Operand is VariableDefinition v && vars.Contains(v))
                return false;
            foreach (var t in Targets(i))
            {
                int at = code.IndexOf(t);
                if (at > r.Start && at < r.End && !ok.Contains(t))
                    return false;
            }
        }
        foreach (var h in method.Body.ExceptionHandlers)
            foreach (var x in new[] { h.TryStart, h.TryEnd, h.HandlerStart, h.HandlerEnd, h.FilterStart })
            {
                int at = x == null ? -1 : code.IndexOf(x);
                if (at > r.Start && at < r.End)
                    return false;
            }
        return true;
    }

    static IEnumerable<Instruction> Targets(Instruction i) =>
        i.Operand is Instruction t ? new[] { t } : i.Operand is Instruction[] ts ? ts : Enumerable.Empty<Instruction>();

    // jumps from outside [start, end) to <from> now land on <to>
    static void Enter(MethodBody body, int start, int end, Instruction from, Instruction to)
    {
        var code = body.Instructions;
        for (int q = 0; q < code.Count; q++)
        {
            if (q >= start && q < end)
                continue;
            var i = code[q];
            if (i.Operand == from && i != to)
                i.Operand = to;
            if (i.Operand is Instruction[] ts)
                for (int k = 0; k < ts.Length; k++)
                    if (ts[k] == from)
                        ts[k] = to;
        }
    }

    static MethodDefinition Routine(ModuleDefinition m, string type, string name) =>
        m.GetType(type)?.Methods.Where(x => x.Name == name && x.HasBody).ToList() is var l && l?.Count == 1 ? l[0] : null;

    static int Leave(string what, string why)
    {
        Console.Error.WriteLine($"bosses: {what}: {why}; left out");
        return 0;
    }

    // ---- sight

    static readonly string[] SightLoop =
    {
        "ldc.r4 #999999", "stloc $best", "ldc.i4 #0", "stloc $j", "br @cond",
        "body:",
        "ldsfld Terraria.Main::npc", "ldloc $j", "ldelem.ref", "ldfld Terraria.NPC::active", "brfalse @step",
        "ldsfld Terraria.Main::npc", "ldloc $j", "ldelem.ref", "ldfld Terraria.NPC::type", "ldc.i4 #134", "blt @step",
        "ldsfld Terraria.Main::npc", "ldloc $j", "ldelem.ref", "ldfld Terraria.NPC::type", "ldc.i4 #136", "bgt @step",
        "ldsfld Terraria.Main::player", "ldsfld Terraria.Main::myPlayer", "ldelem.ref", "callvirt Terraria.Entity::get_Center",
        "ldsfld Terraria.Main::npc", "ldloc $j", "ldelem.ref", "callvirt Terraria.Entity::get_Center",
        "call Microsoft.Xna.Framework.Vector2::op_Subtraction",
        "stloc $d", "ldloca $d", "call Microsoft.Xna.Framework.Vector2::Length", "ldloc $best", "bge.un @step",
        "ldsfld Terraria.Main::player", "ldsfld Terraria.Main::myPlayer", "ldelem.ref", "callvirt Terraria.Entity::get_Center",
        "ldc.i4 #1", "ldc.i4 #1",
        "ldsfld Terraria.Main::npc", "ldloc $j", "ldelem.ref", "callvirt Terraria.Entity::get_Center",
        "ldc.i4 #1", "ldc.i4 #1",
        "hit:",
        "call Terraria.Collision::CanHit", "brfalse @step",
        "ldloca $d", "call Microsoft.Xna.Framework.Vector2::Length", "stloc $best",
        "ldsfld Terraria.Main::npc", "ldloc $j", "ldelem.ref", "ldfld Terraria.Entity::position", "stloc $at",
        "step:",
        "ldloc $j", "ldc.i4 #1", "add", "stloc $j",
        "cond:",
        "ldloc $j", "ldsfld Terraria.Main::maxNPCs", "blt @body",
    };

    static int Sight(ModuleDefinition m, Func<string, MethodReference> to)
    {
        const string what = "the Destroyer's health bar";
        if (!Known("Terraria.Collision", "CanHit", "Microsoft.Xna.Framework.Vector2") || !Known("Terraria.Collision", "CanHit", "Microsoft.Xna.Framework.Point")
            || !Known("Terraria.Entity", "get_Center"))
            return Leave(what, "the routines it asks are not the ones it was written for");
        var method = Routine(m, "Terraria.Main", "DrawInterface_14_EntityHealthBars");
        if (method == null)
            return Leave(what, "no routine");
        var body = method.Body;
        body.SimplifyMacros();
        var r = Find(method, SightLoop);
        var hit = r == null ? null : body.Instructions[r.Marks["hit"]];
        var canHit = (MethodReference)hit?.Operand;
        if (r == null || canHit.Parameters.Count != 6 || canHit.Parameters[0].ParameterType.FullName != "Microsoft.Xna.Framework.Vector2"
            || !Private(method, r, new[] { "$j", "$d" }))
        {
            body.OptimizeMacros();
            return Leave(what, "the search is not as expected");
        }
        var il = body.GetILProcessor();
        var first = body.Instructions[r.Start];
        var begin = il.Create(OpCodes.Call, to("SightBegin"));
        il.InsertBefore(first, begin);
        Enter(body, r.Start + 1, r.End + 1, first, begin);
        il.InsertBefore(hit, il.Create(OpCodes.Ldloc, r.Locals["$j"]));
        hit.Operand = to("Sight");
        body.OptimizeMacros();
        Console.Error.WriteLine($"bosses: {what}: the nearest segment in sight asked first");
        return 2;
    }

    // ---- dark

    static readonly string[] DarkLoop =
    {
        "stloc $l", "br @cond1",
        "body1:",
        "ldloc $y0", "stloc $m", "br @cond2",
        "body2:",
        "ldloc $l", "ldloc $m", "call Terraria.Lighting::Brightness", "ldc.r4 #0", "bne.un @stepm",
        "black:",
        "call Microsoft.Xna.Framework.Color::get_Black", "stloc $color",
        "stepm:",
        "ldloc $m", "ldc.i4 #1", "add", "stloc $m",
        "cond2:",
        "ldloc $m", "ldloc $y1", "ble @body2",
        "ldloc $l", "ldc.i4 #1", "add", "stloc $l",
        "cond1:",
        "ldloc $l", "ldloc $x1", "ble @body1",
        "end:",
    };

    static int Dark(ModuleDefinition m, Func<string, MethodReference> to, Func<string, FieldReference> flag)
    {
        const string what = "an NPC drawn behind the tiles";
        if (!Known("Terraria.Lighting", "Brightness") || !Known("Terraria.Graphics.Light.LegacyLighting", "GetColor")
            || !Known("Terraria.Lighting", "get_GlobalBrightness"))
            return Leave(what, "the lighting it reads is not the one it was written for");
        var method = Routine(m, "Terraria.Main", "DrawNPCDirect");
        if (method == null)
            return Leave(what, "no routine");
        var body = method.Body;
        body.SimplifyMacros();
        var r = Find(method, DarkLoop);
        if (r == null || !Private(method, r, new[] { "$l", "$m" }) || r.End >= body.Instructions.Count)
        {
            body.OptimizeMacros();
            return Leave(what, "the look at the tiles is not as expected");
        }
        var code = body.Instructions;
        var il = body.GetILProcessor();
        var store = code[r.Start];
        var black = code[r.Marks["black"]];
        var end = code[r.End];
        VariableDefinition L(string x) => r.Locals[x];
        // the game's own loop: what the cut would say is kept first (audit), each dark tile it finds
        // is told, and the two are compared after it
        var game = il.Create(OpCodes.Ldloc, L("$l"));
        var seen = il.Create(OpCodes.Call, to("DarkSeen"));
        il.InsertBefore(black, seen);
        Enter(body, -1, -1, black, seen);
        il.InsertBefore(end, il.Create(OpCodes.Call, to("DarkAfter")));
        var after = new List<Instruction>
        {
            il.Create(OpCodes.Ldsfld, flag("DarkOn")),
            il.Create(OpCodes.Brfalse, game),
            il.Create(OpCodes.Ldloc, L("$l")), il.Create(OpCodes.Ldloc, L("$x1")), il.Create(OpCodes.Ldloc, L("$y0")), il.Create(OpCodes.Ldloc, L("$y1")),
            il.Create(OpCodes.Call, to("Dark")),
            il.Create(OpCodes.Brfalse, end),
            il.Create(OpCodes.Call, (MethodReference)black.Operand),
            il.Create(OpCodes.Stloc, L("$color")),
            il.Create(OpCodes.Br, end),
            game, il.Create(OpCodes.Ldloc, L("$x1")), il.Create(OpCodes.Ldloc, L("$y0")), il.Create(OpCodes.Ldloc, L("$y1")),
            il.Create(OpCodes.Call, to("DarkBefore")),
        };
        var prev = store;
        foreach (var x in after)
        {
            il.InsertAfter(prev, x);
            prev = x;
        }
        body.OptimizeMacros();
        Console.Error.WriteLine($"bosses: {what}: the first dark tile decides it");
        return 3;
    }

    // ---- immune

    static readonly string[] ImmuneLoop =
    {
        "ldc.i4 #0", "stloc $j", "br @cond",
        "body:",
        "ldarg this", "ldfld Terraria.NPC::immune", "ldloc $j", "ldelem.i4", "ldc.i4 #0", "ble @step",
        "ldarg this", "ldfld Terraria.NPC::immune", "ldloc $j",
        "address:",
        "ldelema System.Int32", "dup", "ldind.i4", "ldc.i4 #1", "sub", "stind.i4",
        "step:",
        "ldloc $j", "ldc.i4 #1", "add", "stloc $j",
        "cond:",
        "ldloc $j", "ldc.i4 #256", "blt @body",
        "end:",
    };

    static int Immune(ModuleDefinition m, Func<string, MethodReference> to)
    {
        const string what = "the NPCs' hit counters";
        var npc = m.GetType("Terraria.NPC");
        var immune = npc?.Fields.FirstOrDefault(f => f.Name == "immune" && f.FieldType.FullName == "System.Int32[]");
        var update = Routine(m, "Terraria.NPC", "UpdateNPC");
        if (immune == null || update == null)
            return Leave(what, "no counters or no update");
        update.Body.SimplifyMacros();
        var r = Find(update, ImmuneLoop);
        bool loop = r != null && Private(update, r, new[] { "$j" }) && r.End < update.Body.Instructions.Count;
        Instruction own = loop ? update.Body.Instructions[r.Marks["address"]] : null;     // the countdown's own element address
        update.Body.OptimizeMacros();
        if (!loop)
            return Leave(what, "the countdown is not as expected");

        // every use of an NPC's counters: an element read or written (the array the store's first
        // operand), the length, or the countdown's own element address; anything else leaves it out
        var stores = new List<(MethodDefinition method, Instruction at)>();
        foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
        {
            var code = method.Body.Instructions;
            for (int q = 0; q < code.Count; q++)
            {
                var i = code[q];
                if (!(i.Operand is FieldReference f && f.Name == immune.Name && f.DeclaringType.FullName == npc.FullName))
                    continue;
                if (i.OpCode == OpCodes.Stfld)
                    continue;                                       // a new array: a mark holds its array, not the field
                if (i.OpCode != OpCodes.Ldfld)
                    return Leave(what, $"{method.FullName}: {i.OpCode} of the counters");
                var (use, depth) = Consumer(code, q);
                if (use == null)
                    return Leave(what, $"{method.FullName}: the counters go where they are not followed");
                if (use.OpCode == OpCodes.Ldelem_I4 && depth == 2 || use.OpCode == OpCodes.Ldlen && depth == 1)
                    continue;
                if (use.OpCode == OpCodes.Stelem_I4 && depth == 3)
                {
                    stores.Add((method, use));
                    continue;
                }
                if (use == own)
                    continue;
                return Leave(what, $"{method.FullName}: {use.OpCode} of the counters");
            }
        }

        // the countdown passed while the NPC is marked, and marked after it
        var body = update.Body;
        body.SimplifyMacros();
        r = Find(update, ImmuneLoop);
        var il = body.GetILProcessor();
        var first = body.Instructions[r.Start];
        var end = body.Instructions[r.End];
        var head = il.Create(OpCodes.Ldarg_0);
        il.InsertBefore(first, head);
        il.InsertBefore(first, il.Create(OpCodes.Call, to("ImmuneSkip")));
        il.InsertBefore(first, il.Create(OpCodes.Brtrue, end));
        Enter(body, r.Start + 3, r.End + 3, first, head);
        il.InsertBefore(end, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(end, il.Create(OpCodes.Call, to("ImmuneDone")));
        body.OptimizeMacros();

        // every other write
        var store = to("ImmuneStore");
        foreach (var (method, at) in stores)
        {
            at.OpCode = OpCodes.Call;
            at.Operand = store;
        }
        Console.Error.WriteLine($"bosses: {what}: the countdown passed while none is above 0; {stores.Count} writes tell");
        return 2 + stores.Count;
    }

    // the instruction that takes the value instruction q leaves on the stack, and where that value lies
    // among what it takes, counted from the top (straight code only)
    static (Instruction, int) Consumer(IList<Instruction> code, int q)
    {
        int depth = 1;
        for (int j = q + 1; j < code.Count; j++)
        {
            var i = code[j];
            int pops = Rests.Pops(i);
            if (pops >= depth)
                return (i, pops == 99 ? -1 : depth);
            if (i.OpCode.FlowControl != FlowControl.Next && i.OpCode.FlowControl != FlowControl.Call)
                return (null, 0);
            depth += Rests.Pushes(i) - pops;
        }
        return (null, 0);
    }

    // ---- tiles

    static readonly string[] TilesLook =
    {
        "ldc.i4 #0", "stloc $in", "ldloc $in", "brtrue @end",
        "start:",
        "ldloc $x0", "stloc $k", "br @condk",
        "bodyk:",
        "ldloc $y0", "stloc $l", "br @condl",
        "bodyl:",
        "ldsfld Terraria.Main::tile", "ldloc $k", "ldloc $l", "call Terraria.Tile[0...,0...]::Get", "brfalse @stepl",
        "ldsfld Terraria.Main::tile", "ldloc $k", "ldloc $l", "call Terraria.Tile[0...,0...]::Get", "callvirt Terraria.Tile::nactive", "brfalse @liquid",
        "ldsfld Terraria.Main::tileSolid", "ldsfld Terraria.Main::tile", "ldloc $k", "ldloc $l", "call Terraria.Tile[0...,0...]::Get",
        "ldfld Terraria.Tile::type", "ldelem.u1", "brtrue @square",
        "ldsfld Terraria.Main::tileSolidTop", "ldsfld Terraria.Main::tile", "ldloc $k", "ldloc $l", "call Terraria.Tile[0...,0...]::Get",
        "ldfld Terraria.Tile::type", "ldelem.u1", "brfalse @liquid",
        "ldsfld Terraria.Main::tile", "ldloc $k", "ldloc $l", "call Terraria.Tile[0...,0...]::Get", "ldfld Terraria.Tile::frameY", "brfalse @square",
        "liquid:",
        "ldsfld Terraria.Main::tile", "ldloc $k", "ldloc $l", "call Terraria.Tile[0...,0...]::Get", "ldfld Terraria.Tile::liquid", "ldc.i4 #64", "ble @stepl",
        "square:",
        "ldloca $v", "ldloc $k", "ldc.i4 #16", "mul", "conv.r4", "stfld Microsoft.Xna.Framework.Vector2::X",
        "ldloca $v", "ldloc $l", "ldc.i4 #16", "mul", "conv.r4", "stfld Microsoft.Xna.Framework.Vector2::Y",
        "ldarg this", "ldflda Terraria.Entity::position", "ldfld Microsoft.Xna.Framework.Vector2::X", "ldarg this", "ldfld Terraria.Entity::width", "conv.r4", "add",
        "ldloc $v", "ldfld Microsoft.Xna.Framework.Vector2::X", "ble.un @stepl",
        "ldarg this", "ldflda Terraria.Entity::position", "ldfld Microsoft.Xna.Framework.Vector2::X",
        "ldloc $v", "ldfld Microsoft.Xna.Framework.Vector2::X", "ldc.r4 #16", "add", "bge.un @stepl",
        "ldarg this", "ldflda Terraria.Entity::position", "ldfld Microsoft.Xna.Framework.Vector2::Y", "ldarg this", "ldfld Terraria.Entity::height", "conv.r4", "add",
        "ldloc $v", "ldfld Microsoft.Xna.Framework.Vector2::Y", "ble.un @stepl",
        "ldarg this", "ldflda Terraria.Entity::position", "ldfld Microsoft.Xna.Framework.Vector2::Y",
        "ldloc $v", "ldfld Microsoft.Xna.Framework.Vector2::Y", "ldc.r4 #16", "add", "bge.un @stepl",
        "ldc.i4 #1", "stloc $in", "br @stepk",
        "stepl:",
        "ldloc $l", "ldc.i4 #1", "add", "stloc $l",
        "condl:",
        "ldloc $l", "ldloc $y1", "blt @bodyl",
        "stepk:",
        "ldloc $k", "ldc.i4 #1", "add", "stloc $k",
        "condk:",
        "ldloc $k", "ldloc $x1", "blt @bodyk",
        "end:",
    };

    static int Tiles(ModuleDefinition m, Func<string, MethodReference> to, Func<string, FieldReference> flag)
    {
        const string what = "a Destroyer segment in the blocks";
        if (!Known("Terraria.Tile", "nactive"))
            return Leave(what, "the tile's routine is not the one it was written for");
        var method = Routine(m, "Terraria.NPC", "AI_037_Destroyer");
        if (method == null)
            return Leave(what, "no routine");
        var body = method.Body;
        body.SimplifyMacros();
        var r = Find(method, TilesLook);
        if (r == null || !Private(method, r, new[] { "$k", "$l", "$v" }, "start") || r.End >= body.Instructions.Count)
        {
            body.OptimizeMacros();
            return Leave(what, "the look at the blocks is not as expected");
        }
        var code = body.Instructions;
        var il = body.GetILProcessor();
        var start = code[r.Marks["start"]];
        var end = code[r.End];
        VariableDefinition L(string x) => r.Locals[x];
        Instruction[] Args() => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldloc, L("$x0")), il.Create(OpCodes.Ldloc, L("$x1")),
            il.Create(OpCodes.Ldloc, L("$y0")), il.Create(OpCodes.Ldloc, L("$y1")) };
        // after the game's own look: compared (audit)
        foreach (var x in Args().Concat(new[] { il.Create(OpCodes.Ldloc, L("$in")), il.Create(OpCodes.Call, to("TilesCheck")) }))
            il.InsertBefore(end, x);
        // before it: the cut, when on
        var fast = new List<Instruction> { il.Create(OpCodes.Ldsfld, flag("TilesOn")), il.Create(OpCodes.Brfalse, start) };
        fast.AddRange(Args());
        fast.Add(il.Create(OpCodes.Call, to("InTiles")));
        fast.Add(il.Create(OpCodes.Stloc, L("$in")));
        fast.Add(il.Create(OpCodes.Br, end));
        foreach (var x in fast)
            il.InsertBefore(start, x);
        body.OptimizeMacros();
        Console.Error.WriteLine($"bosses: {what}: the squares first, and the first tile ends it");
        return 2;
    }

    // ---- wet

    // Collision.WetCollision as shipped (its sum checked again here, before anything else changes it):
    // the places in it, by index. The probe is the square at vector (local 1), num wide (2), num2 high
    // (3); the loops go over columns i (7) and rows j (8).
    const int WetColumn = 117, WetRow = 120, WetNextRow = 323, WetNextColumn = 330, WetLiquidHit = 222, WetSlopeHit = 321;

    static int Wet(ModuleDefinition m, Func<string, MethodReference> to, Func<string, FieldReference> flag)
    {
        const string what = "the look for liquid";
        var method = m.GetType("Terraria.Collision")?.Methods.SingleOrDefault(x => x.Name == "WetCollision" && x.HasBody);
        // (FNAPatches) the routine as summed at Look, compared by its shape: MonoMod's weave rewrites the encoding the sum takes
        if (method == null || !Known("Terraria.Collision", "WetCollision") || wetShape == 0 || Shape(method) != wetShape)
            return Leave(what, "the routine is not the one it was written for");
        var body = method.Body;
        body.SimplifyMacros();
        var code = body.Instructions;
        var vars = body.Variables;
        bool Is(int at, OpCode op, object operand = null) => code[at].OpCode == op && (operand == null || Equals(code[at].Operand, operand));
        // the places, as summed: the loops' heads and steps and the two returns of true
        if (vars.Count < 9 || !Is(WetColumn, OpCodes.Ldloc, vars[5]) || !Is(WetColumn + 1, OpCodes.Stloc, vars[8]) || !Is(WetRow, OpCodes.Ldsfld)
            || !Is(WetNextRow, OpCodes.Ldloc, vars[8]) || !Is(WetNextColumn, OpCodes.Ldloc, vars[7]) || !Is(WetNextColumn + 6, OpCodes.Blt, code[WetColumn])
            || !Is(WetNextRow + 6, OpCodes.Blt, code[WetRow]) || !Is(WetLiquidHit, OpCodes.Ldc_I4, 1) || !Is(WetLiquidHit + 1, OpCodes.Ret)
            || !Is(WetSlopeHit, OpCodes.Ldc_I4, 1) || !Is(WetSlopeHit + 1, OpCodes.Ret))
        {
            body.OptimizeMacros();
            return Leave(what, "the routine is not as expected");
        }
        var il = body.GetILProcessor();
        var vector = vars[1];
        var probeW = vars[2];
        var probeH = vars[3];
        var i = vars[7];
        var j = vars[8];
        var column = code[WetColumn];
        var row = code[WetRow];
        var nextRow = code[WetNextRow];
        var nextColumn = code[WetNextColumn];
        var hits = new[] { code[WetLiquidHit], code[WetSlopeHit] };
        var okColumn = new VariableDefinition(m.TypeSystem.Boolean);
        var okRow = new VariableDefinition(m.TypeSystem.Boolean);
        vars.Add(okColumn);
        vars.Add(okRow);
        var vx = (FieldReference)code[6].Operand;     // Vector2::X, as the routine reads it
        var vy = (FieldReference)code[13].Operand;    // Vector2::Y
        if (vx.Name != "X" || vy.Name != "Y")
        {
            body.OptimizeMacros();
            return Leave(what, "the routine is not as expected");
        }
        // the square at i (or j) can meet the probe: (vector.X + num > i * 16) and (vector.X < i * 16 + 16), the
        // same sums the routine makes before it returns true; the rows with 17: a tile's liquid starts at or
        // below the row's top and ends less than 17 below it (16 + the liquid's fraction of 15/16 at most)
        List<Instruction> Guard(VariableDefinition index, FieldReference axis, VariableDefinition size, float span, VariableDefinition ok,
            Instruction body0, Instruction step) => new List<Instruction>
        {
            il.Create(OpCodes.Ldsfld, flag("WetGuard")), il.Create(OpCodes.Brfalse, body0),
            il.Create(OpCodes.Ldloc, vector), il.Create(OpCodes.Ldfld, axis), il.Create(OpCodes.Ldloc, size), il.Create(OpCodes.Conv_R4), il.Create(OpCodes.Add),
            il.Create(OpCodes.Ldloc, index), il.Create(OpCodes.Ldc_I4, 16), il.Create(OpCodes.Mul), il.Create(OpCodes.Conv_R4),
            il.Create(OpCodes.Cgt),
            il.Create(OpCodes.Ldloc, vector), il.Create(OpCodes.Ldfld, axis),
            il.Create(OpCodes.Ldloc, index), il.Create(OpCodes.Ldc_I4, 16), il.Create(OpCodes.Mul), il.Create(OpCodes.Conv_R4),
            il.Create(OpCodes.Ldc_R4, span), il.Create(OpCodes.Add),
            il.Create(OpCodes.Clt),
            il.Create(OpCodes.And), il.Create(OpCodes.Stloc, ok),
            il.Create(OpCodes.Ldsfld, flag("WetOn")), il.Create(OpCodes.Brfalse, body0),
            il.Create(OpCodes.Ldloc, ok), il.Create(OpCodes.Brfalse, step),
        };
        foreach (var (index, axis, size, span, ok, body0, step) in new[] { (i, vx, probeW, 16f, okColumn, column, nextColumn), (j, vy, probeH, 17f, okRow, row, nextRow) })
        {
            // the loop's jump back to its body now goes through the guard
            var into = code.Where(x => x.Operand == body0).ToList();
            var g = Guard(index, axis, size, span, ok, body0, step);
            foreach (var x in g)
                il.InsertBefore(body0, x);
            foreach (var x in into)
                x.Operand = g[0];
        }
        // audit: a true answer from a square the guards would pass
        foreach (var hit in hits)
        {
            var first = il.Create(OpCodes.Ldloc, okColumn);
            il.InsertBefore(hit, first);
            il.InsertBefore(hit, il.Create(OpCodes.Ldloc, okRow));
            il.InsertBefore(hit, il.Create(OpCodes.Call, to("WetHit")));
            Enter(body, -1, -1, hit, first);
        }
        body.OptimizeMacros();
        Console.Error.WriteLine($"bosses: {what}: the squares that cannot meet the probe are passed");
        return 3;
    }

    // ---- buffs

    // a walk over the NPC's buff slots whose every round begins "buffType[i] <= 0: next": with no buff
    // in any slot it does nothing. Its head and its end (the rest of its body is whatever it is).
    static readonly string[] BuffHead =
    {
        "ldc.i4 #0", "stloc $i", "br *",
        "ldarg this", "ldfld Terraria.NPC::buffType", "ldloc $i", "ldelem.i4", "ldc.i4 #0", "ble *",
    };
    static readonly string[] BuffTail =
    {
        "ldloc $i", "ldc.i4 #1", "add", "stloc $i",
        "ldloc $i", "ldsfld Terraria.NPC::maxBuffs", "blt *",
    };

    static int Buffs(ModuleDefinition m, Func<string, MethodReference> to)
    {
        const string what = "the NPCs' buffs";
        var npc = m.GetType("Terraria.NPC");
        var buffType = npc?.Fields.FirstOrDefault(f => f.Name == "buffType" && f.FieldType.FullName == "System.Int32[]");
        var walks = new List<(MethodDefinition method, Instruction first, Instruction end, bool done)>();
        foreach (var (name, done) in new[] { ("UpdateNPC_BuffSetFlags", true), ("UpdateNPC_BuffClearExpiredBuffs", false) })
        {
            var method = Routine(m, "Terraria.NPC", name);
            if (method == null || buffType == null)
                return Leave(what, "no " + name);
            method.Body.SimplifyMacros();
            var code = method.Body.Instructions;
            Instruction first = null, end = null;
            // the head and the end, each once, and the jumps between them as the walk makes them
            var h = Find(method, BuffHead);
            var t = Find(method, BuffTail);
            if (h != null && t != null && t.Start > h.End && t.End < code.Count && h.Locals["$i"] == t.Locals["$i"])
            {
                // i = 0, to the test; each round: buffType[i] <= 0: to the step; the step, the test, back to the round
                bool ok = code[h.Start + 2].Operand == code[t.Start + 4] && code[h.End - 1].Operand == code[t.Start]
                    && code[t.End - 1].Operand == code[h.Start + 3];
                var r = new Match { Start = h.Start, End = t.End };
                r.Locals["$i"] = h.Locals["$i"];
                if (ok && Private(method, r, new[] { "$i" }))
                {
                    first = code[h.Start];
                    end = code[t.End];
                }
            }
            method.Body.OptimizeMacros();
            if (first == null)
                return Leave(what, name + "'s walk is not as expected");
            walks.Add((method, first, end, done));
        }

        // every use of the slots: an element read or written, the length, cleared (zeros: no buff); anything
        // else leaves it out
        var stores = new List<Instruction>();
        foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
        {
            var code = method.Body.Instructions;
            for (int q = 0; q < code.Count; q++)
            {
                var i = code[q];
                if (!(i.Operand is FieldReference f && f.Name == buffType.Name && f.DeclaringType.FullName == npc.FullName))
                    continue;
                if (i.OpCode == OpCodes.Stfld)
                    continue;                                       // a new array: a mark holds its array, not the field
                if (i.OpCode != OpCodes.Ldfld)
                    return Leave(what, $"{method.FullName}: {i.OpCode} of the buff slots");
                var (use, depth) = Consumer(code, q);
                if (use == null)
                    return Leave(what, $"{method.FullName}: the buff slots go where they are not followed");
                if (use.OpCode == OpCodes.Ldelem_I4 && depth == 2 || use.OpCode == OpCodes.Ldlen && depth == 1
                    || use.OpCode == OpCodes.Call && depth == 3 && use.Operand is MethodReference c && c.DeclaringType.FullName == "System.Array" && c.Name == "Clear")
                    continue;
                if (use.OpCode == OpCodes.Stelem_I4 && depth == 3)
                {
                    stores.Add(use);
                    continue;
                }
                return Leave(what, $"{method.FullName}: {use.OpCode} of the buff slots");
            }
        }

        foreach (var (method, first, end, done) in walks)
        {
            var body = method.Body;
            var il = body.GetILProcessor();
            var head = il.Create(OpCodes.Ldarg_0);
            int at = body.Instructions.IndexOf(first), until = body.Instructions.IndexOf(end);
            il.InsertBefore(first, head);
            il.InsertBefore(first, il.Create(OpCodes.Call, to("BuffsSkip")));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue, end));
            Enter(body, at + 3, until + 3, first, head);
            if (done)
            {
                il.InsertBefore(end, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(end, il.Create(OpCodes.Call, to("BuffsDone")));
            }
        }
        var store = to("BuffStore");
        foreach (var x in stores)
        {
            x.OpCode = OpCodes.Call;
            x.Operand = store;
        }
        Console.Error.WriteLine($"bosses: {what}: the walks passed while no slot holds one; {stores.Count} writes tell");
        return walks.Count + stores.Count;
    }
}
