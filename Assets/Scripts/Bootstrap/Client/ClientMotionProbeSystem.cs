#if !UNITY_SERVER || UNITY_EDITOR
using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Movement;
using VaticanDefendor.Network;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Client
{
    // Development input fixture. Uses the same input component, command stream and rules as UI input.
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup), OrderFirst = true)]
    public partial class ClientMotionProbeSystem : SystemBase
    {
        private double _started;
        private int _segment = -1;
        private int _dash = -1;
        private double _inputAt;
        private float3 _inputPosition;
        private float2 _inputDirection;
        private bool _waiting;

        protected override void OnCreate()
        {
            Enabled = UnityEngine.Debug.isDebugBuild &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "-vdMotionProbe") >= 0;
            RequireForUpdate<ClientRunStatus>();
            RequireForUpdate<ClientInputSettings>();
        }

        protected override void OnUpdate()
        {
            ClientRunStatus status = SystemAPI.GetSingleton<ClientRunStatus>();
            if (status.Phase != RunPhase.Active || status.SyncReady == 0 || status.Paused != 0) return;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            float3 position = default;
            bool found = false;
            foreach (var pose in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<HeroMoveInput, PredictedGhost>())
            {
                position = pose.ValueRO.Position;
                found = true;
                break;
            }
            if (!found) return;
            if (_waiting && math.dot(position.xz - _inputPosition.xz, _inputDirection) > 0.01f)
            {
                UnityEngine.Debug.Log($"[MotionProbe] responseMs={(now - _inputAt) * 1000d:F2} segment={_segment} position={position}.");
                _waiting = false;
            }
            if (_started == 0) _started = now;
            double elapsed = now - _started;
            int segment = (int)(elapsed / 3d);
            float2 direction = (segment % 4) switch
            {
                0 => new float2(1, 0),
                1 => new float2(0, -1),
                2 => new float2(-1, 0),
                _ => new float2(0, 1)
            };
            if (segment != _segment)
            {
                _segment = segment;
                _inputAt = now;
                _inputPosition = position;
                _inputDirection = direction;
                _waiting = true;
                UnityEngine.Debug.Log($"[MotionProbe] input segment={segment} direction={direction} position={position}.");
            }
            SystemAPI.SetSingleton(new ClientInputSettings
            {
                HasScriptedInput = 1,
                ScriptedDirection = direction
            });
            int dash = (int)(elapsed / 6d);
            if (elapsed % 6d >= 1d && dash != _dash)
            {
                _dash = dash;
                ClientMoveIntent intent = SystemAPI.GetSingleton<ClientMoveIntent>();
                intent.PendingDash = 1;
                SystemAPI.SetSingleton(intent);
                UnityEngine.Debug.Log($"[MotionProbe] dash requested segment={segment}.");
            }
        }
    }
}
#endif
