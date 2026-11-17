// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Split.cs, MIT), except where marked (FNAPatches).
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Splits the game's longest routines into parts, each moved whole into a routine of its own that
// the original calls: an arm of an if, a loop's body, or a run of statements, entered only at its
// start, with nothing on the stack where it starts or where it leaves.
//
// Why: the runtime's compiler keeps track of 1024 locals in a routine, its own included. In the
// longest routines it gives up on the rest, which then live in the frame, and it clears every one
// that holds a reference at every call, with everything that lies between them: the longest AI
// routine cleared 85 KB at each call, once per NPC per update. It also stops inlining there. Parts
// of a usual length are compiled in full (docs/notes.md).
//
// Exact by construction: the same instructions in the same order; only the frame they run in
// differs. A local the part uses becomes the part's own when no value of it goes in or comes out
// (on no path is it read before written from the part's start, nor read after the part before
// written: liveness over the whole routine, handlers included); else a copy handed in, when the
// part does not write it or nothing reads what it wrote; else its address. The routine's own
// arguments the same way; `this` stays `this`. A part that leaves to more than one place returns
// which; the caller goes there.
static partial class Split
{
    static int Limit = 1500;      // instructions: a routine longer is split, no part is longer
    const int Least = 40;         // nor shorter

    public static int Apply(ModuleDefinition m)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("TRPATCH_SPLIT_LIMIT"), out int l) && l > Least)
            Limit = l;
        bool debug = Environment.GetEnvironmentVariable("TRPATCH_DEBUG") == "1";
        int routines = 0, parts = 0;
        var report = new List<string>();
        foreach (var t in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.ToList())
        {
            if (t.HasGenericParameters || t.IsValueType || t.IsInterface)
                continue;
            foreach (var md in t.Methods.ToList())
            {
                if (!md.HasBody || md.Body.Instructions.Count <= Limit || md.HasGenericParameters || md.IsConstructor)
                    continue;
                int was = md.Body.Instructions.Count, locals = md.Body.Variables.Count;
                int k = Routine(md);
                if (k > 0)
                {
                    routines++;
                    parts += k;
                    report.Add($"{was,6} {locals,5} -> {md.Body.Instructions.Count,5} {md.Body.Variables.Count,4} in {k,3} parts  {t.Name}::{md.Name}");
                }
            }
        }
        if (debug)
            foreach (var r in report.OrderByDescending(x => x).Take(24))
                Console.Error.WriteLine("split: " + r);
        Console.Error.WriteLine($"split: {routines} routines split into {parts} parts (no part over {Limit} instructions)");
        return parts;
    }

    static IEnumerable<TypeDefinition> Nested(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));

    // what one routine is cut into; the parts made
    static int Routine(MethodDefinition md)
    {
        var body = md.Body;
        if (body.Variables.Any(v => v.IsPinned))
            return 0;
        body.SimplifyMacros();
        var s = new Shape(md);
        if (!s.Ok)
        {
            body.OptimizeMacros();
            return 0;
        }
        var picked = new List<Region>();
        s.Plan(0, s.F.N, picked, 0);
        picked.RemoveAll(r => (r.Kinds = Kinds(md, s, r)) == null);
        if (picked.Count == 0)
        {
            body.OptimizeMacros();
            return 0;
        }
        Outline(md, s, picked);
        return picked.Count;
    }

    // a stretch of code [A, B) that can be moved: where it may leave to (B first when it runs on
    // into B), and whether it returns
    sealed class Region
    {
        public int A, B;
        public List<int> Exits = new List<int>();
        public bool Returns;
        public SortedDictionary<int, int> Kinds;   // what each local and argument it uses becomes
    }

    sealed class Shape
    {
        public readonly MethodDefinition M;
        public readonly MethodBody Body;
        public readonly Flow F;
        public readonly int[] Depth;            // stack depth before an instruction (-1: never run)
        public readonly bool Ok;
        public readonly int L, T, W;            // locals; locals and arguments; words of a set
        readonly int[] firstFrom, lastFrom;     // the lowest and highest instruction branching to one
        readonly List<(int s, int e, int eh)> ranges = new List<(int, int, int)>();
        readonly List<int>[] next;              // a block's successors, handlers included
        readonly ulong[] liveIn, liveOut;
        readonly List<Region> arms = new List<Region>();

        public Shape(MethodDefinition md)
        {
            M = md;
            Body = md.Body;
            F = new Flow(Body);
            int n = F.N;
            L = Body.Variables.Count;
            T = L + md.Parameters.Count;
            W = (T + 63) / 64;
            Depth = Depths();
            if (Depth == null)
                return;
            firstFrom = new int[n + 1];
            lastFrom = new int[n + 1];
            for (int q = 0; q <= n; q++)
            {
                firstFrom[q] = int.MaxValue;
                lastFrom[q] = -1;
            }
            for (int q = 0; q < n; q++)
                foreach (int t in Targets(F.Code[q]))
                {
                    firstFrom[t] = Math.Min(firstFrom[t], q);
                    lastFrom[t] = Math.Max(lastFrom[t], q);
                }
            for (int h = 0; h < Body.ExceptionHandlers.Count; h++)
            {
                var x = Body.ExceptionHandlers[h];
                ranges.Add((At(x.TryStart), At(x.TryEnd), h));
                ranges.Add((At(x.HandlerStart), At(x.HandlerEnd), h));
                if (x.FilterStart != null)
                    ranges.Add((At(x.FilterStart), At(x.HandlerStart), h));
            }
            next = Successors();
            liveIn = new ulong[F.Blocks * W];
            liveOut = new ulong[F.Blocks * W];
            Liveness();
            Arms();
            Ok = true;
        }

        int At(Instruction i) => i == null ? F.N : F.At[i];

        IEnumerable<int> Targets(Instruction i)
        {
            if (i.Operand is Instruction t)
                yield return At(t);
            else if (i.Operand is Instruction[] ts)
                foreach (var x in ts)
                    yield return At(x);
        }

        static bool RunsOn(Instruction i)
        {
            var f = i.OpCode.FlowControl;
            return f == FlowControl.Next || f == FlowControl.Cond_Branch || f == FlowControl.Meta || f == FlowControl.Break
                || f == FlowControl.Call && i.OpCode != OpCodes.Jmp;
        }

        int[] Depths()
        {
            int n = F.N;
            var d = new int[n + 1];
            for (int q = 0; q <= n; q++)
                d[q] = -1;
            var work = new Stack<int>();
            bool Set(int q, int v)
            {
                if (d[q] == -1)
                {
                    d[q] = v;
                    work.Push(q);
                    return true;
                }
                return d[q] == v;
            }
            Set(0, 0);
            foreach (var h in Body.ExceptionHandlers)
            {
                bool caught = h.HandlerType == ExceptionHandlerType.Catch || h.HandlerType == ExceptionHandlerType.Filter;
                if (!Set(At(h.HandlerStart), caught ? 1 : 0) || h.FilterStart != null && !Set(At(h.FilterStart), 1))
                    return null;
            }
            while (work.Count > 0)
            {
                int q = work.Pop();
                if (q == n)
                    return null;                      // runs off the end
                var i = F.Code[q];
                int pops, pushes;
                if (i.OpCode == OpCodes.Ret)
                {
                    pops = M.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
                    pushes = 0;
                }
                else if (i.OpCode == OpCodes.Leave)
                {
                    if (!Set(At((Instruction)i.Operand), 0))
                        return null;
                    continue;
                }
                else if (i.OpCode == OpCodes.Jmp || i.OpCode == OpCodes.Calli || i.OpCode == OpCodes.Localloc || i.OpCode == OpCodes.Arglist
                    || i.OpCode == OpCodes.Tail)
                    return null;
                else
                {
                    pops = Rests.Pops(i);
                    pushes = Rests.Pushes(i);
                    if (pops == 99)
                        return null;
                }
                int after = d[q] - pops + pushes;
                if (after < 0)
                    return null;
                foreach (int t in Targets(i))
                    if (!Set(t, after))
                        return null;
                if (RunsOn(i) && !Set(q + 1, after))
                    return null;
            }
            return d;
        }

        List<int>[] Successors()
        {
            int blocks = F.Blocks;
            var s = new List<int>[blocks];
            for (int b = 0; b < blocks; b++)
            {
                s[b] = new List<int>();
                var last = F.Code[F.End(b) - 1];
                foreach (int t in Targets(last))
                    s[b].Add(F.BlockOf[t]);
                if (RunsOn(last) && F.End(b) < F.N)
                    s[b].Add(b + 1);
            }
            foreach (var h in Body.ExceptionHandlers)
            {
                int ts = At(h.TryStart), te = At(h.TryEnd), hs = At(h.HandlerStart), he = At(h.HandlerEnd);
                int to = F.BlockOf[h.FilterStart != null ? At(h.FilterStart) : hs];
                for (int b = F.BlockOf[ts]; b < blocks && F.Starts[b] < te; b++)
                    s[b].Add(to);
                for (int b = F.BlockOf[hs]; b < blocks && F.Starts[b] < he; b++)
                {
                    var last = F.Code[F.End(b) - 1];
                    if (last.OpCode == OpCodes.Endfinally)
                        for (int q = ts; q < te; q++)       // where the guarded part was left for
                            if (F.Code[q].OpCode == OpCodes.Leave)
                                s[b].Add(F.BlockOf[At((Instruction)F.Code[q].Operand)]);
                }
                if (h.FilterStart != null)
                    for (int b = F.BlockOf[At(h.FilterStart)]; b < blocks && F.Starts[b] < hs; b++)
                        if (F.Code[F.End(b) - 1].OpCode == OpCodes.Endfilter)
                            s[b].Add(F.BlockOf[hs]);
            }
            return s;
        }

        // the local or argument an instruction reads or writes (-1: none)
        public int Slot(Instruction i)
        {
            if (i.Operand is VariableDefinition v)
                return v.Index;
            if (i.Operand is ParameterDefinition p && p.Index >= 0 && p != Body.ThisParameter)
                return L + p.Index;
            return -1;
        }

        void Effect(int q, out int read, out int write)
        {
            var i = F.Code[q];
            read = write = -1;
            var c = i.OpCode.Code;
            if (c == Code.Ldloc || c == Code.Ldloca && !F.Form[q] || c == Code.Ldarg || c == Code.Ldarga)
                read = Slot(i);
            else if (c == Code.Starg)
                write = Slot(i);
            if (F.Writes[q] >= 0)
                write = F.Writes[q];
        }

        void Liveness()
        {
            int blocks = F.Blocks;
            var use = new ulong[blocks * W];
            var def = new ulong[blocks * W];
            for (int b = 0; b < blocks; b++)
                for (int q = F.Starts[b], e = F.End(b); q < e; q++)
                {
                    Effect(q, out int r, out int w);
                    if (r >= 0 && (def[b * W + (r >> 6)] & 1UL << (r & 63)) == 0)
                        use[b * W + (r >> 6)] |= 1UL << (r & 63);
                    if (w >= 0)
                        def[b * W + (w >> 6)] |= 1UL << (w & 63);
                }
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int b = blocks - 1; b >= 0; b--)
                {
                    foreach (int t in next[b])
                        for (int w = 0; w < W; w++)
                            liveOut[b * W + w] |= liveIn[t * W + w];
                    for (int w = 0; w < W; w++)
                    {
                        ulong x = use[b * W + w] | liveOut[b * W + w] & ~def[b * W + w];
                        if (x != liveIn[b * W + w])
                        {
                            liveIn[b * W + w] = x;
                            changed = true;
                        }
                    }
                }
            }
        }

        // what is live as instruction q starts
        public ulong[] LiveAt(int q)
        {
            var o = new ulong[W];
            if (q >= F.N)
                return o;
            int b = F.BlockOf[q];
            Array.Copy(liveOut, b * W, o, 0, W);
            for (int j = F.End(b) - 1; j >= q; j--)
            {
                Effect(j, out int r, out int w);
                if (w >= 0)
                    o[w >> 6] &= ~(1UL << (w & 63));
                if (r >= 0)
                    o[r >> 6] |= 1UL << (r & 63);
            }
            return o;
        }

        // what is live after a region, also where an exception from it is caught
        public ulong[] LiveAfter(Region r)
        {
            var o = new ulong[W];
            foreach (int x in r.Exits)
            {
                var l = LiveAt(x);
                for (int w = 0; w < W; w++)
                    o[w] |= l[w];
            }
            foreach (var (s, e, h) in ranges)
            {
                var x = Body.ExceptionHandlers[h];
                if (s == At(x.TryStart) && s <= r.A && r.B <= e)
                {
                    var l = LiveAt(At(x.FilterStart ?? x.HandlerStart));
                    for (int w = 0; w < W; w++)
                        o[w] |= l[w];
                }
            }
            return o;
        }

        static readonly string[] walks = { "System.Diagnostics.StackTrace", "System.Diagnostics.StackFrame" };

        // the region [a, b), or null when it cannot be moved
        public Region Check(int a, int b)
        {
            int n = F.N;
            if (a < 0 || b > n || b - a < 1 || Depth[a] != 0 || a > 0 && F.Code[a - 1].OpCode.OpCodeType == OpCodeType.Prefix)
                return null;
            var r = new Region { A = a, B = b };
            for (int q = a + 1; q < b; q++)
                if (lastFrom[q] >= 0 && (firstFrom[q] < a || lastFrom[q] >= b))
                    return null;                                  // entered past its start
            var inside = new HashSet<int>();
            var outside = new HashSet<int>();
            foreach (var (s, e, h) in ranges)
            {
                if (s >= a && e <= b)
                    inside.Add(h);
                else if (e <= a || s >= b)
                    outside.Add(h);
                else if (s <= a && b <= e)
                    outside.Add(h);
                else
                    return null;                                  // cut through
            }
            if (inside.Overlaps(outside))
                return null;
            for (int q = a; q < b; q++)
            {
                var i = F.Code[q];
                var c = i.OpCode.Code;
                if (c == Code.Ret)
                    r.Returns = true;
                if (c == Code.Endfinally || c == Code.Endfilter || c == Code.Rethrow)
                    if (!ranges.Any(x => inside.Contains(x.eh) && x.s <= q && q < x.e && x.s != At(Body.ExceptionHandlers[x.eh].TryStart)))
                        return null;
                if (i.Operand is MethodReference mr && (walks.Contains(mr.DeclaringType.FullName)
                    || mr.Name == "GetCurrentMethod" || mr.Name == "GetCallingAssembly"))
                    return null;
                if (i.OpCode.OpCodeType == OpCodeType.Prefix && q == b - 1)
                    return null;
                foreach (int t in Targets(i))
                    if (t < a || t >= b)
                    {
                        if (c == Code.Leave || Depth[t] != 0)
                            return null;
                        if (!r.Exits.Contains(t))
                            r.Exits.Add(t);
                    }
            }
            if (RunsOn(F.Code[b - 1]))
            {
                if (b >= n || Depth[b] != 0)
                    return null;
                r.Exits.Remove(b);
                r.Exits.Insert(0, b);
            }
            if (r.Returns && (M.ReturnType.MetadataType != MetadataType.Void || ranges.Any(x => x.s <= a && b <= x.e)))
                return null;
            return r;
        }

        // the arms of the ifs, and the loops' bodies: [start, end) as C# lays them out
        void Arms()
        {
            int n = F.N;
            var seen = new HashSet<(int, int)>();
            void Add(int a, int b)
            {
                if (b - a >= Least && seen.Add((a, b)) && Check(a, b) is Region r)
                    arms.Add(r);
            }
            for (int q = 0; q < n; q++)
            {
                var i = F.Code[q];
                if (i.OpCode.FlowControl == FlowControl.Cond_Branch && i.Operand is Instruction ti && Depth[q + 1] == 0)
                {
                    int t = At(ti);
                    if (t > q + 1)
                    {
                        Add(q + 1, t);
                        var j = F.Code[t - 1];
                        if (j.OpCode == OpCodes.Br && At((Instruction)j.Operand) > t)
                            Add(t, At((Instruction)j.Operand));
                    }
                }
                else if (i.OpCode == OpCodes.Br && At((Instruction)i.Operand) is int c && c > q + 1 && lastFrom[q + 1] >= c)
                    Add(q + 1, c);                                // a loop's body, its test at c
            }
            arms.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : y.B.CompareTo(x.B));
        }

        // where [A, B) may be cut between statements: nothing jumps across, nothing on the stack
        List<int> Cuts(int A, int B)
        {
            var cross = new int[B - A + 2];
            void Mark(int from, int to)                           // cuts c with from <= c <= to
            {
                from = Math.Max(from, A + 1);
                to = Math.Min(to, B - 1);
                if (from <= to)
                {
                    cross[from - A]++;
                    cross[to - A + 1]--;
                }
            }
            for (int q = A; q < B; q++)
                foreach (int t in Targets(F.Code[q]))
                    if (t >= A && t < B)
                    {
                        if (t > q + 1)
                            Mark(q + 1, t - 1);
                        else if (t <= q)
                            Mark(t + 1, q);
                    }
            foreach (var x in Body.ExceptionHandlers)
            {
                int s = Math.Min(At(x.TryStart), x.FilterStart != null ? At(x.FilterStart) : int.MaxValue);
                s = Math.Min(s, At(x.HandlerStart));
                int e = Math.Max(At(x.TryEnd), At(x.HandlerEnd));
                if (s >= A && e <= B)
                    Mark(s + 1, e - 1);
            }
            var cuts = new List<int> { A };
            int run = 0;
            for (int c = A + 1; c < B; c++)
            {
                run += cross[c - A];
                if (run == 0 && Depth[c] == 0 && F.Code[c - 1].OpCode.OpCodeType != OpCodeType.Prefix)
                    cuts.Add(c);
            }
            cuts.Add(B);
            return cuts;
        }

        // picks the regions of [A, B) to move: runs of statements up to the limit; a statement
        // over it is looked into, arm by arm
        public void Plan(int A, int B, List<Region> picked, int depth)
        {
            if (depth > 400)
                return;
            var cuts = Cuts(A, B);
            int cs = -1, ce = -1;
            void Flush()
            {
                if (cs >= 0 && ce - cs >= Least && !(cs == A && ce == B && depth == 0) && Check(cs, ce) is Region r)
                    picked.Add(r);
                cs = -1;
            }
            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                int s = cuts[k], e = cuts[k + 1];
                if (e - s > Limit)
                {
                    Flush();
                    int reach = s;
                    foreach (var r in arms)
                        if (r.A >= reach && r.B <= e && !(r.A == s && r.B == e) && r.A < e)
                        {
                            if (r.B - r.A <= Limit)
                                picked.Add(r);
                            else
                                Plan(r.A, r.B, picked, depth + 1);
                            reach = r.B;
                        }
                    continue;
                }
                if (cs >= 0 && e - cs > Limit)
                    Flush();
                if (cs < 0)
                    cs = s;
                ce = e;
            }
            Flush();
        }
    }
}
