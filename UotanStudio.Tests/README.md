# Backend Tests

This project tests firmware parsing, extraction, packing, compression, device
protocols, and application backend algorithms. It does not test views, control
layout, rendering, editor navigation, or display bindings, and does not initialize
an Avalonia application or a headless UI platform.

## Scope

- `Firmware`: format conformance, bounded I/O, round trips, native-tool
  interoperability, and QEMU filesystem mount verification.
- `Comm`: protocol encoding, transport framing, device operations, and flashing.
- `Services` and `Compare`: execution scheduling, persistence, file comparison,
  and backend workflows. CLI and MCP tests are archived on `codex/archive-cli-mcp`;
  metadata packing coverage now exercises the desktop packing service.
- `Devices`: incremental logcat text decoding.

The application project reference is required for backend services that currently
live in `UotanStudio`. This also brings transitive application dependencies;
there are no direct UI package references in this test project. Workflow nodes
and device models are used only as inputs to existing backend APIs. Tests must
not exercise their UI state or construct views.

## Run

```powershell
dotnet test UotanStudio.Tests/UotanStudio.Tests.csproj -c Debug
```

The project uses xUnit v3 and the .NET 10 Microsoft Testing Platform. If test
discovery returns zero tests, run the built executable directly:

```powershell
& './UotanStudio.Tests/bin/Debug/net10.0/UotanStudio.Tests.exe'
```

Native-tool and QEMU cases need their documented local runtimes. See
[the filesystem mount fixture](TestInfrastructure/FirmwareMountCheck/README.md).

## HDC read-only hardware checks

`HdcServerClientTests` and `HdcDeviceServiceTests` exercise the host protocol and
process/error handling without modifying a device. `HdcReadOnlyDeviceTests` is
opt-in via `UOTAN_TEST_HDC_TARGET` (exact connect key) and `UOTAN_TEST_HDC_EXE`
(official executable path); absent values cause a skip. The existing
`OHOS_HDC_SERVER_PORT` environment override is respected. The hardware checks
read system parameters, shell output, a short HiLog stream, and the public
`/system/bin/toybox` file into a temporary local directory for SHA-256 comparison.
They never write device files, clear logs, install/uninstall, reboot or elevate.

### Direct HDC USB read-only checks

Set `UOTAN_TEST_HDC_USB_SERIAL` to the exact USB serial, then run `HdcDirectReadOnlyDeviceTests`.
The selected FF/50/01 interface must be available and the existing key at `~/.harmony/hdckey` must be readable.
This test never starts/stops HDC servers or generates a new device authorization identity. It performs
  concurrent parameter and HiLog reads, cancellation and connection reuse, plain/LZ4 reception of `/system/bin/toybox` compared against device sha256sum, interrupted transfers, missing-path errors, and debug-process enumeration.
It runs in a nonparallel collection because native USB discovery emits process-global diagnostics.
Do not enable this together with official-server hardware checks: their USB ownership is exclusive.
An explicitly enabled test fails when the interface cannot be opened; it does not silently skip that failure.

Native transfer and forwarding state machines are covered by HdcTransferTests and HdcForwardTests, including upstream byte fixtures, directory traversal rejection, atomic publication, app result modes, loopback bidirectional forwarding and channel-slot release after repeated failures. These simulations do not install packages, send device files or create hardware forwarding rules.
