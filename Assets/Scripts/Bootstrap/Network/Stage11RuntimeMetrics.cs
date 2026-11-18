using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Profiling.LowLevel.Unsafe;
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
        private ProfilerRecorder _mainThread;
        private long _cpuTotal;
        private long _cpuMax;
        private long _snapshotTotal;
        private long _gcAllocatedTotal;
        private int _gen0;
        private int _gen1;
        private int _gen2;
        private float _nextReport;
        private ulong _lastNetworkTx;
        private ulong _lastNetworkRx;
        private ulong _lastSnapshots;
        private bool _rawSamples;
        private bool _profilesStarted;
        private readonly List<SystemMetric> _systems = new List<SystemMetric>();

        private sealed class SystemMetric
        {
            public string Name;
            public ProfilerRecorder Recorder;
            public long Nanoseconds;
            public long Samples;
        }

        private void StartSystemRecorders()
        {
            _profilesStarted = true;
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (ProfilerRecorderHandle handle in handles)
            {
                var description = ProfilerRecorderHandle.GetDescription(handle);
                string name = description.Name;
                if (!name.Contains("VaticanDefendor.") && !name.Contains("PredictedSimulationSystemGroup") &&
                    !name.Contains("GhostSendSystem") && !name.Contains("GhostUpdateSystem")) continue;
                // Nested markers overlap: report individually; never sum them into total ECS time.
                if (description.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds) continue;
                _systems.Add(new SystemMetric { Name = name,
                    Recorder = ProfilerRecorder.StartNew(description.Category, name, 1,
                        ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame) });
            }
        }

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
            _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            _rawSamples = Array.IndexOf(Environment.GetCommandLineArgs(), "-vdMetricSamples") >= 0;
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
            foreach (SystemMetric metric in _systems)
            {
                if (!metric.Recorder.Valid || metric.Recorder.Count == 0) continue;
                metric.Nanoseconds += Math.Max(0L, metric.Recorder.LastValue);
                metric.Samples++;
            }
            if (_mainThread.Valid)
            {
                _cpuTotal += System.Math.Max(0L, _mainThread.LastValue);
                _cpuMax = System.Math.Max(_cpuMax, _mainThread.LastValue);
            }
            if (_snapshotBytes.Valid) _snapshotTotal += Math.Max(0L, _snapshotBytes.LastValue);
            if (_gcAllocatedBytes.Valid) _gcAllocatedTotal += Math.Max(0L, _gcAllocatedBytes.LastValue);
            if (Time.realtimeSinceStartup < _nextReport) return;
            _frames.Sort();
            int count = _frames.Count;
            double sum = 0;
            for (int i = 0; i < count; i++) sum += _frames[i];
            float p95 = count == 0 ? 0f : _frames[Math.Min(count - 1, (int)Math.Ceiling(count * 0.95) - 1)];
            float p99 = count == 0 ? 0f : _frames[Math.Min(count - 1, (int)Math.Ceiling(count * 0.99) - 1)];
            float maximum = count == 0 ? 0f : _frames[count - 1];
            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);
            ulong networkTx = 0;
            ulong networkRx = 0;
            uint receiveQueuePeak = 0;
            uint sendQueuePeak = 0;
            bool networkAvailable = false;
            float rttMs = 0;
            ulong snapshots = 0;
            foreach (World world in World.All)
            {
                if (!world.IsCreated) continue;
                {
                    using (EntityQuery acknowledgements = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkSnapshotAck>()))
                    {
                        using (var values = acknowledgements.ToComponentDataArray<NetworkSnapshotAck>(Allocator.Temp))
                        {
                            foreach (NetworkSnapshotAck value in values)
                            {
                                if (world.IsClient()) rttMs = value.EstimatedRTT;
                                // Public NfE counter: server = successful EndSend snapshots; client = received.
                                snapshots += value.SnapshotPacketLoss.NumPacketsReceived;
                            }
                        }
                    }
                }
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
                $"p95Ms={p95:F2} p99Ms={p99:F2} wall={Time.realtimeSinceStartupAsDouble:F3} maxMs={maximum:F2} fps={(sum > 0 ? 1000d * count / sum : 0):F2} " +
                $"mainThreadAvgMs={(count > 0 ? _cpuTotal / (double)count / 1000000d : 0):F3} mainThreadMaxMs={_cpuMax / 1000000d:F3} cpuRecorder={_mainThread.Valid} " +
                $"snapshots={(snapshots >= _lastSnapshots ? snapshots - _lastSnapshots : snapshots)} snapshotCounterReset={snapshots < _lastSnapshots} " +
                $"snapshotBytes={_snapshotTotal} gcAllocBytes={_gcAllocatedTotal} " +
                $"gcCollections={gen0 - _gen0}/{gen1 - _gen1}/{gen2 - _gen2} " +
                $"netTxBytes={networkTx - _lastNetworkTx} netRxBytes={networkRx - _lastNetworkRx} " +
                $"rxQueuePeak={receiveQueuePeak} txQueuePeak={sendQueuePeak} " +
                $"networkAvailable={networkAvailable} rttMs={rttMs:F1} " +
                $"snapshotRecorder={_snapshotBytes.Valid} gcRecorder={_gcAllocatedBytes.Valid}.");
            if (_rawSamples)
            {
                var samples = new StringBuilder("[Stage11FrameSamples] durationUs=", count * 6 + 40);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) samples.Append(',');
                    samples.Append((long)Math.Round(_frames[i] * 1000));
                }
                Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", samples.ToString());
                foreach (SystemMetric metric in _systems)
                {
                    Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null,
                        "[Stage11System] name={0} totalNs={1} frames={2} valid={3}",
                        metric.Name, metric.Nanoseconds, metric.Samples, metric.Recorder.Valid);
                    metric.Nanoseconds = 0;
                    metric.Samples = 0;
                }
                if (!_profilesStarted && Array.IndexOf(Environment.GetCommandLineArgs(), "-vdProfileSystems") >= 0) StartSystemRecorders();
            }
            _lastSnapshots = snapshots;
            _frames.Clear();
            _cpuTotal = 0;
            _cpuMax = 0;
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
            foreach (SystemMetric metric in _systems) metric.Recorder.Dispose();
            if (_mainThread.Valid) _mainThread.Dispose();
            if (_snapshotBytes.Valid) _snapshotBytes.Dispose();
            if (_gcAllocatedBytes.Valid) _gcAllocatedBytes.Dispose();
        }
    }
}
