// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Guard.cs, MIT).
using System;
using System.Globalization;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The sums the passes that check the game's version take of its code (TileCachePatch, Rests,
// Settles): an instruction as text that Mono and .NET write alike. Instruction.ToString formats
// numbers by the runtime's own rules, and under .NET the sums differed from Mono's, so a patcher
// run there left those passes out without a word.
static class Guard
{
    public static ulong Sum(ulong h, Instruction i)
    {
        foreach (char c in Text(i))
            h = (h ^ c) * 1099511628211;
        return h;
    }

    static string Text(Instruction i)
    {
        var s = new StringBuilder();
        s.Append(i.Offset.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(i.OpCode.Name);
        switch (i.Operand)
        {
            case null:
                break;
            case Instruction t:
                s.Append(' ').Append(t.Offset.ToString(CultureInfo.InvariantCulture));
                break;
            case Instruction[] ts:
                foreach (var t in ts)
                    s.Append(' ').Append(t.Offset.ToString(CultureInfo.InvariantCulture));
                break;
            case float f:
                s.Append(' ').Append(BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8", CultureInfo.InvariantCulture));
                break;
            case double d:
                s.Append(' ').Append(BitConverter.DoubleToInt64Bits(d).ToString("x16", CultureInfo.InvariantCulture));
                break;
            case IFormattable n:
                s.Append(' ').Append(n.ToString(null, CultureInfo.InvariantCulture));
                break;
            case string text:
                s.Append(" \"").Append(text).Append('"');
                break;
            case MemberReference r:
                s.Append(' ').Append(r.FullName);
                break;
            case VariableDefinition v:
                s.Append(" V").Append(v.Index.ToString(CultureInfo.InvariantCulture));
                break;
            case ParameterDefinition p:
                s.Append(" A").Append(p.Index.ToString(CultureInfo.InvariantCulture));
                break;
            case CallSite c:
                s.Append(' ').Append(c.FullName);
                break;
            default:
                s.Append(' ').Append(i.Operand.GetType().Name);
                break;
        }
        return s.ToString();
    }
}
