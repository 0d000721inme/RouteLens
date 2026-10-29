# ETW network experiment — not integrated or production ready

This is isolated research code, not included in `build.ps1` or the release app.
Constructing it does not start capture. `Start()` explicitly attempts to create
the named `RouteLens-Network-Experiment` system ETW session, using network events
only. If that name is already in use or permissions are insufficient, it returns
an error and does not stop the existing session. `Dispose()` stops only the handle
successfully created by this instance. Memory is bounded to the latest 1024
observations; no trace file is written and there is no network traffic generated.

`NetworkEventDecoder` accepts only the documented classic version 2 TCP/UDP
IPv4/IPv6 send and receive payload prefix, and uses the PID in the payload, not
the potentially unrelated ETW header PID. Unknown versions and truncated data
are rejected. Event size is an ETW field, not validated wire-byte or packet
statistics. PID alone is not a process-instance identity. Loss counters, ignored
versions and in-memory evictions require explicit presentation as gaps. The
prototype stops itself after two minutes while its process remains alive.

**Not validated on a real elevated session.** In particular, native ABI and raw
payload assumptions have not been checked against actual Windows events; no
process lifetime reconciliation, PID reuse protection, elevated helper, IPC,
crash recovery, service isolation, or UI integration exists. An
abnormally terminated process may leave a session behind. Do not expose this as
a working monitoring feature until all of these are implemented and tested on
Windows 10/11 under controlled TCP/UDP IPv4/IPv6 traffic with independent ground
truth, loss injection, session contention and crash tests. Never stop another
component's session to resolve a conflict.

Run `./RunChecks.ps1` to compile and run offline synthetic payload and ABI tests;
the checks never start a trace. `LiveLoopbackCheck.cs` is a separate manually
compiled, opt-in elevated smoke test. It creates a system ETW session for up to
two minutes and sends synthetic data on loopback only; it prints only its own
event coverage and aggregate health counts. It has not been run here and is not
a substitute for the required independent loss and attribution tests.
References: [TCP ETW](https://learn.microsoft.com/en-us/windows/win32/etw/tcpip),
[UDP ETW](https://learn.microsoft.com/en-us/windows/win32/etw/udpip),
[StartTrace](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracew),
[EVENT_TRACE_PROPERTIES](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_properties).
