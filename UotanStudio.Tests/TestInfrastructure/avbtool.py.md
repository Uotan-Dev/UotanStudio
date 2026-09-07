# Pinned AVB Cross-Validation Tool

`avbtool.py` is an unmodified copy of the Android Open Source Project tool,
retrieved from the fixed LineageOS mirror revision used by the firmware review.

- Version: `avbtool 1.3.0`
- Repository: <https://github.com/LineageOS/android_external_avb>
- Commit: `91f1bfc948d35265c3b1e5e97bf1c410e65d18fa`
- Source: <https://raw.githubusercontent.com/LineageOS/android_external_avb/91f1bfc948d35265c3b1e5e97bf1c410e65d18fa/avbtool.py>
- SHA-256: `fc5332131a907ae22978e16a84fa12a14d277a60d8e2ad77c31a0097dafeb657`
- License: MIT, with the original copyright and permission notice retained in the script.

The test project explicitly copies this file to its output directory. A missing
fixture fails the build; a changed digest or a failed version check fails the test.
The script uses LF line endings to preserve the upstream digest on Windows checkouts.

AVB cross-validation requires an existing WSL distribution with `python3`
and `openssl` available. The default is `Alpine`; `UOTAN_TEST_WSL_DISTRO` selects
another existing distribution. Missing dependencies produce explicit skips.
The tests execute the copied script through
`python3` using separate process arguments, including for paths containing spaces.
They do not install system packages, edit repositories or sudoers, or overwrite
`/usr/local/bin/avbtool`. No network access is needed during testing.

Run the two `AvbAvbToolCrossValidationTests` cases and
`AvbHashtreeAndDescriptorsTests.ManagedAddHashtreeFooter_IsAcceptedByAvbTool` to check
native-to-managed hash footers, managed-to-native hash footers, and managed-to-native
hashtree footers. The latter two also pass the expected signing key to `verify_image`.
