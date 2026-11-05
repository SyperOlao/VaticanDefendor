using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Entities;
using Unity.NetCode;
using VaticanDefendor.Client.Shell;
using VaticanDefendor.Network;
using VaticanDefendor.Client;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Editor
{
    public static class ClientShellVerification
    {
        [MenuItem("VaticanDefendor/Client/Verify Connection Transitions")]
        public static void VerifyConnectionTransitions()
        {
            World world = ClientServerBootstrap.CreateClientWorld("Connection transition verification");
            try
            {
                EntityManager entities = world.EntityManager;
                Entity state = entities.CreateEntity(typeof(ClientJoinConfiguration),
                    typeof(ClientConnectionStatus), typeof(ClientMoveIntent),
                    typeof(ClientConnectionTarget), typeof(ClientRunStatus));
                entities.SetComponentData(state, new ClientConnectionTarget { Host = "127.0.0.1", Port = 9 });
                entities.SetComponentData(state, new ClientRunStatus { SyncReady = 1, Revision = 7, ChurchSeen = 1 });
                using (var port = new NetcodeClientSessionPort(world))
                {
                    if (!port.Connect(out string error)) throw new InvalidOperationException(error);
                    if (entities.GetComponentData<ClientConnectionStatus>(state).Phase != ClientConnectionPhase.Connecting)
                        throw new InvalidOperationException("Connect did not set Connecting.");
                    ClientRunStatus run = entities.GetComponentData<ClientRunStatus>(state);
                    if (run.SyncReady != 0 || run.Revision != 0 || run.ChurchSeen != 0)
                        throw new InvalidOperationException("Connect did not reset run synchronization.");
                    port.Disconnect();
                    if (entities.GetComponentData<ClientConnectionStatus>(state).Phase != ClientConnectionPhase.Idle)
                        throw new InvalidOperationException("Disconnect did not set Idle.");
                }
                Debug.Log("[ConnectionVerification] PASS: connect, synchronization reset and disconnect.");
            }
            finally
            {
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(world);
                world.Dispose();
            }
        }

        [MenuItem("VaticanDefendor/Client/Verify Shell Contracts")]
        public static void Verify()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/ActiveCombat.unity");
            GameObject canvas = GameObject.Find("MobileCombatCanvas");
            if (canvas == null || canvas.GetComponent<ClientShellView>() == null)
                throw new InvalidOperationException("Saved scene lacks ClientShellView.");
            SerializedObject view = new SerializedObject(canvas.GetComponent<ClientShellView>());
            if (view.FindProperty("_heroPrefab").objectReferenceValue == null ||
                view.FindProperty("_targetPrefab").objectReferenceValue == null ||
                view.FindProperty("_churchPrefab").objectReferenceValue == null ||
                view.FindProperty("_savedBalance").objectReferenceValue == null)
                throw new InvalidOperationException("Shell lost saved prefab or presentation settings.");
            Require(canvas.transform, "PhoneSafeArea/MainMenu/MenuActions/NormalGame");
            Require(canvas.transform, "PhoneSafeArea/PauseOverlay/Paused/Resume");
            Require(canvas.transform, "PhoneSafeArea/SettingsModal/SettingsWindow/Done");
            RequireSurface(canvas.transform, "PhoneSafeArea/SplitContainer/TacticalMap/TacticalMapCanvas");
            RequireSurface(canvas.transform, "PhoneSafeArea/SplitContainer/BattleViewport/BattleRenderSurface");

            PointerOwnership pointers = new PointerOwnership();
            if (!pointers.TryAcquire(7, PointerRegion.Interface) ||
                pointers.TryAcquire(7, PointerRegion.Combat) ||
                pointers.IsOwnedBy(7, PointerRegion.Combat))
                throw new InvalidOperationException("UI touch leaked into combat region.");
            pointers.Release(7);
            if (!pointers.TryAcquire(7, PointerRegion.Combat))
                throw new InvalidOperationException("Released touch could not enter combat region.");

            FakeView fakeView = new FakeView();
            FakePort fakePort = new FakePort();
            using (ClientShellPresenter presenter = new ClientShellPresenter(fakeView, fakePort, 0.25f))
            {
                AssertScreen(fakeView, ClientShellScreen.Launch);
                fakeView.RaiseStart();
                AssertScreen(fakeView, ClientShellScreen.Connecting);
                fakePort.Phase = ClientConnectionPhase.InGame;
                presenter.Tick();
                AssertScreen(fakeView, ClientShellScreen.Game);
                presenter.SubmitMovement(Vector2.right, false);
                if (fakePort.Movement != Vector2.right) throw new InvalidOperationException("Game movement did not reach port.");
                presenter.SubmitAttack(Vector2.up);
                if (fakePort.AttackCount != 1 || fakePort.AttackAim != Vector2.up)
                    throw new InvalidOperationException("Game attack did not reach port.");
                fakePort.Run.Phase = RunPhase.BattleFinished;
                fakePort.Run.Outcome = RunOutcome.Defeat;
                presenter.Tick();
                if (fakeView.Model.Message != "Бой завершён")
                    throw new InvalidOperationException("Battle finished message is missing.");
                presenter.SubmitAttack(Vector2.up);
                presenter.SubmitMovement(Vector2.right, false);
                if (fakePort.AttackCount != 1 || fakePort.Movement != Vector2.zero)
                    throw new InvalidOperationException("Terminal run accepted client intent.");
                fakePort.Run.Phase = RunPhase.ResultPending;
                presenter.Tick();
                if (fakeView.Model.Message != "Результат сохраняется")
                    throw new InvalidOperationException("Pending result message is missing.");
                fakePort.Run.Phase = RunPhase.Completed;
                fakePort.Run.ConfirmedPermanentReward = 7;
                presenter.Tick();
                if (fakeView.Model.Message != "Награда подтверждена: +7")
                    throw new InvalidOperationException("Confirmed reward message is missing.");
                fakePort.Run = default;
                fakePort.Run.Paused = 1;
                presenter.Tick();
                if (fakeView.Model.Message != "Забег на паузе")
                    throw new InvalidOperationException("Pause state is missing from MVP.");
                fakePort.Run.Paused = 0;
                fakeView.RaiseMenu();
                AssertScreen(fakeView, ClientShellScreen.Menu);
                if (fakePort.LastPause != 1)
                    throw new InvalidOperationException("Menu did not request server pause.");
                presenter.SubmitMovement(Vector2.right, false);
                presenter.SubmitAttack(Vector2.right);
                if (fakePort.Movement != Vector2.zero) throw new InvalidOperationException("Menu allowed hero movement.");
                if (fakePort.AttackCount != 1) throw new InvalidOperationException("Menu allowed hero attack.");
                fakeView.RaiseSettings();
                AssertScreen(fakeView, ClientShellScreen.Settings);
                fakeView.RaiseMapRatio(1f / 3f);
                if (Mathf.Abs(fakeView.Model.MapFraction - 1f / 3f) > 0.001f)
                    throw new InvalidOperationException("Map ratio did not reach ViewModel.");
                fakeView.RaiseBack();
                AssertScreen(fakeView, ClientShellScreen.Menu);
                fakePort.Phase = ClientConnectionPhase.Lost;
                presenter.Tick();
                AssertScreen(fakeView, ClientShellScreen.Lost);
                if (fakeView.Model.ActionLabel != "Reconnect")
                    throw new InvalidOperationException("Reconnect action is missing.");
                fakePort.Phase = ClientConnectionPhase.Error;
                fakePort.Message = "Join rejected: SessionExpired";
                presenter.Tick();
                AssertScreen(fakeView, ClientShellScreen.Error);
                if (!fakeView.Model.Message.Contains("SessionExpired"))
                    throw new InvalidOperationException("Expired session refusal is missing.");
                fakePort.Phase = ClientConnectionPhase.InGame;
                presenter.Tick();
                fakeView.RaiseMenu();
                fakeView.RaiseResume();
                AssertScreen(fakeView, ClientShellScreen.Game);
                if (fakePort.LastPause != 0)
                    throw new InvalidOperationException("Resume did not request server unpause.");
                fakeView.RaiseExit();
                if (!fakePort.ExitCalled) throw new InvalidOperationException("Exit did not reach the port.");
                fakeView.RaiseLeave();
                AssertScreen(fakeView, ClientShellScreen.Launch);
            }
            fakePort.Phase = ClientConnectionPhase.Idle;
            fakeView.RaiseStart();
            if (fakePort.Phase != ClientConnectionPhase.Idle)
                throw new InvalidOperationException("Disposed Presenter kept a View subscription.");

            EditorSceneManager.OpenScene("Assets/ActiveCombat/Network/ClientUiStand.unity");
            if (GameObject.Find("Client UI Stand")?.GetComponent<ClientUiStandBootstrap>() == null)
                throw new InvalidOperationException("UI stand scene lacks its bootstrap.");
            Debug.Log("[ClientShellVerification] Saved layout, scene references, pointer exclusivity, Presenter transitions and UI stand passed.");
        }

        private static void Require(Transform root, string path)
        {
            if (root.Find(path) == null) throw new InvalidOperationException($"Saved layout is missing {path}.");
        }

        private static void RequireSurface(Transform root, string path)
        {
            Transform item = root.Find(path);
            CombatRenderSurface surface = item != null ? item.GetComponent<CombatRenderSurface>() : null;
            if (surface == null || surface.OutputCamera == null || surface.Image == null)
                throw new InvalidOperationException($"Saved render surface is incomplete: {path}.");
        }

        private static void AssertScreen(FakeView view, ClientShellScreen expected)
        {
            if (view.Model.Screen != expected)
                throw new InvalidOperationException($"Expected {expected}, got {view.Model.Screen}.");
        }

        private sealed class FakeView : IClientShellView
        {
            public event Action StartRequested;
            public event Action MenuRequested;
            public event Action ResumeRequested;
            public event Action SettingsRequested;
            public event Action BackRequested;
            public event Action LeaveRequested;
            public event Action ExitRequested;
            public event Action<float> MapRatioRequested;
            public ClientShellViewModel Model;
            public void Render(ClientShellViewModel model) { Model = model; }
            public void RaiseStart() { StartRequested?.Invoke(); }
            public void RaiseMenu() { MenuRequested?.Invoke(); }
            public void RaiseSettings() { SettingsRequested?.Invoke(); }
            public void RaiseBack() { BackRequested?.Invoke(); }
            public void RaiseResume() { ResumeRequested?.Invoke(); }
            public void RaiseLeave() { LeaveRequested?.Invoke(); }
            public void RaiseExit() { ExitRequested?.Invoke(); }
            public void RaiseMapRatio(float value) { MapRatioRequested?.Invoke(value); }
        }

        private sealed class FakePort : IClientSessionPort
        {
            public ClientConnectionPhase Phase = ClientConnectionPhase.Idle;
            public Vector2 Movement;
            public Vector2 AttackAim;
            public int AttackCount;
            public bool ExitCalled;
            public ClientRunStatus Run;
            public string Message = "test";
            public int LastPause = -1;
            public ClientSessionSnapshot Read() => new ClientSessionSnapshot(Phase, Message, "run", "release",
                true, Vector3.zero, true, run: Run);
            public bool Connect(out string error) { error = null; Phase = ClientConnectionPhase.Connecting; return true; }
            public void Disconnect() { Phase = ClientConnectionPhase.Idle; }
            public void SubmitMovement(Vector2 direction, bool run) { Movement = direction; }
            public void SubmitAttack(Vector2 aim) { AttackAim = aim; AttackCount++; }
            public void SubmitPause(bool pause) { LastPause = pause ? 1 : 0; }
            public void SubmitConstruction(string definitionId, int startX, int startZ,
                int endX, int endZ) { }
            public void Exit() { ExitCalled = true; }
            public void Dispose() { }
        }
    }
}
