#if !UNITY_SERVER || UNITY_EDITOR
using System;
using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Network;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientReconnectProbe : MonoBehaviour
    {
        private enum Step : byte
        {
            WaitForGame,
            WaitForPause,
            WaitForDisconnect,
            WaitForReturn,
            Done
        }

        private NetcodeClientSessionPort _port;
        private Step _step;
        private string _runId;
        private long _gold;
        private int _structures;
        private float _heroHealth;
        private float _deadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateForDevelopmentProbe()
        {
            if (!Debug.isDebugBuild) return;
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (argument != "-vdProbeReconnect") continue;
                new GameObject("Reconnect probe").AddComponent<ClientReconnectProbe>();
                return;
            }
        }

        private void Update()
        {
            if (_step == Step.Done) return;
            if (_port == null)
            {
                foreach (World world in World.All)
                {
                    if (!world.IsCreated || (world.Flags & WorldFlags.GameClient) == 0) continue;
                    _port = new NetcodeClientSessionPort(world);
                    _deadline = Time.realtimeSinceStartup + 30f;
                    break;
                }
                if (_port == null) return;
            }
            ClientSessionSnapshot snapshot = _port.Read();
            if (Time.realtimeSinceStartup > _deadline)
            {
                Fail("Timed out at " + _step + ": " + snapshot.Message);
                return;
            }
            switch (_step)
            {
                case Step.WaitForGame:
                    if (snapshot.Phase != ClientConnectionPhase.InGame || !snapshot.HasHero) return;
                    _runId = snapshot.RunId;
                    _port.SubmitPause(true);
                    _step = Step.WaitForPause;
                    _deadline = Time.realtimeSinceStartup + 10f;
                    break;
                case Step.WaitForPause:
                    if (snapshot.Run.Paused == 0) return;
                    _gold = snapshot.Construction.Gold;
                    _structures = snapshot.Construction.Structures.Length;
                    _heroHealth = snapshot.Combat.HeroHealth;
                    _port.Disconnect();
                    _step = Step.WaitForDisconnect;
                    _deadline = Time.realtimeSinceStartup + 30f;
                    break;
                case Step.WaitForDisconnect:
                    if (!snapshot.CanConnect) return;
                    if (!_port.Connect(out string error))
                    {
                        Fail(error);
                        return;
                    }
                    _step = Step.WaitForReturn;
                    _deadline = Time.realtimeSinceStartup + 30f;
                    break;
                case Step.WaitForReturn:
                    if (snapshot.Phase == ClientConnectionPhase.Error)
                    {
                        Fail(snapshot.Message);
                        return;
                    }
                    if (snapshot.Phase != ClientConnectionPhase.InGame || !snapshot.HasHero) return;
                    if (snapshot.RunId != _runId || snapshot.Run.Paused == 0 ||
                        snapshot.Construction.Gold != _gold ||
                        snapshot.Construction.Structures.Length != _structures ||
                        snapshot.Combat.HeroHealth != _heroHealth)
                    {
                        Fail("Returned state differs from pinned run snapshot.");
                        return;
                    }
                    Debug.Log($"[Stage9Probe] Same client World reconnected to run {_runId}; " +
                        $"gold={_gold} structures={_structures} heroHP={_heroHealth}.");
                    _step = Step.Done;
                    Application.Quit(0);
                    break;
            }
        }

        private void Fail(string reason)
        {
            Debug.LogError("[Stage9Probe] " + reason);
            _step = Step.Done;
            Application.Quit(3);
        }

        private void OnDestroy() => _port?.Dispose();
    }
}
#endif
