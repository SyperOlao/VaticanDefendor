#if !UNITY_SERVER || UNITY_EDITOR
using System;
using UnityEngine;
using VaticanDefendor.Network;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Client;
using VaticanDefendor.RunRules;
using Unity.Mathematics;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientUiStandBootstrap : MonoBehaviour
    {
        private StandSessionPort _port;

        private void Awake()
        {
            _port = new StandSessionPort();
            ClientShellView view = gameObject.AddComponent<ClientShellView>();
            view.Initialize(_port);
        }

        private void OnGUI()
        {
            GUI.Box(new Rect(8, Screen.height - 132, 430, 122), "UI stand — simulated session, no server authority");
            if (GUI.Button(new Rect(18, Screen.height - 102, 90, 32), "Connected")) _port.SetPhase(ClientConnectionPhase.InGame, "Simulated session");
            if (GUI.Button(new Rect(118, Screen.height - 102, 90, 32), "Lost")) _port.SetPhase(ClientConnectionPhase.Lost, "Transport closed");
            if (GUI.Button(new Rect(218, Screen.height - 102, 90, 32), "Rejected")) _port.SetPhase(ClientConnectionPhase.Error, "Content mismatch");
            if (GUI.Button(new Rect(318, Screen.height - 102, 90, 32), "Idle")) _port.SetPhase(ClientConnectionPhase.Idle, "Ready");
            if (GUI.Button(new Rect(18, Screen.height - 61, 90, 32), "Hit sample")) _port.ShowHit();
            if (GUI.Button(new Rect(118, Screen.height - 61, 90, 32), "Death sample")) _port.ShowDeath();
            if (GUI.Button(new Rect(18, Screen.height - 170, 90, 32), "Battle end")) _port.SetRunPhase(RunPhase.BattleFinished);
            if (GUI.Button(new Rect(118, Screen.height - 170, 90, 32), "Saving")) _port.SetRunPhase(RunPhase.ResultPending);
            if (GUI.Button(new Rect(218, Screen.height - 170, 100, 32), "Confirmed")) _port.SetRunPhase(RunPhase.Completed);
        }

        private sealed class StandSessionPort : IClientSessionPort
        {
            private ClientConnectionPhase _phase = ClientConnectionPhase.Idle;
            private string _message = "Ready";
            private Vector3 _position;
            private ClientRunStatus _run = new ClientRunStatus
            {
                ChurchSeen = 1, ChurchHealth = 600f, ChurchMaximumHealth = 600f,
                ChurchLevel = 1, ChurchPosition = new float3(0f, 0f, -3f)
            };
            private ClientCombatSnapshot _combat = new ClientCombatSnapshot(true,
                new Vector3(0f, 0f, 2f), 53f, 53f, false, 0,
                default, 0f, 0, 0, 0);
            private readonly ClientConstructionSnapshot _construction = new ClientConstructionSnapshot(
                new[]
                {
                    new ClientConstructionOfferModel("structure.wall", StructureKind.Wall,
                        10, 1.2f, 15, 15, 2.4f, 12, 0, 0f, 0f),
                    new ClientConstructionOfferModel("structure.sawmill", StructureKind.Sawmill,
                        50, 1.6f, 15, 15, 2.4f, 12, 25, 10f, 2.6f)
                }, Array.Empty<ClientStructureSnapshot>(), 150, 0, default, 0);

            public ClientSessionSnapshot Read()
            {
                return new ClientSessionSnapshot(_phase, _message, "stand-run", "stand-content",
                    _phase == ClientConnectionPhase.InGame, _position, true,
                    combat: _combat, construction: _construction, run: _run);
            }

            public bool Connect(out string error)
            {
                error = null;
                SetPhase(ClientConnectionPhase.Connecting, "Simulated transport handshake");
                return true;
            }

            public void Disconnect()
            {
                SetPhase(ClientConnectionPhase.Idle, "Ready");
            }

            public void SubmitMovement(Vector2 direction, bool run)
            {
                if (_phase == ClientConnectionPhase.InGame)
                {
                    _position += new Vector3(direction.x, 0, direction.y) * Time.deltaTime * (run ? 5 : 3);
                }
            }

            public void SubmitAttack(Vector2 aim)
            {
                if (_phase == ClientConnectionPhase.InGame)
                    _message = "Attack intent sent; stand has no server authority";
            }

            public void SubmitPause(bool pause)
            {
                _run.Paused = (byte)(pause ? 1 : 0);
                _message = pause ? "Забег на паузе" : "Забег продолжен";
            }

            public void SubmitConstruction(string definitionId, int startX, int startZ,
                int endX, int endZ)
            {
                if (_phase == ClientConnectionPhase.InGame)
                    _message = $"Build intent {definitionId} ({startX},{startZ}) → ({endX},{endZ}); stand has no server authority";
            }

            public void ShowHit()
            {
                _combat = new ClientCombatSnapshot(true, new Vector3(0f, 0f, 2f),
                    23f, 53f, false, _combat.LastEventSequence + 1,
                    CombatAttackOutcome.Hit, 30f, 150, 0, 0);
            }

            public void ShowDeath()
            {
                _combat = new ClientCombatSnapshot(true, new Vector3(0f, 0f, 2f),
                    0f, 53f, true, _combat.LastEventSequence + 1,
                    CombatAttackOutcome.Hit, 23f, 158, 18, 1);
            }

            public void SetRunPhase(RunPhase phase)
            {
                _run.Phase = phase;
                _run.Outcome = RunOutcome.Victory;
                _run.ConfirmedPermanentReward = 0;
                _run.ConfirmedProfileVersion = phase == RunPhase.Completed ? 2 : 0;
            }

            public void Exit() { }
            public void Dispose() { }

            public void SetPhase(ClientConnectionPhase phase, string message)
            {
                _phase = phase;
                _message = message;
            }
        }
    }
}
#endif
