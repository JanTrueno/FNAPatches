// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Inits.cs, MIT), except where marked (FNAPatches).
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// A routine compiled from C# asks the runtime to clear all of its locals at every call. The
// runtime leaves out the locals it can follow, but it follows only so many of a routine, and
// never one whose address is taken: the game's largest routines clear tens of kilobytes at each
// call, and they are called for every NPC, projectile and drawn thing.
//
// This pass withdraws the request where it cannot be seen. A local written before it is read on
// every path needs no clearing; each local for which that is not shown here is cleared by hand
// as the routine starts, which is what the request did for it. Left alone: a routine that takes
// stack memory (the request covers that memory too), and one with a local that cannot be cleared
// by hand.
static class Inits
{
    static IEnumerable<TypeDefinition> Nested(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));

    static VariableDefinition Local(Instruction i) => i.Operand as VariableDefinition;

    public static int Apply(ModuleDefinition m)
    {
        bool debug = Environment.GetEnvironmentVariable("TRPATCH_DEBUG") == "1";
        int done = 0, left = 0, byHand = 0, locals = 0;
        var report = new List<(int locals, int hand, string name)>();
        foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
        {
            var body = method.Body;
            if (!body.InitLocals || body.Variables.Count == 0)
                continue;
            if (body.Instructions.Any(i => i.OpCode == OpCodes.Localloc)
                || body.Variables.Any(v => v.VariableType is PinnedType))
            {
                left++;
                continue;
            }
            body.SimplifyMacros();
            var unproven = Unproven(body);
            var hand = body.Variables.Where(v => unproven[v.Index]).ToList();
            if (hand.Any(v => !(v.VariableType is PointerType) && !Clearable(v.VariableType)))
            {
                body.OptimizeMacros();
                left++;
                continue;
            }
            Order(body);
            var il = body.GetILProcessor();
            var first = body.Instructions[0];
            foreach (var v in hand)
            {
                if (v.VariableType is PointerType)
                {
                    il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
                    il.InsertBefore(first, il.Create(OpCodes.Conv_U));
                    il.InsertBefore(first, il.Create(OpCodes.Stloc, v));
                }
                else
                {
                    il.InsertBefore(first, il.Create(OpCodes.Ldloca, v));
                    il.InsertBefore(first, il.Create(OpCodes.Initobj, v.VariableType));
                }
            }
            body.InitLocals = false;
            body.OptimizeMacros();
            done++;
            byHand += hand.Count;
            locals += body.Variables.Count;
            if (debug)
                report.Add((body.Variables.Count, hand.Count, method.FullName));
        }
        foreach (var r in report.OrderByDescending(x => x.locals).Take(16))
            Console.Error.WriteLine($"inits: {r.locals} locals, {r.hand} by hand: {r.name}");
        Console.Error.WriteLine($"inits: {done} routines no longer clear their locals at each call ({byHand} of {locals} locals cleared by hand); {left} left as they were");
        return done;
    }

    // The runtime clears some locals whatever is asked: a value that holds references, and a
    // reference whose address is handed out. It clears from the lowest to the highest of them in
    // one piece, so they go last, next to each other and to the references it keeps together.
    // The order of a routine's locals means nothing else.
    static void Order(MethodBody body)
    {
        var exposed = new HashSet<VariableDefinition>();
        var code = body.Instructions;
        for (int q = 0; q < code.Count; q++)
            if (code[q].OpCode == OpCodes.Ldloca && !(q + 1 < code.Count && code[q + 1].OpCode == OpCodes.Initobj))
                exposed.Add(Local(code[q]));
        bool Last(VariableDefinition v)
        {
            var t = v.VariableType;
            return Refs(t) && (t.IsValueType || t is GenericParameter || exposed.Contains(v));
        }
        var was = body.Variables.ToList();
        var now = was.Where(v => !Last(v)).Concat(was.Where(Last)).ToList();
        if (now.SequenceEqual(was))
            return;
        body.Variables.Clear();
        foreach (var v in now)
            body.Variables.Add(v);
    }

    static bool Clearable(TypeReference t) =>
        !(t is ByReferenceType) && !(t is FunctionPointerType) && !(t is RequiredModifierType) && !(t is OptionalModifierType) && !(t is SentinelType);

    // whether a value of the type holds references the collector follows (when in doubt: yes)
    static readonly Dictionary<string, bool> refs = new Dictionary<string, bool>();

    internal static bool Refs(TypeReference t, int depth = 0)
    {
        if (t is PointerType || t is FunctionPointerType)
            return false;
        if (t is RequiredModifierType || t is OptionalModifierType)
            return Refs(((TypeSpecification)t).ElementType, depth);
        if (t is ByReferenceType || t is ArrayType || t is GenericParameter || t is PinnedType || t is SentinelType)
            return true;
        if (t.IsPrimitive)
            return false;
        if (!t.IsValueType)
            return true;
        string key = t.FullName;
        if (refs.TryGetValue(key, out bool known))
            return known;
        bool holds = true;
        TypeDefinition d = null;
        try { d = t.Resolve(); } catch (Exception) { }
        if (d != null && depth < 8)
        {
            holds = false;
            if (!d.IsEnum)
                foreach (var f in d.Fields.Where(f => !f.IsStatic))
                {
                    var ft = f.FieldType;
                    if (t is GenericInstanceType g)
                        ft = Fill(ft, g, d);
                    if (ft == null || Refs(ft, depth + 1)) { holds = true; break; }
                }
        }
        refs[key] = holds;
        return holds;
    }

    // whether a constructor of the type writes all of a value: not one with a size or a place
    // given to its fields by hand (a fixed buffer, fields laid over each other), where bytes lie
    // that no assignment reaches (when in doubt: no)
    static readonly Dictionary<string, bool> whole = new Dictionary<string, bool>();

    internal static bool Whole(TypeReference t, int depth = 0)
    {
        if (t is PointerType || t.IsPrimitive || !t.IsValueType)
            return true;
        string key = t.FullName;
        if (whole.TryGetValue(key, out bool known))
            return known;
        bool all = false;
        TypeDefinition d = null;
        try { d = t.Resolve(); } catch (Exception) { }
        if (d != null && depth < 8 && !d.IsExplicitLayout && d.ClassSize <= 0 && d.PackingSize <= 0)
        {
            all = true;
            if (!d.IsEnum)
                foreach (var f in d.Fields.Where(f => !f.IsStatic))
                {
                    var ft = f.FieldType;
                    if (t is GenericInstanceType g)
                        ft = Fill(ft, g, d);
                    if (ft == null || ft is GenericParameter || !Whole(ft, depth + 1)) { all = false; break; }
                }
        }
        whole[key] = all;
        return all;
    }

    // a field's type with the generic arguments of the instance it is a field of (null: not followed)
    static TypeReference Fill(TypeReference t, GenericInstanceType g, TypeDefinition d)
    {
        if (t is GenericParameter p)
            return p.Owner == d && p.Position < g.GenericArguments.Count ? g.GenericArguments[p.Position] : null;
        if (t is GenericInstanceType inner)
        {
            var filled = new GenericInstanceType(inner.ElementType);
            foreach (var a in inner.GenericArguments)
            {
                var x = Fill(a, g, d);
                if (x == null)
                    return null;
                filled.GenericArguments.Add(x);
            }
            return filled;
        }
        return t;
    }

    // the locals that some path may read, or hand out the address of, before a write
    static bool[] Unproven(MethodBody body)
    {
        var f = new Flow(body);
        var code = f.Code;
        int n = f.N, count = body.Variables.Count, words = (count + 63) / 64;
        var at = f.At;
        var blockOf = f.BlockOf;
        var starts = f.Starts;
        int blocks = f.Blocks;
        int End(int b) => f.End(b);
        var writes = f.Writes;
        var form = f.Form;

        // what is written on every path to a block's start
        var gen = new ulong[blocks * words];
        for (int q = 0; q < n; q++)
            if (writes[q] >= 0)
                gen[blockOf[q] * words + (writes[q] >> 6)] |= 1UL << (writes[q] & 63);
        var into = new ulong[blocks * words];
        var seen = new bool[blocks];
        var handlers = new List<int>[blocks];
        foreach (var h in body.ExceptionHandlers)
        {
            // a handler starts with no more than was written as its guarded part started
            int b = blockOf[at[h.TryStart]];
            foreach (var x in new[] { h.HandlerStart, h.FilterStart })
                if (x != null)
                    (handlers[b] ?? (handlers[b] = new List<int>())).Add(blockOf[at[x]]);
        }
        var work = new Stack<int>();
        var queued = new bool[blocks];
        seen[0] = true;
        work.Push(0);
        queued[0] = true;
        var o = new ulong[words];
        void Meet(int to, ulong[] from, int offset)
        {
            bool changed = false;
            if (!seen[to])
            {
                Array.Copy(from, offset, into, to * words, words);
                seen[to] = true;
                changed = true;
            }
            else
                for (int w = 0; w < words; w++)
                {
                    ulong was = into[to * words + w], now = was & from[offset + w];
                    if (now != was)
                    {
                        into[to * words + w] = now;
                        changed = true;
                    }
                }
            if (changed && !queued[to])
            {
                queued[to] = true;
                work.Push(to);
            }
        }
        while (work.Count > 0)
        {
            int b = work.Pop();
            queued[b] = false;
            if (handlers[b] != null)
                foreach (int to in handlers[b])
                    Meet(to, into, b * words);
            for (int w = 0; w < words; w++)
                o[w] = into[b * words + w] | gen[b * words + w];
            int end = End(b);
            var last = code[end - 1];
            var flow = last.OpCode.FlowControl;
            if (last.Operand is Instruction t)
                Meet(blockOf[at[t]], o, 0);
            else if (last.Operand is Instruction[] ts)
                foreach (var x in ts)
                    Meet(blockOf[at[x]], o, 0);
            if (flow != FlowControl.Branch && flow != FlowControl.Return && flow != FlowControl.Throw && end < n)
                Meet(b + 1, o, 0);
        }

        // the reads met before a write
        var unproven = new bool[count];
        for (int b = 0; b < blocks; b++)
        {
            if (!seen[b])
                continue;                                     // never run
            Array.Copy(into, b * words, o, 0, words);
            for (int q = starts[b], end = End(b); q < end; q++)
            {
                var i = code[q];
                if (i.OpCode == OpCodes.Ldloc || i.OpCode == OpCodes.Ldloca && !form[q])
                {
                    int v = Local(i).Index;
                    if ((o[v >> 6] & 1UL << (v & 63)) == 0)
                        unproven[v] = true;
                }
                if (writes[q] >= 0)
                    o[writes[q] >> 6] |= 1UL << (writes[q] & 63);
            }
        }
        return unproven;
    }
}
