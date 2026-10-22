using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public sealed class CombatNavigation : MonoBehaviour
{
    public ActiveCombatGame Game;
    public bool bUpdating => PendingOperation != null;
    private readonly List<NavMeshBuildSource> Sources = new List<NavMeshBuildSource>(256);
    private NavMeshData Data;
    private NavMeshDataInstance Instance;
    private AsyncOperation PendingOperation;
    private int PreviousRevision = -1;
    private int PendingRevision;

    public void EnsureCurrent()
    {
        if (PendingOperation != null)
        {
            if (!PendingOperation.isDone)
            {
                return;
            }
            PendingOperation = null;
            PreviousRevision = PendingRevision;
            PublishRevision();
        }
        if (Data == null)
        {
            Rebuild();
        }
        else if (PreviousRevision != Game.NavigationRevision)
        {
            using var Measurement = CombatPerformanceMarkers.Navigation.Auto();
            CollectSources();
            PendingRevision = Game.NavigationRevision;
            PendingOperation = NavMeshBuilder.UpdateNavMeshDataAsync(Data, BuildSettings(), Sources, BuildBounds());
        }
    }

    public void Rebuild()
    {
        using var Measurement = CombatPerformanceMarkers.Navigation.Auto();
        if (PendingOperation != null)
        {
            NavMeshBuilder.Cancel(Data);
            PendingOperation = null;
        }
        CollectSources();
        NavMeshBuildSettings Settings = BuildSettings();
        if (Data == null)
        {
            Data = new NavMeshData(Settings.agentTypeID);
        }
        if (!NavMeshBuilder.UpdateNavMeshData(Data, Settings, Sources, BuildBounds()))
        {
            throw new System.InvalidOperationException("The match navigation mesh could not be built.");
        }
        if (!Instance.valid)
        {
            Instance = NavMesh.AddNavMeshData(Data);
        }
        PreviousRevision = Game.NavigationRevision;
        PublishRevision();
    }

    private void PublishRevision()
    {
        Game.Dispatch(new CombatCommand { Kind = CombatCommandKind.NavigationMeshReady, Index = PreviousRevision });
    }

    private NavMeshBuildSettings BuildSettings()
    {
        NavMeshBuildSettings Settings = NavMesh.GetSettingsByIndex(0);
        Settings.agentRadius = 0.35f;
        Settings.agentHeight = 1.8f;
        Settings.overrideVoxelSize = true;
        Settings.voxelSize = 0.12f;
        return Settings;
    }

    private Bounds BuildBounds()
    {
        float Span = (Game.ArenaBound + 1) * 2;
        return new Bounds(Vector3.zero, new Vector3(Span, 10, Span));
    }

    private void CollectSources()
    {
        Sources.Clear();
        Sources.Add(new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.Box,
            transform = Matrix4x4.TRS(new Vector3(0, -0.1f, 0), Quaternion.identity, Vector3.one),
            size = new Vector3(BuildBounds().size.x, 0.2f, BuildBounds().size.z), area = 0
        });
        foreach (ActiveCombatGame.StructureState Structure in Game.Structures)
        {
            if (Structure.Rules.Kind == CombatStructureKind.Trap)
            {
                continue;
            }
            Sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.ModifierBox,
                transform = Matrix4x4.TRS(Structure.Position + Vector3.up, Quaternion.identity, Vector3.one),
                size = new Vector3(Structure.HalfExtents.x * 2, 4, Structure.HalfExtents.y * 2), area = 1
            });
        }
        foreach (CombatSnapshotEntry<CombatSceneryState> Entry in Game.Root.Simulation.ReadModel.Scenery.Entries)
        {
            if (Entry.State.bRemoved == 0)
            {
                Sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.ModifierBox,
                    transform = Matrix4x4.TRS((Vector3)Entry.State.Position + Vector3.up, Quaternion.identity, Vector3.one),
                    size = new Vector3(1.6f, 4, 1.6f), area = 1
                });
            }
        }
    }

    private void OnDestroy()
    {
        Instance.Remove();
        if (Data != null)
        {
            NavMeshBuilder.Cancel(Data);
            Destroy(Data);
        }
    }
}
