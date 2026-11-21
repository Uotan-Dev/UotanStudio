/*
 * RISC-V 64 vector kernels for UotanStudio.Hash.Simd.
 *
 * RVV 1.0, integer only. Every routine is a leaf function, position independent
 * (no literal pools, no absolute addresses) so its .text bytes can be copied to
 * an executable page at run time. Arguments follow the LP64D ABI:
 * a0/x10, a1/x11, a2/x12; return value in a0; t0/t3/t4 temporaries.
 *
 * Kernels:
 *   rv_xor        (uint8_t *dst, const uint8_t *src, size_t n)      n, alignment: any
 *   rv_xor_const  (uint8_t *dst, uint8_t value, size_t n)           n, alignment: any
 *   rv_sum        (const uint8_t *src, size_t n) -> uint32          n, alignment: any
 *   rv_xor_fold   (const uint16_t *src, size_t nwords) -> uint16    nwords, 2B-aligned pointer
 *
 * The machine code bytes embedded in the managed emitters are produced from
 * this file with: as -march=rv64imafdcv riscv64.S && objcopy -O binary.
 */

    .text
    .option arch, +v

/* dst ^= src, byte blocks of LMUL*VLEN bits, tail folded into the loop. */
    .globl rv_xor
rv_xor:
1:  vsetvli t0, a2, e8, m8, ta, ma
    vle8.v v0, (a0)
    vle8.v v8, (a1)
    vxor.vv v0, v0, v8
    vse8.v v0, (a0)
    add a0, a0, t0
    add a1, a1, t0
    sub a2, a2, t0
    bnez a2, 1b
    ret

/* dst ^= value. Broadcast the byte once, then the same loop. */
    .globl rv_xor_const
rv_xor_const:
    vsetvli t4, zero, e8, m8, ta, ma
    vmv.v.x v24, a1
1:  vsetvli t0, a2, e8, m8, ta, ma
    vle8.v v0, (a0)
    vxor.vv v0, v0, v24
    vse8.v v0, (a0)
    add a0, a0, t0
    sub a2, a2, t0
    bnez a2, 1b
    ret

/* Sum of bytes modulo 2^32: widen e8 -> e16 -> e32 per iteration and keep
 * one e32m8 lane accumulator; vredsum folds it once at the end. */
    .globl rv_sum
rv_sum:
    vsetvli t4, zero, e32, m8, ta, ma
    vmv.v.i v16, 0
1:  vsetvli t0, a1, e8, m2, ta, ma
    vle8.v v0, (a0)
    vmv.v.i v4, 0
    vwaddu.vv v8, v0, v4
    vsetvli t4, t0, e16, m4, ta, ma
    vmv.v.i v12, 0
    vwaddu.vv v24, v8, v12
    vsetvli t4, t0, e32, m8, ta, ma
    vadd.vv v16, v16, v24
    add a0, a0, t0
    sub a1, a1, t0
    bnez a1, 1b
    vsetvli t4, zero, e32, m8, ta, ma
    vmv.v.i v24, 0
    vredsum.vs v0, v16, v24
    vmv.x.s a0, v0
    ret

/* XOR-fold of native 16-bit words; vredxor reduces the lane accumulator. */
    .globl rv_xor_fold
rv_xor_fold:
    vsetvli t4, zero, e16, m8, ta, ma
    vmv.v.i v16, 0
1:  vsetvli t0, a1, e16, m8, ta, ma
    vle16.v v0, (a0)
    vxor.vv v16, v16, v0
    slli t3, t0, 1
    add a0, a0, t3
    sub a1, a1, t0
    bnez a1, 1b
    vsetvli t4, zero, e16, m1, ta, ma
    vmv.v.i v24, 0
    vsetvli t4, zero, e16, m8, ta, ma
    vredxor.vs v0, v16, v24
    vmv.x.s a0, v0
    ret

/* Execution-coherence probes for the managed emitters; not used in
 * production. Each returns a0 + constant. */
    .globl rv_probe_add7
rv_probe_add7:
    addi a0, a0, 7
    ret

    .globl rv_probe_add11
rv_probe_add11:
    addi a0, a0, 11
    ret
