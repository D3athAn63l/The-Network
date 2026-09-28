using TheNetwork.Persist;

namespace TheNetwork.Domain.Contractors
{
    /// <summary>
    /// Morale v1 (SIMULATION § 4.2): cohesion, confidence, fatigue and a descriptor persisted with
    /// hysteresis, so a group does not flicker between states. Replaceable policy; the numbers stay
    /// internal and the UI shows only the descriptor.
    /// </summary>
    public static class MoraleModel
    {
        public const int DesperateFunds = 150;

        /// <summary>
        /// The descriptor for the current state. Entry thresholds are stricter than exit thresholds:
        /// the current descriptor is kept until its exit condition is met, unless a higher-priority
        /// state is entered.
        /// </summary>
        public static MoraleDescriptor Evaluate(ContractorSimulation sim, MoraleDescriptor current)
        {
            OrgMorale m = sim.morale;
            Doctrine d = sim.doctrine;
            bool exhausted = current == MoraleDescriptor.Exhausted ? m.fatigue > 0.55f : m.fatigue > 0.75f;
            if (exhausted) return MoraleDescriptor.Exhausted;
            bool desperate = current == MoraleDescriptor.Desperate ? sim.funds < DesperateFunds * 2 : sim.funds < DesperateFunds;
            if (desperate) return MoraleDescriptor.Desperate;
            bool drivenType = (d.ambition + d.greed) * 0.5f > 0.62f;
            bool reckless = current == MoraleDescriptor.Reckless ? m.confidence < 0.45f && drivenType : m.confidence < 0.35f && drivenType;
            if (reckless) return MoraleDescriptor.Reckless;
            bool shaken = current == MoraleDescriptor.Shaken ? m.confidence < 0.4f : m.confidence < 0.3f;
            if (shaken) return MoraleDescriptor.Shaken;
            bool cautious = current == MoraleDescriptor.Cautious ? m.confidence < 0.55f : m.confidence < 0.45f || (m.confidence < 0.5f && d.caution > 0.65f);
            if (cautious) return MoraleDescriptor.Cautious;
            bool confident = current == MoraleDescriptor.Confident ? m.confidence > 0.62f : m.confidence > 0.72f;
            if (confident) return MoraleDescriptor.Confident;
            return MoraleDescriptor.Steady;
        }

        /// <summary>Resolver morale factor (SIMULATION § 3.2): Confident 1.1 … Desperate 0.75.</summary>
        public static float Factor(MoraleDescriptor d)
        {
            switch (d)
            {
                case MoraleDescriptor.Confident: return 1.1f;
                case MoraleDescriptor.Cautious: return 0.95f;
                case MoraleDescriptor.Shaken: return 0.85f;
                case MoraleDescriptor.Exhausted: return 0.8f;
                case MoraleDescriptor.Desperate: return 0.75f;
                default: return 1f;
            }
        }

        /// <summary>A loss: confidence falls; cohesion binds or breaks depending on loyalty.</summary>
        public static void Shock(ContractorSimulation sim, float lossShare, bool leaderLost, int now)
        {
            OrgMorale m = sim.morale;
            float hit = 0.12f + 0.6f * lossShare + (leaderLost ? 0.15f : 0f);
            m.confidence = ContractorService.Clamp(m.confidence - hit, 0f, 1f);
            float bond = sim.doctrine.loyalty - 0.5f;
            m.cohesion = ContractorService.Clamp(m.cohesion + (bond > 0 ? 0.05f * bond : -0.25f * lossShare + 0.2f * bond), 0f, 1f);
            m.fatigue = ContractorService.Clamp(m.fatigue + 0.15f + 0.3f * lossShare, 0f, 1f);
            m.lastShockTick = now;
        }

        public static void Success(ContractorSimulation sim)
        {
            OrgMorale m = sim.morale;
            m.confidence = ContractorService.Clamp(m.confidence + 0.08f, 0f, 1f);
            m.cohesion = ContractorService.Clamp(m.cohesion + 0.02f, 0f, 1f);
            m.fatigue = ContractorService.Clamp(m.fatigue + 0.08f, 0f, 1f);
        }

        /// <summary>Daily drift toward a baseline set by doctrine and recent losses (SIMULATION § 4.1).</summary>
        public static void Drift(ContractorSimulation sim, float days, bool committed, float recentLossWeight)
        {
            OrgMorale m = sim.morale;
            Doctrine d = sim.doctrine;
            float baseline = ContractorService.Clamp(0.5f + 0.1f * d.professionalism + 0.05f * d.ambition - 0.25f * recentLossWeight, 0.2f, 0.75f);
            m.confidence = Toward(m.confidence, baseline, 0.02f * days);
            m.cohesion = Toward(m.cohesion, 0.55f + 0.25f * d.loyalty, 0.01f * days);
            m.fatigue = ContractorService.Clamp(m.fatigue + (committed ? 0.015f : -0.06f) * days, 0f, 1f);
        }

        private static float Toward(float v, float target, float step)
        {
            if (v < target) return v + step > target ? target : v + step;
            return v - step < target ? target : v - step;
        }
    }
}
