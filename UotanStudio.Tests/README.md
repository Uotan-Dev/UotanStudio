# Backend Tests

This project tests firmware parsing, extraction, packing, compression, device
protocols, and application backend algorithms. It does not test views, control
layout, rendering, editor navigation, or display bindings, and does not initialize
an Avalonia application or a headless UI platform.

## Scope

- `Firmware`: format conformance, bounded I/O, round trips, native-tool
  interoperability, and QEMU filesystem mount verification.
- `Comm`: protocol encoding, transport framing, device operations, and flashing.
- `Services`, `Cli`, and `Compare`: execution scheduling, persistence, command
  parsing, file comparison, and backend workflows.
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
