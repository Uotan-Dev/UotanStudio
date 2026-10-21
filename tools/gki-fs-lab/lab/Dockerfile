FROM docker.m.daocloud.io/library/alpine:3.20

RUN sed -i 's#https://dl-cdn.alpinelinux.org#https://mirrors.tuna.tsinghua.edu.cn#g' /etc/apk/repositories \
    && apk add --no-cache \
        bash coreutils findutils grep sed diffutils tar gzip xz zstd lz4 \
        cpio util-linux busybox-static \
        gcc g++ make binutils bc flex bison perl python3 linux-headers musl-dev \
        elfutils-dev openssl-dev zlib-dev ncurses-dev \
        clang lld llvm ccache git ca-certificates openssl \
        qemu-system-x86_64 qemu-img \
        e2fsprogs e2fsprogs-extra f2fs-tools erofs-utils attr \
        libstdc++ libgcc icu-libs zlib

ENV CCACHE_DIR=/lab/cache/ccache
WORKDIR /lab
