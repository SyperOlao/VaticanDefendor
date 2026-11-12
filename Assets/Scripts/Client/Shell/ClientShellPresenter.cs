#if !UNITY_SERVER || UNITY_EDITOR
using System;
using UnityEngine;
using VaticanDefendor.Network;
using VaticanDefendor.RunRules;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Client.Shell
{
    public sealed class ClientShellPresenter : IDisposable
    {
        private readonly IClientShellView _view;
        private readonly IClientSessionPort _session;
        private ClientShellScreen _overlay;
        private ClientShellScreen _returnScreen;
        private string _localError;
        private bool _disposed;
        private float _mapFraction;

        public ClientShellPresenter(IClientShellView view, IClientSessionPort session, float initialMapFraction)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _mapFraction = Mathf.Clamp(initialMapFraction, 0.2f, 0.5f);
            _view.StartRequested += Start;
            _view.MenuRequested += OpenMenu;
            _view.ResumeRequested += Resume;
            _view.SettingsRequested += OpenSettings;
            _view.BackRequested += Back;
            _view.LeaveRequested += Leave;
            _view.ExitRequested += Exit;
            _view.MapRatioRequested += SetMapRatio;
            Tick();
        }

        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            ClientSessionSnapshot snapshot = _session.Read();
            ClientShellScreen screen = ResolveScreen(snapshot.Phase);
            if (screen != ClientShellScreen.Game)
            {
                _session.SubmitMovement(Vector2.zero, false);
                (_session as IClientGameplayPort)?.SetCharging(false);
            }

            string message = _localError ?? snapshot.Message;
            if (snapshot.Run.Phase != RunPhase.Active && screen == ClientShellScreen.Game)
            {
                message = snapshot.Run.Phase == RunPhase.Completed
                    ? $"Награда подтверждена: +{snapshot.Run.ConfirmedPermanentReward}"
                    : snapshot.Run.Phase == RunPhase.ResultPending
                        ? "Результат сохраняется"
                        : "Бой завершён";
            }
            else if (snapshot.Run.Paused != 0 && screen == ClientShellScreen.Game)
            {
                message = "Забег на паузе";
            }
            string title;
            string action;
            switch (screen)
            {
                case ClientShellScreen.Launch:
                    title = "VaticanDefendor";
                    action = "Start";
                    break;
                case ClientShellScreen.Connecting:
                    title = "Connecting";
                    action = "Connecting...";
                    break;
                case ClientShellScreen.Game:
                    title = "Run";
                    action = string.Empty;
                    break;
                case ClientShellScreen.Menu:
                    title = "Menu";
                    action = "Resume";
                    message = snapshot.Run.Paused != 0 ? "Забег на паузе" : "Ожидание паузы сервера";
                    break;
                case ClientShellScreen.Settings:
                    title = "Display settings";
                    action = "Done";
                    break;
                case ClientShellScreen.Lost:
                    title = "Connection lost";
                    action = "Reconnect";
                    break;
                default:
                    title = "Connection error";
                    action = "Retry";
                    break;
            }

            _view.Render(new ClientShellViewModel(screen, title, message, action,
                snapshot, snapshot.CanConnect && screen != ClientShellScreen.Connecting, _mapFraction,
                (_session as IClientGameplayPort)?.ReadGameplay() ?? default));
        }

        public void SubmitMovement(Vector2 direction, bool run)
        {
            if (_disposed)
            {
                return;
            }

            ClientShellScreen screen = ResolveScreen(_session.Read().Phase);
            bool active = screen == ClientShellScreen.Game &&
                _session.Read().Run.Phase == RunPhase.Active && _session.Read().Run.Paused == 0;
            _session.SubmitMovement(active ? direction : Vector2.zero, active && run);
        }

        public void SubmitAttack(Vector2 aim)
        {
            if (!_disposed && ResolveScreen(_session.Read().Phase) == ClientShellScreen.Game &&
                _session.Read().Run.Phase == RunPhase.Active && _session.Read().Run.Paused == 0)
            {
                _session.SubmitAttack(aim);
            }
        }

        public void SetCharging(bool bCharging)
        {
            if (!_disposed)
            {
                (_session as IClientGameplayPort)?.SetCharging(bCharging);
            }
        }

        public void SubmitGathering()
        {
            if (!_disposed && ResolveScreen(_session.Read().Phase) == ClientShellScreen.Game)
            {
                (_session as IClientGameplayPort)?.SubmitGathering();
            }
        }

        public void SubmitDefense()
        {
            if (!_disposed && ResolveScreen(_session.Read().Phase) == ClientShellScreen.Game &&
                _session.Read().Run.Phase == RunPhase.Active && _session.Read().Run.Paused == 0)
            {
                (_session as IClientGameplayPort)?.SubmitDefense();
            }
        }

        public void SubmitDash()
        {
            if (!_disposed && ResolveScreen(_session.Read().Phase) == ClientShellScreen.Game)
            {
                (_session as IClientGameplayPort)?.SubmitDash();
            }
        }

        public void SelectWeapon(int Slot)
        {
            if (!_disposed && ResolveScreen(_session.Read().Phase) == ClientShellScreen.Game)
            {
                (_session as IClientGameplayPort)?.SelectWeapon(Slot);
            }
        }

        public void SubmitStructureAction(ConstructionOperation Operation, int StructureId, float Horizontal = 0, float Vertical = 0)
        {
            if (!_disposed && ResolveScreen(_session.Read().Phase) == ClientShellScreen.Game &&
                _session.Read().Run.Phase == RunPhase.Active && _session.Read().Run.Paused == 0)
            {
                (_session as IClientGameplayPort)?.SubmitStructureAction(Operation, StructureId, Horizontal, Vertical);
            }
        }

        public void SubmitConstruction(string definitionId, float startX, float startZ, float endX, float endZ)
        {
            if (!_disposed && ResolveScreen(_session.Read().Phase) == ClientShellScreen.Game &&
                _session.Read().Run.Phase == RunPhase.Active && _session.Read().Run.Paused == 0)
                _session.SubmitConstruction(definitionId, startX, startZ, endX, endZ);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _view.StartRequested -= Start;
            _view.MenuRequested -= OpenMenu;
            _view.ResumeRequested -= Resume;
            _view.SettingsRequested -= OpenSettings;
            _view.BackRequested -= Back;
            _view.LeaveRequested -= Leave;
            _view.ExitRequested -= Exit;
            _view.MapRatioRequested -= SetMapRatio;
            _session.SubmitMovement(Vector2.zero, false);
            _session.Dispose();
        }

        private ClientShellScreen ResolveScreen(ClientConnectionPhase phase)
        {
            if (!string.IsNullOrEmpty(_localError))
            {
                return ClientShellScreen.Error;
            }
            if (phase == ClientConnectionPhase.InGame)
            {
                return _overlay == ClientShellScreen.Menu || _overlay == ClientShellScreen.Settings
                    ? _overlay
                    : ClientShellScreen.Game;
            }

            if (phase == ClientConnectionPhase.Idle && _overlay == ClientShellScreen.Settings)
            {
                return ClientShellScreen.Settings;
            }

            _overlay = default;
            switch (phase)
            {
                case ClientConnectionPhase.Idle:
                    return ClientShellScreen.Launch;
                case ClientConnectionPhase.Connecting:
                    return ClientShellScreen.Connecting;
                case ClientConnectionPhase.Lost:
                    return ClientShellScreen.Lost;
                default:
                    return ClientShellScreen.Error;
            }
        }

        private void Start()
        {
            _localError = null;
            if (!_session.Connect(out string error))
            {
                _localError = error;
            }

            Tick();
        }

        private void OpenMenu()
        {
            if (_session.Read().Phase != ClientConnectionPhase.InGame)
            {
                return;
            }

            _overlay = ClientShellScreen.Menu;
            _session.SubmitMovement(Vector2.zero, false);
            _session.SubmitPause(true);
            Tick();
        }

        private void Resume()
        {
            _session.SubmitPause(false);
            _overlay = default;
            Tick();
        }

        private void OpenSettings()
        {
            ClientConnectionPhase phase = _session.Read().Phase;
            if (phase != ClientConnectionPhase.Idle && phase != ClientConnectionPhase.InGame)
            {
                return;
            }

            _returnScreen = phase == ClientConnectionPhase.InGame
                ? (_overlay == ClientShellScreen.Menu ? ClientShellScreen.Menu : ClientShellScreen.Game)
                : ClientShellScreen.Launch;
            _overlay = ClientShellScreen.Settings;
            _session.SubmitMovement(Vector2.zero, false);
            Tick();
        }

        private void Back()
        {
            _overlay = _returnScreen == ClientShellScreen.Menu ? ClientShellScreen.Menu : default;
            Tick();
        }

        private void Leave()
        {
            _session.SubmitMovement(Vector2.zero, false);
            _session.Disconnect();
            _overlay = default;
            Tick();
        }

        private void Exit()
        {
            _session.Exit();
        }

        private void SetMapRatio(float fraction)
        {
            _mapFraction = Mathf.Clamp(fraction, 0.2f, 0.5f);
            Tick();
        }
    }
}
#endif
