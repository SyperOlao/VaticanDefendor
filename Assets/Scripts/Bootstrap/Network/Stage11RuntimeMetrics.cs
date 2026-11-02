using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode;
using Unity.Profiling;
using UnityEngine;

namespace VaticanDefendor.Bootstrap
{
    // Development-only measurements. Snapshot bytes exclude RPC and transport overhead.
    public sealed class Stage11RuntimeMetrics : MonoBehaviour
    {
        private readonly List<float> _frames = new List<float>(512);
        private ProfilerRecorder _snapshotBytes;
        private ProfilerRecorder _gcAllocatedBytes;
        private long _snapshotTotal;
        private long _gcAllocatedTotal;
        private int _gen0;
        private int _gen1;
        private int _gen2;
        private float _nextReport;
        private ulong _lastNetworkTx;
        private ulong _lastNetworkRx;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateForDevelopmentMetrics()
        {
            if (!Debug.isDebugBuild) return;
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (argument != "-vdMetrics") continue;
                new GameObject("Stage 11 runtime metrics").AddComponent<Stage11RuntimeMetrics>();
                return;
            }
        }

        private void Awake()
        {
#if UNITY_SERVER
            bool server = true;
#else
            bool server = false;
#endif
            bool headless = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
            if (headless && !server)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }
            _snapshotBytes = ProfilerRecorder.StartNew(ProfilerCategory.Network,
                server ? "Snapshot Size (Server)" : "Snapshot Size (Client)", 1);
            _gcAllocatedBytes = ProfilerRecorder.StartNew(ProfilerCategory.Memory,
                "GC Allocated In Frame", 1);
            _gen0 = GC.CollectionCount(0);
            _gen1 = GC.CollectionCount(1);
            _gen2 = GC.CollectionCount(2);
            _nextReport = Time.realtimeSinceStartup + 5f;
            Debug.Log($"[Stage11Metrics] role={(server ? "server" : "client")} " +
                $"headless={headless} targetFrameRate={Application.targetFrameRate} " +
                $"snapshotRecorder={_snapshotBytes.Valid} gcRecorder={_gcAllocatedBytes.Valid}.");
        }

        private void Update()
        {
            _frames.Add(Time.unscaledDeltaTime * 1000f);
            if (_snapshotBytes.Valid) _snapshotTotal += Math.Max(0L, _snapshotBytes.LastValue);
            if (_gcAllocatedBytes.Valid) _gcAllocatedTotal += Math.Max(0L, _gcAllocatedBytes.LastValue);
            if (Time.realtimeSinceStartup < _nextReport) return;
            _frames.Sort();
            int count = _frames.Count;
            double sum = 0;
            for (int i = 0; i < count; i++) sum += _frames[i];
            float p95 = count == 0 ? 0f : _frames[Math.Min(count - 1, (int)Math.Ceiling(count * 0.95) - 1)];
            float maximum = count == 0 ? 0f : _frames[count - 1];
            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);
            ulong networkTx = 0;
            ulong networkRx = 0;
            uint receiveQueuePeak = 0;
            uint sendQueuePeak = 0;
            bool networkAvailable = false;
            foreach (World world in World.All)
            {
                if (!world.IsCreated) continue;
                using (EntityQuery query = world.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkStreamDriver>()))
                {
                    if (query.CalculateEntityCount() != 1) continue;
                    NetworkStreamDriver stream = query.GetSingleton<NetworkStreamDriver>();
                    ref NetworkDriverStore store = ref stream.DriverStore;
                    for (int driverId = store.FirstDriver; driverId < store.LastDriver; driverId++)
                    {
                        var statistics = store.GetDriverRO(driverId).GetStatistics();
                        networkTx += statistics.TxTotalBytes;
                        networkRx += statistics.RxTotalBytes;
                        receiveQueuePeak = Math.Max(receiveQueuePeak, statistics.RxMaximumQueueUsage);
                        sendQueuePeak = Math.Max(sendQueuePeak, statistics.TxMaximumQueueUsage);
                        networkAvailable = true;
                    }
                }
            }
            Debug.Log($"[Stage11Metrics] frames={count} avgMs={(count == 0 ? 0 : sum / count):F2} " +
                $"p95Ms={p95:F2} maxMs={maximum:F2} " +
                $"snapshotBytes={_snapshotTotal} gcAllocBytes={_gcAllocatedTotal} " +
                $"gcCollections={gen0 - _gen0}/{gen1 - _gen1}/{gen2 - _gen2} " +
                $"netTxBytes={networkTx - _lastNetworkTx} netRxBytes={networkRx - _lastNetworkRx} " +
                $"rxQueuePeak={receiveQueuePeak} txQueuePeak={sendQueuePeak} " +
                $"networkAvailable={networkAvailable} " +
                $"snapshotRecorder={_snapshotBytes.Valid} gcRecorder={_gcAllocatedBytes.Valid}.");
            _frames.Clear();
            _snapshotTotal = 0;
            _gcAllocatedTotal = 0;
            _gen0 = gen0;
            _gen1 = gen1;
            _gen2 = gen2;
            _lastNetworkTx = networkTx;
            _lastNetworkRx = networkRx;
            _nextReport = Time.realtimeSinceStartup + 5f;
        }

        private void OnDestroy()
        {
            if (_snapshotBytes.Valid) _snapshotBytes.Dispose();
            if (_gcAllocatedBytes.Valid) _gcAllocatedBytes.Dispose();
        }
    }
}
