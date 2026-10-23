#!/usr/bin/env bash
set -euo pipefail

REPO=${REPO:-/repo}
RUN_DIR=""
KERNELS="5.10,6.6,6.18"
JOBS=$(nproc)
REUSE=0
KEEP_WORK=${KEEP_WORK:-0}
BUILD_MODULES=${BUILD_MODULES:-0}
CACHE_ROOT=/lab/cache/kernels
WORK_ROOT=/lab/work
FRAGMENT="$REPO/tools/gki-fs-lab/lab/configs/gki-lab.fragment"
MIRRORS=(
  "https://mirrors.tuna.tsinghua.edu.cn/git/AOSP/kernel/common"
  "https://mirrors.ustc.edu.cn/aosp/kernel/common"
  "https://mirrors.bfsu.edu.cn/git/AOSP/kernel/common"
)

usage() {
  echo "usage: build-kernels.sh --run-dir <dir> [--kernels 5.10,6.6,6.18] [--jobs N] [--reuse]" >&2
}

while [ $# -gt 0 ]; do
  case "$1" in
    --run-dir) RUN_DIR=$2; shift 2 ;;
    --kernels) KERNELS=$2; shift 2 ;;
    --jobs) JOBS=$2; shift 2 ;;
    --reuse) REUSE=1; shift ;;
    --keep-work) KEEP_WORK=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage; exit 2 ;;
  esac
done

[ -n "$RUN_DIR" ] || { usage; exit 2; }
[ -f "$FRAGMENT" ] || { echo "missing fragment: $FRAGMENT" >&2; exit 2; }
mkdir -p "$RUN_DIR/kernels" "$RUN_DIR/logs" "$CACHE_ROOT" "$WORK_ROOT"
FRAGMENT_HASH=$(sha256sum "$FRAGMENT" | cut -d' ' -f1)

branch_for() {
  case "$1" in
    5.10) echo android13-5.10 ;;
    6.1) echo android14-6.1 ;;
    6.6) echo android15-6.6 ;;
    6.18) echo android17-6.18 ;;
    *) echo "" ;;
  esac
}

build_one() {
  local version=$1
  local branch
  branch=$(branch_for "$version")
  [ -n "$branch" ] || { echo "unsupported kernel: $version" >&2; exit 2; }
  local required="$REPO/tools/gki-fs-lab/lab/configs/required-$version.txt"
  [ -f "$required" ] || { echo "missing required-symbol file: $required" >&2; exit 2; }
  local log="$RUN_DIR/logs/build-$version.log"
  local src="$WORK_ROOT/src-$version"
  local build="$WORK_ROOT/build-$version"
  local cache=""
  local commit=""

  if [ "$REUSE" = 1 ]; then
    cache=$(find "$CACHE_ROOT" -maxdepth 1 -type d -name "$version-*" | sort | tail -1)
    if [ -n "$cache" ] && [ -s "$cache/bzImage" ]; then
      echo "[kernel $version] reusing $cache"
      commit=$(sed -n 's/.*"commit":[[:space:]]*"\([^"]*\)".*/\1/p' "$cache/kernel-meta.json" 2>/dev/null | head -1)
      cp "$cache/kernel-meta.json" "$RUN_DIR/kernels/$version-meta.json"
      return 0
    fi
  fi

  rm -rf "$src" "$build"
  mkdir -p "$src" "$build"
  echo "[kernel $version] cloning $branch from a mirror"
  local cloned=0
  for mirror in "${MIRRORS[@]}"; do
    echo "[kernel $version] trying $mirror" | tee -a "$log"
    if timeout 900 git clone --depth 1 --single-branch --branch "$branch" "$mirror" "$src" >>"$log" 2>&1; then
      cloned=1
      break
    fi
    rm -rf "$src"
    mkdir -p "$src"
  done
  [ "$cloned" = 1 ] || { echo "[kernel $version] all mirrors failed" >&2; exit 1; }

  commit=$(git -C "$src" rev-parse HEAD)
  cache="$CACHE_ROOT/$version-$commit-$FRAGMENT_HASH"
  if [ "$REUSE" = 1 ] && [ -s "$cache/bzImage" ]; then
    echo "[kernel $version] cache hit after clone: $cache"
  else
    rm -rf "$cache"
    mkdir -p "$cache"
    echo "[kernel $version] configuring ($branch @ $commit)"
    {
      make -C "$src" O="$build" ARCH=x86_64 gki_defconfig
      "$src/scripts/kconfig/merge_config.sh" -m -O "$build" "$build/.config" "$FRAGMENT"
      make -C "$src" O="$build" ARCH=x86_64 olddefconfig
    } >>"$log" 2>&1

    while IFS= read -r symbol; do
      [ -n "$symbol" ] || continue
      if ! grep -qxF "$symbol" "$build/.config"; then
        echo "[kernel $version] required symbol missing: $symbol" >&2
        exit 1
      fi
    done < "$required"

    echo "[kernel $version] building with $JOBS jobs"
    targets=(bzImage)
    [ "$BUILD_MODULES" = 1 ] && targets+=(modules)
    if [ "$version" = "5.10" ]; then
      make -C "$src" O="$build" ARCH=x86_64 -j"$JOBS" \
        CC="ccache gcc" HOSTCC="ccache gcc" "${targets[@]}" 2>&1 | tee -a "$log"
    else
      make -C "$src" O="$build" ARCH=x86_64 -j"$JOBS" \
        LLVM=1 CC="ccache clang" HOSTCC="ccache clang" "${targets[@]}" 2>&1 | tee -a "$log"
    fi

    local release
    release=$(make -s -C "$src" O="$build" ARCH=x86_64 kernelrelease)
    cp "$build/arch/x86/boot/bzImage" "$cache/bzImage"
    cp "$build/.config" "$cache/config"
    cp "$build/System.map" "$cache/System.map"
    [ -f "$build/modules.builtin" ] && cp "$build/modules.builtin" "$cache/modules.builtin"
    cat > "$cache/kernel-meta.json" <<EOF
{
  "version": "$version",
  "branch": "$branch",
  "commit": "$commit",
  "release": "$release",
  "fragmentSha256": "$FRAGMENT_HASH",
  "builtAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF
  fi

  cp "$cache/kernel-meta.json" "$RUN_DIR/kernels/$version-meta.json"
  cp "$cache/config" "$RUN_DIR/kernels/config-$version"
  echo "[kernel $version] ready: $(sed -n 's/.*"release":[[:space:]]*"\([^"]*\)".*/\1/p' "$cache/kernel-meta.json")"

  if [ "$KEEP_WORK" != 1 ]; then
    rm -rf "$src" "$build"
  fi
}

IFS=',' read -r -a requested <<< "$KERNELS"
for version in "${requested[@]}"; do
  build_one "$version"
done

echo "[kernel] done"
