#!/bin/sh
set -eu

images=${1:?expected directory containing ext4.img, erofs.img, f2fs.img}
test "$(id -u)" -eq 0
work=$(mktemp -d)
mounted=
cleanup() {
    if [ -n "$mounted" ]; then
        umount "$mounted" || true
    fi
    rm -rf -- "$work"
}
trap cleanup EXIT

for format in ext4 erofs f2fs; do
    image="$images/$format.img"
    test -s "$image"
    before=$(sha256sum "$image")
    before=${before%% *}
    mountpoint="$work/$format"
    mkdir "$mountpoint"
    mount -t "$format" -o loop,ro "$image" "$mountpoint"
    mounted="$mountpoint"
    test "$(cat "$mountpoint/data")" = source-content
    test "$(cat "$mountpoint/alias")" = source-content
    test "$(readlink "$mountpoint/link")" = data
    test -d "$mountpoint/empty"
    test -c "$mountpoint/device"
    test "$(stat -c '%u:%g:%a:%Y' "$mountpoint/data")" = '70001:80002:4755:2500000000'
    test "$(stat -c '%i' "$mountpoint/data")" = "$(stat -c '%i' "$mountpoint/alias")"
    hash=$(getfattr -n user.binary --only-values "$mountpoint/data" 2>/dev/null | sha256sum)
    test "${hash%% *}" = 785b0751fc2c53dc14a4ce3d800e69ef9ce1009eb327ccf458afe09c242c26c9
    if getfattr -n security.capability --only-values "$mountpoint/data" > "$work/capability.bin" 2> "$work/capability.err"; then
        capability=$(sha256sum "$work/capability.bin")
        test "${capability%% *}" = 0af5fa788adb7dd10f02c81cc94989ceb649d1f7e4fc092da809b75bfff30fd7
        echo "WSL_CAPABILITY_OK:$format"
    elif grep -q 'Not supported' "$work/capability.err"; then
        echo "WSL_CAPABILITY_UNSUPPORTED:$format"
        if [ "$format" = f2fs ] && [ "${WSL_REQUIRE_F2FS_CAPABILITY:-0}" = 1 ]; then
            exit 1
        fi
    else
        cat "$work/capability.err" >&2
        exit 1
    fi
    umount "$mountpoint"
    mounted=
    after=$(sha256sum "$image")
    test "${after%% *}" = "$before"
    echo "WSL_LOOP_OK:$format"
done

if [ -s "$images/mixed-f2fs.img" ]; then
    image="$images/mixed-f2fs.img"
    before=$(sha256sum "$image")
    before=${before%% *}
    mkdir "$work/mixed"
    mount -t f2fs -o loop,ro "$image" "$work/mixed"
    mounted="$work/mixed"
    expected=88fdfca4194cabc29f73fee7331f1a6b137b42a8e362691a35d4eb36d3bea33e
    compression_supported=1
    for name in lz4 zstd; do
        if hash=$(sha256sum "$mounted/$name" 2> "$work/compression.err"); then
            test "${hash%% *}" = "$expected"
        elif grep -q 'Not supported' "$work/compression.err"; then
            compression_supported=0
            break
        else
            cat "$work/compression.err" >&2
            exit 1
        fi
    done
    umount "$mounted"
    mounted=
    after=$(sha256sum "$image")
    test "${after%% *}" = "$before"
    if [ "$compression_supported" -eq 1 ]; then
        echo WSL_MIXED_COMPRESSION_OK
    else
        echo WSL_MIXED_COMPRESSION_UNSUPPORTED
        test "${WSL_REQUIRE_F2FS_CAPABILITY:-0}" != 1
    fi
elif [ "${WSL_REQUIRE_F2FS_CAPABILITY:-0}" = 1 ]; then
    echo "Missing mixed-f2fs.img for strict replay validation" >&2
    exit 1
fi

if [ -s "$images/external-erofs.img" ]; then
    image="$images/external-erofs.img"
    before=$(sha256sum "$image")
    before=${before%% *}
    mkdir "$work/external-erofs"
    mount -t erofs -o loop,ro "$image" "$work/external-erofs"
    mounted="$work/external-erofs"
    hash=$(sha256sum "$mounted/data")
    test "${hash%% *}" = a4d80cd14fd693919ee998c73c81d6e2b81f2b6247ab1e2a5a22453f3aab2a24
    umount "$mounted"
    mounted=
    after=$(sha256sum "$image")
    test "${after%% *}" = "$before"
    echo WSL_EXTERNAL_EROFS_OK
fi

if [ -s "$images/casefold-ext4.img" ]; then
    image="$images/casefold-ext4.img"
    before=$(sha256sum "$image")
    before=${before%% *}
    mkdir "$work/casefold"
    mount -t ext4 -o loop,ro "$image" "$work/casefold"
    mounted="$work/casefold"
    test "$(cat "$mounted/Case/upper")" = casefold-content
    umount "$mounted"
    mounted=
    after=$(sha256sum "$image")
    test "${after%% *}" = "$before"
    echo WSL_CASEFOLD_LOOKUP_OK
fi
