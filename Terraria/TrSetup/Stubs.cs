// From wild1walker's PortMaster Terraria port (portmaster-terraria, MIT: tools/trpatch/Stubs.cs), copied as it is.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The game names Windows types in code it runs only on Windows (System.Windows.Forms, System.Drawing's
// bitmaps). Mono has them. Microsoft's .NET does not, and it loads what a method names when it compiles
// the method. `trpatch stubs <folder> <framework folder> <file>...`: every type the framework lacks gets an empty
// stand-in in TrStubs.dll, with the members the game names, and the game's references point at it.
static class Stubs
{
    sealed class Strict : IAssemblyResolver
    {
        readonly string[] dirs;
        readonly Dictionary<string, AssemblyDefinition> found = new Dictionary<string, AssemblyDefinition>();
        public Strict(params string[] dirs) { this.dirs = dirs; }
        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            if (found.TryGetValue(name.Name, out var a))
                return a ?? throw new AssemblyResolutionException(name);
            foreach (var dir in dirs)
                foreach (var ext in new[] { ".dll", ".exe" })
                {
                    string path = Path.Combine(dir, name.Name + ext);
                    if (File.Exists(path))
                        return found[name.Name] = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = this, InMemory = true });
                }
            found[name.Name] = null;
            throw new AssemblyResolutionException(name);
        }
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters p) => Resolve(name);
        public void Dispose() { }
    }

    static string Key(TypeReference t) => t.DeclaringType != null ? Key(t.DeclaringType) + "/" + t.Name : t.Namespace + "." + t.Name;

    public static int Run(string dir, string framework, string[] files)
    {
        var resolver = new Strict(framework, dir);
        var modules = files.Select(f => ModuleDefinition.ReadModule(Path.Combine(dir, f), new ReaderParameters
            { AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate })).ToList();
        var stubs = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("TrStubs", new Version(1, 0, 0, 0)), "TrStubs", ModuleKind.Dll);
        var sm = stubs.MainModule;
        var obj = sm.TypeSystem.Object;

        // what is missing: the type references that end nowhere in the framework
        var missing = new Dictionary<string, List<TypeReference>>();
        var known = new Dictionary<string, bool>();
        bool Missing(TypeReference t)
        {
            if (t.DeclaringType != null)
                return Missing(t.DeclaringType);
            if (t is TypeDefinition || t.Scope is ModuleDefinition)
                return false;
            string key = t.Scope.Name + ":" + Key(t);
            if (!known.TryGetValue(key, out bool gone))
            {
                try { gone = t.Resolve() == null; }
                catch (AssemblyResolutionException) { gone = true; }
                known[key] = gone;
            }
            return gone;
        }
        var interfaces = new HashSet<string>();
        var bases = new HashSet<string>();
        foreach (var m in modules)
        {
            foreach (var t in m.GetTypeReferences())
                if (Missing(t))
                {
                    if (!missing.TryGetValue(Key(t), out var list))
                        missing[Key(t)] = list = new List<TypeReference>();
                    list.Add(t);
                }
            foreach (var t in All(m))
            {
                foreach (var i in t.Interfaces)
                    interfaces.Add(Key(i.InterfaceType.GetElementType()));
                if (t.BaseType != null)
                    bases.Add(Key(t.BaseType.GetElementType()));
            }
        }
        if (missing.Count == 0)
        {
            Console.Error.WriteLine("stubs: nothing is missing");
            return 0;
        }

        // members named on the missing types, and which fields are static
        var members = modules.SelectMany(m => m.GetMemberReferences())
            .Where(r => missing.ContainsKey(Key(r.DeclaringType.GetElementType()))).ToList();
        var statics = new HashSet<MemberReference>();
        foreach (var m in modules)
            foreach (var method in All(m).SelectMany(t => t.Methods).Where(x => x.HasBody))
                foreach (var ins in method.Body.Instructions)
                    if ((ins.OpCode == OpCodes.Ldsfld || ins.OpCode == OpCodes.Stsfld || ins.OpCode == OpCodes.Ldsflda)
                        && ins.Operand is FieldReference f)
                        statics.Add(f);
        var withMembers = new HashSet<string>(members.Select(r => Key(r.DeclaringType.GetElementType())));

        // the stand-ins: an interface where the game implements it, an enum where it is a value with
        // no members, else a struct or a class with a constructor
        var made = new Dictionary<string, TypeDefinition>();
        TypeDefinition Stub(TypeReference t)
        {
            string key = Key(t);
            if (made.TryGetValue(key, out var d))
                return d;
            bool value = missing[key].Any(r => r.IsValueType), nested = t.DeclaringType != null;
            var vis = nested ? TypeAttributes.NestedPublic : TypeAttributes.Public;
            if (interfaces.Contains(key))
                d = new TypeDefinition(t.Namespace, t.Name, vis | TypeAttributes.Interface | TypeAttributes.Abstract);
            else if (value && !withMembers.Contains(key))
            {
                d = new TypeDefinition(t.Namespace, t.Name, vis | TypeAttributes.Sealed, sm.ImportReference(typeof(Enum)));
                d.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, sm.TypeSystem.Int32));
            }
            else if (value)
                d = new TypeDefinition(t.Namespace, t.Name, vis | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, sm.ImportReference(typeof(ValueType)));
            else
            {
                d = new TypeDefinition(t.Namespace, t.Name, vis | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, obj);
                var ctor = new MethodDefinition(".ctor", MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, sm.TypeSystem.Void);
                var il = ctor.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, new MethodReference(".ctor", sm.TypeSystem.Void, obj) { HasThis = true });
                il.Emit(OpCodes.Ret);
                d.Methods.Add(ctor);
            }
            made[key] = d;
            if (nested) Stub(t.DeclaringType).NestedTypes.Add(d);
            else sm.Types.Add(d);
            return d;
        }
        TypeReference Map(TypeReference t)
        {
            switch (t)
            {
                case ByReferenceType b: return new ByReferenceType(Map(b.ElementType));
                case PointerType p: return new PointerType(Map(p.ElementType));
                case ArrayType a: return new ArrayType(Map(a.ElementType), a.Rank);
                case GenericInstanceType g:
                {
                    var n = new GenericInstanceType(Map(g.ElementType));
                    foreach (var arg in g.GenericArguments) n.GenericArguments.Add(Map(arg));
                    return n;
                }
                case GenericParameter _: case TypeSpecification _:
                    throw new InvalidOperationException($"stubs: {t.FullName} in a member of a missing type");
                default:
                    return missing.ContainsKey(Key(t)) && Missing(t) ? Stub(t) : sm.ImportReference(t);
            }
        }
        foreach (var list in missing.Values)
            Stub(list[0]);
        var seen = new HashSet<string>();
        foreach (var r in members)
        {
            var d = made[Key(r.DeclaringType.GetElementType())];
            if (r.DeclaringType is GenericInstanceType || !seen.Add(d.FullName + "::" + r.FullName))
            {
                if (r.DeclaringType is GenericInstanceType)
                    throw new InvalidOperationException($"stubs: {r.FullName}: a generic missing type");
                continue;
            }
            if (r is FieldReference f)
            {
                d.Fields.Add(new FieldDefinition(f.Name, FieldAttributes.Public | (statics.Contains(f) ? FieldAttributes.Static : 0), Map(f.FieldType)));
                continue;
            }
            var mr = (MethodReference)r;
            if (mr.HasGenericParameters || mr is GenericInstanceMethod)
                throw new InvalidOperationException($"stubs: {r.FullName}: a generic method of a missing type");
            bool ctor = mr.Name == ".ctor";
            if (ctor && !mr.HasParameters && d.Methods.Any(x => x.Name == ".ctor"))
            {
                d.Methods.Single(x => x.Name == ".ctor").IsPublic = true;
                continue;
            }
            var attrs = MethodAttributes.Public | MethodAttributes.HideBySig;
            if (!mr.HasThis) attrs |= MethodAttributes.Static;
            if (ctor) attrs |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            if (d.IsInterface) attrs |= MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot;
            var def = new MethodDefinition(mr.Name, attrs, Map(mr.ReturnType));
            foreach (var p in mr.Parameters)
                def.Parameters.Add(new ParameterDefinition(Map(p.ParameterType)));
            if (!d.IsInterface)
            {
                var il = def.Body.GetILProcessor();
                // code access security: .NET has none, and every demand passes (a library asks
                // before it uses what it may use anyway)
                if (d.Namespace.StartsWith("System.Security") && def.ReturnType.MetadataType == MetadataType.Void)
                    il.Emit(OpCodes.Ret);
                else
                {
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Throw);
                }
            }
            d.Methods.Add(def);
        }
        foreach (var key in made.Keys.OrderBy(k => k))
            Console.Error.WriteLine($"stubs: {key}{(made[key].IsInterface ? " (interface)" : made[key].IsEnum ? " (enum)" : made[key].IsValueType ? " (struct)" : "")}"
                + $"{(bases.Contains(key) ? " (a base of a game type)" : "")}: {made[key].Methods.Count + made[key].Fields.Count} members");

        // members the framework lacks on types it has: not stubbed, named for a look
        foreach (var m in modules)
            foreach (var r in m.GetMemberReferences())
            {
                var t = r.DeclaringType.GetElementType();
                if (missing.ContainsKey(Key(t)) || t is TypeDefinition || r.DeclaringType is ArrayType)
                    continue;
                bool gone;
                try { gone = r.Resolve() == null; }
                catch (Exception) { gone = true; }
                if (gone)
                    Console.Error.WriteLine($"stubs: the framework has no {r.FullName} ({m.Name})");
            }

        // the game's references move to the stand-ins
        for (int i = 0; i < modules.Count; i++)
        {
            var m = modules[i];
            var to = new AssemblyNameReference("TrStubs", new Version(1, 0, 0, 0));
            int moved = 0;
            foreach (var t in m.GetTypeReferences().ToList())
                if (t.DeclaringType == null && Missing(t))
                {
                    if (moved++ == 0) m.AssemblyReferences.Add(to);
                    t.Scope = to;
                }
            var used = new HashSet<IMetadataScope>(m.GetTypeReferences().Select(t => t.Scope));
            foreach (var a in m.AssemblyReferences.Where(a => !used.Contains(a) && a.Name.StartsWith("System.Windows")).ToList())
                m.AssemblyReferences.Remove(a);
            if (moved == 0)
                continue;
            string path = Path.Combine(dir, files[i]);
            m.Write(path + ".tmp");
            File.Delete(path);
            File.Move(path + ".tmp", path);
            Console.Error.WriteLine($"stubs: {files[i]}: {moved} types moved");
        }
        stubs.Write(Path.Combine(dir, "TrStubs.dll"));
        return 0;
    }

    static IEnumerable<TypeDefinition> All(ModuleDefinition m)
    {
        var stack = new Stack<TypeDefinition>(m.Types);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            foreach (var n in t.NestedTypes) stack.Push(n);
            yield return t;
        }
    }
}
