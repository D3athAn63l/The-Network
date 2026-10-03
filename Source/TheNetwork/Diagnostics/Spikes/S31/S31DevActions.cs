using LudeonTK;
using UnityEngine;
using Verse;

namespace TheNetwork.Diagnostics.Spikes.S31
{
    /// <summary>
    /// Dev Mode → "The Network (PHYSICAL SPIKES — S31)". A separate category, unreachable from Quick smoke or Full safe regression.
    /// Destructive actions spend the session arm (one arm = one action); "Show current spike state" and "Verify after load" are read-only.
    /// </summary>
    public static class S31DevActions
    {
        private const string Cat = "The Network (PHYSICAL SPIKES — S31)";

        [DebugAction(Cat, "S31 — Arm physical spike...", allowedGameStates = AllowedGameStates.Playing)]
        public static void Arm()
        {
            Find.WindowStack.Add(new Dialog_S31Arm());
        }

        [DebugAction(Cat, "S31 — Create/ensure dedicated test map [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void CreateMap()
        {
            S31Spike.EnsureMapAction();
        }

        [DebugAction(Cat, "S31 — Run M1 normal-exit probe (A) [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void RunNormalExit()
        {
            S31Spike.StartRun(S31Run.Kind.NormalExit);
        }

        [DebugAction(Cat, "S31 — Run M1 map-removal probe (B) [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void RunMapRemoval()
        {
            S31Spike.StartRun(S31Run.Kind.MapRemoval);
        }

        [DebugAction(Cat, "S31 — Run M1 injured-exit probe (C) [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void RunInjuredExit()
        {
            S31Spike.StartRun(S31Run.Kind.InjuredExit);
        }

        [DebugAction(Cat, "S31 — Prepare save/load checkpoint (D) [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void PrepareCheckpoint()
        {
            S31Spike.PrepareCheckpoint();
        }

        [DebugAction(Cat, "S31 — Verify after load (D, read-only)", allowedGameStates = AllowedGameStates.Playing)]
        public static void VerifyAfterLoad()
        {
            S31Spike.VerifyAfterLoad();
        }

        [DebugAction(Cat, "S31 — Run M1 multi-pawn probe (E) [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void RunMulti()
        {
            S31Spike.StartRun(S31Run.Kind.MultiExit);
        }

        [DebugAction(Cat, "S31 — Run M1 populated-world/redress probe (F) [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void RunRedress()
        {
            S31Spike.StartRun(S31Run.Kind.Redress);
        }

        [DebugAction(Cat, "S31 — Rematerialize same pawn (G) [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Rematerialize()
        {
            S31Spike.StartRun(S31Run.Kind.Rematerialize);
        }

        [DebugAction(Cat, "S31 — Show current spike state (read-only)", allowedGameStates = AllowedGameStates.Playing)]
        public static void ShowState()
        {
            S31Spike.ShowState();
        }

        [DebugAction(Cat, "S31 — Cleanup [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Cleanup()
        {
            S31Spike.Cleanup();
        }
    }

    /// <summary>The arm: a modal that states what S31 will create and requires typing the exact phrase. Runtime only.</summary>
    public sealed class Dialog_S31Arm : Window
    {
        private string typed = "";
        private string refusal;

        public Dialog_S31Arm()
        {
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
        }

        public override Vector2 InitialSize => new Vector2(640f, 460f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(inRect);
            Text.Font = GameFont.Medium;
            l.Label("Arm spike S31 (physical, dev only)");
            Text.Font = GameFont.Small;
            l.Gap(6f);
            l.Label(S31Spike.ArmWarning);
            l.Gap(10f);
            l.Label("To arm ONE destructive S31 action, type exactly:  " + S31Ids.ArmPhrase);
            typed = l.TextEntry(typed);
            if (refusal != null) l.Label("Not armed: " + refusal);
            l.Gap(6f);
            if (l.ButtonText("Arm one destructive S31 action"))
            {
                string reason;
                if (S31Spike.TryArm(typed, out reason)) Close();
                else refusal = reason;
            }
            if (l.ButtonText("Cancel")) Close();
            l.End();
        }
    }
}
