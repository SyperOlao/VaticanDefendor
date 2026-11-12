using System;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Scenes;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using VaticanDefendor;
using VaticanDefendor.Combat;

public static class CharacterGpuVerification
{
    private static World _world;
    private static Entity[] _actors;

    public static string Start()
    {
        if (!EditorApplication.isPlaying || _world != null)
        {
            throw new InvalidOperationException("Enter Play Mode once before starting character GPU verification.");
        }
        _world = ClientServerBootstrap.CreateClientWorld("Character GPU Verification");
        Entity Settings = _world.EntityManager.CreateEntity();
        _world.EntityManager.AddComponentObject(Settings, new CombatFeelSettings { Configuration = new CombatFeelConfiguration() });
        SceneSystem.LoadSceneAsync(_world.Unmanaged,
            new Unity.Entities.Hash128(AssetDatabase.AssetPathToGUID("Assets/ActiveCombat/Network/NetworkShared.unity")));
        _world.GetOrCreateSystemManaged<EcsWorldPresentationSystem>().Initialize(
            AssetDatabase.LoadAssetAtPath<CombatRenderCatalog>("Assets/ActiveCombat/Settings/CombatRenderCatalog.asset"));
        if (UnityEngine.EventSystems.EventSystem.current != null)
        {
            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        }
        foreach (UnityEngine.UI.InputField Field in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.InputField>(FindObjectsSortMode.None))
        {
            Field.DeactivateInputField();
        }
        foreach (Canvas Canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            Canvas.enabled = false;
        }
        foreach (Camera Camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            Camera.enabled = false;
        }
        var CameraObject = new GameObject("Character Verification Camera", typeof(Camera));
        Camera View = CameraObject.GetComponent<Camera>();
        View.transform.position = new Vector3(2, 4.5f, -21);
        View.transform.LookAt(new Vector3(0, 1, -30));
        View.orthographic = true;
        View.aspect = 1280f / 720f;
        View.orthographicSize = 3.5f;
        View.clearFlags = CameraClearFlags.SolidColor;
        View.backgroundColor = new Color(0.11f, 0.13f, 0.16f);
        var LightObject = new GameObject("Character Verification Light", typeof(Light));
        LightObject.GetComponent<Light>().type = LightType.Directional;
        LightObject.GetComponent<Light>().intensity = 2;
        LightObject.transform.rotation = Quaternion.Euler(35, -35, 0);
        return "Loading baked character prefabs.";
    }

    public static string Spawn()
    {
        EntityManager Entities = _world.EntityManager;
        using EntityQuery Collection = Entities.CreateEntityQuery(typeof(CharacterVisualPrefab));
        if (Collection.IsEmptyIgnoreFilter)
        {
            throw new InvalidOperationException("Baked character prefabs have not loaded yet.");
        }
        _actors = new Entity[5];
        for (int Index = 0; Index < _actors.Length; Index++)
        {
            Entity Actor = Entities.CreateEntity(typeof(LocalTransform), typeof(CombatHealth), typeof(CombatImpactState));
            Entities.SetComponentData(Actor, LocalTransform.FromPosition(new float3(-4 + Index * 2, 0, -30)));
            Entities.SetComponentData(Actor, new CombatHealth { Current = 100, Maximum = 100 });
            if (Index == 0)
            {
                Entities.AddComponentData(Actor, new HeroActionState { Facing = new float2(0, 1) });
                DynamicBuffer<EquippedWeapon> Weapons = Entities.AddBuffer<EquippedWeapon>(Actor);
                for (int Weapon = 0; Weapon < 3; Weapon++)
                {
                    Weapons.Add(new EquippedWeapon { ModelIndex = Weapon, Range = 2, DashRange = 3, JumpHeight = 0.5f });
                }
            }
            else
            {
                Entities.AddComponent<CombatTargetTag>(Actor);
                Entities.AddComponentData(Actor, new EnemyCombatTuning { Kind = Index - 1, WindupSeconds = 1, RecoverySeconds = 1, DashDuration = 0.3f, HalfWidth = 0.6f, AttackRadiusMeters = 2 });
                Entities.AddComponentData(Actor, new EnemyCombatAttackState { Direction = new float2(0, 1) });
            }
            _actors[Index] = Actor;
        }
        return "Spawned player and four enemy roles.";
    }

    public static string SetAction(int Mode, float Progress, int Weapon = 0)
    {
        EntityManager Entities = _world.EntityManager;
        for (int Index = 0; Index < _actors.Length; Index++)
        {
            Entity Actor = _actors[Index];
            var Impact = new CombatImpactState();
            var Health = new CombatHealth { Current = 100, Maximum = 100 };
            if (Mode == 6)
            {
                Impact.FlashRemaining = 0.18f * (1 - Progress);
            }
            if (Mode == 7)
            {
                Health.Dead = 1;
                Impact.DeathElapsed = 0.5f * Progress;
            }
            Entities.SetComponentData(Actor, Impact);
            Entities.SetComponentData(Actor, Health);
            if (Index == 0)
            {
                HeroActionState Previous = Entities.GetComponentData<HeroActionState>(Actor);
                var Action = new HeroActionState { Facing = new float2(0, 1), ActiveSlot = (byte)Weapon, AttackSequence = Previous.AttackSequence + 1 };
                switch (Mode)
                {
                    case 1: Action.SwingRemaining = 0.3f * (1 - Progress); break;
                    case 2: Action.MotionKind = 2; Action.MotionDuration = 1; Action.MotionElapsed = Progress; break;
                    case 3: Action.GuardRemaining = 1; break;
                    case 4: Action.MotionKind = 1; Action.MotionDuration = 1; Action.MotionElapsed = Progress; break;
                    case 5: Action.ChargeProgress = Progress * 0.7f; break;
                }
                Entities.SetComponentData(Actor, Action);
            }
            else
            {
                var Attack = new EnemyCombatAttackState { Direction = new float2(0, 1), Sequence = 1 };
                if (Mode == 1)
                {
                    Attack.Phase = 1;
                    Attack.Remaining = 1 - Progress;
                }
                Entities.SetComponentData(Actor, Attack);
            }
        }
        return "Applied character action: " + Mode;
    }

    public static object Inspect()
    {
        EntityManager Entities = _world.EntityManager;
        Entities.CompleteAllTrackedJobs();
        return _actors.Select(Actor =>
        {
            Entity Root = Entities.GetComponentData<CharacterVisualInstance>(Actor).Root;
            CharacterVisualRoot State = Entities.GetComponentData<CharacterVisualRoot>(Root);
            CharacterHandPose Hands = Entities.GetComponentData<CharacterHandPose>(Root);
            return new { State.Mode, State.State, State.Time, State.Progress, State.Speed, Hand = Hands.Right.c3.xyz.ToString(), PoseCount = Entities.GetBuffer<CharacterPoseMatrix>(Root).Length };
        }).ToArray();
    }

    public static string VerifyCleanup()
    {
        EntityManager Entities = _world.EntityManager;
        foreach (Entity Actor in _actors)
        {
            Entities.DestroyEntity(Actor);
        }
        _actors = Array.Empty<Entity>();
        return "Destroyed verification actors; presentation cleanup should remove every rig next frame.";
    }

    public static string CountRigs()
    {
        using EntityQuery Rigs = _world.EntityManager.CreateEntityQuery(typeof(CharacterVisualRoot));
        return "Live character rigs: " + Rigs.CalculateEntityCount();
    }
}
