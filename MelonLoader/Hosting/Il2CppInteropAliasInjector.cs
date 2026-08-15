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
    /// Ensures every BepInEx game interop assembly ships Il2Cpp-prefixed alias types so
    /// MelonLoader mods load <b>verbatim</b> (never rewritten). MelonLoader's interop
    /// prefixes each game type with "Il2Cpp" (e.g. <c>Il2Cpp.LookAtTarget</c> from
    /// Assembly-CSharp, <c>Il2CppTMPro.TMP_Text</c> from Unity.TextMeshPro); BepInEx's
    /// interop keeps original namespaces. This injector clones every game type the
    /// installed mods reference into an "Il2Cpp"-prefixed namespace inside the matching
    /// interop assembly.
    /// <para>
    /// Because BepInEx preloads interop assemblies before plugins run, a freshly
    /// generated alias set takes effect on the next game launch (callers log a restart
    /// hint when <see cref="EnsureAliases"/> returns <c>true</c>).
    /// </para>
    /// </summary>
    public static class Il2CppInteropAliasInjector
    {
        /// <summary>Optional warning logger assigned by the host (MelonLoader plugin or the
        /// BepInEx preloader patcher). Defaults to no-op so the injector stays usable in
        /// both hosting environments without depending on MelonLoader's logger.</summary>
        public static Action<string> LogWarning = _ => { };

        /// <summary>
        /// Ensures every interop assembly under <paramref name="interopDir"/> contains
        /// Il2Cpp-prefixed aliases for <b>all</b> of its game types. A full alias pass
        /// covers any mod reference (e.g. <c>Il2Cpp.LookAtTarget</c> from Assembly-CSharp
        /// or <c>Il2CppTMPro.TMP_Text</c> from Unity.TextMeshPro) no matter which interop
        /// assembly the type lives in, so new mods never need a regenerated alias set.
        /// Returns <c>true</c> if any file was rewritten (restart required to take effect).
        /// </summary>
        public static bool EnsureAliases(string interopDir)
        {
            try
            {
                if (string.IsNullOrEmpty(interopDir) || !Directory.Exists(interopDir))
                    return false;

                var gen = new AliasGenerator(interopDir);
                return gen.Run();
            }
            catch (Exception ex)
            {
                LogWarning($"[InteropAliasInjector] interop alias injection failed: {ex}");
                return false;
            }
        }

        private sealed class AliasGenerator
        {
            private const char Sep = '\u0001'; // (assembly, typeFullName) key separator

            // All loaded interop modules that we may add aliases to (assembly name -> module).
            private readonly Dictionary<string, ModuleDefMD> _modulesByAsm = new(StringComparer.Ordinal);
            // Global index: "asm\0full" -> TypeDef.
            private readonly Dictionary<string, TypeDef> _origByFull = new();
            private readonly Dictionary<string, List<TypeDef>> _nestedByParent = new();
            private readonly Dictionary<string, TypeDef> _aliasByOrig = new();
            private readonly HashSet<ModuleDefMD> _dirtyModules = new();
            private readonly string _interopDir;
            private HashSet<string> _collected = new();

            private const string MarkerVersion = "full-v1";
            private const string MarkerFile = ".melonloader-aliased";

            internal AliasGenerator(string interopDir)
            {
                _interopDir = interopDir;
                foreach (var file in Directory.GetFiles(interopDir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    var asmName = Path.GetFileNameWithoutExtension(file);
                    if (IsFrameworkAssembly(asmName))
                        continue;
                    try
                    {
                        var m = ModuleDefMD.Load(file);
                        var name = m.Assembly?.Name?.String ?? asmName;
                        // Skip ENGINE runtime modules outright: when the engine namespaces
                        // (UnityEngine.* / Unity.* / System.*) dominate the module's top-level
                        // types, it is an engine runtime assembly (Unity.InputSystem,
                        // Unity.Services.Core.Device, ...). MelonLoader never prefixes those and
                        // mods reference them unprefixed - aliasing them generates bogus
                        // Il2CppUnity.* alias types and cross-assembly references that break the
                        // writes. Unity plugins such as Unity.TextMeshPro (namespace "TMPro")
                        // are NOT engine-dominant and still get aliased.
                        int engineCount = 0, nonEngineCount = 0;
                        foreach (var t in m.Types)
                        {
                            if (t.IsNested) continue;
                            var n = (string)t.Namespace ?? "";
                            if (IsEngineNamespace(n)) engineCount++;
                            else nonEngineCount++;
                        }
                        if (nonEngineCount == 0 || nonEngineCount < engineCount)
                            continue;
                        // Self-heal: strip any pre-existing Il2Cpp-prefixed alias types (from an
                        // older on-demand pass or a previous full pass) BEFORE indexing, so a
                        // regeneration is idempotent and never double-prefixes (Il2Cpp.Il2Cpp.X)
                        // nor leaves stale aliases that reference original private members.
                        // Only non-framework modules land here and BepInEx originals never carry an
                        // Il2Cpp-prefixed namespace, so this only ever strips our own aliases
                        // (Il2Cpp.*, Il2CppTMPro.*, Il2CppFMOD.*, ... top-level types).
                        foreach (var t in m.Types.ToList())
                        {
                            if (t.IsNested) continue;
                            var ns = (string)t.Namespace ?? "";
                            if (ns.StartsWith("Il2Cpp", StringComparison.Ordinal))
                                m.Types.Remove(t);
                        }
                        _modulesByAsm[name] = m;
                        foreach (var t in m.GetTypes())
                        {
                            var key = Key(name, t.FullName);
                            _origByFull[key] = t;
                            if (t.IsNested && t.DeclaringType != null)
                            {
                                var pkey = Key(name, t.DeclaringType.FullName);
                                if (!_nestedByParent.TryGetValue(pkey, out var l))
                                    _nestedByParent[pkey] = l = new List<TypeDef>();
                                l.Add(t);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // skip modules we cannot read, but log why (helps diagnose locked files)
                        LogWarning($"[InteropAliasInjector] cannot load {asmName}: {ex.Message}");
                    }
                }
            }

            internal bool Run()
            {
                if (_modulesByAsm.Count == 0)
                {
                    LogWarning($"[InteropAliasInjector] no modules loaded from {_interopDir}");
                    return false;
                }

                // Fingerprint skip: a full alias pass records the interop fingerprint
                // (file name/size/timestamp) + marker version. If nothing changed since,
                // the full alias set is already in place and we skip. This is reliable:
                // when BepInEx regenerates the interop the fingerprint changes and a full
                // pass runs again. New mods never need a regeneration (the full set already
                // covers every game type).
                var markerPath = Path.Combine(_interopDir, MarkerFile);
                if (File.Exists(markerPath))
                {
                    var parts = File.ReadAllText(markerPath).Split('\n');
                    if (parts.Length >= 2 && parts[0].Trim() == MarkerVersion && parts[1].Trim() == ComputeFingerprint(_interopDir))
                        return false;
                }

                // Full regeneration: seed every top-level game type of every module so
                // ANY mod reference (Il2Cpp.LookAtTarget, Il2CppTMPro.TMP_Text, ...) is
                // covered no matter which interop assembly the type lives in. No per-mod
                // scanning needed; new mods never require another regeneration.
                var seeds = new HashSet<string>();
                foreach (var kv in _modulesByAsm)
                    foreach (var t in kv.Value.Types)
                        if (!t.IsNested && !IsEngineType(t))
                            seeds.Add(Key(kv.Key, t.FullName));

                _collected = new HashSet<string>();
                foreach (var s in seeds)
                    if (_origByFull.TryGetValue(s, out var td))
                        CollectType(td);

                foreach (var full in _collected.OrderBy(x => x))
                    if (_origByFull.TryGetValue(full, out var td))
                        EnsureAlias(td);
                if (_aliasByOrig.Count == 0)
                    return false;

                // Write back each modified module via temp file + move. One or two files can
                // legitimately fail (e.g. a file BepInEx/Unity keeps locked at runtime) - log,
                // skip it and keep going so the rest of the alias set still lands. Only when
                // EVERY module was written do we record the fingerprint marker, otherwise we
                // return false so the next launch retries the incomplete pass.
                bool allWritten = true;
                foreach (var m in _dirtyModules)
                {
                    var origPath = m.Location;
                    if (string.IsNullOrEmpty(origPath) || !File.Exists(origPath))
                        continue;
                    var tmp = origPath + ".aliastmp" + Guid.NewGuid().ToString("N");
                    try
                    {
                        m.Write(tmp);
                        // Release the memory-mapped handle BEFORE replacing the original file.
                        // ModuleDefMD.Load keeps the file mapped, and replacing a mapped file
                        // with File.Move throws "Access to the path is denied" even though a
                        // plain copy-over (content write) succeeds. Disposing frees the handle
                        // so the original file can be deleted/replaced.
                        m.Dispose();
                        File.Move(tmp, origPath, true);
                    }
                    catch (Exception ex)
                    {
                        allWritten = false;
                        LogWarning($"[InteropAliasInjector] write FAILED for '{Path.GetFileName(origPath)}' (skipped, retry next launch): {ex.Message}");
                    }
                    finally
                    {
                        if (File.Exists(tmp))
                            File.Delete(tmp);
                    }
                }

                // Record the interop fingerprint so subsequent launches skip this pass
                // until the interop actually changes (e.g. BepInEx regenerates it). Only
                // record when the whole set was written successfully.
                if (allWritten)
                {
                    try
                    {
                        File.WriteAllText(markerPath, MarkerVersion + "\n" + ComputeFingerprint(_interopDir));
                    }
                    catch { }
                    return true;
                }

                return false;
            }

            private static string ComputeFingerprint(string interopDir)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var f in Directory.GetFiles(interopDir, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var fi = new FileInfo(f);
                    sb.Append(Path.GetFileName(f)).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append('|');
                }
                return sb.ToString();
            }

            // ---- closure collection ----
            private void CollectType(TypeDef td)
            {
                if (IsEngineType(td)) return;
                var asm = GetTypeAsm(td);
                if (asm == null) return;
                var key = Key(asm, td.FullName);
                if (!_collected.Add(key)) return;

                if (td.BaseType != null) CollectTypeRef(asm, td.BaseType);
                foreach (var i in td.Interfaces) CollectTypeRef(asm, i.Interface);
                foreach (var gp in td.GenericParameters)
                    foreach (var c in gp.GenericParamConstraints) CollectTypeRef(asm, c.Constraint);
                foreach (var f in td.Fields) CollectTypeRef(asm, f.FieldType);
                foreach (var m in td.Methods)
                {
                    CollectTypeRef(asm, m.ReturnType);
                    foreach (var p in m.Parameters) CollectTypeRef(asm, p.Type);
                    foreach (var gp in m.GenericParameters)
                        foreach (var c in gp.GenericParamConstraints) CollectTypeRef(asm, c.Constraint);
                    if (m.HasBody)
                    {
                        foreach (var v in m.Body.Variables) CollectTypeRef(asm, v.Type);
                        foreach (var ins in m.Body.Instructions) CollectOperand(asm, ins.Operand);
                    }
                }
                if (_nestedByParent.TryGetValue(key, out var nts))
                    foreach (var nt in nts) CollectType(nt);
            }

            private void CollectTypeRef(string asm, ITypeDefOrRef t)
            {
                if (t == null) return;
                if (t is TypeSpec ts2) { CollectTypeRef(asm, ts2.TypeSig); return; }
                if (t is GenericSig) return;
                var tasm = GetTypeAsm(t) ?? asm;
                var key = Key(tasm, ToOrigFullName(t));
                if (_origByFull.TryGetValue(key, out var td))
                    CollectType(td);
            }

            private void CollectTypeRef(string asm, TypeSig ts)
            {
                if (ts == null) return;
                switch (ts)
                {
                    case ClassOrValueTypeSig covt: CollectTypeRef(asm, covt.TypeDefOrRef); break;
                    case GenericInstSig gis:
                        CollectTypeRef(asm, gis.GenericType);
                        foreach (var ga in gis.GenericArguments) CollectTypeRef(asm, ga);
                        break;
                    case SZArraySig sa: CollectTypeRef(asm, sa.Next); break;
                    case ArraySig arr: CollectTypeRef(asm, arr.Next); break;
                    case PtrSig pt: CollectTypeRef(asm, pt.Next); break;
                    case ByRefSig br: CollectTypeRef(asm, br.Next); break;
                    case PinnedSig pn: CollectTypeRef(asm, pn.Next); break;
                }
            }

            private void CollectOperand(string asm, object op)
            {
                switch (op)
                {
                    case IMethod m:
                        CollectTypeRef(asm, m.DeclaringType.ToTypeSig());
                        if (m is MethodSpec ms && ms.GenericInstMethodSig != null)
                            foreach (var ga in ms.GenericInstMethodSig.GenericArguments) CollectTypeRef(asm, ga);
                        break;
                    case IField f:
                        CollectTypeRef(asm, f.DeclaringType.ToTypeSig());
                        break;
                    case ITypeDefOrRef t:
                        CollectTypeRef(asm, t.ToTypeSig());
                        break;
                }
            }

            // ---- alias creation ----
            private TypeDef EnsureAlias(TypeDef orig)
            {
                if (IsEngineType(orig))
                    return orig;
                var asm = GetTypeAsm(orig);
                if (asm == null || !_modulesByAsm.TryGetValue(asm, out var mod))
                    return orig;
                var key = Key(asm, orig.FullName);
                if (_aliasByOrig.TryGetValue(key, out var existing))
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
                    var origNs = (string)orig.Namespace ?? "";
                    alias = new TypeDefUser("Il2Cpp" + origNs, orig.Name);
                    alias.Attributes = orig.Attributes;
                    mod.Types.Add(alias);
                    _dirtyModules.Add(mod);
                }
                _aliasByOrig[key] = alias;
                CloneRecursive(orig, alias, mod);
                return alias;
            }

            private void CloneRecursive(TypeDef orig, TypeDef alias, ModuleDefMD mod)
            {
                if (orig.BaseType != null)
                    alias.BaseType = MapType(mod, orig.BaseType);
                foreach (var i in orig.Interfaces)
                    alias.Interfaces.Add(new InterfaceImplUser(MapType(mod, i.Interface)));
                foreach (var gp in orig.GenericParameters)
                {
                    var ngp = new GenericParamUser(gp.Number, gp.Flags, gp.Name);
                    foreach (var c in gp.GenericParamConstraints)
                        ngp.GenericParamConstraints.Add(new GenericParamConstraintUser(MapType(mod, c.Constraint)));
                    alias.GenericParameters.Add(ngp);
                }
                foreach (var f in orig.Fields)
                {
                    var nf = new FieldDefUser(f.Name, new FieldSig(MapType(mod, f.FieldType)), f.Attributes);
                    if (f.HasConstant) nf.Constant = f.Constant;
                    alias.Fields.Add(nf);
                }
                foreach (var m in orig.Methods)
                {
                    try { alias.Methods.Add(CreateMethodClone(mod, m)); }
                    catch { /* skip methods we can't clone */ }
                }
                if (_nestedByParent.TryGetValue(Key(GetTypeAsm(orig), orig.FullName), out var nts))
                    foreach (var nt in nts)
                        EnsureAlias(nt);
            }

            private MethodDef CreateMethodClone(ModuleDefMD mod, MethodDef m)
            {
                var ret = MapType(mod, m.ReturnType);
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
                        ngp.GenericParamConstraints.Add(new GenericParamConstraintUser(MapType(mod, c.Constraint)));
                    nm.GenericParameters.Add(ngp);
                }
                foreach (var p in m.Parameters)
                {
                    if (p.Index == 0 && !m.IsStatic) continue;
                    var pdef = new ParamDefUser { Name = p.Name };
                    pdef.Sequence = (ushort)(nm.ParamDefs.Count + 1);
                    nm.ParamDefs.Add(pdef);
                    sig.Params.Add(MapType(mod, p.Type));
                }
                if (m.HasBody)
                    nm.Body = CloneBody(mod, m, nm);
                return nm;
            }

            private CilBody CloneBody(ModuleDefMD mod, MethodDef m, MethodDef dst)
            {
                var body = new CilBody();
                foreach (var v in m.Body.Variables)
                    body.Variables.Add(new Local(MapType(mod, v.Type)));
                var map = new Dictionary<Instruction, Instruction>();
                foreach (var ins in m.Body.Instructions)
                {
                    var ni = new Instruction(ins.OpCode);
                    map[ins] = ni;
                    body.Instructions.Add(ni);
                }
                foreach (var ins in m.Body.Instructions)
                    map[ins].Operand = MapOperand(mod, ins.Operand, map);
                foreach (var eh in m.Body.ExceptionHandlers)
                {
                    var neh = new ExceptionHandler(eh.HandlerType);
                    if (eh.TryStart != null) neh.TryStart = map[eh.TryStart];
                    if (eh.TryEnd != null) neh.TryEnd = map[eh.TryEnd];
                    if (eh.HandlerStart != null) neh.HandlerStart = map[eh.HandlerStart];
                    if (eh.HandlerEnd != null) neh.HandlerEnd = map[eh.HandlerEnd];
                    if (eh.CatchType != null) neh.CatchType = MapType(mod, eh.CatchType);
                    if (eh.FilterStart != null) neh.FilterStart = map[eh.FilterStart];
                    body.ExceptionHandlers.Add(neh);
                }
                body.InitLocals = m.Body.InitLocals;
                body.MaxStack = m.Body.MaxStack;
                body.KeepOldMaxStack = true;
                return body;
            }

            private object MapOperand(ModuleDefMD mod, object op, Dictionary<Instruction, Instruction> map)
            {
                switch (op)
                {
                    case null: return null;
                    case Instruction target: return map[target];
                    case Instruction[] targets: return targets.Select(t => map[t]).ToArray();
                    case IList<Instruction> targets:
                        return targets.Select(t => map[t]).ToList();
                    case MemberRef mr:
                        return mr.Signature is FieldSig ? (object)MapField(mod, mr) : MapMethod(mod, mr);
                    case FieldDef fd: return MapField(mod, fd);
                    case MethodDef md: return MapMethod(mod, md);
                    case MethodSpec mspec: return MapMethod(mod, mspec);
                    case ITypeDefOrRef t: return MapType(mod, t);
                    default: return op;
                }
            }

            private IMethod MapMethod(ModuleDefMD mod, IMethod m)
            {
                if (m is MethodDef md && md.DeclaringType == null)
                    return m;
                var declType = m.DeclaringType;
                // Non-game methods (Il2CppInterop.Runtime, System, UnityEngine, ...) stay as-is,
                // but map a MethodSpec's generic args so game-type container elements are aliased.
                // Game methods on our alias types are re-targeted onto the alias type itself so
                // the alias's own bodies resolve to its own (public) members - private/internal
                // members of the original would otherwise throw MethodAccessException.
                if (declType == null || !IsGameType(declType))
                {
                    if (m is MethodSpec mspec)
                    {
                        var mappedArgs = new List<TypeSig>();
                        bool changed = false;
                        foreach (var ga in mspec.GenericInstMethodSig.GenericArguments)
                        {
                            var mapped = MapType(mod, ga);
                            mappedArgs.Add(mapped);
                            if (!ReferenceEquals(mapped, ga)) changed = true;
                        }
                        if (changed)
                        {
                            var baseM = MapMethod(mod, mspec.Method);
                            var nms = new MethodSpecUser((IMethodDefOrRef)baseM);
                            nms.GenericInstMethodSig = new GenericInstMethodSig(mappedArgs.ToArray());
                            return nms;
                        }
                    }
                    return m;
                }
                var decl = MapType(mod, declType);
                if (m is MethodSpec ms)
                {
                    var baseMethod = MapMethod(mod, ms.Method);
                    var nms = new MethodSpecUser((IMethodDefOrRef)baseMethod);
                    nms.GenericInstMethodSig = new GenericInstMethodSig(ms.GenericInstMethodSig.GenericArguments.Select(ga => MapType(mod, ga)).ToArray());
                    return nms;
                }
                if (m is MethodDef md2)
                {
                    var sig = CreateMethodSigClone(mod, md2);
                    return new MemberRefUser(mod, md2.Name, sig, decl);
                }
                if (m is MemberRef mref)
                {
                    var sig = CreateMethodSigClone(mod, mref);
                    return new MemberRefUser(mod, mref.Name, sig, decl);
                }
                return m;
            }

            private MethodSig CreateMethodSigClone(ModuleDefMD mod, IMethod m)
            {
                var msig = m.MethodSig;
                if (msig == null)
                {
                    var vs = MethodSig.CreateStatic(mod.CorLibTypes.Void);
                    vs.HasThis = false;
                    return vs;
                }
                var ret = MapType(mod, msig.RetType);
                MethodSig sig = msig.GetCallingConvention() == CallingConvention.HasThis
                    ? MethodSig.CreateInstance(ret)
                    : MethodSig.CreateStatic(ret);
                sig.HasThis = msig.HasThis;
                sig.ExplicitThis = msig.ExplicitThis;
                sig.CallingConvention = msig.CallingConvention;
                foreach (var p in msig.Params)
                    sig.Params.Add(MapType(mod, p));
                if (msig.ParamsAfterSentinel != null)
                    foreach (var p in msig.ParamsAfterSentinel)
                        sig.ParamsAfterSentinel.Add(MapType(mod, p));
                return sig;
            }

            private IField MapField(ModuleDefMD mod, IField f)
            {
                if (f is FieldDef fd && fd.DeclaringType == null)
                    return f;
                var declType = f.DeclaringType;
                if (declType == null)
                    return f;
                if (!IsGameType(declType))
                {
                    // Non-game declaring type, e.g. Il2CppInterop.Runtime.Il2CppClassPointerStore<T>
                    // or an engine type. Still re-target generic arguments that are game types:
                    // the alias .cctor() must write Il2CppClassPointerStore<Il2Cpp.X>::NativeClassPtr
                    // (NOT <X>) because the mod's GetComponentInChildren<Il2Cpp.X>() reads the
                    // <Il2Cpp.X> slot - it must be the same initialized slot. Engine decl types
                    // (UnityEngine.*/Unity.*/System.*) map to themselves and stay untouched.
                    var mappedDecl = MapType(mod, declType);
                    if (ReferenceEquals(mappedDecl, declType))
                        return f;
                    var nfsX = new FieldSig(MapType(mod, f.FieldSig.Type));
                    return new MemberRefUser(mod, f.Name, nfsX, mappedDecl);
                }
                // Map a field whose declaring type is one of the game interop types we
                // alias. This is critical: the alias type's .cctor() writes the private
                // NativeFieldInfoPtr_* fields of ITS OWN alias type (private members are
                // only accessible from the declaring type). If we left the field pointing
                // at the ORIGINAL type (private), the alias .cctor would throw
                // FieldAccessException (e.g. Il2Cpp.CylinderShellSelector..cctor).
                var decl = MapType(mod, declType);
                var nfs = new FieldSig(MapType(mod, f.FieldSig.Type));
                return new MemberRefUser(mod, f.Name, nfs, decl);
            }

            // ---- type mapping ----
            private int _mapDepth;

            private ITypeDefOrRef MapType(ModuleDefMD mod, ITypeDefOrRef t)
            {
                if (t == null) return null;
                if (_mapDepth > 80) return t;
                _mapDepth++;
                try { return MapTypeCore(mod, t); }
                finally { _mapDepth--; }
            }

            private ITypeDefOrRef MapTypeCore(ModuleDefMD mod, ITypeDefOrRef t)
            {
                if (t is TypeSpec ts)
                {
                    var nts = MapType(mod, ts.TypeSig);
                    if (ReferenceEquals(nts, ts.TypeSig)) return ts;
                    return new TypeSpecUser(nts);
                }
                if (t is TypeDef td)
                {
                    var asm = GetTypeAsm(td);
                    var key = asm == null ? null : Key(asm, td.FullName);
                    if (td.IsNested && td.DeclaringType != null)
                    {
                        if (key != null && _aliasByOrig.TryGetValue(key, out var aliasN))
                            return ResolveAlias(mod, aliasN);
                        if (key != null && _origByFull.ContainsKey(key))
                            return ResolveAlias(mod, EnsureAlias(td));
                        return td;
                    }
                    if (key != null && _aliasByOrig.TryGetValue(key, out var alias))
                        return ResolveAlias(mod, alias);
                    // Game type not aliased YET (processing order): alias it on the spot so
                    // method signatures line up (e.g. TMP_Text.get_font must return the alias
                    // TMP_FontAsset, not the original, or the CLR reports MissingMethodException
                    // against the mod's Il2Cpp-prefixed MethodRef).
                    if (key != null && !IsEngineType(td) && _origByFull.ContainsKey(key))
                        return ResolveAlias(mod, EnsureAlias(td));
                    // Not aliased: keep the original TypeDef reference.
                    return td;
                }
                if (t is TypeRef tr)
                {
                    var asm = GetScopeAssembly(tr);
                    var origFull = ToOrigFullName(tr);
                    if (asm != null)
                    {
                        var key = Key(asm, origFull);
                        // Any game type that HAS an alias resolves to that alias - whether the
                        // reference is Il2Cpp-prefixed (a mod-style ref) or unprefixed (inside
                        // another alias type, e.g. the generic argument of
                        // Il2CppClassPointerStore<CylinderShellSelector> in an alias .cctor,
                        // which MUST become Il2CppClassPointerStore<Il2Cpp.CylinderShellSelector>
                        // so the mod's GetComponentInChildren<Il2Cpp.CylinderShellSelector>()
                        // finds the same initialized native-class pointer).
                        if (_aliasByOrig.TryGetValue(key, out var alias))
                            return ResolveAlias(mod, alias);
                        // Engine runtime types (UnityEngine.* / System.* namespaces) keep
                        // pointing at the BepInEx originals - never alias those and never
                        // rewrite unprefixed refs to them (e.g. ActionOnInput.OnEvent ->
                        // UnityEngine.InputSystem.InputAction/CallbackContext).
                        if (IsEngineType(tr))
                            return tr;
                        if (tr.IsNested && tr.DeclaringType != null)
                        {
                            if (_origByFull.TryGetValue(key, out var td2))
                                return ResolveAlias(mod, EnsureAlias(td2));
                        }
                        else if (_origByFull.TryGetValue(key, out var td3))
                        {
                            // Top-level game type not aliased yet (processing order): alias now.
                            return ResolveAlias(mod, EnsureAlias(td3));
                        }
                        // Aliased game type not created yet: build an Il2Cpp-prefixed TypeRef
                        // chain pointing at the target assembly (the alias TypeDef will exist
                        // once that module is written).
                        if (IsAliasedType(tr))
                            return MakeIl2CppRefChain(mod, tr, asm);
                    }
                    return tr;
                }
                return t;
            }

            // Resolve an alias TypeDef for use inside <paramref name="mod"/>. Same module:
            // return the TypeDef directly. Different module (a game/plugin assembly referencing
            // an alias type living in another interop assembly): return a TypeRef pointing at
            // that assembly's Il2Cpp-prefixed type, otherwise dnlib fails the write with
            // "TypeDef 'Il2Cpp.X' is not defined in this module" (cross-module TypeDef refs).
            private ITypeDefOrRef ResolveAlias(ModuleDefMD mod, TypeDef alias)
            {
                if (alias == null) return null;
                if (alias.Module == mod)
                    return alias;
                return MakeAliasRef(mod, alias);
            }

            private TypeRef MakeAliasRef(ModuleDefMD mod, TypeDef alias)
            {
                if (alias.DeclaringType != null)
                {
                    var parent = MakeAliasRef(mod, alias.DeclaringType);
                    return new TypeRefUser(mod, string.Empty, alias.Name, parent);
                }
                var asmName = alias.Module?.Assembly?.Name?.String;
                var ns = (string)alias.Namespace ?? "";
                return new TypeRefUser(mod, ns, alias.Name, GetAssemblyRef(mod, asmName ?? ""));
            }

            private ITypeDefOrRef MakeIl2CppRefChain(ModuleDefMD mod, ITypeDefOrRef t, string asm)
            {
                if (t.DeclaringType != null)
                {
                    var parent = MakeIl2CppRefChain(mod, t.DeclaringType, asm);
                    return new TypeRefUser(mod, string.Empty, t.Name, parent as IResolutionScope);
                }
                var ns = (string)t.Namespace ?? "";
                if (ns.StartsWith("Il2Cpp", StringComparison.Ordinal))
                    ns = ns.Substring("Il2Cpp".Length);
                return new TypeRefUser(mod, "Il2Cpp" + ns, t.Name, GetAssemblyRef(mod, asm));
            }

            private TypeSig MapType(ModuleDefMD mod, TypeSig ts)
            {
                if (ts == null) return null;
                if (_mapDepth > 80) return ts;
                _mapDepth++;
                try { return MapTypeCore(mod, ts); }
                finally { _mapDepth--; }
            }

            private TypeSig MapTypeCore(ModuleDefMD mod, TypeSig ts)
            {
                switch (ts)
                {
                    case ClassOrValueTypeSig covt:
                    {
                        var mapped = MapType(mod, covt.TypeDefOrRef);
                        if (ReferenceEquals(mapped, covt.TypeDefOrRef)) return ts;
                        return mapped.ToTypeSig();
                    }
                    case GenericInstSig gis:
                    {
                        var nt = MapType(mod, gis.GenericType) as ClassOrValueTypeSig ?? gis.GenericType;
                        var n = new GenericInstSig(nt);
                        foreach (var ga in gis.GenericArguments)
                            n.GenericArguments.Add(MapType(mod, ga));
                        return n;
                    }
                    case SZArraySig sa: return new SZArraySig(MapType(mod, sa.Next));
                    case ArraySig arr: return new ArraySig(MapType(mod, arr.Next), arr.Rank);
                    case PtrSig pt: return new PtrSig(MapType(mod, pt.Next));
                    case ByRefSig br: return new ByRefSig(MapType(mod, br.Next));
                    case PinnedSig pn: return new PinnedSig(MapType(mod, pn.Next));
                    default: return ts;
                }
            }

            // ---- helpers ----
            private static string Key(string asm, string full) => asm + Sep + full;

            private static bool IsFrameworkAssembly(string name)
            {
                if (name == "Il2Cppmscorlib" || name == "mscorlib" || name == "netstandard")
                    return true;
                if (name == "__Generated")
                    return true; // BepInEx/Unity runtime-generated helper, always regenerated & locked
                if (name.StartsWith("Il2CppSystem", StringComparison.Ordinal)) return true;
                if (name.StartsWith("Il2CppInterop", StringComparison.Ordinal)) return true;
                if (name.StartsWith("System", StringComparison.Ordinal)) return true;
                if (name.StartsWith("Mono", StringComparison.Ordinal)) return true;
                if (name.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
                return false;
            }

            private static string GetScopeAssembly(ITypeDefOrRef tr)
            {
                if (tr.DeclaringType != null)
                    return GetScopeAssembly(tr.DeclaringType);
                if (tr is TypeRef tref && tref.ResolutionScope is AssemblyRef ar)
                    return ar.Name;
                if (tr is TypeDef td && td.Module != null)
                    return td.Module.Assembly?.Name?.String ?? Path.GetFileNameWithoutExtension(td.Module.Name ?? "");
                return null;
            }

            private static string GetTypeAsm(ITypeDefOrRef tr)
            {
                if (tr is TypeDef td && td.Module != null)
                    return td.Module.Assembly?.Name?.String ?? Path.GetFileNameWithoutExtension(td.Module.Name ?? "");
                return GetScopeAssembly(tr);
            }

            private static string ToOrigFullName(ITypeDefOrRef tr)
            {
                if (tr.DeclaringType != null)
                    return ToOrigFullName(tr.DeclaringType) + "/" + tr.Name;
                var ns = (string)tr.Namespace ?? "";
                if (ns.StartsWith("Il2Cpp", StringComparison.Ordinal))
                    ns = ns.Substring("Il2Cpp".Length);
                return (ns.Length == 0 ? "" : ns + ".") + tr.Name;
            }

            private static bool IsAliasedType(ITypeDefOrRef tr)
            {
                // A type needs an alias if it's referenced with an Il2Cpp prefix and lives in
                // one of the loaded interop modules (framework/Unity built-ins are excluded).
                var ns = (string)tr.Namespace ?? "";
                if (!ns.StartsWith("Il2Cpp", StringComparison.Ordinal))
                    return false;
                var asm = GetScopeAssembly(tr);
                // Il2CppInterop.Runtime / Il2CppSystem.* etc. also carry an "Il2Cpp" namespace
                // prefix but are framework assemblies - never re-target those, keep the ref as-is.
                if (asm == null || IsFrameworkAssembly(asm))
                    return false;
                return true;
            }

            // Engine runtime types whose namespace already starts with UnityEngine.* or System.*.
            // MelonLoader never Il2Cpp-prefixes those - mods reference them UNPREFIXED and
            // BepInEx's interop already provides them, so they must NOT be aliased. This matters
            // for assemblies like Unity.InputSystem.dll whose types live under
            // "UnityEngine.InputSystem.*" (aliasing them would rewrite unprefixed cross-assembly
            // references like ActionOnInput.OnEvent -> UnityEngine.InputSystem.InputAction and
            // break them). Unity plugins such as Unity.TextMeshPro (namespace "TMPro") are NOT
            // engine types and still get aliased (Il2CppTMPro).
            private static bool IsEngineType(ITypeDefOrRef t)
            {
                var ns = (string)t.Namespace ?? "";
                return IsEngineNamespace(ns);
            }

            // Engine namespaces: UnityEngine.*, Unity.* (Unity.Services.*, Unity.XR.*,
            // Unity.Collections.*, Unity.Mathematics.*, ...) and System.*. MelonLoader never
            // Il2Cpp-prefixes types under these - mods reference them UNPREFIXED and BepInEx's
            // interop already provides them, so they must NOT be aliased. Unity plugins such as
            // Unity.TextMeshPro (namespace "TMPro") are NOT engine namespaces and still get
            // aliased (Il2CppTMPro).
            private static bool IsEngineNamespace(string ns)
            {
                return ns.StartsWith("UnityEngine", StringComparison.Ordinal)
                    || ns.StartsWith("Unity.", StringComparison.Ordinal)
                    || ns.StartsWith("System", StringComparison.Ordinal);
            }

            // True when the type lives in one of the game interop modules we alias (i.e. it is
            // an original game type that will get an Il2Cpp-prefixed alias). Used to decide
            // whether member references on it must be re-targeted onto the alias type. Note the
            // original game type's namespace is usually EMPTY (Assembly-CSharp top-level) or
            // non-Il2Cpp (e.g. Unity.TextMeshPro), so IsAliasedType() (namespace prefix check)
            // is NOT the right test here - presence in _origByFull is.
            private bool IsGameType(ITypeDefOrRef t)
            {
                if (t == null) return false;
                if (IsEngineType(t)) return false;
                if (t is TypeSpec ts)
                {
                    var sig = ts.TypeSig;
                    if (sig is ClassOrValueTypeSig covt) return IsGameType(covt.TypeDefOrRef);
                    if (sig is GenericInstSig gis && gis.GenericType != null) return IsGameType(gis.GenericType.TypeDefOrRef);
                    return false;
                }
                var asm = GetTypeAsm(t);
                if (asm == null || !_modulesByAsm.ContainsKey(asm))
                    return false;
                return _origByFull.ContainsKey(Key(asm, ToOrigFullName(t)));
            }

            private static AssemblyRef GetAssemblyRef(ModuleDefMD mod, string asmName)
            {
                return mod.GetAssemblyRef(asmName);
            }
        }
    }
}
#endif
