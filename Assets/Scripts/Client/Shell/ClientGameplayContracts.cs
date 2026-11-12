#if !UNITY_SERVER || UNITY_EDITOR
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Client.Shell
{
    public readonly struct ClientGameplaySnapshot
    {
        public readonly CombatImpactState Impact;
        public readonly float ComboIdleSeconds;
        public readonly ResourceAmounts Resources;
        public readonly HeroActionState Action;
        public readonly EquippedWeapon[] Weapons;
        public readonly ResourceKind GatheringKind;
        public readonly int GatheringRemaining;
        public readonly float ChargeSeconds;
        public readonly ConstructionEconomyTuning Construction;
        public readonly float Stamina;
        public readonly float MaximumStamina;
        public readonly int WaveNumber;
        public readonly byte WavePhase;
        public readonly float WaveCountdown;

        public ClientGameplaySnapshot(ResourceAmounts Resources, HeroActionState Action,
            EquippedWeapon[] Weapons, ResourceKind GatheringKind, int GatheringRemaining, float ChargeSeconds, ConstructionEconomyTuning Construction = default, int WaveNumber = 0, byte WavePhase = 0, float WaveCountdown = 0, float Stamina = 0, float MaximumStamina = 0, CombatImpactState Impact = default, float ComboIdleSeconds = 0)
        {
            this.Impact = Impact;
            this.ComboIdleSeconds = ComboIdleSeconds;
            this.Resources = Resources;
            this.Action = Action;
            this.Weapons = Weapons;
            this.GatheringKind = GatheringKind;
            this.GatheringRemaining = GatheringRemaining;
            this.ChargeSeconds = ChargeSeconds;
            this.Construction = Construction;
            this.WaveNumber = WaveNumber;
            this.WavePhase = WavePhase;
            this.WaveCountdown = WaveCountdown;
            this.Stamina = Stamina;
            this.MaximumStamina = MaximumStamina;
        }
    }

    public interface IClientGameplayPort
    {
        ClientGameplaySnapshot ReadGameplay();
        void SetCharging(bool bCharging);
        void SubmitGathering();
        void SubmitDash();
        void SubmitDefense();
        void SelectWeapon(int Slot);
        void SubmitStructureAction(ConstructionOperation Operation, int StructureId, float Horizontal = 0, float Vertical = 0);
    }
}
#endif
