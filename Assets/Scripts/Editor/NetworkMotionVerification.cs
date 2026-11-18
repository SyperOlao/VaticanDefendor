using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VaticanDefendor.Agents;
using VaticanDefendor.Client;
using VaticanDefendor.Combat;
using VaticanDefendor.Movement;
using VaticanDefendor.Navigation;
using VaticanDefendor.Network;
using VaticanDefendor.Session;
using VaticanDefendor.Waves;

namespace VaticanDefendor.Editor
{
    // Real client/server Worlds and serialized ghosts; no replacement networking or scene writes.
    [InitializeOnLoad]
    public static class NetworkMotionVerification
    {
        private const string Running = "VaticanDefendor.MotionVerification.Running";
        private static int _stage;
        private static double _since;
        private static float3 _teleport;
        private static float3 _previous;
        private static bool _snapped;

        static NetworkMotionVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += Changed;
        }

        public static void RunRules()
        {
            Debug.Log(CombatFeelVerification.Run());
            Debug.Log(GameplayVerification.Run());
            Debug.Log(EnemyNavigationVerification.Run());
        }

        public static void Run()
        {
            SessionState.SetBool(Running, true);
            SessionState.SetBool(Running + ".Finished", false);
            SessionState.SetFloat(Running + ".Started", (float)EditorApplication.timeSinceStartup);
            EditorSceneManager.OpenScene(NetworkPrototypeAssets.MainScenePath);
            LocalOnlinePlay.PlayInEditor();
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(Running, false)) return;
            if (EditorApplication.timeSinceStartup - SessionState.GetFloat(Running + ".Started", 0) > 120)
            { Finish(3, "Timeout at stage " + _stage); return; }
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling) return;
            try
            {
                World client = null, server = null;
                foreach (World world in World.All)
                {
                    if (world.IsClient()) client = world;
                    if (world.IsServer()) server = world;
                }
                if (client == null || server == null) return;
                EntityManager ce = client.EntityManager, se = server.EntityManager;
                using var status = ce.CreateEntityQuery(typeof(ClientConnectionStatus));
                using var ch = ce.CreateEntityQuery(typeof(HeroMoveInput), typeof(LocalTransform));
                using var sh = se.CreateEntityQuery(typeof(HeroMoveInput), typeof(LocalTransform));
                using var waves = se.CreateEntityQuery(typeof(WaveScheduleConfig));
                using var enemies = se.CreateEntityQuery(typeof(EnemyAgentTag), typeof(LocalTransform));
                using var ghosts = ce.CreateEntityQuery(typeof(CombatTargetTag), typeof(GhostInstance));
                using var predicted = ce.CreateEntityQuery(typeof(CombatTargetTag), typeof(GhostInstance), typeof(PredictedGhost));
                if (waves.CalculateEntityCount() != 1) return;
                if (_stage == 0)
                {
                    WaveScheduleConfig config = waves.GetSingleton<WaveScheduleConfig>();
                    config.InitialEnemyCount = 512;
                    config.MaximumSimultaneousEnemies = 512;
                    config.MaximumWaves = 1;
                    config.MaximumSpawnsPerTick = 32;
                    config.SpawnIntervalSeconds = 0.001f;
                    waves.SetSingleton(config);
                    _stage = 1;
                }
                if (status.IsEmptyIgnoreFilter || status.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame ||
                    ch.CalculateEntityCount() != 1 || sh.CalculateEntityCount() != 1) return;
                Entity hero = sh.GetSingletonEntity(), clientHero = ch.GetSingletonEntity();
                float3 position = ce.GetComponentData<LocalTransform>(clientHero).Position;
                if (_stage == 1)
                {
                    if (enemies.CalculateEntityCount() != 512 || ghosts.CalculateEntityCount() != 512) return;
                    using var targets = enemies.ToEntityArray(Allocator.Temp);
                    float3 origin = se.GetComponentData<LocalTransform>(hero).Position;
                    for (int i = 0; i < targets.Length; i++)
                    {
                        EnemyAgentTuning tuning = se.GetComponentData<EnemyAgentTuning>(targets[i]);
                        tuning.SpeedMetersPerSecond = 0;
                        se.SetComponentData(targets[i], tuning);
                        EnemyCombatTuning combat = se.GetComponentData<EnemyCombatTuning>(targets[i]);
                        combat.Damage = 0;
                        se.SetComponentData(targets[i], combat);
                        if (i < 64) se.SetComponentData(targets[i], LocalTransform.FromPosition(origin +
                            new float3((i % 8 - 4) * 0.4f, 0, 5 + i / 8 * 0.2f)));
                    }
                    using var settings = ce.CreateEntityQuery(typeof(NetworkMotionSettings));
                    NetworkMotionSettings motion = settings.GetSingleton<NetworkMotionSettings>();
                    motion.MaximumPredictedEnemies = 4;
                    settings.SetSingleton(motion);
                    _stage = 2; _since = EditorApplication.timeSinceStartup;
                }
                else if (_stage == 2 && EditorApplication.timeSinceStartup - _since > 2)
                {
                    Check(predicted.CalculateEntityCount() > 0 && predicted.CalculateEntityCount() <= 4,
                        "512 enemy ghosts must honor a four-enemy prediction budget.");
                    using var serverGhosts = se.CreateEntityQuery(typeof(EnemyAgentTag), typeof(PredictedGhost));
                    Check(serverGhosts.CalculateEntityCount() == 512, "Client budget must not disable server enemies.");
                    using var settings = ce.CreateEntityQuery(typeof(NetworkMotionSettings));
                    NetworkMotionSettings motion = settings.GetSingleton<NetworkMotionSettings>();
                    motion.MaximumPredictedEnemies = 0;
                    settings.SetSingleton(motion);
                    _stage = 3; _since = EditorApplication.timeSinceStartup;
                }
                else if (_stage == 3 && EditorApplication.timeSinceStartup - _since > 1)
                {
                    Check(predicted.IsEmptyIgnoreFilter, "Zero enemy prediction must leave interpolated ghosts.");
                    Check(ce.HasComponent<PredictedGhost>(clientHero), "Hero must stay predicted.");
                    _teleport = se.GetComponentData<LocalTransform>(hero).Position + new float3(5, 0, 0);
                    _teleport.y = server.GetExistingSystemManaged<NavigationFlowSystem>().Map.Height(_teleport.x, _teleport.z);
                    _previous = position;
                    se.SetComponentData(hero, LocalTransform.FromPosition(_teleport));
                    _stage = 4; _since = EditorApplication.timeSinceStartup;
                }
                else if (_stage == 4)
                {
                    if (math.distance(position, _previous) > 1f) _snapped = true;
                    _previous = position;
                    if (EditorApplication.timeSinceStartup - _since < 1) return;
                    Check(_snapped && math.distance(position, _teleport) < 0.05f,
                        "A five-meter server correction must snap, without slow smoothing.");
                    LocalTransform wrong = ce.GetComponentData<LocalTransform>(clientHero);
                    wrong.Position.x += 8;
                    ce.SetComponentData(clientHero, wrong);
                    _stage = 5; _since = EditorApplication.timeSinceStartup;
                }
                else if (_stage == 5 && EditorApplication.timeSinceStartup - _since > 1)
                {
                    Check(math.distance(se.GetComponentData<LocalTransform>(hero).Position, _teleport) < 0.05f,
                        "Client coordinates must not move the authoritative hero.");
                    Check(math.distance(position, _teleport) < 0.05f, "Prediction must reconcile tampered coordinates.");
                    using var input = ce.CreateEntityQuery(typeof(ClientInputSettings));
                    input.SetSingleton(new ClientInputSettings { HasScriptedInput = 1, ScriptedDirection = new float2(1, 0) });
                    _stage = 6; _since = EditorApplication.timeSinceStartup;
                }
                else if (_stage == 6)
                {
                    float2 actual = se.GetComponentData<LocalTransform>(hero).Position.xz;
                    Check(server.GetExistingSystemManaged<NavigationFlowSystem>().IsStaticWalkable(actual),
                        "Authoritative movement entered a static obstacle.");
                    if (EditorApplication.timeSinceStartup - _since < 6) return;
                    Check(actual.x > _teleport.x + 1, "Hero input did not move the server hero.");
                    Finish(0, "PASS: 512 ghosts, bounded prediction and interpolation, server simulation retained, " +
                        "large correction snap, client tamper reconciliation, input and static obstacles.");
                }
            }
            catch (Exception error) { Finish(3, error.ToString()); }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Finish(int code, string message)
        {
            SessionState.SetInt(Running + ".Code", code);
            SessionState.SetBool(Running + ".Finished", true);
            SessionState.SetBool(Running, false);
            Debug.Log("[NetworkMotionVerification] " + message);
            EditorApplication.ExitPlaymode();
        }

        private static void Changed(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Running + ".Finished", false))
                EditorApplication.delayCall += () => EditorApplication.Exit(SessionState.GetInt(Running + ".Code", 3));
        }
    }
}
