// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch, MIT), as it is there, except where marked "(FNAPatches)".
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// What is on the evaluation stack before every instruction of a method, as far as tiles go: which
// values are a Terraria.Tile, which a reference to one, which a null constant that ends up where a
// Tile is expected. TilePtr rewrites those when Tile becomes a pointer to its record.
// The method's macros must be simplified (ldarg.0 -> ldarg, short branches -> long).
sealed class TileSim
{
    public sealed class Val
    {
        public TypeReference Type;              // null: not known, or of no interest here
        public HashSet<Instruction> Nulls;      // the ldnull instructions this value may be
        public Instruction From;                // the one instruction that pushed it (null: several may have)
        public bool New => From != null && From.OpCode == OpCodes.Newobj && IsTile(((MethodReference)From.Operand).DeclaringType);
    }

    public readonly HashSet<Instruction> NullTiles = new HashSet<Instruction>();     // ldnull used as a Tile
    public readonly HashSet<Instruction> Indirect = new HashSet<Instruction>();      // ldind.ref, stind.ref on a Tile&
    public readonly HashSet<Instruction> Casts = new HashSet<Instruction>();         // castclass, isinst Tile of a Tile
    public readonly Dictionary<Instruction, Val[]> In = new Dictionary<Instruction, Val[]>();
    public readonly List<KeyValuePair<Instruction, string>> Notes = new List<KeyValuePair<Instruction, string>>();   // uses to treat by hand
    // the instructions that take the value an instruction pushed
    public readonly Dictionary<Instruction, List<Instruction>> Takers = new Dictionary<Instruction, List<Instruction>>();
    // the instructions whose value meets another instruction's at a join: a taker after the join is not in Takers
    public readonly HashSet<Instruction> Merged = new HashSet<Instruction>();

    readonly MethodDefinition method;
    readonly Stack<Instruction> work = new Stack<Instruction>();
    static readonly Val Unknown = new Val();

    public static bool IsTile(TypeReference t) => t != null && !(t is TypeSpecification) && t.Namespace == "Terraria" && t.Name == "Tile";
    static bool IsTileRef(TypeReference t) => t is ByReferenceType b && IsTile(b.ElementType);
    public static bool IsGrid(TypeReference t) => t is ArrayType a && IsTile(a.ElementType);

    public TileSim(MethodDefinition method)
    {
        this.method = method;
        var body = method.Body;
        if (body.Instructions.Count == 0)
            return;
        Flow(body.Instructions[0], new Val[0]);
        foreach (var h in body.ExceptionHandlers)
        {
            var caught = h.HandlerType == ExceptionHandlerType.Catch ? new[] { new Val { Type = h.CatchType } }
                : h.HandlerType == ExceptionHandlerType.Filter ? new[] { new Val { Type = method.Module.TypeSystem.Object } } : new Val[0];
            Flow(h.HandlerStart, caught);
            if (h.FilterStart != null)
                Flow(h.FilterStart, new[] { new Val { Type = method.Module.TypeSystem.Object } });
        }
        while (work.Count > 0)
        {
            var ins = work.Pop();
            Step(ins, In[ins]);
        }
        foreach (var at in In)
        {
            int n = at.Key.OpCode.Code == Code.Leave ? 0 : PopCount(at.Key, at.Value.Length);
            for (int k = 1; k <= n && k <= at.Value.Length; k++)
            {
                var from = at.Value[at.Value.Length - k].From;
                if (from == null)
                    continue;
                if (!Takers.TryGetValue(from, out var list))
                    Takers[from] = list = new List<Instruction>();
                list.Add(at.Key);
            }
        }
    }

    // how many values the instruction takes from a stack of this depth
    int PopCount(Instruction ins, int depth)
    {
        switch (ins.OpCode.Code)
        {
            case Code.Call: case Code.Callvirt: case Code.Newobj:
                var m = (MethodReference)ins.Operand;
                return m.Parameters.Count + (m.HasThis && ins.OpCode.Code != Code.Newobj ? 1 : 0);
            case Code.Calli:
                var c = (CallSite)ins.Operand;
                return c.Parameters.Count + 1 + (c.HasThis ? 1 : 0);
            case Code.Ret:
                return method.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
            default:
                return Pops(ins, depth);
        }
    }

    void Note(Instruction at, string what)
    {
        if (!Notes.Any(n => n.Key == at && n.Value == what))
            Notes.Add(new KeyValuePair<Instruction, string>(at, what));
    }

    // is this instruction the only one that takes the value <from> pushed
    public bool OnlyTaker(Instruction from, Instruction taker) =>
        from != null && !Merged.Contains(from) && Takers.TryGetValue(from, out var list) && list.Count == 1 && list[0] == taker;

    public string Where(Instruction at) => $"{method.FullName} IL_{at.Offset:x4}";

    void Flow(Instruction to, Val[] stack)
    {
        if (to == null)
            return;
        if (!In.TryGetValue(to, out var old))
        {
            In[to] = stack;
            work.Push(to);
            return;
        }
        if (old.Length != stack.Length)
        {
            Note(to, $"stack depths {old.Length} and {stack.Length} meet");
            return;
        }
        bool changed = false;
        for (int i = 0; i < old.Length; i++)
        {
            Val a = old[i], b = stack[i];
            if (ReferenceEquals(a, b))
                continue;
            if (a.From != b.From)
            {
                if (a.From != null) Merged.Add(a.From);
                if (b.From != null) Merged.Add(b.From);
            }
            var m = new Val
            {
                Type = a.Type == null ? b.Type : b.Type == null ? a.Type : IsTile(b.Type) ? b.Type : a.Type,
                Nulls = a.Nulls == null ? b.Nulls : b.Nulls == null || b.Nulls.IsSubsetOf(a.Nulls) ? a.Nulls : new HashSet<Instruction>(a.Nulls.Concat(b.Nulls)),
                From = a.From == b.From ? a.From : null,
            };
            if (IsTile(m.Type) && m.Nulls != null)
                NullTiles.UnionWith(m.Nulls);
            if (m.Type != a.Type || m.Nulls != a.Nulls || m.From != a.From)
            {
                old[i] = m;
                changed = true;
            }
        }
        if (changed)
            work.Push(to);
    }

    // the value goes where <expected> is wanted
    void Expect(Instruction at, Val v, TypeReference expected)
    {
        if (expected == null)
            return;
        if (IsTile(expected))
        {
            if (v.Nulls != null)
                NullTiles.UnionWith(v.Nulls);
        }
        else if (IsTile(v.Type) && !(expected is GenericParameter))
            Note(at, $"a Tile used as {expected.FullName} ({at.OpCode} {Operand(at)})");
        else if (IsGrid(v.Type) && !IsGrid(expected))
            Note(at, $"a Tile array used as {expected.FullName} ({at.OpCode} {Operand(at)})");
    }

    static string Operand(Instruction i) => i.Operand is MemberReference m ? m.FullName : i.Operand is ParameterDefinition p ? p.Name : "";

    static TypeReference Subst(TypeReference t, TypeReference declaring, MethodReference m)
    {
        switch (t)
        {
            case GenericParameter gp:
                if (gp.Type == GenericParameterType.Type && declaring is GenericInstanceType gt && gp.Position < gt.GenericArguments.Count)
                    return gt.GenericArguments[gp.Position];
                if (gp.Type == GenericParameterType.Method && m is GenericInstanceMethod gm && gp.Position < gm.GenericArguments.Count)
                    return gm.GenericArguments[gp.Position];
                return t;
            case ByReferenceType b:
                var e = Subst(b.ElementType, declaring, m);
                return e == b.ElementType ? t : new ByReferenceType(e);
            case ArrayType a:
                var ae = Subst(a.ElementType, declaring, m);
                return ae == a.ElementType ? t : new ArrayType(ae, a.Rank);
            default:
                return t;
        }
    }

    public static TypeReference FieldType(FieldReference f) => Subst(f.FieldType, f.DeclaringType, null);
    public static TypeReference ParamType(MethodReference m, int i) => Subst(m.Parameters[i].ParameterType, m.DeclaringType, m);
    public static TypeReference ReturnType(MethodReference m) => Subst(m.ReturnType, m.DeclaringType, m);

    static int Pops(Instruction ins, int depth)
    {
        switch (ins.OpCode.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi:
                return 2;
            case StackBehaviour.PopAll: return depth;
            case StackBehaviour.Varpop: throw new InvalidOperationException("varpop " + ins.OpCode);
            default: return 3;
        }
    }

    static int Pushes(Instruction ins)
    {
        switch (ins.OpCode.StackBehaviourPush)
        {
            case StackBehaviour.Push0: return 0;
            case StackBehaviour.Push1_push1: return 2;
            case StackBehaviour.Varpush: throw new InvalidOperationException("varpush " + ins.OpCode);
            default: return 1;
        }
    }

    void Step(Instruction ins, Val[] before)
    {
        var s = new List<Val>(before);
        Val Pop()
        {
            if (s.Count == 0) { Note(ins, "pop from an empty stack"); return Unknown; }
            var v = s[s.Count - 1];
            s.RemoveAt(s.Count - 1);
            return v;
        }
        void Push(TypeReference t) => s.Add(new Val { Type = t, From = ins });
        var module = method.Module;
        switch (ins.OpCode.Code)
        {
            case Code.Ldarg: Push(((ParameterDefinition)ins.Operand).ParameterType); break;
            case Code.Ldarga: Push(new ByReferenceType(((ParameterDefinition)ins.Operand).ParameterType)); break;
            case Code.Starg: Expect(ins, Pop(), ((ParameterDefinition)ins.Operand).ParameterType); break;
            case Code.Ldloc: Push(((VariableDefinition)ins.Operand).VariableType); break;
            case Code.Ldloca: Push(new ByReferenceType(((VariableDefinition)ins.Operand).VariableType)); break;
            case Code.Stloc: Expect(ins, Pop(), ((VariableDefinition)ins.Operand).VariableType); break;
            case Code.Ldnull: s.Add(new Val { Nulls = new HashSet<Instruction> { ins }, From = ins }); break;
            case Code.Ldstr: Push(module.TypeSystem.String); break;
            case Code.Dup: { var v = Pop(); s.Add(v); s.Add(v); break; }
            case Code.Ldfld: Pop(); Push(FieldType((FieldReference)ins.Operand)); break;
            case Code.Ldflda: Pop(); Push(new ByReferenceType(FieldType((FieldReference)ins.Operand))); break;
            case Code.Ldsfld: Push(FieldType((FieldReference)ins.Operand)); break;
            case Code.Ldsflda: Push(new ByReferenceType(FieldType((FieldReference)ins.Operand))); break;
            case Code.Stfld: { var v = Pop(); Pop(); Expect(ins, v, FieldType((FieldReference)ins.Operand)); break; }
            case Code.Stsfld: Expect(ins, Pop(), FieldType((FieldReference)ins.Operand)); break;
            case Code.Call: case Code.Callvirt: case Code.Newobj:
            {
                var m = (MethodReference)ins.Operand;
                for (int i = m.Parameters.Count - 1; i >= 0; i--)
                    Expect(ins, Pop(), ParamType(m, i));
                if (m.HasThis && ins.OpCode.Code != Code.Newobj)
                {
                    var self = Pop();
                    if (IsTile(self.Type) && !IsTile(m.DeclaringType))
                        Note(ins, $"a Tile as the object of {m.FullName}");
                    if (IsGrid(self.Type) && !IsGrid(m.DeclaringType))
                        Note(ins, $"a Tile array as the object of {m.FullName}");
                }
                if (ins.OpCode.Code == Code.Newobj)
                    Push(m.DeclaringType);
                else if (m.ReturnType.MetadataType != MetadataType.Void)
                    Push(ReturnType(m));
                break;
            }
            case Code.Calli:
            {
                var c = (CallSite)ins.Operand;
                Pop();
                for (int i = c.Parameters.Count - 1; i >= 0; i--)
                    Expect(ins, Pop(), c.Parameters[i].ParameterType);
                if (c.HasThis) Pop();
                if (c.ReturnType.MetadataType != MetadataType.Void) Push(c.ReturnType);
                break;
            }
            case Code.Ret:
                if (method.ReturnType.MetadataType != MetadataType.Void)
                    Expect(ins, Pop(), method.ReturnType);
                break;
            case Code.Castclass: case Code.Isinst:
            {
                var v = Pop();
                var t = (TypeReference)ins.Operand;
                if (IsTile(t))
                {
                    if (IsTile(v.Type)) Casts.Add(ins);
                    else Note(ins, $"{ins.OpCode} Tile of a {v.Type?.FullName ?? "?"}");
                }
                else if (IsTile(v.Type))
                    Note(ins, $"a Tile {ins.OpCode} {t.FullName}");
                Push(t);
                break;
            }
            case Code.Ldind_Ref:
            {
                var a = Pop();
                if (IsTileRef(a.Type)) Indirect.Add(ins);
                Push((a.Type as ByReferenceType)?.ElementType);
                break;
            }
            case Code.Stind_Ref:
            {
                var v = Pop();
                var a = Pop();
                if (IsTileRef(a.Type)) { Indirect.Add(ins); Expect(ins, v, ((ByReferenceType)a.Type).ElementType); }
                else if (IsTile(v.Type)) Note(ins, $"a Tile stored through {a.Type?.FullName ?? "?"}");
                break;
            }
            case Code.Ldelem_Ref: case Code.Ldelem_Any:
            {
                Pop();
                var arr = Pop();
                var e = ins.Operand as TypeReference ?? (arr.Type as ArrayType)?.ElementType;
                if (IsTile(e)) Note(ins, "an element of a Tile[] loaded");
                Push(e);
                break;
            }
            case Code.Stelem_Ref: case Code.Stelem_Any:
            {
                var v = Pop();
                Pop();
                var arr = Pop();
                var e = ins.Operand as TypeReference ?? (arr.Type as ArrayType)?.ElementType;
                if (IsTile(e) || IsTile(v.Type)) Note(ins, $"a Tile stored into a {arr.Type?.FullName ?? "?"}");
                break;
            }
            case Code.Ldelema: Pop(); Pop(); Push(new ByReferenceType((TypeReference)ins.Operand)); break;
            case Code.Newarr:
                Pop();
                if (IsTile((TypeReference)ins.Operand)) Note(ins, "newarr Tile");
                Push(new ArrayType((TypeReference)ins.Operand));
                break;
            case Code.Unbox_Any: case Code.Ldobj:
                Pop();
                if (IsTile((TypeReference)ins.Operand)) Note(ins, $"{ins.OpCode} Tile");
                Push((TypeReference)ins.Operand);
                break;
            case Code.Ldtoken:
                if (ins.Operand is TypeReference tt && IsTile(tt)) Note(ins, "ldtoken Tile");
                Push(null);
                break;
            case Code.Constrained:
                if (IsTile((TypeReference)ins.Operand)) Note(ins, "constrained. Tile");
                break;
            case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un:
            case Code.Beq: case Code.Bne_Un: case Code.Bge: case Code.Bge_Un: case Code.Bgt: case Code.Bgt_Un:
            case Code.Ble: case Code.Ble_Un: case Code.Blt: case Code.Blt_Un:
            {
                Val b = Pop(), a = Pop();
                if (IsTile(a.Type)) Expect(ins, b, a.Type);
                if (IsTile(b.Type)) Expect(ins, a, b.Type);
                if ((IsTile(a.Type) && b.Type != null && !IsTile(b.Type)) || (IsTile(b.Type) && a.Type != null && !IsTile(a.Type)))
                    Note(ins, $"a Tile compared with a {(IsTile(a.Type) ? b.Type : a.Type).FullName}");
                if (ins.OpCode.FlowControl != FlowControl.Cond_Branch) Push(null);
                break;
            }
            case Code.Leave: s.Clear(); break;
            default:
            {
                int pops = Pops(ins, s.Count);
                for (int i = 0; i < pops; i++)
                {
                    var v = Pop();
                    if (IsTile(v.Type) && ins.OpCode.Code != Code.Pop && ins.OpCode.Code != Code.Brtrue && ins.OpCode.Code != Code.Brfalse)
                        Note(ins, $"a Tile taken by {ins.OpCode}");
                }
                int pushes = Pushes(ins);
                for (int i = 0; i < pushes; i++) Push(null);
                break;
            }
        }
        var after = s.ToArray();
        switch (ins.OpCode.FlowControl)
        {
            case FlowControl.Branch:
                Flow((Instruction)ins.Operand, after);
                break;
            case FlowControl.Cond_Branch:
                if (ins.Operand is Instruction[] targets)
                    foreach (var t in targets) Flow(t, (Val[])after.Clone());
                else
                    Flow((Instruction)ins.Operand, (Val[])after.Clone());
                Flow(ins.Next, after);
                break;
            case FlowControl.Return: case FlowControl.Throw:
                break;
            default:
                Flow(ins.Next, after);
                break;
        }
    }
}
