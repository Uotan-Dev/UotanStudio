# Device communication boundaries

`UotanStudio.Comm` owns device discovery, transport and protocol sessions. It does
not own UI policy, firmware formats, media presentation, mirroring or delivery plans.
scrcpy and other presentation protocols live in `UotanStudio.Mirroring`; `UotanStudio.Media`
stays free of Comm references.

## Device I/O ownership

Every device read/write — USB, TCP, UDP, serial and the adb/HDC server protocols —
happens inside this project. Hosts must not open sockets, serial ports or USB handles
themselves; they consume `DeviceController`/`DeviceLease`, the `Contracts` façades and the
public protocol client classes. `Transports/Usb/Backend` interop is never a host surface.

## Layers

Dependencies point downward only. `Contracts` and `Routing` are zero-dependency
leaves that every upper layer may reference.

| Boundary | Code | Responsibility |
| --- | --- | --- |
| OS adapters | `Transports/Usb/Backend/{Windows,Linux,MacOS,HarmonyOS,LibUsb}` | Native enumeration, handles, endpoint I/O and hotplug. |
| Bearers | `Transports/{Usb,Serial,Tcp,Udp}` | Bounded I/O, timeouts, cancellation and disposal; USB session extensions and `UsbStream` live beside the sessions they wrap. |
| Protocols | `Protocols/{Adb,Hdc,Fastboot,Edl,Bootrom,Mtk,Scrcpy}` | Framing, authentication, command and session state. Fastboot network transports and framing live under `Protocols/Fastboot/Transport`; flash-time image helpers under `Protocols/Fastboot/Images`. |
| Contracts | `Contracts/` | Host-facing façade interfaces (`IAdbDeviceSession`, `HdcSession` contracts, endpoints, session kinds). No internal dependencies. |
| Routing | `Routing/` | TCP server liveness probes (`DebugBridgeRoute`), server endpoint facts (`DebugBridgeEndpoints`), ownership decisions and routed session opening (`Devices/Services/DebugBridgeSessionOpener`). |
| Inventory | `Devices/Discovery` | Separate USB and server discovery sources; exact endpoint identity and reported state. |
| Controller | `Devices/Services` | Snapshot generation, exact route opening, key-based leases (`OpenLeaseByKeyAsync`), adopted server-session leases (`AdoptSession`), and exclusive endpoint leases. |

`DeviceEndpointId` includes the discovery source, transport and address. A
physical USB association requires unique, verified identity evidence; matching
VID/PID or enumeration order is not sufficient. ADB and HDC server
serial/connect keys are authoritative. An offline or unauthorized server target
must not silently become a direct USB target.

## Automatic server routing

Server liveness is probed over plain TCP only — the library never starts, stops
or shells out to `adb`/`hdc` executables. When a server is listening, its
targets are served through the server; when the probe reports connection
refused, targets route through direct USB/TCP. Positive probe results are
cached for a short TTL, a refused connection is never cached, and `BeginProbeScope`
merges repeated probes within one scan pass. The USB access policy is registered
when the `CommKit` facade is constructed; without it, debug interfaces fail closed.

Direct HDC server shutdown uses the host-channel protocol (`HdcServerClient.KillAsync`);
there is no in-library process execution.

The USB OS adapters call `UsbAccessPolicy` before opening a debug interface;
the communication composition root supplies the existing server-ownership
check. Without a configured policy, ADB/HDC debug interfaces fail closed.

## USB backend defaults and hotplug matrix

`UsbApiKind.Auto` resolves to the platform-native backend everywhere: Windows → WinUSB,
Linux → usbfs, macOS → IOKit/IOUSBHost. libusb (and the HarmonyOS DDK) are explicit
opt-ins via `DeviceCommunicationConfiguration.UsbBackend`.

| Platform (native backend) | Change-event source | Fallback |
| --- | --- | --- |
| Windows (WinUSB) | `WM_DEVICECHANGE` message-only window | polling |
| Linux (usbfs) | uevent netlink socket (`NETLINK_KOBJECT_UEVENT`) | polling |
| macOS (IOKit/IOUSBHost) | not implemented yet | polling |
| libusb (opt-in) | libusb hotplug callbacks (Linux/macOS; unavailable on Windows) | polling |

## Lifecycle services

- **Removal watch** (`DeviceController.StartRemovalWatch`): on a USB removal every open
  lease bound to that physical path is disposed — queued commands are cancelled first, the
  running one settles, then session → transport → reservation release — the cached
  properties are dropped and `DeviceRemoved` fires. A metadata change (mode switch)
  releases the old interface's leases, keeps the cache and fires `DeviceChanged`.
- **Server watch** (`DeviceController.StartServerStateWatch`): periodic adb/HDC probes
  flip `DebugBridgeAvailabilityChanged` and release adopted server-session leases whose
  serial vanished from the server list or whose server stopped listening. Only a refused
  connection means "server absent".
- **Command pool** (`DeviceLease.Commands` → `DeviceCommandDispatcher`): per-device FIFO,
  strictly serial; results and exceptions return through the submitted `Task`. Streaming
  work (logcat, shell streaming, mirroring) must not go through the pool.
- **Property cache** (`DeviceController.Properties`): per-identity cache for parameters
  that cannot change while a physical device exists; keyed by the normalized device
  identity, invalidated on removal or explicit refresh, retained across mode switches.
- **Serial consistency**: when the USB descriptor carries no serial, the protocol readback
  (fastboot `getvar serialno`) is authoritative; server serial ↔ USB node matching in
  `UsbPhysicalIdentity` uses exact, instance-id-tail, or case-only comparison with a
  strict single-candidate guard — enumeration order, VID/PID and list position never map.

## MTK notes

BROM/preloader USB (VID 0x0E8D, PID 0x0003/0x2000/0x2001) is classified as `mtk` and has a
controller session factory; the byte protocol over USB bulk endpoints matches the serial
profile (mtkclient). V6 all-in-one DA containers are accepted (Firmware-side parsing is
DA-version independent). Device-initiated XML file operations are served only when
`MtkSession.HostFileStore` is attached; without it the session stays fail-closed.
SLA/DAA key material comes exclusively from an `IMtkSlaKeyProvider`/`IMtkDaaResourceProvider`
(e.g. `MtkDirectoryKeyStore` over a user-supplied directory); nothing is bundled.

## Session ownership

Discovery and ADB/Fastboot/EDL/RKDL transaction paths use the controller.
Sessions are obtained as `DeviceLease` values — `OpenAsync` for snapshot
endpoints, `OpenLeaseByKeyAsync` for exact stable USB keys, and `AdoptSession`
for server sessions that have no ownable USB endpoint. `OpenServerAdbSession`
remains the explicit non-leased server path. Callers dispose the lease; the
lease disposes the session and transport and releases the endpoint reservation.

Headless MCP uses the same discovery sources and exact opener. Direct HDC
session pooling and platform-specific runtime behavior still require separate
migration or validation; do not describe the controller as exclusive ownership
of every communication operation yet. In-process leases do not prevent another
process from owning a USB interface.

## Resource registration and provenance

Programmers, DAs, EMI images and authentication resources are external inputs. Every resource
that a Comm selection flow may consume is registered with the fields below; selection decisions
carry the registered SHA-256, never a filename or model-name guess, and a resource with an
unknown license stays out of any default package.

```text
resource_path:
resource_kind:      programmer | da | emi | auth-key
sha256:
origin:             user-supplied | bundled | derived
source:             url / commit / supplier
license:
redistribution:     allowed | user-obligation | forbidden
applicable_devices: hardware codes / HWID / PKHash constraints
verification:       scripted | real-transport | device-readonly | unverified (+ date)
```

## Validation

Use the solution build and xUnit v3 tests with a nonzero test guard:

```text
dotnet build UotanStudio.slnx --no-restore --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter "FullyQualifiedName~UotanStudio.Tests.Comm" --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter "FullyQualifiedName~UotanStudio.Tests.Devices|FullyQualifiedName~HeadlessControllerGatewayTests" --minimum-expected-tests 1
```

Device tests are opt-in. Read-only ADB queries and Sync listing can validate a
selected serial without changing device contents. `UOTAN_TEST_ADB_TARGET`
selects that exact target. `UOTAN_TEST_HDC_TARGET` does the same for HDC;
`UOTAN_TEST_USB_BACKEND=libusb` runs the whole device suite over the libusb
backend (verified against real hardware), and reboot scenarios additionally
require `UOTAN_TEST_ALLOW_REBOOT=1`. Physical identity validation additionally requires
`UOTAN_TEST_ADB_USB_ID`; a missing USB serial or nonmatching PnP identity is
an unverified association, not a successful USB test. Do not stop a server,
claim its USB interface, deploy scrcpy, flash, install or reboot a connected
device as part of routine validation.

Windows-only builds do not prove Linux/macOS/HarmonyOS backend behavior or
NativeAOT runtime behavior. Tests skipped for absent equipment remain skipped,
not passed.
