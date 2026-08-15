#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace MelonLoader.Hosting
{
    /// <summary>
    /// Ensures the BepInEx game interop assembly ships <c>Il2Cpp.*</c> alias types so
    /// MelonLoader mods (which reference Il2Cpp-prefixed game types in their TypeRefs and
    /// Harmony attribute blobs) load <b>verbatim</b>, without ever rewriting the mods.
    /// <para>
    /// BepInEx 6's Il2CppInterop keeps the original namespaces (e.g. <c>EntityLocation</c>),
    /// while MelonLoader mods are compiled against MelonLoader's interop which prefixes game
    /// types with <c>Il2Cpp.</c>. This injector rewrites the interop assembly (not the mods)
    /// by cloning every game type the installed mods reference into an <c>Il2Cpp.*</c>
    /// namespace. Idempotent: if the interop already has the aliases it does nothing.
    /// </para>
    /// <para>
    /// Because BepInEx preloads interop assemblies before plugins run, a freshly installed
    /// alias interop takes effect on the next game launch. Callers should tell the user to
    /// restart when <see cref="EnsureAliases"/> returns <c>true</c>.
    /// </para>
    /// </summary>
    public static class Il2CppInteropAliasInjector
    {
        /// <summary>
        /// Ensures <paramref name="interopPath"/> contains <c>Il2Cpp.*</c> aliases for every
        /// game type referenced by the assemblies under <paramref name="modsDirs"/>.
        /// Returns <c>true</c> if the file was rewritten (restart required to take effect).
        /// </summary>
        public static bool EnsureAliases(string interopPath, params string[] modsDirs)
        {
            try
            {
                if (string.IsNullOrEmpty(interopPath) || !File.Exists(interopPath))
                    return false;

                var gen = new AliasGenerator(interopPath);
                return gen.Run(modsDirs);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BepInExHost] interop alias injection failed: {ex.Message}");
                return false;
            }
        }

        private sealed class AliasGenerator
        {
            private readonly ModuleDefMD _mod;
            private readonly string _interopPath;
            private readonly Dictionary<string, TypeDef> _origByFull = new();
            private readonly Dictionary<string, TypeDef> _aliasByOrig = new();
            private readonly Dictionary<string, List<TypeDef>> _nestedByParent = new();
            private HashSet<string> _collected = new();

            internal AliasGenerator(string interopPath)
            {
                _interopPath = interopPath;
                _mod = ModuleDefMD.Load(interopPath);
            }

            internal bool Run(IReadOnlyList<string> modsDirs)
            {
                // Note: always regenerate from the CURRENT installed mods, so that adding
                // a new mod (which may reference game types not aliased before) is picked
                // up on the next launch. Regeneration is idempotent; if the alias set is
                // unchanged the rewritten file is identical in content.

                // Index original types + nesting.
                foreach (var t in _mod.GetTypes())
                {
                    _origByFull[t.FullName] = t;
                    if (t.IsNested && t.DeclaringType != null)
                    {
                        if (!_nestedByParent.TryGetValue(t.DeclaringType.FullName, out var l))
                            _nestedByParent[t.DeclaringType.FullName] = l = new List<TypeDef>();
                        l.Add(t);
                    }
                }

                // Collect game-type references from the installed mods.
                var seeds = new HashSet<string>();
                if (modsDirs != null)
                    foreach (var dir in modsDirs)
                        CollectFromMods(seeds, dir);
                if (seeds.Count == 0)
                    return false;

                // Closure collection (read-only).
                _collected = new HashSet<string>();
                foreach (var s in seeds)
                {
                    if (_origByFull.TryGetValue(s, out var td))
                        CollectType(td);
                }

                // Clone collected types into Il2Cpp.* aliases.
                foreach (var full in _collected.OrderBy(x => x))
                {
                    if (_origByFull.TryGetValue(full, out var td))
                        EnsureAlias(td);
                }
                if (_aliasByOrig.Count == 0)
                    return false;

                // Write to a temp file first: _mod was memory-mapped from _interopPath,
                // so writing back to the same file is blocked by the mapping.
                var tmpPath = _interopPath + ".aliastmp" + Guid.NewGuid().ToString("N");
                try
                {
                    _mod.Write(tmpPath);
                    File.Move(tmpPath, _interopPath, true);
                }
                finally
                {
                    if (File.Exists(tmpPath))
                        File.Delete(tmpPath);
                }
                return true;
            }

            // ---- seed collection from mod assemblies ----
            private void CollectFromMods(HashSet<string> names, string dir)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                        return;
                    foreach (var file in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var md = ModuleDefMD.Load(file);
                            foreach (var tr in md.GetTypeRefs())
                                if (IsGameType(tr))
                                    names.Add(ToOrigFullName(tr));
                            foreach (var t in md.GetTypes())
                            {
                                CollectAttrs(names, t.CustomAttributes);
                                foreach (var m in t.Methods) CollectAttrs(names, m.CustomAttributes);
                                foreach (var f in t.Fields) CollectAttrs(names, f.CustomAttributes);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            private void CollectAttrs(HashSet<string> names, IList<CustomAttribute> attrs)
            {
                foreach (var attr in attrs)
                {
                    try
                    {
                        foreach (var a in attr.ConstructorArguments) CollectArg(names, a);
                        foreach (var n in attr.NamedArguments) CollectArg(names, n.Argument);
                    }
                    catch { }
                }
            }

            private void CollectArg(HashSet<string> names, CAArgument arg)
            {
                if (arg.Type != null && arg.Type.FullName == "System.Type" && arg.Value is ITypeDefOrRef tr)
                {
                    if (IsGameType(tr))
                        names.Add(ToOrigFullName(tr));
                }
                else if (arg.Value is CAArgument nested) CollectArg(names, nested);
                else if (arg.Value is IList<CAArgument> arr)
                    foreach (var e in arr) CollectArg(names, e);
            }

            // ---- closure collection ----
            private void CollectType(TypeDef td)
            {
                var full = td.FullName;
                if (!_collected.Add(full)) return;
                if (td.BaseType != null) CollectTypeRef(td.BaseType);
                foreach (var i in td.Interfaces) CollectTypeRef(i.Interface);
                foreach (var gp in td.GenericParameters)
                    foreach (var c in gp.GenericParamConstraints) CollectTypeRef(c.Constraint);
                foreach (var f in td.Fields) CollectTypeRef(f.FieldType);
                foreach (var m in td.Methods)
                {
                    CollectTypeRef(m.ReturnType);
                    foreach (var p in m.Parameters) CollectTypeRef(p.Type);
                    foreach (var gp in m.GenericParameters)
                        foreach (var c in gp.GenericParamConstraints) CollectTypeRef(c.Constraint);
                    if (m.HasBody)
                    {
                        foreach (var v in m.Body.Variables) CollectTypeRef(v.Type);
                        foreach (var ins in m.Body.Instructions) CollectOperand(ins.Operand);
                    }
                }
                if (_nestedByParent.TryGetValue(full, out var nts))
                    foreach (var nt in nts) CollectType(nt);
            }

            private void CollectTypeRef(ITypeDefOrRef t)
            {
                if (t == null) return;
                if (t is TypeSpec ts2) { CollectTypeRef(ts2.TypeSig); return; }
                if (t is GenericSig) return;
                if (IsGameType(t))
                {
                    var full = ToOrigFullName(t);
                    if (_origByFull.TryGetValue(full, out var td))
                        CollectType(td);
                }
            }

            private void CollectTypeRef(TypeSig ts)
            {
                if (ts == null) return;
                switch (ts)
                {
                    case ClassOrValueTypeSig covt: CollectTypeRef(covt.TypeDefOrRef); break;
                    case GenericInstSig gis:
                        CollectTypeRef(gis.GenericType);
                        foreach (var ga in gis.GenericArguments) CollectTypeRef(ga);
                        break;
                    case SZArraySig sa: CollectTypeRef(sa.Next); break;
                    case ArraySig arr: CollectTypeRef(arr.Next); break;
                    case PtrSig pt: CollectTypeRef(pt.Next); break;
                    case ByRefSig br: CollectTypeRef(br.Next); break;
                    case PinnedSig pn: CollectTypeRef(pn.Next); break;
                }
            }

            private void CollectOperand(object op)
            {
                switch (op)
                {
                    case IMethod m:
                        CollectTypeRef(m.DeclaringType.ToTypeSig());
                        if (m is MethodSpec ms && ms.GenericInstMethodSig != null)
                            foreach (var ga in ms.GenericInstMethodSig.GenericArguments) CollectTypeRef(ga);
                        break;
                    case IField f:
                        CollectTypeRef(f.DeclaringType.ToTypeSig());
                        break;
                    case ITypeDefOrRef t:
                        CollectTypeRef(t.ToTypeSig());
                        break;
                }
            }

            // ---- alias creation ----
            private TypeDef EnsureAlias(TypeDef orig)
            {
                if (_aliasByOrig.TryGetValue(orig.FullName, out var existing))
                    return existing;

                TypeDef alias;
                if (orig.IsNested)
                {
                    var parent = EnsureAlias(orig.DeclaringType);
                    alias = new TypeDefUser(string.Empty, orig.Name);
                    alias.Attributes = orig.Attributes;
                    parent.NestedTypes.Add(alias);
                }
                else
                {
                    alias = new TypeDefUser("Il2Cpp", orig.Name);
                    alias.Attributes = orig.Attributes;
                    _mod.Types.Add(alias);
                }
                _aliasByOrig[orig.FullName] = alias;
                CloneRecursive(orig, alias);
                return alias;
            }

            private void CloneRecursive(TypeDef orig, TypeDef alias)
            {
                if (orig.BaseType != null)
                    alias.BaseType = MapType(orig.BaseType);
                foreach (var i in orig.Interfaces)
                    alias.Interfaces.Add(new InterfaceImplUser(MapType(i.Interface)));
                foreach (var gp in orig.GenericParameters)
                {
                    var ngp = new GenericParamUser(gp.Number, gp.Flags, gp.Name);
                    foreach (var c in gp.GenericParamConstraints)
                        ngp.GenericParamConstraints.Add(new GenericParamConstraintUser(MapType(c.Constraint)));
                    alias.GenericParameters.Add(ngp);
                }
                foreach (var f in orig.Fields)
                {
                    var nf = new FieldDefUser(f.Name, new FieldSig(MapType(f.FieldType)), f.Attributes);
                    if (f.HasConstant) nf.Constant = f.Constant;
                    alias.Fields.Add(nf);
                }
                foreach (var m in orig.Methods)
                {
                    try
                    {
                        alias.Methods.Add(CreateMethodClone(m));
                    }
                    catch
                    {
                        // skip a method we can't clone (bad signature etc.)
                    }
                }
                if (_nestedByParent.TryGetValue(orig.FullName, out var nts))
                    foreach (var nt in nts)
                        EnsureAlias(nt);
            }

            private MethodDef CreateMethodClone(MethodDef m)
            {
                var ret = MapType(m.ReturnType);
                MethodSig sig;
                if (m.IsStatic)
                    sig = MethodSig.CreateStatic(ret);
                else
                    sig = MethodSig.CreateInstance(ret);
                sig.HasThis = m.HasThis;
                sig.ExplicitThis = m.ExplicitThis;
                sig.CallingConvention = m.MethodSig.CallingConvention;
                var nm = new MethodDefUser(m.Name, sig, m.Attributes)
                {
                    ImplAttributes = m.ImplAttributes,
                };
                foreach (var gp in m.GenericParameters)
                {
                    var ngp = new GenericParamUser(gp.Number, gp.Flags, gp.Name);
                    foreach (var c in gp.GenericParamConstraints)
                        ngp.GenericParamConstraints.Add(new GenericParamConstraintUser(MapType(c.Constraint)));
                    nm.GenericParameters.Add(ngp);
                }
                foreach (var p in m.Parameters)
                {
                    if (p.Index == 0 && !m.IsStatic) continue;
                    var pdef = new ParamDefUser();
                    pdef.Name = p.Name;
                    pdef.Sequence = (ushort)(nm.ParamDefs.Count + 1);
                    nm.ParamDefs.Add(pdef);
                    sig.Params.Add(MapType(p.Type));
                }
                if (m.HasBody)
                    nm.Body = CloneBody(m, nm);
                return nm;
            }

            private CilBody CloneBody(MethodDef m, MethodDef dst)
            {
                var body = new CilBody();
                foreach (var v in m.Body.Variables)
                    body.Variables.Add(new Local(MapType(v.Type)));
                var map = new Dictionary<Instruction, Instruction>();
                foreach (var ins in m.Body.Instructions)
                {
                    var ni = new Instruction(ins.OpCode);
                    map[ins] = ni;
                    body.Instructions.Add(ni);
                }
                foreach (var ins in m.Body.Instructions)
                    map[ins].Operand = MapOperand(ins.Operand, map);
                foreach (var eh in m.Body.ExceptionHandlers)
                {
                    var neh = new ExceptionHandler(eh.HandlerType);
                    if (eh.TryStart != null) neh.TryStart = map[eh.TryStart];
                    if (eh.TryEnd != null) neh.TryEnd = map[eh.TryEnd];
                    if (eh.HandlerStart != null) neh.HandlerStart = map[eh.HandlerStart];
                    if (eh.HandlerEnd != null) neh.HandlerEnd = map[eh.HandlerEnd];
                    if (eh.CatchType != null) neh.CatchType = MapType(eh.CatchType);
                    if (eh.FilterStart != null) neh.FilterStart = map[eh.FilterStart];
                    body.ExceptionHandlers.Add(neh);
                }
                body.InitLocals = m.Body.InitLocals;
                body.MaxStack = m.Body.MaxStack;
                body.KeepOldMaxStack = true;
                return body;
            }

            private object MapOperand(object op, Dictionary<Instruction, Instruction> map)
            {
                switch (op)
                {
                    case null: return null;
                    case Instruction target: return map[target];
                    case Instruction[] targets: return targets.Select(t => map[t]).ToArray();
                    case IList<Instruction> targets:
                        return targets.Select(t => map[t]).ToList();
                    case MemberRef mr:
                        return mr.Signature is FieldSig ? (object)MapField(mr) : MapMethod(mr);
                    case FieldDef fd: return MapField(fd);
                    case MethodDef md: return MapMethod(md);
                    case MethodSpec mspec: return MapMethod(mspec);
                    case ITypeDefOrRef t: return MapType(t);
                    default: return op;
                }
            }

            private IMethod MapMethod(IMethod m)
            {
                if (m is MethodDef md && md.DeclaringType == null)
                    return m;
                var declType = m.DeclaringType;
                // Non-game methods (Il2CppInterop.Runtime, System, UnityEngine, ...) stay as-is,
                // but still map a MethodSpec's generic args so game-type container elements are aliased.
                if (declType == null || !IsGameType(declType))
                {
                    if (m is MethodSpec mspec)
                    {
                        var mappedArgs = new List<TypeSig>();
                        bool changed = false;
                        foreach (var ga in mspec.GenericInstMethodSig.GenericArguments)
                        {
                            var mapped = MapType(ga);
                            mappedArgs.Add(mapped);
                            if (!ReferenceEquals(mapped, ga)) changed = true;
                        }
                        if (changed)
                        {
                            var baseM = MapMethod(mspec.Method);
                            var nms = new MethodSpecUser((IMethodDefOrRef)baseM);
                            nms.GenericInstMethodSig = new GenericInstMethodSig(mappedArgs.ToArray());
                            return nms;
                        }
                    }
                    return m;
                }
                var decl = MapType(declType);
                if (m is MethodSpec ms)
                {
                    var baseMethod = MapMethod(ms.Method);
                    var nms = new MethodSpecUser((IMethodDefOrRef)baseMethod);
                    nms.GenericInstMethodSig = new GenericInstMethodSig(ms.GenericInstMethodSig.GenericArguments.Select(ga => MapType(ga)).ToArray());
                    return nms;
                }
                if (m is MethodDef md2)
                {
                    var sig = CreateMethodSigClone(md2);
                    return new MemberRefUser(_mod, md2.Name, sig, decl);
                }
                if (m is MemberRef mref)
                {
                    var sig = CreateMethodSigClone(mref);
                    return new MemberRefUser(_mod, mref.Name, sig, decl);
                }
                return m;
            }

            private MethodSig CreateMethodSigClone(IMethod m)
            {
                var msig = m.MethodSig;
                if (msig == null)
                {
                    var vs = MethodSig.CreateStatic(_mod.CorLibTypes.Void);
                    vs.HasThis = false;
                    return vs;
                }
                var ret = MapType(msig.RetType);
                MethodSig sig = msig.GetCallingConvention() == CallingConvention.HasThis
                    ? MethodSig.CreateInstance(ret)
                    : MethodSig.CreateStatic(ret);
                sig.HasThis = msig.HasThis;
                sig.ExplicitThis = msig.ExplicitThis;
                sig.CallingConvention = msig.CallingConvention;
                foreach (var p in msig.Params)
                    sig.Params.Add(MapType(p));
                if (msig.ParamsAfterSentinel != null)
                    foreach (var p in msig.ParamsAfterSentinel)
                        sig.ParamsAfterSentinel.Add(MapType(p));
                return sig;
            }

            private IField MapField(IField f)
            {
                if (f is FieldDef fd && fd.DeclaringType == null)
                    return f;
                var declType = f.DeclaringType;
                if (declType == null || !IsGameType(declType))
                    return f;
                var decl = MapType(declType);
                var nfs = new FieldSig(MapType(f.FieldSig.Type));
                return new MemberRefUser(_mod, f.Name, nfs, decl);
            }

            // ---- type mapping: game types -> Il2Cpp-prefixed aliases ----
            private int _mapDepth;

            private ITypeDefOrRef MapType(ITypeDefOrRef t)
            {
                if (t == null) return null;
                if (_mapDepth > 80) return t;
                _mapDepth++;
                try { return MapTypeCore(t); }
                finally { _mapDepth--; }
            }

            private ITypeDefOrRef MapTypeCore(ITypeDefOrRef t)
            {
                if (t is TypeSpec ts)
                {
                    var nts = MapType(ts.TypeSig);
                    if (ReferenceEquals(nts, ts.TypeSig)) return ts;
                    return new TypeSpecUser(nts);
                }
                if (t is TypeDef td)
                {
                    if (td.IsNested && td.DeclaringType != null)
                    {
                        var tkey = ToOrigFullName(td);
                        if (_aliasByOrig.TryGetValue(tkey, out var aliasN))
                            return aliasN;
                        return EnsureAlias(td);
                    }
                    if (IsGameType(td))
                    {
                        if (_aliasByOrig.TryGetValue(td.FullName, out var alias))
                            return alias;
                        return new TypeRefUser(_mod, "Il2Cpp" + td.Namespace, td.Name, _mod.GetAssemblyRef("Assembly-CSharp"));
                    }
                    return td;
                }
                if (t is TypeRef tr)
                {
                    if (tr.IsNested && tr.DeclaringType != null)
                    {
                        var full = ToOrigFullName(tr);
                        if (_aliasByOrig.TryGetValue(full, out var aliasN))
                            return aliasN;
                        if (_origByFull.TryGetValue(full, out var td2))
                            return EnsureAlias(td2);
                        return MakeTypeRefChain(tr);
                    }
                    if (IsGameType(tr))
                    {
                        var full = ToOrigFullName(tr);
                        if (_aliasByOrig.TryGetValue(full, out var alias))
                            return alias;
                        return new TypeRefUser(_mod, "Il2Cpp" + tr.Namespace, tr.Name, _mod.GetAssemblyRef("Assembly-CSharp"));
                    }
                    return tr;
                }
                return t;
            }

            private ITypeDefOrRef MakeTypeRefChain(ITypeDefOrRef t)
            {
                if (t.DeclaringType != null)
                {
                    var parent = MakeTypeRefChain(t.DeclaringType);
                    return new TypeRefUser(_mod, string.Empty, t.Name, parent as IResolutionScope);
                }
                var ns = t.Namespace ?? "";
                if (ns.StartsWith("Il2Cpp", StringComparison.Ordinal))
                    ns = ns.Substring(6);
                return new TypeRefUser(_mod, "Il2Cpp" + ns, t.Name, _mod.GetAssemblyRef("Assembly-CSharp"));
            }

            private TypeSig MapType(TypeSig ts)
            {
                if (ts == null) return null;
                if (_mapDepth > 80) return ts;
                _mapDepth++;
                try { return MapTypeCore(ts); }
                finally { _mapDepth--; }
            }

            private TypeSig MapTypeCore(TypeSig ts)
            {
                switch (ts)
                {
                    case ClassOrValueTypeSig covt:
                    {
                        var mapped = MapType(covt.TypeDefOrRef);
                        if (ReferenceEquals(mapped, covt.TypeDefOrRef)) return ts;
                        return mapped.ToTypeSig();
                    }
                    case GenericInstSig gis:
                    {
                        var nt = MapType(gis.GenericType) as ClassOrValueTypeSig ?? gis.GenericType;
                        var n = new GenericInstSig(nt);
                        foreach (var ga in gis.GenericArguments)
                            n.GenericArguments.Add(MapType(ga));
                        return n;
                    }
                    case SZArraySig sa:
                        return new SZArraySig(MapType(sa.Next));
                    case ArraySig arr:
                        return new ArraySig(MapType(arr.Next), arr.Rank);
                    case PtrSig pt:
                        return new PtrSig(MapType(pt.Next));
                    case ByRefSig br:
                        return new ByRefSig(MapType(br.Next));
                    case PinnedSig pn:
                        return new PinnedSig(MapType(pn.Next));
                    default:
                        return ts;
                }
            }

            // ---- helpers ----
            private static bool IsGameType(ITypeDefOrRef tr)
            {
                if (tr == null) return false;
                if (tr is TypeSpec) return true; // handled by caller via recursion
                if (tr.DeclaringType != null)
                    return IsGameType(tr.DeclaringType);
                string scopeName;
                if (tr is TypeDef td)
                {
                    if (td.Module == null) return false;
                    scopeName = td.Module.Name;
                }
                else if (tr is TypeRef tref)
                {
                    if (tref.ResolutionScope is AssemblyRef ar)
                        scopeName = ar.Name;
                    else if (tref.ResolutionScope is ModuleDef mdo)
                        scopeName = mdo.Name;
                    else if (tref.ResolutionScope is ModuleRef mr)
                        scopeName = mr.Name;
                    else
                        return false;
                }
                else return false;

                if (scopeName == "Il2Cppmscorlib" || scopeName.StartsWith("Il2CppSystem", StringComparison.Ordinal) ||
                    scopeName.StartsWith("Il2CppInterop", StringComparison.Ordinal) || scopeName.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    scopeName.StartsWith("System", StringComparison.Ordinal) || scopeName.StartsWith("mscorlib", StringComparison.Ordinal) ||
                    scopeName.StartsWith("netstandard", StringComparison.Ordinal) || scopeName.StartsWith("Mono", StringComparison.Ordinal))
                    return false;
                return scopeName.StartsWith("Assembly-CSharp", StringComparison.Ordinal) || scopeName == "Assembly-CSharp-firstpass";
            }

            private static string ToOrigFullName(ITypeDefOrRef tr)
            {
                if (tr.DeclaringType != null)
                    return ToOrigFullName(tr.DeclaringType) + "/" + tr.Name;
                var ns = tr.Namespace ?? "";
                if (ns.StartsWith("Il2Cpp", StringComparison.Ordinal))
                    ns = ns.Substring(6);
                return (ns.Length == 0 ? "" : ns + ".") + tr.Name;
            }
        }
    }
}
#endif
