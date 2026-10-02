using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using TheNetwork.Domain;
using TheNetwork.Kernel;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>What a fingerprint capture produced. The runner never treats "no fingerprint" as one thing: it is unavailable by design, not started, failed, or here.</summary>
    public enum FingerprintStatus
    {
        /// <summary>The live Network is running and was fingerprinted.</summary>
        Available = 0,

        /// <summary>There is no live Network to protect (a headless host, no world). Legitimate: RT-INFRA-001 may SKIP.</summary>
        NetworkUnavailable = 1,

        /// <summary>The live Network exists but the game has not started it yet. There is no settled state to compare, and a run never starts it.</summary>
        NetworkNotStarted = 2,

        /// <summary>The Network is running (or should be) and the capture threw. The safety sentinel itself broke: RT-INFRA-001 FAILS (fail closed).</summary>
        Failed = 3
    }

    /// <summary>The result of one live-state capture: a fingerprint, or exactly why there is none.</summary>
    public sealed class FingerprintCapture
    {
        public readonly FingerprintStatus Status;
        public readonly LiveFingerprint Fingerprint;
        public readonly Exception Error;
        public readonly string Detail;

        private FingerprintCapture(FingerprintStatus status, LiveFingerprint fingerprint, Exception error, string detail)
        {
            Status = status;
            Fingerprint = fingerprint;
            Error = error;
            Detail = detail;
        }

        public static FingerprintCapture Available(LiveFingerprint f)
        {
            return new FingerprintCapture(FingerprintStatus.Available, f, null, null);
        }

        public static FingerprintCapture NetworkUnavailable(string detail = null)
        {
            return new FingerprintCapture(FingerprintStatus.NetworkUnavailable, null, null, detail);
        }

        public static FingerprintCapture NetworkNotStarted(string detail = null)
        {
            return new FingerprintCapture(FingerprintStatus.NetworkNotStarted, null, null, detail);
        }

        public static FingerprintCapture Failed(Exception error, string detail = null)
        {
            return new FingerprintCapture(FingerprintStatus.Failed, null, error, detail);
        }

        public override string ToString()
        {
            return Status + (Detail != null ? " (" + Detail + ")" : "") + (Error != null ? " " + Error.GetType().Name + ": " + Error.Message : "");
        }
    }

    /// <summary>
    /// A read-only fingerprint of a Network world's DURABLE truth. Every store the safe suites must never touch is hashed by CONTENT, not
    /// by count: every persisted field of every actor (and its components), contract (terms, ledger, Field Log), operation (checkpoints,
    /// outcome, plan), relation edge, knowledge book, history record, summary, journal event, character, cast entry, intel request, lead,
    /// opportunity and pending consequence, plus the scheduler's jobs and the id counters. Hosts add selected safety-critical colony state
    /// beside it (payment silver, cargo, world objects) with <see cref="With"/>. It is taken before and after every slice of a run; if one
    /// value moved, a test touched live truth and the run says so (RT-INFRA-001).
    ///
    /// It reads fields only (it never calls a service, creates, repairs, schedules or caches anything), skips dictionaries, sets and other
    /// rebuildable index structures, and never follows an engine object. What it does NOT prove: that every possible piece of RimWorld
    /// state is untouched. Its boundary is the Network's durable fields plus whatever the host adds.
    /// </summary>
    public sealed class LiveFingerprint
    {
        private const int MaxDiffLines = 24;

        private readonly SortedDictionary<string, long> parts = new SortedDictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Per-entity hashes ("contracts.contracts[3]#17"), so a difference can name the entity that moved.</summary>
        private readonly Dictionary<string, long> entities = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Compiled per-type accessors (the default, fast) or plain reflection (the fallback). Both produce the same hashes; tests flip it to prove it.</summary>
        public static bool UseCompiledWalker = true;

        /// <summary>Forgets every compiled accessor (tests: to measure the one-time compile cost, or to prove the reflective fallback matches).</summary>
        public static void DiscardCompiledProfiles()
        {
            DurableHasher.DiscardProfiles();
        }

        /// <summary>A one-line description of the last walk (types compiled vs reflective, nodes), for the performance report.</summary>
        public static string LastWalkStats;

        public int PartCount => parts.Count;

        public int EntityCount => entities.Count;

        public LiveFingerprint With(string name, long value)
        {
            parts[name] = value;
            return this;
        }

        public long Get(string name)
        {
            long v;
            return parts.TryGetValue(name, out v) ? v : 0;
        }

        public static LiveFingerprint Of(DomainContext ctx, IdAllocator ids, NetScheduler scheduler, EventJournal journal)
        {
            LiveFingerprint f = new LiveFingerprint();
            if (ids != null)
            {
                f.With("ids.nextId", ids.PeekNextId);
                f.With("ids.nextEventSeq", ids.PeekNextEventSeq);
                f.With("ids.nextJobSeq", ids.PeekNextJobSeq);
            }
            if (ctx == null) return f;

            DurableHasher hasher = new DurableHasher(UseCompiledWalker);
            f.AddStore(hasher, "actors", ctx.actors);
            f.AddStore(hasher, "characters", ctx.characters);
            f.AddStore(hasher, "cast", ctx.cast);
            f.AddStore(hasher, "intel", ctx.intel);
            f.AddStore(hasher, "opportunities", ctx.opportunities);
            f.AddStore(hasher, "contracts", ctx.contracts);
            f.AddStore(hasher, "operations", ctx.operations);
            f.AddStore(hasher, "relations", ctx.relations);
            f.AddStore(hasher, "knowledge", ctx.knowledge);
            f.AddStore(hasher, "consequences", ctx.consequences);
            f.AddStore(hasher, "history", ctx.ledger);
            f.AddStore(hasher, "summaries", ctx.summaries);
            if (journal != null) f.AddStore(hasher, "journal", journal);
            f.With("walk.truncated", hasher.Truncated);
            LastWalkStats = hasher.Stats();

            if (scheduler != null)
            {
                long h = DurableHasher.Seed;
                int jobs = 0;
                foreach (ScheduledJob j in scheduler.AllJobs)
                {
                    jobs++;
                    h = DurableHasher.Mix(h, j.seq);
                    h = DurableHasher.Mix(h, j.dueTick);
                    h = DurableHasher.Mix(h, DurableHasher.StringHash(j.kind));
                    h = DurableHasher.Mix(h, j.target);
                    h = DurableHasher.Mix(h, j.arg);
                }
                f.With("scheduler.jobs", jobs);
                f.With("scheduler.hash", h);
            }
            return f;
        }

        /// <summary>Hashes one store: each element of each list field on its own (named), every other field into one scalar bucket.</summary>
        private void AddStore(DurableHasher hasher, string name, object store)
        {
            if (store == null)
            {
                parts[name + ".hash"] = 0;
                return;
            }
            long whole = DurableHasher.Seed;
            long scalars = DurableHasher.Seed;
            FieldInfo[] fields = DurableHasher.FieldsOf(store.GetType());
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo fi = fields[i];
                object value = fi.GetValue(store);
                IList list = value as IList;
                if (list != null)
                {
                    long listHash = DurableHasher.Mix(DurableHasher.Seed, list.Count);
                    for (int k = 0; k < list.Count; k++)
                    {
                        object element = list[k];
                        long eh = hasher.HashRef(element, 1);
                        listHash = DurableHasher.Mix(listHash, eh);
                        entities[name + "." + fi.Name + "[" + k + "]" + hasher.IdLabel(element)] = eh;
                    }
                    parts[name + "." + fi.Name + ".count"] = list.Count;
                    whole = DurableHasher.Mix(whole, listHash);
                }
                else
                {
                    scalars = DurableHasher.Mix(scalars, hasher.HashRef(value, 1));
                }
            }
            parts[name + ".scalars"] = scalars;
            parts[name + ".hash"] = DurableHasher.Mix(whole, scalars);
        }

        /// <summary>The names of every value that differs from <paramref name="other"/>, "name: this -> other", then the entities that moved.</summary>
        public List<string> Diff(LiveFingerprint other)
        {
            List<string> d = new List<string>();
            foreach (KeyValuePair<string, long> kv in parts)
            {
                long o;
                if (!other.parts.TryGetValue(kv.Key, out o)) d.Add(kv.Key + ": " + kv.Value + " -> (missing)");
                else if (o != kv.Value) d.Add(kv.Key + ": " + kv.Value + " -> " + o);
            }
            foreach (KeyValuePair<string, long> kv in other.parts) if (!parts.ContainsKey(kv.Key)) d.Add(kv.Key + ": (missing) -> " + kv.Value);
            if (d.Count == 0) return d;

            // Name the entities that moved (only worth the work once something differs).
            List<string> moved = new List<string>();
            foreach (KeyValuePair<string, long> kv in entities)
            {
                long o;
                if (!other.entities.TryGetValue(kv.Key, out o)) moved.Add(kv.Key + ": removed");
                else if (o != kv.Value) moved.Add(kv.Key + ": changed");
            }
            foreach (KeyValuePair<string, long> kv in other.entities) if (!entities.ContainsKey(kv.Key)) moved.Add(kv.Key + ": added");
            moved.Sort(StringComparer.Ordinal);
            for (int i = 0; i < moved.Count && d.Count < MaxDiffLines; i++) d.Add(moved[i]);
            if (moved.Count > 0 && d.Count >= MaxDiffLines) d.Add("(" + moved.Count + " entities moved in all; the first are listed)");
            return d;
        }

        public bool Same(LiveFingerprint other)
        {
            return other != null && Diff(other).Count == 0;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, long> kv in parts) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Folds every durable field of an object graph into one hash. Durable means: every instance field of the data classes (public or
    /// private, so a score held in a private field counts), nested value types (typed ids, tiles, refs) by content, lists and arrays element
    /// by element in order, nested objects by content. Not durable (skipped): dictionaries, sets and queues (the rebuildable indexes),
    /// delegates, engine/Verse/Unity objects, fields marked <c>[NonSerialized]</c>, statics. A new persisted field is covered automatically.
    ///
    /// Read-only and bounded: it only reads fields, with a depth limit and a node budget. Speed: each type is profiled once into (a) a compiled
    /// delegate that folds all of its numeric/enum/bool/float leaves with no boxing and (b) a compiled delegate that fetches its reference-typed
    /// fields; if expression trees are unavailable the same leaves are read by plain reflection (identical hashes, much slower). Main thread only.
    /// </summary>
    internal sealed class DurableHasher
    {
        internal const long Seed = 1469598103934665603L;
        private const int MaxDepth = 24;
        private const long NodeBudget = 6000000;

        /// <summary>
        /// Types are unique per runtime, so identity is enough. The default comparer's <c>Type.GetHashCode</c> is slow on Mono (measured: about 0.4
        /// microseconds a lookup, which made type lookup most of the cost of a capture); the object-header hash is a few nanoseconds.
        /// </summary>
        private sealed class TypeIdentityComparer : IEqualityComparer<Type>
        {
            public static readonly TypeIdentityComparer Instance = new TypeIdentityComparer();

            public bool Equals(Type a, Type b)
            {
                return ReferenceEquals(a, b);
            }

            public int GetHashCode(Type t)
            {
                return RuntimeHelpers.GetHashCode(t);
            }
        }

        private enum Kind { Numeric, Enum, String, Opaque, List, Object }

        private sealed class Leaf
        {
            public FieldInfo[] Path;      // field chain from the instance (nested value types are flattened)
            public Type Type;             // the leaf field's own type
        }

        private sealed class Profile
        {
            public Kind Kind;
            public long TypeHash;
            public Leaf[] Prims = new Leaf[0];
            public Leaf[] Refs = new Leaf[0];
            public Func<object, long> Prim;          // compiled fold of Prims (null = use reflection)
            public Action<object, object[]> Fetch;   // compiled fetch of Refs into a buffer (null = use reflection)
            public Func<object, long> IdOf;          // compiled "id.Value" (null = none or reflection)
            public FieldInfo[] IdPath;               // the "id" field and its "Value" field (null = none)
        }

        private static readonly MethodInfo MixMethod = typeof(DurableHasher).GetMethod("Mix", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo DoubleBits = typeof(BitConverter).GetMethod("DoubleToInt64Bits");

        private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>(TypeIdentityComparer.Instance);

        private readonly bool compiled;
        // The profiles (and their compiled accessors) are built once per process and kept: compiling is the expensive part, and a type's shape never
        // changes. One cache per mode so the reflective fallback can be compared with the compiled path. Main thread only.
        private static readonly Dictionary<Type, Profile> compiledProfiles = new Dictionary<Type, Profile>(TypeIdentityComparer.Instance);
        private static readonly Dictionary<Type, Profile> reflectiveProfiles = new Dictionary<Type, Profile>(TypeIdentityComparer.Instance);

        private readonly Dictionary<Type, Profile> profiles;
        private readonly List<object[]> buffers = new List<object[]>();
        private long nodes;

        public long Truncated;

        /// <summary>How many of the profiled object types use compiled accessors, and how many fell back to reflection; the values and nodes walked.</summary>
        public string Stats()
        {
            int c = 0, r = 0;
            foreach (KeyValuePair<Type, Profile> kv in profiles)
            {
                if (kv.Value.Kind != Kind.Object) continue;
                if (kv.Value.Prims.Length + kv.Value.Refs.Length == 0 || (kv.Value.Prim != null || kv.Value.Prims.Length == 0) && (kv.Value.Fetch != null || kv.Value.Refs.Length == 0)) c++;
                else r++;
            }
            return c + " compiled / " + r + " reflective object types, " + nodes + " nodes";
        }

        internal static void DiscardProfiles()
        {
            compiledProfiles.Clear();
            reflectiveProfiles.Clear();
        }

        public DurableHasher(bool compiled)
        {
            this.compiled = compiled;
            profiles = compiled ? compiledProfiles : reflectiveProfiles;
        }

        // ------------------------------------------------------------------ primitives

        internal static long Mix(long h, long v)
        {
            unchecked
            {
                h ^= v;
                h *= 1099511628211L;
                h ^= (long)((ulong)h >> 29);
                return h;
            }
        }

        internal static long StringHash(string s)
        {
            if (s == null) return 0x2545F491;
            unchecked
            {
                long h = Seed;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 1099511628211L;
                }
                return Mix(h, s.Length);
            }
        }

        // ------------------------------------------------------------------ what is durable

        internal static bool IsOpaque(Type t)
        {
            if (typeof(Delegate).IsAssignableFrom(t)) return true;
            if (typeof(IDictionary).IsAssignableFrom(t)) return true;
            if (t.IsGenericType)
            {
                Type g = t.GetGenericTypeDefinition();
                if (g == typeof(Dictionary<,>) || g == typeof(HashSet<>) || g == typeof(SortedDictionary<,>) || g == typeof(SortedSet<>) || g == typeof(Queue<>) || g == typeof(Stack<>) || g == typeof(LinkedList<>)) return true;
            }
            string ns = t.Namespace;
            if (ns != null && (ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("Verse", StringComparison.Ordinal) || ns.StartsWith("RimWorld", StringComparison.Ordinal) || ns.StartsWith("System.Reflection", StringComparison.Ordinal) || ns.StartsWith("System.Threading", StringComparison.Ordinal))) return true;
            return t == typeof(Type);
        }

        /// <summary>
        /// Fields that hold derived or cached values, never durable truth, and that a read-only call may legitimately fill in (for example
        /// <c>ContractorSimulation.cachedStrength</c>, which the contractor tab computes on first read). Convention: a runtime-only cache field is
        /// named <c>cached*</c>. <c>HistoryRecord.narrativeSeed</c> is derived from (networkSeed, id) when the ledger is indexed and is not persisted.
        /// </summary>
        private static bool IsRuntimeOnly(FieldInfo fi)
        {
            return fi.Name.StartsWith("cached", StringComparison.Ordinal) || fi.Name == "narrativeSeed";
        }

        /// <summary>The durable instance fields of a type, in a stable order (the hierarchy included, so inherited private state counts).</summary>
        internal static FieldInfo[] FieldsOf(Type t)
        {
            FieldInfo[] fields;
            if (fieldCache.TryGetValue(t, out fields)) return fields;
            List<FieldInfo> list = new List<FieldInfo>();
            for (Type c = t; c != null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType)
            {
                FieldInfo[] declared = c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                {
                    FieldInfo fi = declared[i];
                    if (fi.IsNotSerialized || IsOpaque(fi.FieldType) || IsRuntimeOnly(fi)) continue;
                    list.Add(fi);
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.DeclaringType.FullName + "." + a.Name, b.DeclaringType.FullName + "." + b.Name));
            fields = list.ToArray();
            fieldCache[t] = fields;
            return fields;
        }

        private static bool IsNumeric(Type t)
        {
            return t.IsPrimitive && t != typeof(IntPtr) && t != typeof(UIntPtr);
        }

        private static bool IsFlattenableStruct(Type t)
        {
            return t.IsValueType && !t.IsPrimitive && !t.IsEnum && t != typeof(decimal) && !IsOpaque(t) && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>));
        }

        private static void Collect(Type t, List<FieldInfo> prefix, List<Leaf> prims, List<Leaf> refs, int depth)
        {
            FieldInfo[] fields = FieldsOf(t);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo fi = fields[i];
                List<FieldInfo> path = new List<FieldInfo>(prefix) { fi };
                Type ft = fi.FieldType;
                if (IsNumeric(ft) || ft.IsEnum) prims.Add(new Leaf { Path = path.ToArray(), Type = ft });
                else if (depth < 4 && IsFlattenableStruct(ft)) Collect(ft, path, prims, refs, depth + 1);
                else refs.Add(new Leaf { Path = path.ToArray(), Type = ft });
            }
        }

        private static long ToLong(object boxed)
        {
            if (boxed == null) return 0;
            Type t = boxed.GetType();
            if (t == typeof(bool)) return (bool)boxed ? 3 : 2;
            if (t == typeof(float)) return BitConverter.DoubleToInt64Bits((double)(float)boxed);
            if (t == typeof(double)) return BitConverter.DoubleToInt64Bits((double)boxed);
            if (t == typeof(ulong)) return unchecked((long)(ulong)boxed);
            if (t == typeof(char)) return (char)boxed;
            if (t.IsEnum) return Convert.ToInt64(boxed);
            return Convert.ToInt64(boxed);
        }

        private static object ReadPath(object instance, FieldInfo[] path)
        {
            object v = instance;
            for (int i = 0; i < path.Length && v != null; i++) v = path[i].GetValue(v);
            return v;
        }

        // ------------------------------------------------------------------ profiles

        private Profile ProfileOf(Type t)
        {
            Profile p;
            if (profiles.TryGetValue(t, out p)) return p;
            p = new Profile { TypeHash = StringHash(t.FullName) };
            if (IsNumeric(t)) p.Kind = Kind.Numeric;
            else if (t.IsEnum) p.Kind = Kind.Enum;
            else if (t == typeof(string)) p.Kind = Kind.String;
            else if (t == typeof(decimal)) p.Kind = Kind.Numeric;
            else if (IsOpaque(t)) p.Kind = Kind.Opaque;
            else if (typeof(IList).IsAssignableFrom(t)) p.Kind = Kind.List;
            else
            {
                p.Kind = Kind.Object;
                List<Leaf> prims = new List<Leaf>(), refs = new List<Leaf>();
                Collect(t, new List<FieldInfo>(), prims, refs, 0);
                p.Prims = prims.ToArray();
                p.Refs = refs.ToArray();
                FieldInfo idf = t.GetField("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo valf = idf == null ? null : idf.FieldType.GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (idf != null && valf != null && IsNumeric(valf.FieldType)) p.IdPath = new[] { idf, valf };
                if (compiled) TryCompile(t, p);
            }
            profiles[t] = p;
            return p;
        }

        private static Expression LeafExpression(Expression instance, Leaf leaf)
        {
            Expression e = instance;
            for (int i = 0; i < leaf.Path.Length; i++) e = Expression.Field(e, leaf.Path[i]);
            return e;
        }

        private static Expression AsLong(Expression e, Type t)
        {
            if (t == typeof(bool)) return Expression.Condition(e, Expression.Constant(3L), Expression.Constant(2L));
            if (t == typeof(float) || t == typeof(double)) return Expression.Call(DoubleBits, Expression.Convert(e, typeof(double)));
            if (t == typeof(long)) return e;
            return Expression.Convert(e, typeof(long));
        }

        /// <summary>Compiles the type's accessors. Any failure (no expression-tree support, a visibility surprise) leaves the reflective path for that type.</summary>
        private static void TryCompile(Type t, Profile p)
        {
            try
            {
                ParameterExpression o = Expression.Parameter(typeof(object), "o");
                ParameterExpression inst = Expression.Variable(t, "t");
                ParameterExpression h = Expression.Variable(typeof(long), "h");
                Func<object, long> prim = null;
                if (p.Prims.Length > 0)
                {
                    List<Expression> body = new List<Expression> { Expression.Assign(inst, Expression.Convert(o, t)), Expression.Assign(h, Expression.Constant(Seed)) };
                    for (int i = 0; i < p.Prims.Length; i++) body.Add(Expression.Assign(h, Expression.Call(MixMethod, h, AsLong(LeafExpression(inst, p.Prims[i]), p.Prims[i].Type))));
                    body.Add(h);
                    prim = Expression.Lambda<Func<object, long>>(Expression.Block(typeof(long), new[] { inst, h }, body), o).Compile();
                }
                Action<object, object[]> fetch = null;
                if (p.Refs.Length > 0)
                {
                    ParameterExpression buf = Expression.Parameter(typeof(object[]), "buf");
                    List<Expression> body = new List<Expression> { Expression.Assign(inst, Expression.Convert(o, t)) };
                    for (int i = 0; i < p.Refs.Length; i++)
                    {
                        body.Add(Expression.Assign(Expression.ArrayAccess(buf, Expression.Constant(i)), Expression.Convert(LeafExpression(inst, p.Refs[i]), typeof(object))));
                    }
                    fetch = Expression.Lambda<Action<object, object[]>>(Expression.Block(typeof(void), new[] { inst }, body), o, buf).Compile();
                }
                Func<object, long> idOf = null;
                if (p.IdPath != null)
                {
                    Expression e = Expression.Convert(o, t);
                    for (int i = 0; i < p.IdPath.Length; i++) e = Expression.Field(e, p.IdPath[i]);
                    idOf = Expression.Lambda<Func<object, long>>(AsLong(e, p.IdPath[1].FieldType), o).Compile();
                }
                // Prove the accessors work (visibility, unboxing) against an empty instance before relying on them.
                object probe = t.IsValueType ? Activator.CreateInstance(t) : System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
                if (prim != null) prim(probe);
                if (fetch != null) fetch(probe, new object[p.Refs.Length]);
                if (idOf != null) idOf(probe);
                p.Prim = prim;
                p.Fetch = fetch;
                p.IdOf = idOf;
            }
            catch (Exception)
            {
                p.Prim = null;
                p.Fetch = null;
                p.IdOf = null;
            }
        }

        // ------------------------------------------------------------------ hashing

        private object[] BufferFor(int depth, int length)
        {
            while (buffers.Count <= depth) buffers.Add(new object[8]);
            object[] b = buffers[depth];
            if (b.Length < length)
            {
                b = new object[Math.Max(length, b.Length * 2)];
                buffers[depth] = b;
            }
            return b;
        }

        /// <summary>"#17" when the element has an <c>id</c> field holding a typed id, else "".</summary>
        public string IdLabel(object element)
        {
            if (element == null) return "";
            Profile p = ProfileOf(element.GetType());
            if (p.IdPath == null) return "";
            if (p.IdOf != null) return "#" + p.IdOf(element);
            object idv = p.IdPath[0].GetValue(element);
            return idv == null ? "" : "#" + p.IdPath[1].GetValue(idv);
        }

        /// <summary>The hash of one value of any shape (null, a number, a string, a list, an object).</summary>
        public long HashRef(object o, int depth)
        {
            if (o == null) return 0x5BD1E995;
            string str = o as string;
            if (str != null) return StringHash(str);
            Type t = o.GetType();
            Profile p = ProfileOf(t);
            switch (p.Kind)
            {
                case Kind.Numeric:
                case Kind.Enum:
                    return t == typeof(decimal) ? o.GetHashCode() : ToLong(o);
                case Kind.String:
                    return StringHash((string)o);
                case Kind.Opaque:
                    return 11;
            }
            if (depth > MaxDepth || ++nodes > NodeBudget)
            {
                Truncated++;
                return 7;
            }
            if (p.Kind == Kind.List)
            {
                IList list = (IList)o;
                long lh = Mix(Seed, list.Count);
                for (int i = 0; i < list.Count; i++) lh = Mix(lh, HashRef(list[i], depth + 1));
                return lh;
            }
            return HashObject(o, p, depth);
        }

        private long HashObject(object o, Profile p, int depth)
        {
            long h = Mix(Seed, p.TypeHash);
            if (p.Prims.Length > 0) h = Mix(h, p.Prim != null ? p.Prim(o) : PrimByReflection(o, p));
            int n = p.Refs.Length;
            if (n == 0) return h;
            object[] buf = BufferFor(depth, n);
            if (p.Fetch != null)
            {
                p.Fetch(o, buf);
            }
            else
            {
                for (int i = 0; i < n; i++) buf[i] = ReadPath(o, p.Refs[i].Path);
            }
            for (int i = 0; i < n; i++)
            {
                object v = buf[i];
                buf[i] = null; // never hold the live object longer than this call
                h = Mix(h, HashRef(v, depth + 1));
            }
            return h;
        }

        private static long PrimByReflection(object o, Profile p)
        {
            long h = Seed;
            for (int i = 0; i < p.Prims.Length; i++) h = Mix(h, ToLong(ReadPath(o, p.Prims[i].Path)));
            return h;
        }
    }
}
