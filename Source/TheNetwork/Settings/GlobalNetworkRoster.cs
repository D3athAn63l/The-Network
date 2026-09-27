using System;
using System.Collections.Generic;
using System.Xml;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Settings
{
    public sealed class RosterGeneration : IExposable
    {
        public int generatorVersion;
        public string lastGeneratedAt;
        public int castSeed;

        public void ExposeData()
        {
            Scribe_Values.Look(ref generatorVersion, "generatorVersion", 0);
            Scribe_Values.Look(ref lastGeneratedAt, "lastGeneratedAt");
            Scribe_Values.Look(ref castSeed, "castSeed", 0);
        }
    }

    /// <summary>
    /// The global cast (DATA_MODEL § 18.1): who may exist in new Network worlds. Runtime code only
    /// reads it, at world import and in the settings UI. Templates load one by one; an entry that
    /// cannot be constructed is kept as raw XML in <see cref="unreadable"/>, and an entry that loads
    /// but fails validation is quarantined in place. The rest of the cast always loads.
    /// </summary>
    public sealed class GlobalNetworkRoster : IExposable
    {
        public List<ContractorTemplate> contractorTemplates = new List<ContractorTemplate>();
        public List<FixerTemplate> fixerTemplates = new List<FixerTemplate>();
        public RosterGeneration generation = new RosterGeneration();

        /// <summary>Entries that could not be constructed at all; preserved verbatim, never discarded.</summary>
        public List<QuarantineRecord> unreadable = new List<QuarantineRecord>();

        public bool IsEmpty => contractorTemplates.Count == 0 && fixerTemplates.Count == 0;

        public bool NeverGenerated => generation == null || generation.generatorVersion == 0;

        public int CountActive(bool contractors)
        {
            int n = 0;
            if (contractors)
            {
                for (int i = 0; i < contractorTemplates.Count; i++) if (contractorTemplates[i].IsActive) n++;
            }
            else
            {
                for (int i = 0; i < fixerTemplates.Count; i++) if (fixerTemplates[i].IsActive) n++;
            }
            return n;
        }

        public int CountQuarantined()
        {
            int n = unreadable.Count;
            for (int i = 0; i < contractorTemplates.Count; i++) if (contractorTemplates[i].IsQuarantined) n++;
            for (int i = 0; i < fixerTemplates.Count; i++) if (fixerTemplates[i].IsQuarantined) n++;
            return n;
        }

        public void ExposeData()
        {
            List<QuarantineRecord> sink = null;
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // Previously unreadable entries are loaded first so they are kept verbatim.
                NetScribe.LookListTolerant(ref unreadable, "unreadable", "settings.unreadable", null, null);
                sink = new List<QuarantineRecord>();
            }
            else
            {
                NetScribe.LookListTolerant(ref unreadable, "unreadable", "settings.unreadable");
            }
            NetScribe.LookListTolerant(ref contractorTemplates, "contractorTemplates", "settings.contractors", sink, CaptureMalformed);
            NetScribe.LookListTolerant(ref fixerTemplates, "fixerTemplates", "settings.fixers", sink, CaptureMalformed);
            Scribe_Deep.Look(ref generation, "generation");

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (unreadable == null) unreadable = new List<QuarantineRecord>();
                if (sink != null) unreadable.AddRange(sink);
            }
            if (contractorTemplates == null) contractorTemplates = new List<ContractorTemplate>();
            if (fixerTemplates == null) fixerTemplates = new List<FixerTemplate>();
            if (generation == null) generation = new RosterGeneration();
        }

        private static void CaptureMalformed(CastTemplate t, XmlNode node)
        {
            if (t.malformed && t.quarantined == null)
            {
                t.quarantined = new TemplateQuarantine
                {
                    reasonKey = "MalformedField",
                    message = "Unreadable value in: " + t.malformedDetail,
                    rawXml = NetScribe.Truncate(node.OuterXml, NetScribe.MaxRawXmlChars)
                };
            }
        }

        /// <summary>
        /// Structural validation after load and after migration. Quarantines (never deletes) entries
        /// with no template id, no name, or a duplicate id. Returns the number newly quarantined.
        /// </summary>
        public int Validate(List<string> report)
        {
            int newly = 0;
            HashSet<string> seen = new HashSet<string>();
            for (int pass = 0; pass < 2; pass++)
            {
                int count = pass == 0 ? contractorTemplates.Count : fixerTemplates.Count;
                for (int i = 0; i < count; i++)
                {
                    CastTemplate t = pass == 0 ? (CastTemplate)contractorTemplates[i] : fixerTemplates[i];
                    if (t.quarantined != null) continue;
                    string reason = null;
                    if (string.IsNullOrEmpty(t.templateId)) reason = "MissingTemplateId";
                    else if (!seen.Add(t.templateId)) reason = "DuplicateTemplateId";
                    else if (string.IsNullOrEmpty(t.displayName)) reason = "MissingName";
                    if (reason != null)
                    {
                        t.quarantined = new TemplateQuarantine { reasonKey = reason, message = reason + " (" + t.KindKey + ")" };
                        newly++;
                        report?.Add(t.KindKey + " '" + t.DisplayLabel + "' quarantined: " + reason);
                    }
                }
            }
            return newly;
        }

        public CastTemplate FindTemplate(string templateId)
        {
            if (string.IsNullOrEmpty(templateId)) return null;
            for (int i = 0; i < contractorTemplates.Count; i++) if (contractorTemplates[i].templateId == templateId) return contractorTemplates[i];
            for (int i = 0; i < fixerTemplates.Count; i++) if (fixerTemplates[i].templateId == templateId) return fixerTemplates[i];
            return null;
        }

        /// <summary>
        /// Replaces Generated entries only (DATA_MODEL § 18.3). Custom entries, quarantined entries of
        /// either provenance, and unreadable entries are all kept. New generated entries get new ids.
        /// </summary>
        public void ReplaceGenerated(List<ContractorTemplate> newContractors, List<FixerTemplate> newFixers, RosterGeneration gen)
        {
            contractorTemplates.RemoveAll(t => t.provenance == TemplateProvenance.Generated && t.quarantined == null);
            fixerTemplates.RemoveAll(t => t.provenance == TemplateProvenance.Generated && t.quarantined == null);
            contractorTemplates.AddRange(newContractors);
            fixerTemplates.AddRange(newFixers);
            generation = gen;
        }

        public List<string> ActiveNames()
        {
            List<string> names = new List<string>();
            for (int i = 0; i < contractorTemplates.Count; i++) if (contractorTemplates[i].IsActive) names.Add(contractorTemplates[i].displayName);
            for (int i = 0; i < fixerTemplates.Count; i++) if (fixerTemplates[i].IsActive) names.Add(fixerTemplates[i].displayName);
            return names;
        }

        public static string NewTemplateId()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
