using System;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor;
using VaticanDefendor.Bootstrap;
using VaticanDefendor.Client;
using VaticanDefendor.Client.Shell;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Content;
using VaticanDefendor.Maps;
using VaticanDefendor.Network;
using VaticanDefendor.Server;
using VaticanDefendor.Waves;

public static class RiverNetworkPlayVerification
{
    private static World _server;
    private static World _client;
    private static NetworkLaunchArguments _serverLaunch;
    private static ContentRelease _release;
    private static RunCheckpointData _checkpoint;
    private static float2 _first;
    private static float2 _second;
    private static float2 _treePosition;
    private static int _treeIdentifier;

    public static object Start()
    {
        if (!EditorApplication.isPlaying)
        {
            throw new InvalidOperationException("Start Play mode before the network verification.");
        }
        foreach (OnlineStartup Startup in UnityEngine.Object.FindObjectsByType<OnlineStartup>(FindObjectsSortMode.None))
        {
            UnityEngine.Object.Destroy(Startup.gameObject);
        }
        _release = Resources.Load<ContentSelection>("NetworkContent").Release;
        string PreviousTicket = Environment.GetEnvironmentVariable("VD_DEV_TICKET");
        string PreviousAccount = Environment.GetEnvironmentVariable("VD_DEV_ACCOUNT_ID");
        try
        {
            Environment.SetEnvironmentVariable("VD_DEV_TICKET", Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("VD_DEV_ACCOUNT_ID", "river-integration-verification");
            string Run = Guid.NewGuid().ToString();
            _serverLaunch = NetworkLaunchArguments.Parse(new[] { "-vdRole", "server", "-vdDevelopmentIdentity", "-vdRunId", Run, "-vdPort", "7994" });
            NetworkLaunchArguments ClientLaunch = NetworkLaunchArguments.Parse(new[] { "-vdRole", "client", "-vdDevelopmentIdentity", "-vdRunId", Run, "-vdPort", "7994" });
            _server = ServerCompositionRoot.Create(_serverLaunch, _release);
            DelayWaves();
            _client = ClientCompositionRoot.Create(ClientLaunch, _release);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VD_DEV_TICKET", PreviousTicket);
            Environment.SetEnvironmentVariable("VD_DEV_ACCOUNT_ID", PreviousAccount);
        }
        return new { Started = true, Width = _release.GetGameplay().RiverMap.WidthMeters };
    }

    private static void DelayWaves()
    {
        using EntityQuery Query = _server.EntityManager.CreateEntityQuery(typeof(WaveState));
        WaveState State = Query.GetSingleton<WaveState>();
        State.NextEventSeconds = 3600;
        Query.SetSingleton(State);
    }

    public static object Status()
    {
        using EntityQuery Connection = _client.EntityManager.CreateEntityQuery(typeof(ClientConnectionStatus));
        using EntityQuery Construction = _client.EntityManager.CreateEntityQuery(typeof(ClientConstructionState));
        ClientConstructionState State = Construction.GetSingleton<ClientConstructionState>();
        return new
        {
            Frame = Time.frameCount,
            Connection = Connection.GetSingleton<ClientConnectionStatus>().Phase.ToString(),
            ServerTrees = Count(_server, StructureKind.Tree), ClientTrees = Count(_client, StructureKind.Tree),
            ServerTowers = Count(_server, StructureKind.Tower), ClientTowers = Count(_client, StructureKind.Tower),
            Operation = State.LastResultOperationId, Outcome = State.LastOutcome.ToString(),
            Restored = _server.GetExistingSystemManaged<ServerCheckpointRestoreSystem>()?.Restored
        };
    }

    private static int Count(World World, StructureKind Kind)
    {
        using EntityQuery Query = World.EntityManager.CreateEntityQuery(typeof(StructureInstance));
        using NativeArray<Entity> Entities = Query.ToEntityArray(Allocator.Temp);
        int Result = 0;
        foreach (Entity Entity in Entities)
        {
            if (World.EntityManager.GetComponentData<StructureInstance>(Entity).Kind == Kind)
            {
                Result++;
            }
        }
        return Result;
    }

    private static bool Clear(float2 Position, int Ignored = 0)
    {
        RiverMapConfiguration Map = _release.GetGameplay().RiverMap;
        if (!Map.CanSupport(Position.x, Position.y, 0.8f, 0) || math.length(Position) < 6)
        {
            return false;
        }
        foreach (RiverMapPlacement Rock in Map.Rocks)
        {
            if (math.distance(Position, new float2(Rock.Horizontal, Rock.Vertical)) < Rock.Radius + 1.2f)
            {
                return false;
            }
        }
        using EntityQuery Query = _server.EntityManager.CreateEntityQuery(typeof(StructureInstance), typeof(LocalTransform), typeof(CombatHealth));
        using NativeArray<Entity> Entities = Query.ToEntityArray(Allocator.Temp);
        foreach (Entity Entity in Entities)
        {
            StructureInstance Structure = _server.EntityManager.GetComponentData<StructureInstance>(Entity);
            if (Structure.StructureId == Ignored || _server.EntityManager.GetComponentData<CombatHealth>(Entity).Dead != 0)
            {
                continue;
            }
            if (ConstructionGeometry.Overlaps(Position, 0.8f, 0, _server.EntityManager.GetComponentData<LocalTransform>(Entity).Position.xz,
                Structure.HalfExtent, Structure.RotationRadians))
            {
                return false;
            }
        }
        return true;
    }

    public static object FindPair()
    {
        for (int Horizontal = -14; Horizontal <= 14; Horizontal++)
        {
            for (int Vertical = -14; Vertical <= 14; Vertical++)
            {
                float2 Center = new float2(Horizontal, Vertical) * 2.4f;
                _first = Center - new float2(0.9f);
                _second = Center + new float2(0.9f);
                if (Clear(_first) && Clear(_second))
                {
                    return new { First = _first.ToString(), Second = _second.ToString() };
                }
            }
        }
        throw new InvalidOperationException("No free pair was found in the live river world.");
    }

    public static object Build(int Index)
    {
        float2 Position = _first;
        if (Index == 1)
        {
            Position = _second;
        }
        else if (Index == 2)
        {
            Position = _treePosition;
        }
        ClientShellView View = UnityEngine.Object.FindFirstObjectByType<ClientShellView>();
        Button Selection = View.GetComponentsInChildren<Button>(true).First(Button => Button.name == "Tower");
        var SelectedKind = (StructureKind)typeof(ClientShellView).GetField("_selectedKind", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(View);
        if (SelectedKind != StructureKind.Tower)
        {
            Selection.onClick.Invoke();
        }
        CombatRenderSurface Surface = View.GetComponentsInChildren<CombatRenderSurface>(true).First(Surface => Surface.name == "TacticalMapCanvas");
        RiverMapConfiguration Map = _release.GetGameplay().RiverMap;
        Vector3 Viewport = Surface.OutputCamera.WorldToViewportPoint(RiverMapAccess.Position(Map, Position));
        var Corners = new Vector3[4];
        Surface.Image.rectTransform.GetWorldCorners(Corners);
        var Screen = new Vector2(Mathf.Lerp(Corners[0].x, Corners[2].x, Viewport.x), Mathf.Lerp(Corners[0].y, Corners[2].y, Viewport.y));
        View.BeginBuildGesture(994, Screen);
        if (Index == 1)
        {
            float2 DirectionPoint = Position + new float2(1.3f, 2.1f);
            Viewport = Surface.OutputCamera.WorldToViewportPoint(RiverMapAccess.Position(Map, DirectionPoint));
            Screen = new Vector2(Mathf.Lerp(Corners[0].x, Corners[2].x, Viewport.x), Mathf.Lerp(Corners[0].y, Corners[2].y, Viewport.y));
        }
        View.EndBuildGesture(994, Screen);
        return new { Requested = Position.ToString(), Via = "World picking and ClientShellView gesture" };
    }

    public static object FellTree()
    {
        using EntityQuery Query = _server.EntityManager.CreateEntityQuery(typeof(DestructibleTree), typeof(StructureInstance), typeof(LocalTransform));
        using NativeArray<Entity> Trees = Query.ToEntityArray(Allocator.Temp);
        foreach (Entity Tree in Trees)
        {
            StructureInstance Structure = _server.EntityManager.GetComponentData<StructureInstance>(Tree);
            float2 Position = _server.EntityManager.GetComponentData<LocalTransform>(Tree).Position.xz;
            if (!Clear(Position, Structure.StructureId))
            {
                continue;
            }
            _treeIdentifier = Structure.StructureId;
            _treePosition = Position;
            CombatDamage.Apply(_server.EntityManager, Tree, 10000);
            return new { Tree = _treeIdentifier, Position = Position.ToString(), DamageApplied = true };
        }
        throw new InvalidOperationException("No clear tree footprint was found.");
    }

    public static object SaveCheckpoint()
    {
        _checkpoint = JsonUtility.FromJson<RunCheckpointData>(JsonUtility.ToJson(RunCheckpointCapture.Capture(_server)));
        _checkpoint.Validate(_serverLaunch.RunId, _serverLaunch.DevelopmentAccountId, _release.ReleaseId);
        if (_checkpoint.structures.Any(Structure => Structure.stableId == _treeIdentifier) || Count(_server, StructureKind.Tower) != 3 ||
            !_checkpoint.structures.Any(Structure => Structure.kind == (byte)StructureKind.Tower && math.abs(Structure.RotationRadians) > 0.1f))
        {
            throw new InvalidOperationException("Checkpoint did not preserve the verified tree and construction state.");
        }
        System.IO.Directory.CreateDirectory("M:/Prototypes/VaticanDefendor/Builds/RiverIntegration");
        System.IO.File.WriteAllText("M:/Prototypes/VaticanDefendor/Builds/RiverIntegration/Checkpoint.json", JsonUtility.ToJson(_checkpoint, true));
        return new { Schema = _checkpoint.schemaVersion, Trees = Count(_server, StructureKind.Tree), Buildings = Count(_server, StructureKind.Tower), PositionsSaved = true };
    }

    public static object RestartServer()
    {
        ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(_server);
        _server.Dispose();
        _server = ServerCompositionRoot.Create(_serverLaunch, _release);
        _server.GetOrCreateSystemManaged<ServerCheckpointRestoreSystem>().Initialize(_checkpoint);
        return new { Restarted = true };
    }

    public static void Reconnect()
    {
        ClientShellView View = UnityEngine.Object.FindFirstObjectByType<ClientShellView>();
        var Button = (Button)typeof(ClientShellView).GetField("_connectButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(View);
        Button.onClick.Invoke();
    }

    public static object VerifyRestored()
    {
        if (!_server.GetExistingSystemManaged<ServerCheckpointRestoreSystem>().Restored ||
            Count(_server, StructureKind.Tree) != _release.GetGameplay().RiverMap.Trees.Length - 1 || Count(_server, StructureKind.Tower) != 3 ||
            Count(_client, StructureKind.Tree) != _release.GetGameplay().RiverMap.Trees.Length - 1 || Count(_client, StructureKind.Tower) != 3)
        {
            throw new InvalidOperationException("The restored server and client did not preserve the river world.");
        }
        return new { Restored = true, TreeDidNotRegrow = true, Towers = 3, Schema = _checkpoint.schemaVersion, SameClientWorld = _client.IsCreated };
    }

    public static void Stop()
    {
        foreach (World World in new[] { _client, _server })
        {
            if (World != null && World.IsCreated)
            {
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(World);
                if (Unity.Entities.World.DefaultGameObjectInjectionWorld == World)
                {
                    Unity.Entities.World.DefaultGameObjectInjectionWorld = null;
                }
                World.Dispose();
            }
        }
        _client = null;
        _server = null;
    }
}
