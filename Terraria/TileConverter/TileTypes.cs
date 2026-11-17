// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch, MIT), as it is there, except where marked "(FNAPatches)".
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

static partial class TilePtr
{
    // Tile's fields, in the order of its record: 14 bytes with no padding
    static readonly string[] Record =
    {
        "type UInt16", "wall UInt16", "liquid Byte", "sTileHeader UInt16", "bTileHeader Byte", "bTileHeader2 Byte",
        "bTileHeader3 Byte", "frameX Int16", "frameY Int16",
    };
    const int RecordSize = 14;

    // does the type mention Tile other than as a pointer to it or as the grid's element
    static bool Bare(TypeReference t)
    {
        switch (t)
        {
            case null: return false;
            case PointerType p: return !TileSim.IsTile(p.ElementType) && Bare(p.ElementType);
            case ArrayType a: return !TileSim.IsTile(a.ElementType) && Bare(a.ElementType);
            case GenericInstanceType g: return g.GenericArguments.Any(Bare);
            case TypeSpecification s: return Bare(s.ElementType);
            default: return TileSim.IsTile(t);
        }
    }

    // Every Tile of a signature becomes a Tile*: fields, parameters, returns, properties, locals and
    // the references to them in the code. A Tile as a generic argument (Func<Tile, bool>) becomes a
    // native int, which a pointer is where it is passed. Then Tile itself becomes the value its fields
    // make. Returns the number of changes.
    static int Retype(ModuleDefinition m, TypeDefinition tile)
    {
        var fields = tile.Fields.Where(f => !f.IsStatic).Select(f => $"{f.Name} {f.FieldType.MetadataType}").ToArray();
        if (!fields.SequenceEqual(Record))
            throw new InvalidOperationException("Terraria.Tile's fields are not the known ones: " + string.Join(", ", fields));
        if (tile.Interfaces.Count > 0 || tile.HasGenericParameters || tile.BaseType.FullName != "System.Object")
            throw new InvalidOperationException("Terraria.Tile is not a plain class");
        foreach (var type in AllTypes(m))
            if (Bare(type.BaseType) || type.Interfaces.Any(i => Bare(i.InterfaceType)))
                throw new InvalidOperationException($"{type.FullName} derives from a type made of Tile");

        int changes = 0;
        var ptr = new PointerType(tile);
        TypeReference Map(TypeReference t)
        {
            switch (t)
            {
                case null: case GenericParameter _: case PointerType _:
                    return t;
                case ByReferenceType b:
                {
                    var e = Map(b.ElementType);
                    return e == b.ElementType ? t : new ByReferenceType(e);
                }
                case ArrayType a:
                {
                    if (TileSim.IsTile(a.ElementType))
                        return t;
                    var e = Map(a.ElementType);
                    if (e == a.ElementType)
                        return t;
                    var n = new ArrayType(e);
                    n.Dimensions.Clear();
                    foreach (var d in a.Dimensions) n.Dimensions.Add(d);
                    return n;
                }
                case GenericInstanceType g:
                    // (in place: the references to the type's members hold this same object)
                    for (int i = 0; i < g.GenericArguments.Count; i++)
                    {
                        var arg = g.GenericArguments[i];
                        var to = TileSim.IsTile(arg) ? m.TypeSystem.IntPtr : Map(arg);
                        if (to != arg)
                        {
                            g.GenericArguments[i] = to;
                            changes++;
                        }
                    }
                    return t;
                case TypeSpecification s:
                    if (Bare(s))
                        throw new InvalidOperationException($"a Tile in {t.FullName}");
                    return t;
                default:
                    if (!TileSim.IsTile(t))
                        return t;
                    changes++;
                    return ptr;
            }
        }
        var seen = new HashSet<object>();
        void MapMethod(MethodReference r)
        {
            if (r == null || r is MethodDefinition || !seen.Add(r))
                return;
            if (r is GenericInstanceMethod gm)
            {
                for (int i = 0; i < gm.GenericArguments.Count; i++)
                {
                    var arg = gm.GenericArguments[i];
                    var to = TileSim.IsTile(arg) ? m.TypeSystem.IntPtr : Map(arg);
                    if (to != arg)
                    {
                        gm.GenericArguments[i] = to;
                        changes++;
                    }
                }
                MapMethod(gm.ElementMethod);
                return;
            }
            if (!TileSim.IsTile(r.DeclaringType))
                r.DeclaringType = Map(r.DeclaringType);
            r.ReturnType = Map(r.ReturnType);
            foreach (var p in r.Parameters)
                p.ParameterType = Map(p.ParameterType);
        }

        foreach (var type in AllTypes(m))
        {
            foreach (var f in type.Fields)
                f.FieldType = Map(f.FieldType);
            foreach (var p in type.Properties)
                p.PropertyType = Map(p.PropertyType);
            foreach (var method in type.Methods)
            {
                method.ReturnType = Map(method.ReturnType);
                foreach (var p in method.Parameters)
                    p.ParameterType = Map(p.ParameterType);
                foreach (var o in method.Overrides)
                    MapMethod(o);
                if (!method.HasBody)
                    continue;
                foreach (var v in method.Body.Variables)
                    v.VariableType = Map(v.VariableType);
                foreach (var ins in method.Body.Instructions)
                    switch (ins.Operand)
                    {
                        case TypeDefinition _: case FieldDefinition _: case MethodDefinition _:
                            break;
                        case TypeReference t:
                            ins.Operand = Map(t);
                            break;
                        case FieldReference f:
                            if (seen.Add(f))
                            {
                                if (!TileSim.IsTile(f.DeclaringType))
                                    f.DeclaringType = Map(f.DeclaringType);
                                f.FieldType = Map(f.FieldType);
                            }
                            break;
                        case MethodReference r:
                            MapMethod(r);
                            break;
                        case CallSite c:
                            c.ReturnType = Map(c.ReturnType);
                            foreach (var p in c.Parameters)
                                p.ParameterType = Map(p.ParameterType);
                            break;
                    }
            }
        }

        // nothing may still hold a Tile by value: a field of 14 bytes where the code stores a pointer
        var left = new List<string>();
        foreach (var type in AllTypes(m))
        {
            foreach (var f in type.Fields.Where(f => Bare(f.FieldType)))
                left.Add(f.FullName);
            foreach (var method in type.Methods)
            {
                if (Bare(method.ReturnType) || method.Parameters.Any(p => Bare(p.ParameterType)))
                    left.Add(method.FullName);
                if (!method.HasBody)
                    continue;
                if (method.Body.Variables.Any(v => Bare(v.VariableType)))
                    left.Add(method.FullName + ": a local");
                foreach (var ins in method.Body.Instructions)
                {
                    bool bad;
                    switch (ins.Operand)
                    {
                        case TypeReference t: bad = Bare(t); break;
                        case FieldReference f: bad = Bare(f.FieldType); break;
                        case GenericInstanceMethod gm: bad = gm.GenericArguments.Any(Bare); break;
                        case MethodReference r: bad = Bare(r.ReturnType) || r.Parameters.Any(p => Bare(p.ParameterType)); break;
                        default: bad = false; break;
                    }
                    if (bad)
                        left.Add($"{method.FullName} IL_{ins.Offset:x4}: {ins.OpCode} {ins.Operand}");
                }
            }
        }
        if (left.Count > 0)
            throw new InvalidOperationException("a Tile is left by value:\n  " + string.Join("\n  ", left.Distinct()));

        // the value: its fields one after the other, no padding, and no object's methods to dispatch
        foreach (var method in tile.Methods.Where(x => x.IsVirtual && x.IsNewSlot))
        {
            Console.Error.WriteLine($"tile: {method.Name} was virtual");
            method.IsVirtual = false;
            method.IsNewSlot = false;
        }
        tile.BaseType = new TypeReference("System", "ValueType", m, m.TypeSystem.CoreLibrary);
        tile.Attributes = (tile.Attributes & ~(TypeAttributes.LayoutMask | TypeAttributes.Abstract))
            | TypeAttributes.SequentialLayout | TypeAttributes.Sealed;
        tile.PackingSize = 1;
        tile.ClassSize = RecordSize;
        if (!tile.IsValueType)
            throw new InvalidOperationException("Terraria.Tile did not become a value");
        return changes + 1;
    }
}
