// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/SplitOut.cs, MIT), except where marked (FNAPatches).
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Moving the parts Split picked (Split.cs).
static partial class Split
{
    const int Own = 0, Copy = 1, Address = 2;

    static Instruction New(OpCode op, object operand)
    {
        var x = Instruction.Create(OpCodes.Nop);
        x.OpCode = op;
        x.Operand = operand;
        return x;
    }

    // what each local and argument the region uses becomes (null: the region stays)
    static SortedDictionary<int, int> Kinds(MethodDefinition md, Shape s, Region r)
    {
        var used = new SortedSet<int>();
        var written = new HashSet<int>();
        for (int q = r.A; q < r.B; q++)
        {
            var i = s.F.Code[q];
            var c = i.OpCode.Code;
            if (i.Operand is ParameterDefinition p && p == md.Body.ThisParameter && (c == Code.Starg || c == Code.Ldarga))
                return null;
            int x = s.Slot(i);
            if (x < 0)
                continue;
            used.Add(x);
            if (c == Code.Stloc || c == Code.Ldloca || c == Code.Starg || c == Code.Ldarga)
                written.Add(x);
        }
        var into = s.LiveAt(r.A);
        var after = s.LiveAfter(r);
        bool Has(ulong[] set, int x) => (set[x >> 6] & 1UL << (x & 63)) != 0;
        var kinds = new SortedDictionary<int, int>();
        foreach (int x in used)
        {
            bool i = Has(into, x), o = Has(after, x);
            int k = !i && !o ? Own : !written.Contains(x) || !o ? Copy : Address;
            if (k == Address && TypeOf(md, s, x) is ByReferenceType)
                return null;
            kinds[x] = k;
        }
        return kinds;
    }

    static TypeReference TypeOf(MethodDefinition md, Shape s, int x) =>
        x < s.L ? md.Body.Variables[x].VariableType : md.Parameters[x - s.L].ParameterType;

    static void Outline(MethodDefinition md, Shape s, List<Region> picked)
    {
        var body = md.Body;
        var old = s.F.Code.ToArray();
        int n = old.Length;
        picked.Sort((x, y) => x.A.CompareTo(y.A));
        var moved = new HashSet<ExceptionHandler>();
        var calls = new Dictionary<int, List<Instruction>>();
        foreach (var r in picked)
            calls[r.A] = Part(md, s, r, old, moved);

        var list = new List<Instruction>(n);
        for (int q = 0, k = 0; q < n;)
        {
            if (k < picked.Count && picked[k].A == q)
            {
                var call = calls[q];
                old[q].OpCode = call[0].OpCode;                   // branches and handlers may point at it
                old[q].Operand = call[0].Operand;
                list.Add(old[q]);
                list.AddRange(call.Skip(1));
                q = picked[k++].B;
            }
            else
                list.Add(old[q++]);
        }
        body.Instructions.Clear();
        foreach (var i in list)
            body.Instructions.Add(i);
        foreach (var h in moved)
            body.ExceptionHandlers.Remove(h);
        var still = new HashSet<VariableDefinition>(list.Select(i => i.Operand).OfType<VariableDefinition>());
        foreach (var v in body.Variables.Where(v => !still.Contains(v)).ToList())
            body.Variables.Remove(v);
        body.OptimizeMacros();
    }

    // makes the part for region r; the instructions that call it
    static List<Instruction> Part(MethodDefinition md, Shape s, Region r, Instruction[] old, HashSet<ExceptionHandler> moved)
    {
        var body = md.Body;
        var module = md.Module;
        var type = md.DeclaringType;
        int n = old.Length;
        int ways = r.Exits.Count + (r.Returns ? 1 : 0);
        string name;
        int k = 0;
        do name = md.Name + "__" + ++k; while (type.Methods.Any(x => x.Name == name));
        var part = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.HideBySig | (md.IsStatic ? MethodAttributes.Static : 0),
            ways > 1 ? module.TypeSystem.Int32 : module.TypeSystem.Void);
        type.Methods.Add(part);
        var pb = part.Body;
        pb.InitLocals = body.InitLocals;

        var param = new Dictionary<int, ParameterDefinition>();
        var own = new Dictionary<int, VariableDefinition>();
        var temp = new Dictionary<int, VariableDefinition>();
        foreach (var kv in r.Kinds)
        {
            int x = kv.Key, kind = kv.Value;
            var t = TypeOf(md, s, x);
            if (kind == Own)
            {
                own[x] = new VariableDefinition(t);
                pb.Variables.Add(own[x]);
            }
            else
            {
                param[x] = new ParameterDefinition(x < s.L ? "l" + x : md.Parameters[x - s.L].Name, ParameterAttributes.None,
                    kind == Address ? new ByReferenceType(t) : t);
                part.Parameters.Add(param[x]);
            }
        }

        var il = new List<Instruction>();
        var map = new Dictionary<Instruction, Instruction>();
        for (int q = r.A; q < r.B; q++)
        {
            var i = old[q];
            var c = i.OpCode.Code;
            int first = il.Count;
            int x = s.Slot(i);
            bool load = c == Code.Ldloc || c == Code.Ldarg, store = c == Code.Stloc || c == Code.Starg;
            if (x >= 0 && r.Kinds[x] == Own)
                il.Add(New(load ? OpCodes.Ldloc : store ? OpCodes.Stloc : OpCodes.Ldloca, own[x]));
            else if (x >= 0 && r.Kinds[x] == Copy)
                il.Add(New(load ? OpCodes.Ldarg : store ? OpCodes.Starg : OpCodes.Ldarga, param[x]));
            else if (x >= 0)
            {
                var t = TypeOf(md, s, x);
                bool pointer = t is PointerType;
                if (load)
                {
                    il.Add(New(OpCodes.Ldarg, param[x]));
                    il.Add(pointer ? New(OpCodes.Ldind_I, null) : New(OpCodes.Ldobj, t));
                }
                else if (store)
                {
                    if (!temp.TryGetValue(x, out var tv))
                    {
                        temp[x] = tv = new VariableDefinition(t);
                        pb.Variables.Add(tv);
                    }
                    il.Add(New(OpCodes.Stloc, tv));
                    il.Add(New(OpCodes.Ldarg, param[x]));
                    il.Add(New(OpCodes.Ldloc, tv));
                    il.Add(pointer ? New(OpCodes.Stind_I, null) : New(OpCodes.Stobj, t));
                }
                else
                    il.Add(New(OpCodes.Ldarg, param[x]));
            }
            else if (i.Operand is ParameterDefinition)
                il.Add(New(i.OpCode, pb.ThisParameter));
            else if (c == Code.Ret && ways > 1)
            {
                il.Add(New(OpCodes.Ldc_I4, r.Exits.Count));
                il.Add(New(OpCodes.Ret, null));
            }
            else
                il.Add(New(i.OpCode, i.Operand));
            map[i] = il[first];
        }
        // the ways out, in the order of r.Exits (its first is where the region runs on to)
        var outs = new List<Instruction>();
        for (int e = 0; e < System.Math.Max(r.Exits.Count, 1); e++)
        {
            outs.Add(ways > 1 ? New(OpCodes.Ldc_I4, e) : New(OpCodes.Ret, null));
            il.Add(outs[e]);
            if (ways > 1)
                il.Add(New(OpCodes.Ret, null));
        }
        Instruction To(Instruction t)
        {
            if (map.TryGetValue(t, out var m))
                return m;
            int e = r.Exits.IndexOf(s.F.At[t]);
            if (e < 0)
                throw new System.InvalidOperationException($"split: {md.FullName}: a jump out of a part to a place it does not leave to");
            return outs[e];
        }
        foreach (var x in il)
            if (x.Operand is Instruction t && !outs.Contains(x))
                x.Operand = To(t);
            else if (x.Operand is Instruction[] ts)
                x.Operand = ts.Select(To).ToArray();
        foreach (var x in il)
            pb.Instructions.Add(x);

        int At(Instruction i) => i == null ? n : s.F.At[i];
        foreach (var h in body.ExceptionHandlers)
            if (At(h.TryStart) >= r.A && At(h.TryEnd) <= r.B && At(h.HandlerStart) >= r.A && At(h.HandlerEnd) <= r.B
                && (h.FilterStart == null || At(h.FilterStart) >= r.A))
            {
                Instruction End(Instruction e) => e != null && map.TryGetValue(e, out var m) ? m : outs[0];
                pb.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
                {
                    CatchType = h.CatchType,
                    TryStart = map[h.TryStart], TryEnd = End(h.TryEnd),
                    HandlerStart = map[h.HandlerStart], HandlerEnd = End(h.HandlerEnd),
                    FilterStart = h.FilterStart == null ? null : map[h.FilterStart],
                });
                moved.Add(h);
            }
        pb.OptimizeMacros();

        // the call
        var call = new List<Instruction>();
        if (!md.IsStatic)
            call.Add(New(OpCodes.Ldarg, body.ThisParameter));
        foreach (var kv in r.Kinds)
            if (kv.Value != Own)
                call.Add(kv.Key < s.L ? New(kv.Value == Copy ? OpCodes.Ldloc : OpCodes.Ldloca, body.Variables[kv.Key])
                                      : New(kv.Value == Copy ? OpCodes.Ldarg : OpCodes.Ldarga, md.Parameters[kv.Key - s.L]));
        call.Add(New(OpCodes.Call, part));
        if (ways > 1)
        {
            var back = New(OpCodes.Ret, null);
            call.Add(New(OpCodes.Switch, r.Exits.Select(e => old[e]).Concat(r.Returns ? new[] { back } : new Instruction[0]).ToArray()));
            if (r.Returns)
                call.Add(back);
        }
        else if (r.Returns)
            call.Add(New(OpCodes.Ret, null));
        else if (r.Exits.Count == 1 && r.Exits[0] != r.B)
            call.Add(New(OpCodes.Br, old[r.Exits[0]]));
        if (r.B >= n && call.Last().OpCode.FlowControl != FlowControl.Return && call.Last().OpCode.FlowControl != FlowControl.Branch)
        {
            call.Add(New(OpCodes.Ldnull, null));                  // never comes back
            call.Add(New(OpCodes.Throw, null));
        }
        return call;
    }
}
