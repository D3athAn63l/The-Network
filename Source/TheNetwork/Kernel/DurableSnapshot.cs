using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TheNetwork.Kernel
{
    /// <summary>
    /// An IN-PLACE snapshot of a small, explicitly chosen set of Network-owned objects: the touched set of one Physical
    /// Episode reconciliation commit (PHYSICAL_LIFECYCLE § 15.6). <see cref="Capture"/> walks every instance field of each
    /// root and of every Network-owned object reachable from it (components, nested records, lists, dictionaries and sets),
    /// remembering the value of each field and the contents of each collection. <see cref="Restore"/> puts every one of
    /// them back on the SAME objects, so anything else that already holds a reference keeps seeing a consistent object.
    ///
    /// Only types from this assembly are walked; an engine object (a pawn, a def, a faction) is remembered as a reference
    /// and never entered. Nothing here calls a service, schedules, publishes or allocates ids: it is plain memory.
    /// The caller decides what the roots are; capturing a whole store would defeat the purpose and is never done.
    /// </summary>
    public sealed class DurableSnapshot
    {
        private sealed class ObjectNode
        {
            public object target;
            public FieldInfo[] fields;
            public object[] values;
        }

        private sealed class CollectionNode
        {
            public object collection;
            public object[] items;
            public DictionaryEntry[] entries;
        }

        private sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new RefComparer();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly Assembly Own = typeof(DurableSnapshot).Assembly;

        private readonly Dictionary<object, ObjectNode> objects = new Dictionary<object, ObjectNode>(RefComparer.Instance);
        private readonly Dictionary<object, CollectionNode> collections = new Dictionary<object, CollectionNode>(RefComparer.Instance);

        /// <summary>How many Network objects the snapshot covers (diagnostics, the coverage proof).</summary>
        public int ObjectCount => objects.Count;

        /// <summary>Captures a root and every Network-owned object reachable from it. Null roots are ignored.</summary>
        public DurableSnapshot Capture(object root)
        {
            if (root != null) Walk(root);
            return this;
        }

        /// <summary>True when this exact object (or collection) is inside the snapshot: the coverage proof of § 15.7.</summary>
        public bool Covers(object o)
        {
            return o != null && (objects.ContainsKey(o) || collections.ContainsKey(o));
        }

        /// <summary>Puts every captured field and collection back as it was at capture time, on the same objects.</summary>
        public void Restore()
        {
            foreach (ObjectNode n in objects.Values)
            {
                for (int i = 0; i < n.fields.Length; i++) n.fields[i].SetValue(n.target, n.values[i]);
            }
            foreach (CollectionNode c in collections.Values)
            {
                IDictionary dict = c.collection as IDictionary;
                if (dict != null)
                {
                    dict.Clear();
                    for (int i = 0; i < c.entries.Length; i++) dict.Add(c.entries[i].Key, c.entries[i].Value);
                    continue;
                }
                IList list = c.collection as IList;
                if (list != null && !list.IsFixedSize)
                {
                    list.Clear();
                    for (int i = 0; i < c.items.Length; i++) list.Add(c.items[i]);
                    continue;
                }
                if (list != null)
                {
                    for (int i = 0; i < c.items.Length && i < list.Count; i++) list[i] = c.items[i];
                    continue;
                }
                // A set (HashSet<T>): cleared and refilled through its own Clear/Add.
                Type t = c.collection.GetType();
                MethodInfo clear = t.GetMethod("Clear", Type.EmptyTypes);
                MethodInfo add = t.GetMethod("Add");
                if (clear == null || add == null) continue;
                clear.Invoke(c.collection, null);
                for (int i = 0; i < c.items.Length; i++) add.Invoke(c.collection, new[] { c.items[i] });
            }
        }

        private static bool IsOwned(Type t)
        {
            return t != null && t.IsClass && t.Assembly == Own && !typeof(Delegate).IsAssignableFrom(t);
        }

        private static bool IsCollection(object v)
        {
            if (v is string) return false;
            if (v is IList || v is IDictionary) return true;
            Type t = v.GetType();
            return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>);
        }

        private void Walk(object o)
        {
            if (o == null) return;
            if (IsCollection(o))
            {
                WalkCollection(o);
                return;
            }
            if (!IsOwned(o.GetType()) || objects.ContainsKey(o)) return;
            FieldInfo[] fields = FieldsOf(o.GetType());
            ObjectNode node = new ObjectNode { target = o, fields = fields, values = new object[fields.Length] };
            objects[o] = node;
            for (int i = 0; i < fields.Length; i++)
            {
                object v = fields[i].GetValue(o);
                node.values[i] = v;
                if (v == null || v is string || v.GetType().IsValueType) continue;
                Walk(v);
            }
        }

        private void WalkCollection(object c)
        {
            if (collections.ContainsKey(c)) return;
            CollectionNode node = new CollectionNode { collection = c };
            collections[c] = node;
            IDictionary dict = c as IDictionary;
            if (dict != null)
            {
                node.entries = new DictionaryEntry[dict.Count];
                int k = 0;
                foreach (DictionaryEntry e in dict) node.entries[k++] = e;
                for (int i = 0; i < node.entries.Length; i++)
                {
                    object v = node.entries[i].Value;
                    if (v != null && !(v is string) && !v.GetType().IsValueType) Walk(v);
                }
                return;
            }
            List<object> items = new List<object>();
            foreach (object item in (IEnumerable)c) items.Add(item);
            node.items = items.ToArray();
            for (int i = 0; i < node.items.Length; i++)
            {
                object v = node.items[i];
                if (v != null && !(v is string) && !v.GetType().IsValueType) Walk(v);
            }
        }

        private static FieldInfo[] FieldsOf(Type t)
        {
            lock (fieldCache)
            {
                FieldInfo[] f;
                if (fieldCache.TryGetValue(t, out f)) return f;
                List<FieldInfo> list = new List<FieldInfo>();
                for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                {
                    FieldInfo[] declared = x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < declared.Length; i++)
                    {
                        if (declared[i].IsLiteral) continue;
                        list.Add(declared[i]);
                    }
                }
                f = list.ToArray();
                fieldCache[t] = f;
                return f;
            }
        }
    }
}
