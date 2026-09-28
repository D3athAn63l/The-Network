using System;
using System.Collections.Generic;
using System.Xml;
using Verse;

namespace TheNetwork.Kernel
{
    /// <summary>
    /// Persistence helpers (DATA_MODEL § 17).
    ///
    /// The important one is <see cref="LookListTolerant{T}"/>: a Deep list whose elements load one by
    /// one. A malformed element (unknown class, exception during ExposeData) is not loaded, does not
    /// stop its siblings, logs nothing red, and its raw XML is preserved as a quarantine record so
    /// nothing is silently discarded (SAVE_AND_MIGRATION § 7, Spike S5).
    /// </summary>
    public static class NetScribe
    {
        /// <summary>The save version being loaded, set before the stores load (SAVE_AND_MIGRATION § 4.2).</summary>
        public static int LoadingVersion;

        /// <summary>Quarantine records produced while loading; the root moves them into diagnostics.</summary>
        public static readonly List<QuarantineRecord> PendingQuarantine = new List<QuarantineRecord>();

        public const int MaxRawXmlChars = 4000;

        public static bool IsLoading => Scribe.mode == LoadSaveMode.LoadingVars;
        public static bool IsSaving => Scribe.mode == LoadSaveMode.Saving;

        // ------------------------------------------------------------------ tolerant Deep lists

        /// <summary>
        /// Saves like Scribe_Collections (Deep); loads element by element. Elements that cannot be
        /// constructed are quarantined under <paramref name="storeKey"/>. Returns the number
        /// quarantined during this load (0 when saving).
        /// </summary>
        public static int LookListTolerant<T>(ref List<T> list, string label, string storeKey) where T : class, IExposable
        {
            return LookListTolerant(ref list, label, storeKey, PendingQuarantine, null);
        }

        /// <summary>
        /// As above, with an explicit quarantine sink (settings keep their own) and an optional callback
        /// that sees each loaded element with its source XML (to capture the raw text of an element
        /// that loaded but failed validation).
        /// </summary>
        public static int LookListTolerant<T>(ref List<T> list, string label, string storeKey, List<QuarantineRecord> sink, Action<T, XmlNode> onLoaded) where T : class, IExposable
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (list == null) list = new List<T>();
                Scribe_Collections.Look(ref list, label, LookMode.Deep);
                return 0;
            }
            if (Scribe.mode != LoadSaveMode.LoadingVars)
            {
                // Later passes (cross-refs, post-load init) run on the registered elements themselves.
                if (list == null) list = new List<T>();
                return 0;
            }

            list = new List<T>();
            XmlNode parent = Scribe.loader.curXmlParent;
            XmlNode node = parent?[label];
            if (node == null) return 0;
            XmlAttribute isNull = node.Attributes?["IsNull"];
            if (isNull != null && string.Equals(isNull.Value, "true", StringComparison.OrdinalIgnoreCase)) return 0;

            int quarantined = 0;
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                string reason;
                T item = TryLoadElement<T>(child, out reason);
                if (item != null)
                {
                    list.Add(item);
                    onLoaded?.Invoke(item, child);
                }
                else
                {
                    quarantined++;
                    sink?.Add(QuarantineRecord.ForRawXml(storeKey, reason, child.OuterXml));
                }
            }
            return quarantined;
        }

        /// <summary>
        /// Constructs one element the way ScribeExtractor.SaveableFromNode does (same registration for
        /// the cross-ref and post-load passes), but reports failure instead of logging a red error.
        /// </summary>
        public static T TryLoadElement<T>(XmlNode subNode, out string failureReason) where T : class, IExposable
        {
            failureReason = null;
            XmlAttribute isNull = subNode.Attributes?["IsNull"];
            if (isNull != null && string.Equals(isNull.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "NullElement";
                return null;
            }

            Type type = typeof(T);
            XmlAttribute classAttr = subNode.Attributes?["Class"];
            if (classAttr != null)
            {
                Type resolved = null;
                try
                {
                    resolved = GenTypes.GetTypeInAnyAssembly(classAttr.Value);
                    // Persisted Network types live in this assembly; resolve them directly when the game's
                    // type index does not know it (headless tests, or an index built before mods loaded).
                    if (resolved == null) resolved = typeof(NetScribe).Assembly.GetType(classAttr.Value, false);
                }
                catch (Exception)
                {
                    resolved = null;
                }
                if (resolved == null)
                {
                    failureReason = "UnknownClass:" + classAttr.Value;
                    return null;
                }
                if (!typeof(T).IsAssignableFrom(resolved) || resolved.IsAbstract)
                {
                    failureReason = "IncompatibleClass:" + classAttr.Value;
                    return null;
                }
                type = resolved;
            }
            else if (type.IsAbstract)
            {
                failureReason = "AbstractWithoutClass";
                return null;
            }

            IExposable exposable;
            try
            {
                exposable = (IExposable)Activator.CreateInstance(type, true);
            }
            catch (Exception ex)
            {
                failureReason = "ConstructFailed:" + ex.GetType().Name;
                return null;
            }

            XmlNode oldXml = Scribe.loader.curXmlParent;
            IExposable oldParent = Scribe.loader.curParent;
            string oldPath = Scribe.loader.curPathRelToParent;
            Scribe.loader.curXmlParent = subNode;
            Scribe.loader.curParent = exposable;
            Scribe.loader.curPathRelToParent = null;
            try
            {
                exposable.ExposeData();
            }
            catch (Exception ex)
            {
                failureReason = "ExposeFailed:" + ex.GetType().Name + ":" + Truncate(ex.Message, 200);
                return null;
            }
            finally
            {
                Scribe.loader.curXmlParent = oldXml;
                Scribe.loader.curParent = oldParent;
                Scribe.loader.curPathRelToParent = oldPath;
            }

            Scribe.loader.crossRefs.RegisterForCrossRefResolve(exposable);
            Scribe.loader.initer.RegisterForPostLoadInit(exposable);
            return (T)exposable;
        }

        // ------------------------------------------------------------------ typed IDs

        public static void Look(ref ActorId id, string label)
        {
            int v = id.Value;
            Scribe_Values.Look(ref v, label, 0);
            if (IsLoading) id = new ActorId(v);
        }

        public static void Look(ref CharacterId id, string label)
        {
            int v = id.Value;
            Scribe_Values.Look(ref v, label, 0);
            if (IsLoading) id = new CharacterId(v);
        }

        public static void Look(ref IntelRequestId id, string label)
        {
            int v = id.Value;
            Scribe_Values.Look(ref v, label, 0);
            if (IsLoading) id = new IntelRequestId(v);
        }

        public static void Look(ref LeadId id, string label)
        {
            int v = id.Value;
            Scribe_Values.Look(ref v, label, 0);
            if (IsLoading) id = new LeadId(v);
        }

        public static void Look(ref OpportunityId id, string label)
        {
            int v = id.Value;
            Scribe_Values.Look(ref v, label, 0);
            if (IsLoading) id = new OpportunityId(v);
        }

        public static void Look(ref HistoryRecordId id, string label)
        {
            int v = id.Value;
            Scribe_Values.Look(ref v, label, 0);
            if (IsLoading) id = new HistoryRecordId(v);
        }

        public static void Look(ref ContractId id, string label)
        {
            int v = id.Value;
            Scribe_Values.Look(ref v, label, 0);
            if (IsLoading) id = new ContractId(v);
        }

        public static void Look(ref OfferId id, string label)
        {
            int v = id.Value;
            Scribe_Values.Look(ref v, label, 0);
            if (IsLoading) id = new OfferId(v);
        }

        public static void Look(ref OperationId id, string label)
        {
            int v = id.Value;
            Scribe_Values.Look(ref v, label, 0);
            if (IsLoading) id = new OperationId(v);
        }

        public static void Look(ref EntityRef r, string label)
        {
            string s = r.IsValid ? r.ToString() : null;
            Scribe_Values.Look(ref s, label);
            if (IsLoading) r = EntityRef.Parse(s);
        }

        public static void LookIntList<TId>(ref List<TId> list, string label, Func<TId, int> toInt, Func<int, TId> fromInt)
        {
            List<int> raw = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // An empty list is not written: a missing node loads as empty (keeps saves small).
                if (list == null || list.Count == 0) return;
                raw = new List<int>();
                for (int i = 0; i < list.Count; i++) raw.Add(toInt(list[i]));
            }
            Scribe_Collections.Look(ref raw, label, LookMode.Value);
            if (IsLoading)
            {
                list = new List<TId>();
                if (raw != null)
                {
                    for (int i = 0; i < raw.Count; i++)
                    {
                        if (raw[i] > 0) list.Add(fromInt(raw[i]));
                    }
                }
            }
            if (list == null) list = new List<TId>();
        }

        public static void LookStringList(ref List<string> list, string label)
        {
            if (Scribe.mode == LoadSaveMode.Saving && (list == null || list.Count == 0)) return;
            Scribe_Collections.Look(ref list, label, LookMode.Value);
            if (list == null) list = new List<string>();
            if (IsLoading) list.RemoveAll(s => s == null);
        }

        public static void LookEntityRefList(ref List<EntityRef> list, string label)
        {
            List<string> raw = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (list == null || list.Count == 0) return;
                raw = new List<string>();
                for (int i = 0; i < list.Count; i++) raw.Add(list[i].ToString());
            }
            Scribe_Collections.Look(ref raw, label, LookMode.Value);
            if (IsLoading)
            {
                list = new List<EntityRef>();
                if (raw != null)
                {
                    for (int i = 0; i < raw.Count; i++)
                    {
                        EntityRef r = EntityRef.Parse(raw[i]);
                        if (r.IsValid) list.Add(r);
                    }
                }
            }
            if (list == null) list = new List<EntityRef>();
        }

        // ------------------------------------------------------------------ enums as strings

        /// <summary>
        /// Persists an enum by name without ever logging a red error for an unknown name: an unknown
        /// or missing value yields <paramref name="fallback"/> and sets <paramref name="malformed"/>
        /// (used where a caller wants to quarantine instead, for example settings templates).
        /// </summary>
        public static void LookEnum<TEnum>(ref TEnum value, string label, TEnum fallback, ref bool malformed) where TEnum : struct
        {
            // The fallback is the default: it is not written, and a missing node loads as it.
            string s = Scribe.mode == LoadSaveMode.Saving && !value.Equals(fallback) ? value.ToString() : null;
            Scribe_Values.Look(ref s, label);
            if (IsLoading)
            {
                TEnum parsed;
                if (s == null)
                {
                    value = fallback;
                }
                else if (Enum.TryParse(s, false, out parsed) && Enum.IsDefined(typeof(TEnum), parsed))
                {
                    value = parsed;
                }
                else
                {
                    value = fallback;
                    malformed = true;
                }
            }
        }

        public static void LookEnum<TEnum>(ref TEnum value, string label, TEnum fallback) where TEnum : struct
        {
            bool ignored = false;
            LookEnum(ref value, label, fallback, ref ignored);
        }

        public static string Truncate(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
