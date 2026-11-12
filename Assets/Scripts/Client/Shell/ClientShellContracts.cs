#if !UNITY_SERVER || UNITY_EDITOR
using System;
using UnityEngine;
using VaticanDefendor.Network;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Client.Shell
{
    public enum ClientShellScreen
    {
        Launch,
        Connecting,
        Game,
        Menu,
        Settings,
        Lost,
        Error
    }

    public readonly struct ClientSessionSnapshot
    {
        public readonly ClientConnectionPhase Phase;
        public readonly string Message;
        public readonly string RunId;
        public readonly string ContentReleaseId;
        public readonly bool HasHero;
        public readonly Vector3 HeroPosition;
        public readonly bool CanConnect;
        public readonly string ProfileDisplayName;
        public readonly int ProfileVersion;
        public readonly ClientCombatSnapshot Combat;
        public readonly ClientConstructionSnapshot Construction;
        public readonly ClientRunStatus Run;

        public ClientSessionSnapshot(ClientConnectionPhase phase, string message, string runId,
            string contentReleaseId, bool hasHero, Vector3 heroPosition, bool canConnect,
            string profileDisplayName = null, int profileVersion = 0,
            ClientCombatSnapshot combat = default,
            ClientConstructionSnapshot construction = default,
            ClientRunStatus run = default)
        {
            Phase = phase;
            Message = message;
            RunId = runId;
            ContentReleaseId = contentReleaseId;
            HasHero = hasHero;
            HeroPosition = heroPosition;
            CanConnect = canConnect;
            ProfileDisplayName = profileDisplayName;
            ProfileVersion = profileVersion;
            Combat = combat;
            Construction = construction;
            Run = run;
        }
    }

    public readonly struct ClientStructureSnapshot
    {
        public readonly int StructureId;
        public readonly StructureKind Kind;
        public readonly Vector3 Position;
        public readonly float Health;
        public readonly float MaximumHealth;
        public readonly float HalfExtent;
        public readonly float RotationRadians;
        public readonly int Level;

        public ClientStructureSnapshot(int StructureId, StructureKind Kind, Vector3 Position,
            float Health, float MaximumHealth, float HalfExtent, int Level, float RotationRadians)
        {
            this.StructureId = StructureId;
            this.Kind = Kind;
            this.Position = Position;
            this.Health = Health;
            this.MaximumHealth = MaximumHealth;
            this.HalfExtent = HalfExtent;
            this.RotationRadians = RotationRadians;
            this.Level = Level;
        }
    }

    public readonly struct ClientConstructionOfferModel
    {
        public readonly string DefinitionId;
        public readonly StructureKind Kind;
        public readonly int WoodPrice;
        public readonly int StonePrice;
        public readonly int IronPrice;
        public readonly float HalfExtentMeters;
        public readonly int GridWidth;
        public readonly int GridHeight;
        public readonly float CellSizeMeters;
        public readonly int MaximumWallCells;
        public readonly int ProductionAmountPerCycle;
        public readonly float ProductionCycleSeconds;
        public readonly float PickupOffsetZMeters;

        public ClientConstructionOfferModel(string definitionId, StructureKind kind,
            int woodPrice, float halfExtentMeters, int gridWidth, int gridHeight,
            float cellSizeMeters, int maximumWallCells, int productionAmountPerCycle,
            float productionCycleSeconds, float pickupOffsetZMeters, int StonePrice = 0, int IronPrice = 0)
        {
            DefinitionId = definitionId;
            Kind = kind;
            WoodPrice = woodPrice;
            this.StonePrice = StonePrice;
            this.IronPrice = IronPrice;
            HalfExtentMeters = halfExtentMeters;
            GridWidth = gridWidth;
            GridHeight = gridHeight;
            CellSizeMeters = cellSizeMeters;
            MaximumWallCells = maximumWallCells;
            ProductionAmountPerCycle = productionAmountPerCycle;
            ProductionCycleSeconds = productionCycleSeconds;
            PickupOffsetZMeters = pickupOffsetZMeters;
        }
    }

    public readonly struct ClientConstructionSnapshot
    {
        public readonly ClientConstructionOfferModel[] Offers;
        public readonly ClientStructureSnapshot[] Structures;
        public readonly long Wood;
        public readonly uint LastOperationId;
        public readonly ConstructionOutcome LastOutcome;
        public readonly int LastBuiltCount;

        public ClientConstructionSnapshot(ClientConstructionOfferModel[] offers,
            ClientStructureSnapshot[] structures, long wood, uint lastOperationId,
            ConstructionOutcome lastOutcome, int lastBuiltCount)
        {
            Offers = offers;
            Structures = structures;
            Wood = wood;
            LastOperationId = lastOperationId;
            LastOutcome = lastOutcome;
            LastBuiltCount = lastBuiltCount;
        }
    }

    public readonly struct ClientCombatSnapshot
    {
        public readonly float HeroHealth;
        public readonly float HeroMaximumHealth;
        public readonly bool HeroDead;
        public readonly long Experience;
        public readonly int Kills;

        public ClientCombatSnapshot(float HeroHealth, float HeroMaximumHealth, bool HeroDead, long Experience, int Kills)
        {
            this.HeroHealth = HeroHealth;
            this.HeroMaximumHealth = HeroMaximumHealth;
            this.HeroDead = HeroDead;
            this.Experience = Experience;
            this.Kills = Kills;
        }
    }

    public readonly struct ClientShellViewModel
    {
        public readonly ClientShellScreen Screen;
        public readonly string Title;
        public readonly string Message;
        public readonly string ActionLabel;
        public readonly string RunId;
        public readonly string ContentReleaseId;
        public readonly bool CanAct;
        public readonly bool HasHero;
        public readonly Vector3 HeroPosition;
        public readonly float MapFraction;
        public readonly string ProfileDisplayName;
        public readonly int ProfileVersion;
        public readonly ClientCombatSnapshot Combat;
        public readonly ClientConstructionSnapshot Construction;
        public readonly ClientRunStatus Run;
        public readonly ClientGameplaySnapshot Gameplay;

        public ClientShellViewModel(ClientShellScreen screen, string title, string message,
            string actionLabel, ClientSessionSnapshot session, bool canAct, float mapFraction, ClientGameplaySnapshot Gameplay = default)
        {
            Screen = screen;
            Title = title;
            Message = message;
            ActionLabel = actionLabel;
            RunId = session.RunId;
            ContentReleaseId = session.ContentReleaseId;
            CanAct = canAct;
            HasHero = session.HasHero;
            HeroPosition = session.HeroPosition;
            MapFraction = mapFraction;
            ProfileDisplayName = session.ProfileDisplayName;
            ProfileVersion = session.ProfileVersion;
            Combat = session.Combat;
            Construction = session.Construction;
            Run = session.Run;
            this.Gameplay = Gameplay;
        }
    }

    public interface IClientShellView
    {
        event Action StartRequested;
        event Action MenuRequested;
        event Action ResumeRequested;
        event Action SettingsRequested;
        event Action BackRequested;
        event Action LeaveRequested;
        event Action ExitRequested;
        event Action<float> MapRatioRequested;

        void Render(ClientShellViewModel model);
    }

    public interface IClientSessionPort : IDisposable
    {
        ClientSessionSnapshot Read();
        bool Connect(out string error);
        void Disconnect();
        void SubmitMovement(Vector2 direction, bool run);
        void SubmitAttack(Vector2 aim);
        void SubmitPause(bool pause);
        void SubmitConstruction(string definitionId, float startX, float startZ, float endX, float endZ);
        void Exit();
    }
}
#endif
