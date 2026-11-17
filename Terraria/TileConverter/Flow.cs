// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Flow.cs, MIT), except where marked (FNAPatches).
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

// A routine's code cut into blocks, with the places where a local is written whole: a store; the
// address taken to clear the local; the address taken as the place a constructor of the local's
// own type builds in (nothing else of the local is read between: a walk in order meets such a read
// first). For the passes that reason about locals (Inits, Split). Macros simplified.
sealed class Flow
{
    public readonly Collection<Instruction> Code;
    public readonly int N;
    public readonly Dictionary<Instruction, int> At;
    public readonly int[] BlockOf;
    public readonly List<int> Starts = new List<int>();
    public readonly int[] Writes;     // the local written whole by an instruction, or -1
    public readonly bool[] Form;      // an address taken only to write its local whole
    public int Blocks => Starts.Count;
    public int End(int b) => b + 1 < Starts.Count ? Starts[b + 1] : N;

    public Flow(MethodBody body)
    {
        Code = body.Instructions;
        var code = Code;
        int n = N = code.Count;
        At = new Dictionary<Instruction, int>(n);
        for (int q = 0; q < n; q++)
            At[code[q]] = q;

        // blocks
        var leader = new bool[n + 1];
        leader[0] = true;
        for (int q = 0; q < n; q++)
        {
            var i = code[q];
            if (i.Operand is Instruction t)
                leader[At[t]] = true;
            else if (i.Operand is Instruction[] ts)
                foreach (var x in ts)
                    leader[At[x]] = true;
            var flow = i.OpCode.FlowControl;
            if (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch || flow == FlowControl.Return || flow == FlowControl.Throw)
                leader[q + 1] = true;
        }
        foreach (var h in body.ExceptionHandlers)
            foreach (var x in new[] { h.TryStart, h.TryEnd, h.HandlerStart, h.HandlerEnd, h.FilterStart })
                if (x != null)
                    leader[At[x]] = true;
        BlockOf = new int[n];
        for (int q = 0; q < n; q++)
        {
            if (leader[q])
                Starts.Add(q);
            BlockOf[q] = Starts.Count - 1;
        }

        // the writes
        Writes = new int[n];
        Form = new bool[n];
        for (int q = 0; q < n; q++)
            Writes[q] = -1;
        for (int q = 0; q < n; q++)
        {
            var i = code[q];
            if (i.OpCode == OpCodes.Stloc)
                Writes[q] = ((VariableDefinition)i.Operand).Index;
            else if (i.OpCode == OpCodes.Ldloca)
            {
                var v = (VariableDefinition)i.Operand;
                int end = End(BlockOf[q]);
                if (q + 1 < end && code[q + 1].OpCode == OpCodes.Initobj
                    && ((TypeReference)code[q + 1].Operand).FullName == v.VariableType.FullName)
                {
                    Writes[q + 1] = v.Index;
                    Form[q] = true;
                    continue;
                }
                int depth = 1;
                for (int j = q + 1; j < end; j++)
                {
                    var x = code[j];
                    int pops = Rests.Pops(x);
                    if (pops >= depth)
                    {
                        if (pops == depth && x.OpCode == OpCodes.Call && x.Operand is MethodReference r && r.Name == ".ctor" && r.HasThis
                            && v.VariableType.IsValueType && r.DeclaringType.FullName == v.VariableType.FullName && Writes[j] == -1
                            && Inits.Whole(v.VariableType))
                        {
                            Writes[j] = v.Index;
                            Form[q] = true;
                        }
                        break;
                    }
                    if (x.OpCode.FlowControl != FlowControl.Next && x.OpCode.FlowControl != FlowControl.Call)
                        break;
                    depth += Rests.Pushes(x) - pops;
                }
            }
        }
    }
}
