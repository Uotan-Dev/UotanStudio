#!/bin/busybox sh
set -eu

export PATH=/bin:/sbin:/usr/bin:/usr/sbin
mount -t proc proc /proc 2>/dev/null || true
mount -t sysfs sysfs /sys 2>/dev/null || true
mount -t devtmpfs devtmpfs /dev 2>/dev/null || true
mkdir -p /mnt /tmp /dev/gki

report() {
  echo "$*"
}

fail_case() {
  report "CASE_FAIL $1 $2"
  return 1
}

verify_tree() {
  name=$1
  mnt=$2
  manifest="/manifests/$3.tsv"
  ignore=${4:-}
  [ -r "$manifest" ] || { fail_case "$name" "missing-manifest"; return 1; }
  expected=$(wc -l < "$manifest")
  seen=0
  while IFS='|' read -r type path mode uid gid size sha link hardlink xattrs; do
    [ -n "$type" ] || continue
    if [ -n "$ignore" ] && [ "$path" = "$ignore" ]; then
      continue
    fi
    target="$mnt/$path"
    seen=$((seen + 1))
    case "$type" in
      d) [ -d "$target" ] || { fail_case "$name" "missing-dir:$path"; return 1; } ;;
      f) [ -f "$target" ] || { fail_case "$name" "missing-file:$path"; return 1; } ;;
      l) [ -L "$target" ] || { fail_case "$name" "missing-link:$path"; return 1; } ;;
      *) fail_case "$name" "bad-type:$path"; return 1 ;;
    esac

    set -- $(stat -c '%a %u %g %s %i' "$target")
    amode=$1
    auid=$2
    agid=$3
    asize=$4
    ainode=$5
    [ "$amode" = "$mode" ] || { fail_case "$name" "mode:$path:$amode!=$mode"; return 1; }
    [ "$auid" = "$uid" ] || { fail_case "$name" "uid:$path:$auid!=$uid"; return 1; }
    [ "$agid" = "$gid" ] || { fail_case "$name" "gid:$path:$agid!=$gid"; return 1; }

    if [ "$type" = "f" ]; then
      [ "$asize" = "$size" ] || { fail_case "$name" "size:$path:$asize!=$size"; return 1; }
      actual_sha=$(sha256sum "$target" | cut -d' ' -f1)
      [ "$actual_sha" = "$sha" ] || { fail_case "$name" "sha:$path"; return 1; }
    elif [ "$type" = "l" ]; then
      [ "$(readlink "$target")" = "$link" ] || { fail_case "$name" "link:$path"; return 1; }
    fi

    if [ -n "$hardlink" ]; then
      canonical=$(stat -c '%i' "$mnt/$hardlink")
      [ "$ainode" = "$canonical" ] || { fail_case "$name" "hardlink:$path"; return 1; }
    fi

    if [ -n "$xattrs" ]; then
      old_ifs=$IFS
      IFS=';'
      for pair in $xattrs; do
        [ -n "$pair" ] || continue
        xname=${pair%%=*}
        xwant=${pair#*=}
        xgot=$(getfattr --only-values -n "$xname" "$target" 2>/dev/null | od -An -tx1 | tr -d ' \n')
        if [ "$xgot" != "$xwant" ]; then
          IFS=$old_ifs
          fail_case "$name" "xattr:$path:$xname"
          return 1
        fi
      done
      IFS=$old_ifs
    fi
  done < "$manifest"

  [ "$seen" = "$expected" ] || { fail_case "$name" "count:$seen!=$expected"; return 1; }
  expected_actual=$expected
  if [ -n "$ignore" ] && [ -e "$mnt/$ignore" ]; then
    expected_actual=$((expected_actual + 1))
  fi
  actual_count=$(find "$mnt" \( -type f -o -type d -o -type l \) | wc -l)
  actual_count=$((actual_count - 1))
  [ "$actual_count" = "$expected_actual" ] || { fail_case "$name" "extra-entries:$actual_count!=$expected_actual"; return 1; }
  report "READ_OK $name $seen"
  return 0
}

fsck_ext4() {
  name=$1
  dev=$2
  log="/tmp/fsck-$name.log"
  if e2fsck -fn "$dev" >"$log" 2>&1; then
    report "FSCK_BEFORE_OK $name"
  else
    cat "$log"
    fail_case "$name" "fsck-before"
    return 1
  fi
}

run_ext4() {
  name=$1
  dev=$2
  mnt="/mnt/$name"
  mkdir -p "$mnt"
  mount -t ext4 -o ro "$dev" "$mnt" || { fail_case "$name" "mount-ro"; return 1; }
  report "MOUNT_RO_OK $name"
  verify_tree "$name" "$mnt" ext4 || { umount "$mnt" || true; return 1; }
  umount "$mnt" || { fail_case "$name" "umount-ro"; return 1; }

  fsck_ext4 "$name" "$dev" || return 1

  mount -t ext4 -o rw "$dev" "$mnt" || { fail_case "$name" "mount-rw"; return 1; }
  if ! dd if=/dev/urandom of="$mnt/uotan-write.bin" bs=512 count=1 status=none; then
    fail_case "$name" "write"
    umount "$mnt" || true
    return 1
  fi
  write_sha=$(sha256sum "$mnt/uotan-write.bin" | cut -d' ' -f1)
  sync
  umount "$mnt" || { fail_case "$name" "umount-rw"; return 1; }

  if e2fsck -fn "$dev" >"/tmp/fsck-after-$name.log" 2>&1; then
    report "FSCK_AFTER_OK $name"
  else
    cat "/tmp/fsck-after-$name.log"
    fail_case "$name" "fsck-after"
    return 1
  fi

  mount -t ext4 -o ro "$dev" "$mnt" || { fail_case "$name" "remount-ro"; return 1; }
  [ "$(sha256sum "$mnt/uotan-write.bin" | cut -d' ' -f1)" = "$write_sha" ] \
    || { fail_case "$name" "writeback"; umount "$mnt" || true; return 1; }
  verify_tree "$name" "$mnt" ext4 uotan-write.bin || { umount "$mnt" || true; return 1; }
  umount "$mnt" || { fail_case "$name" "final-umount"; return 1; }
  report "WRITE_BACK_OK $name"
  return 0
}

run_f2fs() {
  name=$1
  dev=$2
  mnt="/mnt/$name"
  mkdir -p "$mnt"
  mount -t f2fs -o ro "$dev" "$mnt" || { fail_case "$name" "mount-ro"; return 1; }
  report "MOUNT_RO_OK $name"
  verify_tree "$name" "$mnt" f2fs || { umount "$mnt" || true; return 1; }
  umount "$mnt" || { fail_case "$name" "umount-ro"; return 1; }

  if fsck.f2fs --dry-run --no-kernel-check "$dev" >"/tmp/fsck-before-$name.log" 2>&1; then
    report "FSCK_BEFORE_OK $name"
  else
    cat "/tmp/fsck-before-$name.log"
    fail_case "$name" "fsck-before"
    return 1
  fi

  mount -t f2fs -o rw "$dev" "$mnt" || { fail_case "$name" "mount-rw"; return 1; }
  if ! dd if=/dev/urandom of="$mnt/uotan-write.bin" bs=512 count=1 status=none; then
    fail_case "$name" "write"
    umount "$mnt" || true
    return 1
  fi
  write_sha=$(sha256sum "$mnt/uotan-write.bin" | cut -d' ' -f1)
  sync
  umount "$mnt" || { fail_case "$name" "umount-rw"; return 1; }

  if fsck.f2fs --dry-run --no-kernel-check "$dev" >"/tmp/fsck-after-$name.log" 2>&1; then
    report "FSCK_AFTER_OK $name"
  else
    cat "/tmp/fsck-after-$name.log"
    fail_case "$name" "fsck-after"
    return 1
  fi

  mount -t f2fs -o ro "$dev" "$mnt" || { fail_case "$name" "remount-ro"; return 1; }
  [ "$(sha256sum "$mnt/uotan-write.bin" | cut -d' ' -f1)" = "$write_sha" ] \
    || { fail_case "$name" "writeback"; umount "$mnt" || true; return 1; }
  verify_tree "$name" "$mnt" f2fs uotan-write.bin || { umount "$mnt" || true; return 1; }
  umount "$mnt" || { fail_case "$name" "final-umount"; return 1; }
  report "WRITE_BACK_OK $name"
  return 0
}

run_erofs() {
  name=$1
  dev=$2
  mnt="/mnt/$name"
  mkdir -p "$mnt"
  mount -t erofs -o ro "$dev" "$mnt" || { fail_case "$name" "mount-ro"; return 1; }
  report "MOUNT_RO_OK $name"
  verify_tree "$name" "$mnt" erofs || { umount "$mnt" || true; return 1; }
  umount "$mnt" || { fail_case "$name" "umount-ro"; return 1; }

  if fsck.erofs "$dev" >"/tmp/fsck-$name.log" 2>&1; then
    report "FSCK_BEFORE_OK $name"
  else
    cat "/tmp/fsck-$name.log"
    fail_case "$name" "fsck"
    return 1
  fi

  mount -t erofs -o ro "$dev" "$mnt" || { fail_case "$name" "remount-ro"; return 1; }
  if touch "$mnt/uotan-write.bin" 2>/tmp/erofs-write.err; then
    fail_case "$name" "erofs-write-accepted"
    umount "$mnt" || true
    return 1
  fi
  umount "$mnt" || { fail_case "$name" "final-umount"; return 1; }
  report "RW_REJECTED $name"
  return 0
}

report "UOTAN_GUEST_BEGIN"
report "KERNEL $(uname -r)"
report "CPUS $(nproc)"
zcat /proc/config.gz 2>/dev/null | grep -E 'CONFIG_(EXT4_FS|EROFS_FS|EROFS_FS_ZIP|F2FS_FS|VIRTIO_BLK)=' || true

failures=0
count=0
while IFS="$(printf '\t')" read -r name format device; do
  [ -n "$name" ] || continue
  count=$((count + 1))
  report "CASE_BEGIN $name $format"
  set +e
  case "$format" in
    ext4) run_ext4 "$name" "$device" ;;
    f2fs) run_f2fs "$name" "$device" ;;
    erofs) run_erofs "$name" "$device" ;;
    *) fail_case "$name" "unknown-format" ;;
  esac
  rc=$?
  set -e
  if [ "$rc" -eq 0 ]; then
    report "RESULT $name PASS"
  else
    report "RESULT $name FAIL"
    failures=$((failures + 1))
  fi
done < /cases.tsv

report "SUMMARY cases=$count failures=$failures"
sync
poweroff -f
