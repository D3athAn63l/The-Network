using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Settings
{
    // Global cast templates (DATA_MODEL § 18.1). They live in ModSettings, outside every save, so the
    // type names in this namespace are frozen and the schema is versioned by NetworkSettingsVersion
    // (SAVE_AND_MIGRATION § 11). Templates hold strings, bands and flags only: never a live Pawn,
    // Faction, Thing or Def.

    public enum TemplateProvenance : byte
    {
        Generated = 0,
        Custom = 1
    }

    public enum ContractorForm : byte
    {
        Solo = 0,
        Duo = 1,
        Crew = 2,
        Team = 3,
        Company = 4,
        Specialist = 5
    }

    /// <summary>
    /// Why a template is inactive. A quarantined template stays in the file (with its original XML
    /// when it was malformed), is excluded from generation and world import, and is shown in the
    /// settings UI (SAVE_AND_MIGRATION § 11).
    /// </summary>
    public sealed class TemplateQuarantine : IExposable
    {
        public string reasonKey;
        public string message;
        public string rawXml;

        public void ExposeData()
        {
            Scribe_Values.Look(ref reasonKey, "reason");
            Scribe_Values.Look(ref message, "message");
            Scribe_Values.Look(ref rawXml, "rawXml");
        }

        public TemplateQuarantine Copy()
        {
            return new TemplateQuarantine { reasonKey = reasonKey, message = message, rawXml = rawXml };
        }
    }

    /// <summary>Where a template's origin might lie. Strings only; missing content is ignored at import.</summary>
    public sealed class OriginHints : IExposable
    {
        public string factionDefName;
        public string packageId;
        public string regionHint;

        public void ExposeData()
        {
            Scribe_Values.Look(ref factionDefName, "factionDef");
            Scribe_Values.Look(ref packageId, "packageId");
            Scribe_Values.Look(ref regionHint, "regionHint");
        }

        public OriginHints Copy()
        {
            return new OriginHints { factionDefName = factionDefName, packageId = packageId, regionHint = regionHint };
        }
    }

    /// <summary>Enough to audit or recreate a generated entry.</summary>
    public sealed class TemplateGeneration : IExposable
    {
        public int generatorVersion;
        public int seed;
        public List<string> nameParts = new List<string>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref generatorVersion, "generatorVersion", 0);
            Scribe_Values.Look(ref seed, "seed", 0);
            NetScribe.LookStringList(ref nameParts, "nameParts");
        }

        public TemplateGeneration Copy()
        {
            return new TemplateGeneration { generatorVersion = generatorVersion, seed = seed, nameParts = new List<string>(nameParts) };
        }
    }

    /// <summary>Fields shared by contractor and Fixer templates.</summary>
    public abstract class CastTemplate : IExposable
    {
        /// <summary>A GUID string created once. Never derived from the name; renaming keeps it.</summary>
        public string templateId;
        public TemplateProvenance provenance = TemplateProvenance.Custom;
        public bool enabled = true;
        public TemplateQuarantine quarantined;
        public string displayName;
        public string nickname;
        public FameBand startingFame = FameBand.Unknown;
        public List<string> specialties = new List<string>();
        public TemplateGeneration generation;

        /// <summary>Runtime only: set while loading when a value could not be read.</summary>
        internal bool malformed;
        internal string malformedDetail;

        public bool IsQuarantined => quarantined != null;
        public bool IsActive => enabled && quarantined == null;
        public abstract string KindKey { get; }

        public virtual void ExposeData()
        {
            Scribe_Values.Look(ref templateId, "templateId");
            bool provMalformed = false;
            // A template without provenance is treated as Custom: regeneration never deletes it.
            NetScribe.LookEnum(ref provenance, "provenance", TemplateProvenance.Custom, ref provMalformed);
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Deep.Look(ref quarantined, "quarantined");
            Scribe_Values.Look(ref displayName, "name");
            Scribe_Values.Look(ref nickname, "nickname");
            bool fameMalformed = false;
            NetScribe.LookEnum(ref startingFame, "startingFame", FameBand.Unknown, ref fameMalformed);
            NetScribe.LookStringList(ref specialties, "specialties");
            Scribe_Deep.Look(ref generation, "generation");
            if (provMalformed) MarkMalformed("provenance");
            if (fameMalformed) MarkMalformed("startingFame");
        }

        protected void MarkMalformed(string field)
        {
            malformed = true;
            malformedDetail = malformedDetail == null ? field : malformedDetail + "," + field;
        }

        protected void CopyBaseTo(CastTemplate t)
        {
            t.templateId = templateId;
            t.provenance = provenance;
            t.enabled = enabled;
            t.quarantined = quarantined?.Copy();
            t.displayName = displayName;
            t.nickname = nickname;
            t.startingFame = startingFame;
            t.specialties = new List<string>(specialties ?? new List<string>());
            t.generation = generation?.Copy();
        }

        public string DisplayLabel
        {
            get
            {
                string n = string.IsNullOrEmpty(displayName) ? "(unnamed)" : displayName;
                return string.IsNullOrEmpty(nickname) ? n : n + " \"" + nickname + "\"";
            }
        }
    }

    /// <summary>
    /// A contractor identity that may exist in new worlds. Phase 1 only stores and snapshots these;
    /// contractor actors are instantiated in Phase 2 from each world's own snapshot.
    /// </summary>
    public sealed class ContractorTemplate : CastTemplate
    {
        public ContractorForm form = ContractorForm.Solo;
        public ExperienceBand startingExperience = ExperienceBand.Green;
        public string doctrineStyle;
        public List<string> mobility = new List<string>();

        /// <summary>
        /// Starts with IssuerProfile. Explicit and never re-derived from form, fame, experience, size,
        /// wealth or mobility. Missing from old or hand-edited data means false (SAVE_AND_MIGRATION § 11).
        /// </summary>
        public bool canIssueWork;

        public OriginHints originHints;

        /// <summary>Runtime only: the loaded XML had no canIssueWork node (older or hand-edited data).</summary>
        internal bool canIssueWorkMissing;

        public override string KindKey => "Contractor";

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                canIssueWorkMissing = Scribe.loader.curXmlParent?["canIssueWork"] == null;
            }
            bool formMalformed = false;
            NetScribe.LookEnum(ref form, "form", ContractorForm.Solo, ref formMalformed);
            bool expMalformed = false;
            NetScribe.LookEnum(ref startingExperience, "startingExperience", ExperienceBand.Green, ref expMalformed);
            Scribe_Values.Look(ref doctrineStyle, "doctrineStyle");
            NetScribe.LookStringList(ref mobility, "mobility");
            // forceSave: the value is always written explicitly, so a later default change can never
            // silently flip it (Scribe omits values equal to the default otherwise).
            Scribe_Values.Look(ref canIssueWork, "canIssueWork", false, true);
            Scribe_Deep.Look(ref originHints, "originHints");
            if (formMalformed) MarkMalformed("form");
            if (expMalformed) MarkMalformed("startingExperience");
        }

        public ContractorTemplate Copy()
        {
            ContractorTemplate t = new ContractorTemplate();
            CopyBaseTo(t);
            t.form = form;
            t.startingExperience = startingExperience;
            t.doctrineStyle = doctrineStyle;
            t.mobility = new List<string>(mobility ?? new List<string>());
            t.canIssueWork = canIssueWork;
            t.originHints = originHints?.Copy();
            return t;
        }
    }

    /// <summary>A Fixer identity. Phase 1 instantiates these as world-local actors.</summary>
    public sealed class FixerTemplate : CastTemplate
    {
        public Band feeBand = Band.Medium;
        public Band speedBand = Band.Medium;
        public Band reliabilityBand = Band.Medium;
        public ReachBand contractorReach = ReachBand.Local;
        public ReachBand geographicReach = ReachBand.Regional;
        public string brokerageStyle;
        public string insuranceStyle;

        /// <summary>How the Fixer runs an Intel search; maps to fee and continuation policy keys at import.</summary>
        public string intelStyle;

        public override string KindKey => "Fixer";

        public override void ExposeData()
        {
            base.ExposeData();
            bool m1 = false, m2 = false, m3 = false, m4 = false, m5 = false;
            NetScribe.LookEnum(ref feeBand, "feeBand", Band.Medium, ref m1);
            NetScribe.LookEnum(ref speedBand, "speedBand", Band.Medium, ref m2);
            NetScribe.LookEnum(ref reliabilityBand, "reliabilityBand", Band.Medium, ref m3);
            NetScribe.LookEnum(ref contractorReach, "contractorReach", ReachBand.Local, ref m4);
            NetScribe.LookEnum(ref geographicReach, "geographicReach", ReachBand.Regional, ref m5);
            Scribe_Values.Look(ref brokerageStyle, "brokerageStyle");
            Scribe_Values.Look(ref insuranceStyle, "insuranceStyle");
            Scribe_Values.Look(ref intelStyle, "intelStyle");
            if (m1) MarkMalformed("feeBand");
            if (m2) MarkMalformed("speedBand");
            if (m3) MarkMalformed("reliabilityBand");
            if (m4) MarkMalformed("contractorReach");
            if (m5) MarkMalformed("geographicReach");
        }

        public FixerTemplate Copy()
        {
            FixerTemplate t = new FixerTemplate();
            CopyBaseTo(t);
            t.feeBand = feeBand;
            t.speedBand = speedBand;
            t.reliabilityBand = reliabilityBand;
            t.contractorReach = contractorReach;
            t.geographicReach = geographicReach;
            t.brokerageStyle = brokerageStyle;
            t.insuranceStyle = insuranceStyle;
            t.intelStyle = intelStyle;
            return t;
        }
    }
}
