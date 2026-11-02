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
        public readonly long StoredGold;

        public ClientStructureSnapshot(int structureId, StructureKind kind, Vector3 position,
            long storedGold)
        {
            StructureId = structureId;
            Kind = kind;
            Position = position;
            StoredGold = storedGold;
        }
    }

    public readonly struct ClientConstructionOfferModel
    {
        public readonly string DefinitionId;
        public readonly StructureKind Kind;
        public readonly int GoldPrice;
        public readonly float HalfExtentMeters;
        public readonly int GridWidth;
        public readonly int GridHeight;
        public readonly float CellSizeMeters;
        public readonly int MaximumWallCells;
        public readonly int ProductionGoldPerCycle;
        public readonly float ProductionCycleSeconds;
        public readonly float PickupOffsetZMeters;

        public ClientConstructionOfferModel(string definitionId, StructureKind kind,
            int goldPrice, float halfExtentMeters, int gridWidth, int gridHeight,
            float cellSizeMeters, int maximumWallCells, int productionGoldPerCycle,
            float productionCycleSeconds, float pickupOffsetZMeters)
        {
            DefinitionId = definitionId;
            Kind = kind;
            GoldPrice = goldPrice;
            HalfExtentMeters = halfExtentMeters;
            GridWidth = gridWidth;
            GridHeight = gridHeight;
            CellSizeMeters = cellSizeMeters;
            MaximumWallCells = maximumWallCells;
            ProductionGoldPerCycle = productionGoldPerCycle;
            ProductionCycleSeconds = productionCycleSeconds;
            PickupOffsetZMeters = pickupOffsetZMeters;
        }
    }

    public readonly struct ClientConstructionSnapshot
    {
        public readonly ClientConstructionOfferModel[] Offers;
        public readonly ClientStructureSnapshot[] Structures;
        public readonly long Gold;
        public readonly uint LastOperationId;
        public readonly ConstructionOutcome LastOutcome;
        public readonly int LastBuiltCount;

        public ClientConstructionSnapshot(ClientConstructionOfferModel[] offers,
            ClientStructureSnapshot[] structures, long gold, uint lastOperationId,
            ConstructionOutcome lastOutcome, int lastBuiltCount)
        {
            Offers = offers;
            Structures = structures;
            Gold = gold;
            LastOperationId = lastOperationId;
            LastOutcome = lastOutcome;
            LastBuiltCount = lastBuiltCount;
        }
    }

    public readonly struct ClientCombatSnapshot
    {
        public readonly bool HasTarget;
        public readonly Vector3 TargetPosition;
        public readonly float TargetHealth;
        public readonly float TargetMaximumHealth;
        public readonly bool TargetDead;
        public readonly uint LastEventSequence;
        public readonly uint LastHitEventSequence;
        public readonly CombatAttackOutcome LastOutcome;
        public readonly float LastDamage;
        public readonly long RunGold;
        public readonly long RunExperience;
        public readonly int RunKills;
        public readonly ClientEnemySnapshot[] Enemies;
        public readonly float HeroHealth;
        public readonly float HeroMaximumHealth;
        public readonly bool HeroDead;

        public ClientCombatSnapshot(bool hasTarget, Vector3 targetPosition, float targetHealth,
            float targetMaximumHealth, bool targetDead, uint lastEventSequence,
            CombatAttackOutcome lastOutcome, float lastDamage, long runGold,
            long runExperience, int runKills, uint lastHitEventSequence = 0,
            ClientEnemySnapshot[] enemies = null, float heroHealth = 0f,
            float heroMaximumHealth = 0f, bool heroDead = false)
        {
            HasTarget = hasTarget;
            TargetPosition = targetPosition;
            TargetHealth = targetHealth;
            TargetMaximumHealth = targetMaximumHealth;
            TargetDead = targetDead;
            LastEventSequence = lastEventSequence;
            LastHitEventSequence = lastHitEventSequence;
            LastOutcome = lastOutcome;
            LastDamage = lastDamage;
            RunGold = runGold;
            RunExperience = runExperience;
            RunKills = runKills;
            Enemies = enemies;
            HeroHealth = heroHealth;
            HeroMaximumHealth = heroMaximumHealth;
            HeroDead = heroDead;
        }
    }

    public readonly struct ClientEnemySnapshot
    {
        public readonly ulong GhostKey;
        public readonly Vector3 Position;
        public readonly float Health;
        public readonly float MaximumHealth;
        public readonly bool Dead;

        public ClientEnemySnapshot(ulong ghostKey, Vector3 position, float health,
            float maximumHealth, bool dead)
        {
            GhostKey = ghostKey;
            Position = position;
            Health = health;
            MaximumHealth = maximumHealth;
            Dead = dead;
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

        public ClientShellViewModel(ClientShellScreen screen, string title, string message,
            string actionLabel, ClientSessionSnapshot session, bool canAct, float mapFraction)
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
        void SubmitConstruction(string definitionId, int startX, int startZ, int endX, int endZ);
        void Exit();
    }
}
#endif
