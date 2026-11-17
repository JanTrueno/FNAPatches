// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Rests.cs, MIT): its stack helpers only (FNAPatches).
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Rests
{
    internal static int Pops(Instruction i)
    {
        if (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
        {
            var r = (MethodReference)i.Operand;
            return r.Parameters.Count + (r.HasThis && i.OpCode != OpCodes.Newobj ? 1 : 0);
        }
        switch (i.OpCode.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi:
                return 2;
            case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref:
                return 3;
            default: return 99;
        }
    }

    internal static int Pushes(Instruction i)
    {
        if (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            return ((MethodReference)i.Operand).ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
        switch (i.OpCode.StackBehaviourPush)
        {
            case StackBehaviour.Push0: return 0;
            case StackBehaviour.Push1_push1: return 2;
            default: return 1;
        }
    }

}
