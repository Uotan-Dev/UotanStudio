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
| Protocols | `Protocols/{Adb,Hdc,Fastboot,Edl,Bootrom,Mtk}` | Framing, authentication, command and session state. Fastboot network transports and framing live under `Protocols/Fastboot/Transport`; flash-time image helpers under `Protocols/Fastboot/Images`. Screen mirroring is not a protocol here: scrcpy lives in `UotanStudio.Mirroring`. |
| Contracts | `Contracts/` | Host-facing façade interfaces (`IAdbDeviceSession`, `HdcSession` contracts, endpoints, session kinds). No internal dependencies. |
| Routing | `Routing/` | TCP server liveness probes (`DebugBridgeRoute`), server endpoint facts (`DebugBridgeEndpoints`), ownership decisions and routed session opening (`Devices/Services/DebugBridgeSessionOpener`). |
| Inventory | `Devices/Discovery` | Separate USB and server discovery sources; exact endpoint identity and reported state. |
| Controller | `Devices/Services` | Snapshot generation, exact route opening, key-based leases (`OpenLeaseByKeyAsync`), adopted server-session leases (`AdoptSession`), and exclusive endpoint leases. |

`DeviceEndpointId` includes the discovery source, transport and address. A
physical USB association requires unique, verified identity evidence; matching
VID/PID or enumeration order is not sufficient. ADB and HDC server
serial/connect keys are authoritative. An offline or unauthorized server target
must not silently become a direct USB target.

## Host signing identity

A server route performs no host-side signing: the running `adb`/`hdc` server owns
the identity and the serial is authoritative. Only the direct USB/TCP routes sign
the peer challenge, and both use the same upstream host identity the official
clients use, so authorizing one authorizes all of them:

| Protocol | Private key | Public sidecar | Signing |
| --- | --- | --- | --- |
| ADB | `~/.android/adbkey`, RSA-2048, PKCS#8 PEM | `adbkey.pub`: base64 `RSAPublicKey` + `" "` + `login@host`, no trailing newline | RSA PKCS#1 + SHA-1 over the 20-byte token |
| HDC | `~/.harmony/hdckey`, RSA-3072, PKCS#8 PEM | `hdckey.pub`: PEM `SubjectPublicKeyInfo` with one trailing newline | RSA-PSS + SHA-512 over the daemon token, or the legacy private-key encryption |

Paths and widths follow upstream, per platform. Generation follows the same split as
upstream: only the **user identity** is created when its file is absent
(`LoadOrCreate()`, mirroring `load_userkey()`/`LoadHostUserKey()`), while an
`ADB_VENDOR_KEYS` entry is read-only (`Load()`, mirroring `load_keys()`) and a key
file that exists but cannot be parsed fails instead of being replaced. The one
deliberate addition is sidecar repair: a missing or stale `.pub` is rewritten from
the private key, because the HDC handshake reads `hdckey.pub` and upstream aborts
with `E000005` when it is unusable.

Upstream `adb_get_homedir_path()`
resolves `SHGetFolderPathW(CSIDL_PROFILE)` on Windows and `$HOME`, then the passwd
entry, on Linux and macOS; `uv_os_homedir()` in HDC's `GetUserKeyPath` follows the
same shape. Both key directories are created with mode `0750` and key files with
mode `0600` on Unix; on Windows the files inherit the parent directory ACL. The
ADB public-key label reads `USERNAME`/`COMPUTERNAME` first on Windows and
`LOGNAME`/`HOSTNAME` first on the other platforms, matching
`sysdeps::GetLoginNameUTF8()`/`GetHostNameUTF8()`; adbd compares only the base64
material before the first space, so the label is informational. HDC's handshake
`buf` is `hostname` + `0x0C` + PEM, exactly as upstream `GetPublicKeyinfo` builds
it, because `GetHostPubkeyInfo` splits on the first `0x0C` and stores the remainder
verbatim as the daemon's known-hosts record.

ADB offers more than one identity, like upstream: `AdbAuthentication.LoadIdentityChain()`
loads the user identity first and then every identity named by `ADB_VENDOR_KEYS`,
split on `;` on Windows and `:` elsewhere, where a directory entry contributes only
files named `*.adb_key` and duplicates collapse by the SHA-256 fingerprint of the
DER `SubjectPublicKeyInfo`. Each AUTH token is answered with the next identity, and
once the chain is exhausted the `RSAPUBLICKEY` announcement carries the public half
of the **user** identity, the same key upstream `adb_auth_get_userkey()` reads.

There is no separate host certificate identity. The TLS client certificate that
secure (Android 11+) sessions present is regenerated for each handshake by
`AdbHostCertificate` from the same RSA key that signs AUTH tokens, in the shape
upstream `GenerateX509Certificate()` emits: subject and issuer
`C=US, O=Android, CN=Adb`, ten-year validity, critical `basicConstraints CA:TRUE`,
critical `keyCertSign, cRLSign, digitalSignature`, a subject key identifier, and a
SHA-256 self-signature. adbd authenticates the SHA-256 fingerprint of the public key
inside that certificate (`adbd_tls_verify_cert`), never its subject, serial or
storage location, so nothing certificate-shaped is persisted.

Legacy HDC `RsaEncrypt` and the MTK Boot ROM SLA responder share
`RsaTypeOneSignature` (PKCS#1 v1.5 type-1 over an unframed payload). That helper
is deliberately not a digesting signer: both peers recover `00 01 FF..FF 00 ||
payload` with the host public exponent, so inserting a DigestInfo prefix would
break them.

Not surfaced from upstream, and deliberately not implemented here:

- HDC `AuthVerifyType.PskTlsAes128GcmSha256` (`AUTH_SSL_TLS_PSK`, where the daemon
  RSA-OAEP-encrypts a session PSK to the host public key). It is negotiated only in
  builds with `HDC_SUPPORT_ENCRYPT_TCP`, and `SignToken` fails closed on it. Such a
  daemon also rejects a plaintext `AUTH_SIGNATURE`, which this client cannot answer.
- ADB's client-certificate selection against the device's `AdbKey-0` CA-issuer list
  (`adb_tls_set_certificate`), which matters only when a device authorizes several
  host keys and the TLS session must pick the matching one.

## Automatic server routing

Server liveness is probed over plain TCP only — the library never starts, stops
or shells out to `adb`/`hdc` executables. The default adb and HDC endpoints are
probed once when the controller is created and the conclusive outcomes are
written to the process-global `DebugBridgeRoute.AdbServerAvailable` and
`DebugBridgeRoute.HdcServerAvailable`; the server-lifecycle watch refreshes
both parameters on every pass and explicit server shutdown re-syncs them.
Every adb and HDC operation route reads its parameter instead of probing:
when it marks the server as present, the serial must additionally appear in
the server's target list and targets are served through the server; when
absent, targets route through direct USB/TCP. Only a refused connection marks
a server absent and inconclusive probes keep the last state. Non-default
endpoints keep per-operation probes: positive results are cached for a short
TTL, a refused connection is never cached, and `BeginProbeScope` merges
repeated probes within one scan pass. The USB access policy is registered
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

BROM/preloader USB (VID 0x0E8D; the classifier accepts product ids 0x0003, 0x2000, 0x2001, 0x20FF and 0x3000) is
classified as `mtk` and has a
controller session factory; the byte protocol over USB bulk endpoints matches the serial
profile (mtkclient). V6 all-in-one DA containers are accepted (Firmware-side parsing is
DA-version independent). Device-initiated XML file operations are served only when
`MtkSession.HostFileStore` is attached; without it the session stays fail-closed.
SLA/DAA key material comes exclusively from an `IMtkDaaResourceProvider`
(e.g. `MtkDirectoryKeyStore` over a user-supplied directory); nothing is bundled.

## GPT observation options

`FirehoseProtocol.ReadGptWithPartitionEntryOverridesAsync` is the read-side counterpart of the reference
downloader's `--gpt-part-entry-start-lba`, `--gpt-num-part-entries` and `--gpt-part-entry-size` switches
(`edlclient/Library/firehose_client.py:483-485`, consumed at `edlclient/Library/gpt.py:360-362`). The LBA value
replaces only the location that names the *primary* partition-entry array; every other primary header field still
has to validate, and the array read there must still match the header-declared entry-array CRC and be
byte-identical to the backup copy read from the backup header's own location. A number that points inside the
reported capacity but at the wrong place therefore ends in `InvalidDataException`, while a number that cannot
describe the array at all — an LBA below 2 or one leaving the array past the reported capacity, or dimensions
exceeding the supported entry-array limit — is refused earlier with `ArgumentOutOfRangeException` naming the
offending override. Neither path can yield a partially trusted table. The entry count and entry size are
array-wide dimensions, so they apply to both copies and the declared CRC keeps gating the result. The backup
header is always parsed without an LBA override, and all three values are recorded on the returned snapshot so
named partition operations revalidate the observation with the same dimensions. Upstream hands the same LBA to
`get_backup_gpt` as well (`firehose.py:852-860`); this implementation deliberately keeps the backup anchored to
its own header so the primary-vs-backup byte identity stays a real cross-check rather than a self-comparison.
The read method itself issues no write
command. A snapshot taken through an override is consumable by the flash page's name-based program, erase and
readback paths, which matches the reference tool feeding one parsed GPT to both its read and write lists; the
multi-LUN erase-all preview and the GPT repair path deliberately keep reading the header's own value, because a
single operator-typed LBA cannot describe eight storage units.

## Manifest wipe requests

`Protocols/Edl/FlashBundle/WipeManifestParser.cs` accepts a `<wipe>` element in a rawprogram document and
`FirehoseXmlBuilder.BuildWipe` sends it as a `<program>` of zeros over the requested range, which is what the
Qualcomm loader tool means by `--wipefirst` (`fh_loader/fh_loader.cpp:2814`: "erasefirst and wipefirst are
flags that affect the `<program>` tag by adding erase/wipe prior to programming"). The *element name* is this
repository's own: no upstream tool parses a `<wipe>` tag, so a document written for another downloader will not
carry one, and nothing here should be changed to make `<wipe>` look like upstream syntax. Because a wipe
overwrites data with zeros and cannot be undone, `FlashBundleOptions.WipeConfirmation` must carry exactly the
phrase `WIPE` (public on `WipeConfirmation.RequiredPhrase`, which the host reads) before any wipe byte is
prepared, and a request spanning the whole reported storage additionally needs
`FlashBundleOptions.AllowEntireStorageWipe`; both gates are refused with no command document built, so the
device sees nothing. `SkipWipe` drops the manifest's `<wipe>` entries before they are parsed
(`FlashBundleOrchestrator.Package.cs:134`), so a skipped run carries no wipe preview, no evidence item and no
`WipeEntriesProcessed` count; only `WipeFirstSectors`, which is a separate request, is still honoured in that run.

## NAND geometry on the wire

`FirehoseConfiguration.NandPagesPerBlock` is the operator-supplied pages-per-block count. The `<program>`, `<read>`
and `<erase>` frames take it through `FirehoseProtocol.NandPagesPerBlockHint`, and `<configure>` applies the same
two conditions on its own inside `FirehoseXmlBuilder.BuildConfigure` — all four emit `PAGES_PER_BLOCK` only for a
NAND storage type with a positive count. The reference client gates the same attribute on
`MemoryName == "nand"` and a non-zero count (`edlclient/Library/firehose.py:468-470`) and appends it from
`cmd_program` (`:496`), `cmd_program_buffer` (`:552`), `cmd_erase` (`:606`, `:621`), `cmd_read` (`:671`),
`cmd_read_buffer` (`:732`) and the connect command (`:906-907`). A non-NAND storage type or a
value of zero emits no attribute at all, so an eMMC or UFS target never sees a NAND-only field, and a value above
the 32-bit range is written out verbatim instead of truncated. The attribute is a hint about the target's own
geometry: it is never derived from a probe, and it authorizes no write.

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

Programmers, DAs, EMI images and authentication resources are external inputs. What a selection flow may
consume is described by `Protocols/Common/Evidence/ResourceProvenance.cs`: a required `Origin`, an optional
`LicenseTag`, an optional `Source` and a required `Sha256Hex`. `EdlProgrammerDescriptor.Validate` refuses a
descriptor whose provenance hash contradicts its own `Sha256Hex`, so a row cannot claim one blob and name
another. Selection decisions carry the registered SHA-256, never a filename or model-name guess.

`LicenseTag` is descriptive: no production code reads it, so nothing here automatically keeps an
unknown-license resource out of a package, and the licensing obligation for anything a user supplies stays with
the user.

`Protocols/Edl/Programmer/EdlProgrammerFolderScanner.cs` turns a user-selected loader folder into catalog
rows, and the host now drives it: `UotanStudio/Services/Flash/EdlProgrammerDirectoryService.cs` scans the
folder, builds an `EdlProgrammerCatalog` and asks `EdlRuleProgrammerSelector` for a verdict against the Sahara
identity discovery already recorded. It is `internal` and reachable only through `InternalsVisibleTo`, so the
naming parse and the hashing stay the single producer; the host neither re-parses a file name nor recomputes a
hash. Only `EdlProgrammerMatchDecision.Matched` returns a descriptor, and `EdlSession.InitializeAsync` still
re-measures the image hash before a single programmer byte reaches the transport, so a verdict alone never
authorizes an upload.

## Implemented but deliberately not surfaced in the host

A protocol method with no production caller is not automatically a gap. The following MTK entry points are
implemented and covered by tests, but the host deliberately exposes no control for them:

- `MtkSession.GetBootLoaderVersionAsync` and `GetBromVersionAsync` — these require the preloader command stage and
  fault the session on a refused exchange. The non-fatal `QueryBootIdentityOwnedAsync` path already collects the
  same two bytes during initialization, so no separate control surfaces them.

Vendor exploit implementations (VIP table operations, OnePlus/Xiaomi/Nothing auth bypass, EDL memory debug,
DA patching, DA crash, DA memory write, preloader public write, SECCFG write, XML security resource upload,
download-agent memory peek/poke and target-memory poke/memcpy) have been removed from the codebase entirely;
they are not retained as unsurfaced methods.

Re-exposing any of these requires device evidence for the specific operation plus a dedicated dangerous-operation
confirmation, not just a button.

## Native runtime registration

`Native/LibUsb` carries the libusb binaries for the supported RIDs. `UotanStudio.Comm.csproj` selects the
directory matching the build's `LibUsbRuntimeIdentifier`, copies its platform library
(`libusb-1.0.dll`, `libusb-1.0.dylib`, `libusb-1.0.so`/`libusb-1.0.so.0`) and `libusb-LICENSE.txt` next to the
application, and the content items flow through `ProjectReference`; the host neither downloads nor copies the
library for a supported RID.

The libusb backend is enabled only when that platform file is present in the application directory or an explicit
path is registered. A libusb installed elsewhere on the system does not enable it. `Transports/Usb/Backend/LibUsb/LibUsbRuntime.cs`
is the public entry a host uses to register the absolute path of a prepared native library; it forwards to the
internal `LibUsbNativeLoader`, which stays out of reach across assemblies. The same registration is surfaced as
`CommKit.SetLibusbLibraryPath` and, internally, as `UsbCommunicationLayer.SetLibusbLibraryPath`; the path is
validated and pre-loaded, so a system installation is usable once its file is named explicitly. Registering after a
failed availability probe re-enables the backend; switching the runtime after a successful native call is not
supported.

`UotanStudio/Services/NativeBin/NativeBinProvisioner.cs` remains the fallback for a RID without a bundled file: it
downloads the runtime for the active RID, verifies its SHA-256 and registers the cached path through that entry.

## Validation

Use the solution build and xUnit v3 tests with a nonzero test guard:

```text
dotnet build UotanStudio.slnx --no-restore --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter "FullyQualifiedName~UotanStudio.Tests.Comm" --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter "FullyQualifiedName~UotanStudio.Tests.Devices|FullyQualifiedName~HeadlessControllerGatewayTests" --minimum-expected-tests 1
```

Real-input tests are opt-in through an environment variable each, and skip with that variable named in the
reason when it is unset: `UOTAN_SELINUX_POLICY_SAMPLE`, `UOTAN_ELF_FIREHOSE_SAMPLE`,
`UOTAN_SAMSUNG_DAT_MERGE_ORACLE`, `UOTAN_SAMSUNG_PAYLOAD_MERGE_ORACLE`, `UOTAN_HOS_UPDATE_APP_SAMPLE`,
`UOTAN_MAGISKBOOT_ORACLE` (the upstream reference binary) and `UOTAN_EROFS_CORPUS` — the index and the oracle
resolver live in `UotanStudio.Tests/TestInfrastructure/`. There is no environment switch that selects a live
ADB or HDC target: a device test that finds no target skips, and hardware-side behaviour is therefore not part of
the solution gate. Do not stop a server, claim its USB interface, deploy scrcpy, flash, install or reboot a
connected device as part of routine validation.

Windows-only builds do not prove Linux/macOS/HarmonyOS backend behavior or
NativeAOT runtime behavior. Tests skipped for absent equipment remain skipped,
not passed.
