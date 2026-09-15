# UotanStudio.Delivery

This project owns delivery planning and output generation for Super, Payload, Recovery, DAT/BR, scripts, ZIP packages, checksums, and delivery tools.

The project depends on `UotanStudio.Firmware` for image and payload primitives and on SharpCompress for archive handling. It deliberately has no dependency on the desktop application, project persistence, UI, or key library. Host applications provide those concerns through the small adapters in `UotanStudio/Services/Delivery`, including `IDeliverySigningKeyProvider` for private-key loading.

Keep delivery policy and validation here so the desktop view only captures a `DeliveryPlan`, invokes the service, and presents progress. Add behavior to the existing validation/building paths before introducing new wrappers or parallel implementations.

The source tree is grouped by responsibility:

- `Models/` contains plan, source, group, and signing contracts.
- `Build/` contains output, package, Payload, Super, Recovery, validation, and size calculation code.
- `Scripts/` contains the script generator and its small preflight/text partials.
- `Tools/` contains delivery tool preparation and pruning.
- `Assets/` contains pinned native Recovery updaters (`update-binary-arm64` for DAT shell
  fallback, `update-binary-ab-arm64` for the A/B OTA entry point). `ota.zip` always ships
  `META-INF/com/google/android/update-binary` — the user-specified native updater when
  provided, otherwise the embedded arm64 one — so TWRP and stock A/B recovery can install
  the package without external inputs.

File names are intentionally short because the project and namespace already provide the `Delivery` context.
