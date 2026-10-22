using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public sealed class CombatEntityWorld : IDisposable
{
    private readonly World SimulationWorld = new World("ActiveCombatMatch");
    private readonly EntityQuery Bots;
    public EntityManager Manager => SimulationWorld.EntityManager;

    public CombatEntityWorld()
    {
        Bots = Manager.CreateEntityQuery(typeof(CombatBotState));
    }

    public Entity CreateBot(Vector3 Position, float Health, float AttackRange)
    {
        Entity Bot = Manager.CreateEntity(typeof(CombatBotState));
        Manager.SetComponentData(Bot, new CombatBotState { Position = Position, Health = Health, AttackRange = AttackRange });
        return Bot;
    }

    public void SelectTargets(ActiveCombatGame Game)
    {
        using var Measurement = CombatPerformanceMarkers.Targets.Auto();
        if (Bots.IsEmptyIgnoreFilter)
        {
            return;
        }
        NativeArray<CombatTargetCandidate> Candidates = new NativeArray<CombatTargetCandidate>(Game.Structures.Count + 1, Allocator.TempJob);
        NativeList<CombatNavigationObstacle> Obstacles = new NativeList<CombatNavigationObstacle>(Allocator.TempJob);
        Candidates[0] = new CombatTargetCandidate { Position = Game.PlayerPosition, Priority = 1.5f, Radius = 0.4f };
        for (int Index = 0; Index < Game.Structures.Count; Index++)
        {
            ActiveCombatGame.StructureState Structure = Game.Structures[Index];
            float Priority = 1;
            if (Structure.Rules.Kind == CombatStructureKind.Trap)
            {
                Priority = 0.5f;
            }
            else if (Structure.Rules.Kind == CombatStructureKind.Wall)
            {
                Priority = 0.8f;
            }
            int Identity = Structure.Identity;
            Candidates[Index + 1] = new CombatTargetCandidate { Position = Structure.Position, Priority = Priority, Radius = Mathf.Max(Structure.HalfExtents.x, Structure.HalfExtents.y), Identity = Identity };
            if (Structure.Rules.Kind != CombatStructureKind.Trap)
            {
                Obstacles.Add(new CombatNavigationObstacle { Position = new float2(Structure.Position.x, Structure.Position.z), HalfExtents = new float2(Structure.HalfExtents.x, Structure.HalfExtents.y), Identity = Identity });
            }
        }
        int SceneryIdentity = -1;
        foreach (Transform Scenery in Game.SceneryRoot)
        {
            if (Scenery.gameObject.activeSelf)
            {
                Obstacles.Add(new CombatNavigationObstacle { Position = new float2(Scenery.position.x, Scenery.position.z), HalfExtents = new float2(0.8f, 0.8f), Identity = SceneryIdentity });
            }
            SceneryIdentity--;
        }
        try
        {
            new SelectCombatTargetJob { Candidates = Candidates, Obstacles = Obstacles.AsArray(), BotType = Manager.GetComponentTypeHandle<CombatBotState>(false) }.ScheduleParallel(Bots, default).Complete();
        }
        finally
        {
            Candidates.Dispose();
            Obstacles.Dispose();
        }
        foreach (ActiveCombatGame.EnemyState Enemy in Game.Enemies)
        {
            int Target = Manager.GetComponentData<CombatBotState>(Enemy.Entity).TargetIndex;
            Enemy.TargetBuilding = null;
            if (Target > 0)
            {
                Enemy.TargetBuilding = Game.Structures[Target - 1];
            }
        }
    }

    public void Clear()
    {
        Manager.DestroyEntity(Bots);
    }

    public void Dispose()
    {
        if (SimulationWorld.IsCreated)
        {
            SimulationWorld.Dispose();
        }
    }
}
