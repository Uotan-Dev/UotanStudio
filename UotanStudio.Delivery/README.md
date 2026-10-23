# UotanStudio.Delivery

This project owns delivery planning and output generation for Super, Payload, Recovery, DAT/BR, scripts, ZIP packages, checksums, and delivery tools.

The project depends on `UotanStudio.Firmware` for image and payload primitives, on `UotanStudio.Net` for downloads and release metadata, and on SharpCompress for archive handling. It deliberately has no dependency on the desktop application, project persistence, UI, or key library. Host applications provide those concerns through the small adapters in `UotanStudio/Services/Delivery`, including `IDeliverySigningKeyProvider` for private-key loading.

Keep delivery policy and validation here so the desktop view only captures a `DeliveryPlan`, invokes the service, and presents progress. Add behavior to the existing validation/building paths before introducing new wrappers or parallel implementations.

The source tree is grouped by responsibility:

- `Models/` contains plan, source, group, and signing contracts.
- `Build/` contains output, package, Payload, Super, Recovery, validation, and size calculation code.
- `Scripts/` contains the script generator and its small preflight/text partials.
- `Tools/` contains delivery tool preparation and pruning.
- `Assets/` contains the pinned native `update-binary-arm64` fallback for Recovery DAT packages.
  An A/B `ota.zip` contains `payload.bin`, `payload_properties.txt`, and Android OTA metadata,
  but no device-specific `update-binary`: TWRP detects it as an A/B OTA and invokes its own
  update-engine path. Installation requires a TWRP build with A/B OTA support and a payload that
  the target device accepts. A whole-file ZIP signature is accepted only when its public key is in
  that TWRP build's `/res/keys`; `META-INF/com/android/otacert` does not add trust to TWRP.

File names are intentionally short because the `UotanStudio.Delivery` assembly and namespace already provide the `Delivery` context.