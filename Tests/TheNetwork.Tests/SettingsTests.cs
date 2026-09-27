using System;
using System.Collections.Generic;
using System.IO;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;
using Verse;

namespace TheNetwork.Tests
{
    public static class SettingsTests
    {
        public static string FixtureDir;

        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Settings.MigrationFromV0AndQuarantine", MigrationFromV0));
            t.Add(new KeyValuePair<string, Action>("Settings.RoundTripKeepsQuarantineAndExplicitCanIssue", RoundTrip));
            t.Add(new KeyValuePair<string, Action>("Settings.NewerFileLoadedBestEffort", NewerFile));
            t.Add(new KeyValuePair<string, Action>("Settings.GeneratorShapeAndDeterminism", Generator));
            t.Add(new KeyValuePair<string, Action>("Settings.RegenerateKeepsCustom", Regenerate));
            t.Add(new KeyValuePair<string, Action>("Cast.SnapshotIsolationAndFixersOnly", SnapshotIsolation));
        }

        public static string Fixture(string rel)
        {
            string dir = FixtureDir ?? Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures"), "");
            return Path.Combine(dir, rel);
        }

        public static NetworkSettings LoadSettings(string path)
        {
            NetworkSettings s = new NetworkSettings();
            Scribe.loader.InitLoading(path);
            try
            {
                s.ExposeData();
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }
            // RimWorld calls this in the PostLoadInit pass of the ModSettings object.
            s.AfterLoad();
            return s;
        }

        public static string SaveSettings(NetworkSettings s)
        {
            string path = Path.Combine(Path.GetTempPath(), "thenetwork-settings-" + Guid.NewGuid().ToString("N") + ".xml");
            Scribe.saver.InitSaving(path, "ModSettings");
            try
            {
                s.ExposeData();
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
            }
            return path;
        }

        private static ContractorTemplate C(NetworkSettings s, string id)
        {
            foreach (ContractorTemplate t in s.roster.contractorTemplates) if (t.templateId == id && !(t.IsQuarantined && t.quarantined.reasonKey == "DuplicateTemplateId")) return t;
            return null;
        }

        private static void MigrationFromV0()
        {
            NetworkSettings s = LoadSettings(Fixture("settings/v0_legacy_with_bad_entries.xml"));
            T.Eq(0, s.LoadedVersion, "file without settingsVersion loads as version 0");
            T.Check(s.NeedsWrite, "migration marks the file for rewriting");
            ContractorTemplate deadRed = C(s, "7c9e6679742540de944be07fc1f90ae7");
            T.Check(deadRed != null && !deadRed.IsQuarantined, "Dead Red loaded");
            T.Check(deadRed != null && !deadRed.canIssueWork, "missing canIssueWork defaults to false (never inferred from Company/Famous)");
            ContractorTemplate kessler = C(s, "custom-lone-kessler");
            T.Check(kessler != null && kessler.canIssueWork && kessler.provenance == TemplateProvenance.Custom, "custom entry keeps its explicit canIssueWork = true");
            T.Check(kessler != null && kessler.startingFame == FameBand.Legendary && kessler.form == ContractorForm.Solo, "custom Legendary Solo intact");
            ContractorTemplate handmade = C(s, "handmade-crew");
            T.Check(handmade != null && handmade.provenance == TemplateProvenance.Custom && handmade.enabled, "missing provenance defaults to Custom (regeneration never deletes it)");
            ContractorTemplate bad = C(s, "bad-enum");
            T.Check(bad != null && bad.IsQuarantined && bad.quarantined.reasonKey == "MalformedField", "malformed enum quarantined individually");
            T.Check(bad != null && bad.quarantined.rawXml != null && bad.quarantined.rawXml.Contains("Battalion"), "raw XML of the malformed entry kept");
            T.Check(!bad.IsActive, "quarantined entry inactive");
            bool noIdQuarantined = false, dupQuarantined = false;
            foreach (ContractorTemplate t in s.roster.contractorTemplates)
            {
                if (t.displayName == "No Id Outfit") noIdQuarantined = t.IsQuarantined && t.quarantined.reasonKey == "MissingTemplateId";
                if (t.displayName == "Duplicate Of Dead Red") dupQuarantined = t.IsQuarantined && t.quarantined.reasonKey == "DuplicateTemplateId";
            }
            T.Check(noIdQuarantined, "entry without id quarantined, not given a name-derived id");
            T.Check(dupQuarantined, "duplicate id quarantined");
            T.Eq(1, s.roster.unreadable.Count, "unconstructible entry preserved as raw data");
            T.Check(s.roster.unreadable.Count == 1 && s.roster.unreadable[0].rawXml.Contains("From The Future"), "raw XML of the unreadable entry kept");
            T.Eq(1, s.roster.fixerTemplates.Count, "fixer loaded");
            T.Eq(Band.VeryHigh, s.roster.fixerTemplates[0].reliabilityBand, "fixer bands read");
            T.Eq(ItemOverride.Allowed, s.GetOverride("ModX_Weirdium"), "override read");
            T.Eq(ItemOverride.Blocked, s.GetOverride("SomeRemovedModItem"), "override for a missing def retained");
            T.Eq(3, s.roster.CountActive(true), "the rest of the cast is active");
        }

        private static void RoundTrip()
        {
            NetworkSettings s = LoadSettings(Fixture("settings/v0_legacy_with_bad_entries.xml"));
            string saved = SaveSettings(s);
            string xml = File.ReadAllText(saved);
            T.Check(xml.Contains("<settingsVersion>1</settingsVersion>"), "saved with the current settings version");
            int canIssueNodes = 0, idx = 0;
            while ((idx = xml.IndexOf("<canIssueWork>", idx, StringComparison.Ordinal)) >= 0) { canIssueNodes++; idx++; }
            T.Eq(s.roster.contractorTemplates.Count, canIssueNodes, "canIssueWork written explicitly for every contractor (even false)");
            NetworkSettings again = LoadSettings(saved);
            T.Eq(1, again.LoadedVersion, "reloads as version 1");
            T.Check(!again.NeedsWrite, "no migration on the second load");
            ContractorTemplate bad = C(again, "bad-enum");
            T.Check(bad != null && bad.IsQuarantined && bad.quarantined.rawXml.Contains("Battalion"), "quarantine (with raw XML) survives a save");
            T.Eq(1, again.roster.unreadable.Count, "unreadable entry survives a save exactly once");
            T.Check(C(again, "custom-lone-kessler").canIssueWork, "custom canIssueWork survives");
            T.Check(!C(again, "7c9e6679742540de944be07fc1f90ae7").canIssueWork, "migrated default survives explicitly");
            File.Delete(saved);
        }

        private static void NewerFile()
        {
            NetworkSettings s = LoadSettings(Fixture("settings/v99_newer.xml"));
            T.Check(s.LoadedFromNewerVersion, "newer file detected");
            T.Check(!s.NeedsWrite, "newer file is not marked for rewriting");
            T.Eq(1, s.roster.contractorTemplates.Count, "known fields load best-effort");
            T.Check(s.roster.contractorTemplates[0].canIssueWork, "values read");
            string saved = SaveSettings(s);
            T.Check(File.ReadAllText(saved).Contains("<settingsVersion>99</settingsVersion>"), "a forced save keeps the newer version number");
            File.Delete(saved);
        }

        private static CastGenerator Gen(int seed, int[] counter)
        {
            return new CastGenerator(NamePools.Fallback(), seed, () => "id-" + (++counter[0]), null);
        }

        private static void Generator()
        {
            int[] c1 = { 0 };
            int[] c2 = { 0 };
            List<ContractorTemplate> a = Gen(99, c1).GenerateContractors(100);
            List<ContractorTemplate> b = Gen(99, c2).GenerateContractors(100);
            T.Eq(100, a.Count, "about 100 contractor templates");
            bool same = true;
            HashSet<string> names = new HashSet<string>();
            HashSet<string> ids = new HashSet<string>();
            Dictionary<ContractorForm, int> forms = new Dictionary<ContractorForm, int>();
            int famous = 0, issuersCompany = 0, companies = 0, issuersSolo = 0, solos = 0;
            for (int i = 0; i < a.Count; i++)
            {
                same &= a[i].displayName == b[i].displayName && a[i].form == b[i].form && a[i].canIssueWork == b[i].canIssueWork && a[i].startingFame == b[i].startingFame;
                names.Add(a[i].displayName);
                ids.Add(a[i].templateId);
                T.Check(a[i].provenance == TemplateProvenance.Generated && a[i].generation != null && a[i].generation.generatorVersion == CastGenerator.GeneratorVersion, "generation metadata");
                T.Check(!a[i].templateId.Contains(a[i].displayName), "template id is not name-derived");
                int n;
                forms.TryGetValue(a[i].form, out n);
                forms[a[i].form] = n + 1;
                if (a[i].startingFame >= FameBand.Famous) famous++;
                if (a[i].form == ContractorForm.Company) { companies++; if (a[i].canIssueWork) issuersCompany++; }
                if (a[i].form == ContractorForm.Solo) { solos++; if (a[i].canIssueWork) issuersSolo++; }
                T.Check(a[i].mobility.Contains("Ground"), "mobility present");
            }
            T.Check(same, "same seed and id factory → identical cast");
            T.Eq(100, names.Count, "names unique");
            T.Eq(100, ids.Count, "ids unique");
            T.Check(forms.Count >= 5, "several forms (Solo … Company)");
            T.Check(famous >= 2 && famous <= 25, "a small famous upper tier (" + famous + ")");
            T.Check(companies == 0 || solos == 0 || (issuersCompany / (float)companies) > (issuersSolo / (float)solos), "companies issue work more often than solos");
            List<FixerTemplate> fixers = Gen(99, c1).GenerateFixers(8);
            T.Eq(8, fixers.Count, "a small set of fixers");
            HashSet<string> styles = new HashSet<string>();
            foreach (FixerTemplate f in fixers) styles.Add(f.intelStyle + "/" + f.feeBand + "/" + f.speedBand + "/" + f.reliabilityBand);
            T.Eq(8, styles.Count, "fixers have distinct service profiles");
        }

        private static void Regenerate()
        {
            GlobalNetworkRoster r = new GlobalNetworkRoster();
            int[] counter = { 0 };
            r.contractorTemplates.AddRange(Gen(5, counter).GenerateContractors(3));
            r.contractorTemplates[0].quarantined = new TemplateQuarantine { reasonKey = "MalformedField" };
            ContractorTemplate custom = new ContractorTemplate { templateId = "custom-a", provenance = TemplateProvenance.Custom, displayName = "Mine", canIssueWork = true };
            r.contractorTemplates.Add(custom);
            FixerTemplate customFixer = new FixerTemplate { templateId = "custom-f", provenance = TemplateProvenance.Custom, displayName = "My Fixer", startingFame = FameBand.Legendary };
            r.fixerTemplates.Add(customFixer);
            r.fixerTemplates.AddRange(Gen(5, counter).GenerateFixers(2));
            string quarantinedId = r.contractorTemplates[0].templateId;
            string replacedId = r.contractorTemplates[1].templateId;
            CastGenerator.RegenerateGenerated(r, NamePools.Fallback(), 6, 10, 3, () => "new-" + (++counter[0]), "now");
            T.Check(r.contractorTemplates.Contains(custom) && custom.templateId == "custom-a" && custom.canIssueWork, "custom contractor kept as is");
            T.Check(r.fixerTemplates.Contains(customFixer), "custom Legendary fixer kept");
            T.Check(r.FindTemplate(quarantinedId) != null, "quarantined data is never discarded");
            T.Check(r.FindTemplate(replacedId) == null, "old generated entries replaced");
            T.Eq(1 + 1 + 10, r.contractorTemplates.Count, "custom + quarantined + new generated");
            T.Eq(1 + 3, r.fixerTemplates.Count, "custom + new generated fixers");
            int fresh = 0;
            foreach (ContractorTemplate t in r.contractorTemplates) if (t.templateId.StartsWith("new-")) fresh++;
            T.Eq(10, fresh, "new generated entries get new ids");
            T.Eq(6, r.generation.castSeed, "generation metadata updated");
        }

        private static void SnapshotIsolation()
        {
            TestNet n = new TestNet();
            GlobalNetworkRoster roster = new GlobalNetworkRoster();
            int[] counter = { 0 };
            roster.contractorTemplates.AddRange(Gen(11, counter).GenerateContractors(20));
            roster.fixerTemplates.AddRange(Gen(11, counter).GenerateFixers(4));
            roster.contractorTemplates[3].enabled = false;
            roster.fixerTemplates[1].quarantined = new TemplateQuarantine { reasonKey = "MalformedField" };
            bool issue7 = roster.contractorTemplates[7].canIssueWork;
            string before = SerializeRoster(roster);

            n.ctx.cast.imported = false;
            T.Check(n.ctx.Actors.ImportCast(roster, 1, null), "first import runs");
            T.Check(!n.ctx.Actors.ImportCast(roster, 1, null), "import happens once");
            int fixersMade = n.ctx.Actors.InstantiateFixers();
            T.Eq(3, fixersMade, "only enabled, non-quarantined fixers instantiated");
            T.Eq(19, n.ctx.cast.Count(CastEntryKind.Contractor), "disabled contractor not snapshotted");
            T.Eq(0, n.ctx.cast.CountInstantiated(CastEntryKind.Contractor), "no contractor actors in Phase 1");
            int individuals = 0, contractorActors = 0;
            foreach (NetworkActor a in n.ctx.actors.actors)
            {
                if (a.kind == ActorKind.Individual) individuals++;
                if (a.kind == ActorKind.Organization) contractorActors++;
                T.Check(!a.Has<IssuerProfile>() || a.kind == ActorKind.PlayerProxy, "only the player issues work in Phase 1");
            }
            T.Eq(3, individuals, "fixers are Individual actors");
            T.Eq(0, contractorActors, "no Organization actors");
            foreach (CastEntry e in n.ctx.cast.entries)
            {
                if (e.kind != CastEntryKind.Fixer) continue;
                NetworkActor a = n.ctx.actors.Get(e.actor);
                T.Check(a != null && a.Has<FixerProfile>() && a.Has<IntelSourceProfile>(), "fixer = Individual + FixerProfile + IntelSourceProfile");
                T.Check(a.provenance.templateId == e.templateId && a.provenance.source == ProvenanceSource.GlobalCast, "template id kept as provenance only");
                T.Check(a.id.ToString() != e.templateId, "world-local ActorId");
                KnownCharacter k = n.ctx.characters.Get(a.bindings.embodies);
                T.Check(k != null && k.embodiedBy == a.id && k.custody == CustodyState.Unmaterialized, "fixer embodies a Known Character record with no pawn");
            }
            CastEntry c7 = null;
            foreach (CastEntry e in n.ctx.cast.entries) if (e.templateId == roster.contractorTemplates[7].templateId) c7 = e;
            T.Check(c7 != null && c7.contractor.canIssueWork == issue7, "canIssueWork travels in the snapshot copy");

            // The world never writes back, and later settings edits never reach it.
            T.Eq(before, SerializeRoster(roster), "importing did not change the global roster");
            string fixerName = n.ctx.actors.Get(n.ctx.cast.entries.Find(e => e.kind == CastEntryKind.Fixer).actor).name.Display;
            foreach (FixerTemplate f in roster.fixerTemplates) f.displayName = "Renamed " + f.displayName;
            CastGenerator.RegenerateGenerated(roster, NamePools.Fallback(), 12, 5, 2, () => "x" + (++counter[0]), "later");
            T.Eq(fixerName, n.ctx.actors.Get(n.ctx.cast.entries.Find(e => e.kind == CastEntryKind.Fixer).actor).name.Display, "renaming/regenerating the roster leaves this world's actors unchanged");
            T.Eq(19, n.ctx.cast.Count(CastEntryKind.Contractor), "snapshot unchanged by regeneration");
        }

        public static string SerializeRoster(GlobalNetworkRoster r)
        {
            string path = Path.Combine(Path.GetTempPath(), "thenetwork-roster-" + Guid.NewGuid().ToString("N") + ".xml");
            Scribe.saver.InitSaving(path, "roster");
            try
            {
                r.ExposeData();
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
            }
            string xml = File.ReadAllText(path);
            File.Delete(path);
            return xml;
        }
    }
}
