using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RouteLens.EtwExperiment
{
    // Research prototype. Not connected to the application or enabled by default.
    // Only a session successfully created by this object is stopped. No file logging.
    public sealed class EtwExperiment : IDisposable
    {
        private const string SessionName = "RouteLens-Network-Experiment";
        private const uint RealTime = 0x100, SystemLogger = 0x02000000, TracedGuid = 0x00020000, Network = 0x00010000;
        private const uint Query = 0, Stop = 1, ErrorSuccess = 0;
        private const uint ProcessRealTime = 0x100, ProcessRecord = 0x10000000;
        private const ulong InvalidHandle = ulong.MaxValue;
        private readonly object gate = new object();
        private readonly Queue<NetworkObservation> samples = new Queue<NetworkObservation>();
        private readonly EventRecordCallback callback;
        private ulong session, consumer = InvalidHandle;
        private IntPtr properties, loggerName;
        private Thread worker;
        private Timer expiry;
        private bool running, disposed;
        private long rejected, evicted;
        private uint eventsLost, realtimeBuffersLost, processingError, stopError;
        private const int MaximumSamples = 1024;

        public EtwExperiment() { callback = OnEvent; }
        public bool IsRunning { get { lock (gate) return running; } }
        public string LastFailure { get; private set; }

        // Returns a Win32 error, never stops an already existing session.
        public uint Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("EtwExperiment");
                if (running || properties != IntPtr.Zero) throw new InvalidOperationException("Experiment already started.");
                int fixedSize = Marshal.SizeOf(typeof(TraceProperties));
                int totalSize = fixedSize + 2 * (SessionName.Length + 1);
                properties = Marshal.AllocHGlobal(totalSize);
                Marshal.Copy(new byte[totalSize], 0, properties, totalSize);
                TraceProperties config = new TraceProperties();
                config.Header.BufferSize = (uint)totalSize; config.Header.Flags = TracedGuid; config.Header.ClientContext = 1;
                config.BufferSize = 32; config.MinimumBuffers = 4; config.MaximumBuffers = 32;
                config.LogFileMode = RealTime | SystemLogger; config.FlushTimer = 1; config.EnableFlags = Network;
                config.LoggerNameOffset = (uint)fixedSize;
                Marshal.StructureToPtr(config, properties, false);
                uint status = StartTrace(out session, SessionName, properties);
                if (status != ErrorSuccess) { LastFailure = "StartTrace 错误 " + status; ReleaseProperties(); session = 0; return status; }
                loggerName = Marshal.StringToHGlobalUni(SessionName);
                TraceLogfile input = new TraceLogfile(); input.LoggerName = loggerName; input.ProcessTraceMode = ProcessRealTime | ProcessRecord;
                input.Callback = Marshal.GetFunctionPointerForDelegate(callback);
                consumer = OpenTrace(ref input);
                if (consumer == InvalidHandle || (IntPtr.Size == 4 && consumer == uint.MaxValue))
                {
                    uint error = (uint)Marshal.GetLastWin32Error();
                    if (error == 0) error = 1;
                    StopOwnedSession(); ReleaseProperties(); LastFailure = "OpenTrace 错误 " + error; return error;
                }
                running = true;
                ulong trace = consumer;
                worker = new Thread(delegate() { ProcessEvents(trace); }) { IsBackground = true, Name = "RouteLens ETW experiment" };
                expiry = new Timer(delegate { Dispose(); }, null, 120000, Timeout.Infinite);
                worker.Start();
                return 0;
            }
        }

        private void ProcessEvents(ulong trace)
        {
            uint status = ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero);
            bool cleanup;
            lock (gate) { processingError = status; running = false; cleanup = !disposed; }
            if (cleanup) Dispose();
        }

        private void OnEvent(ref EventRecord record)
        {
            if (record.Header.Provider != NetworkEventDecoder.Tcp && record.Header.Provider != NetworkEventDecoder.Udp) return;
            byte opcode = record.Header.Descriptor.Opcode;
            if (opcode != 10 && opcode != 11 && opcode != 26 && opcode != 27) return;
            try
            {
                if (record.UserData == IntPtr.Zero || record.UserDataLength == 0) { lock (gate) rejected++; return; }
                byte[] data = new byte[record.UserDataLength]; Marshal.Copy(record.UserData, data, 0, data.Length);
                DateTime time;
                try { time = DateTime.FromFileTimeUtc(record.Header.Timestamp); } catch (ArgumentOutOfRangeException) { lock (gate) rejected++; return; }
                NetworkObservation value;
                if (!NetworkEventDecoder.TryDecode(record.Header.Provider, record.Header.Descriptor.Version, record.Header.Descriptor.Opcode, time, data, out value))
                { lock (gate) rejected++; return; }
                lock (gate) { if (samples.Count == MaximumSamples) { samples.Dequeue(); evicted++; } samples.Enqueue(value); }
            }
            catch (Exception) { lock (gate) rejected++; }
        }

        public ObservationHealth Health()
        {
            lock (gate)
            {
                if (session != 0 && properties != IntPtr.Zero && ControlTrace(session, null, properties, Query) == 0)
                {
                    TraceProperties p = (TraceProperties)Marshal.PtrToStructure(properties, typeof(TraceProperties));
                    eventsLost = p.EventsLost; realtimeBuffersLost = p.RealTimeBuffersLost;
                }
                return new ObservationHealth { Running = running, EventsLost = eventsLost, RealTimeBuffersLost = realtimeBuffersLost,
                    RejectedEvents = rejected, EvictedObservations = evicted, ProcessTraceError = processingError, StopError = stopError };
            }
        }
        public NetworkObservation[] Snapshot() { lock (gate) return samples.ToArray(); }

        public void Dispose()
        {
            Thread join;
            ulong ownSession, ownConsumer;
            IntPtr ownProperties;
            Timer ownExpiry;
            lock (gate)
            {
                if (disposed) return; disposed = true;
                ownSession = session; session = 0; ownConsumer = consumer; consumer = InvalidHandle;
                ownProperties = properties; properties = IntPtr.Zero; running = false; join = worker; ownExpiry = expiry; expiry = null;
            }
            if (ownExpiry != null) ownExpiry.Dispose();
            if (ownSession != 0 && ownProperties != IntPtr.Zero)
            {
                uint stopped = ControlTrace(ownSession, null, ownProperties, Stop);
                lock (gate)
                {
                    stopError = stopped;
                    if (stopped == 0)
                    {
                        TraceProperties p = (TraceProperties)Marshal.PtrToStructure(ownProperties, typeof(TraceProperties));
                        eventsLost = p.EventsLost; realtimeBuffersLost = p.RealTimeBuffersLost;
                    }
                }
            }
            if (ownConsumer != InvalidHandle) CloseTrace(ownConsumer);
            if (join != null && join != Thread.CurrentThread) join.Join(3000);
            if (ownProperties != IntPtr.Zero) Marshal.FreeHGlobal(ownProperties);
            if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
        }
        private void StopOwnedSession() { if (session != 0) { ControlTrace(session, null, properties, Stop); session = 0; } }
        private void ReleaseProperties()
        {
            if (properties != IntPtr.Zero) { Marshal.FreeHGlobal(properties); properties = IntPtr.Zero; }
            if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct Wnode { public uint BufferSize, ProviderId; public ulong HistoricalContext; public long Timestamp; public Guid Guid; public uint ClientContext, Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct TraceProperties
        {
            public Wnode Header;
            public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            public IntPtr LoggerThreadId; public uint LogFileNameOffset, LoggerNameOffset;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Descriptor { public ushort Id; public byte Version, Channel, Level, Opcode; public ushort Task; public ulong Keyword; }
        [StructLayout(LayoutKind.Sequential)] private struct EventHeader
        {
            public ushort Size, HeaderType, Flags, EventProperty; public uint ThreadId, ProcessId; public long Timestamp;
            public Guid Provider; public Descriptor Descriptor; public uint KernelTime, UserTime; public Guid ActivityId;
        }
        [StructLayout(LayoutKind.Sequential)] private struct EventRecord
        {
            public EventHeader Header; public uint BufferContext; public ushort ExtendedDataCount, UserDataLength;
            public IntPtr ExtendedData, UserData, UserContext;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ClassicHeader { public ushort Size, FieldTypeFlags; public byte Type, Level; public ushort Version; public uint ThreadId, ProcessId; public long Timestamp; public Guid Guid; public uint KernelTime, UserTime; }
        [StructLayout(LayoutKind.Sequential)] private struct ClassicEvent { public ClassicHeader Header; public uint InstanceId, ParentInstanceId; public Guid ParentGuid; public IntPtr MofData; public uint MofLength, ClientContext; }
        [StructLayout(LayoutKind.Sequential, Size = 172)] private struct Zone { }
        [StructLayout(LayoutKind.Sequential)] private struct LogfileHeader
        {
            public uint BufferSize, Version, ProviderVersion, NumberOfProcessors; public long EndTime;
            public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten, StartBuffers, PointerSize, EventsLost, CpuSpeed;
            public IntPtr LoggerName, LogFileName; public Zone TimeZone; public long BootTime, PerfFreq, StartTime; public uint ReservedFlags, BuffersLost;
        }
        [StructLayout(LayoutKind.Sequential)] private struct TraceLogfile
        {
            public IntPtr LogFileName, LoggerName; public long CurrentTime; public uint BuffersRead, ProcessTraceMode;
            public ClassicEvent CurrentEvent; public LogfileHeader LogfileHeader; public IntPtr BufferCallback;
            public uint BufferSize, Filled, EventsLost; public IntPtr Callback; public uint IsKernelTrace; public IntPtr Context;
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EventRecordCallback(ref EventRecord record);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")] private static extern uint StartTrace(out ulong handle, string name, IntPtr properties);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")] private static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint control);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW", SetLastError = true)] private static extern ulong OpenTrace(ref TraceLogfile logfile);
        [DllImport("advapi32.dll")] private static extern uint ProcessTrace([In] ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);
    }

    public sealed class ObservationHealth
    {
        public bool Running;
        public uint EventsLost, RealTimeBuffersLost, ProcessTraceError, StopError;
        public long RejectedEvents, EvictedObservations;
    }
}
