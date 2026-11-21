/*
 * LoongArch64 vector kernels for UotanStudio.Hash.Simd.
 *
 * LSX (128-bit) and LASX (256-bit), integer only. Every routine is a leaf
 * function, position independent (no literal pools, no absolute addresses) so
 * its .text bytes can be copied to an executable page at run time. Arguments
 * follow the LP64D ABI: a0/r4, a1/r5, a2/r6; return value in a0.
 *
 * The managed wrapper peels byte heads until both pointers are naturally
 * aligned and hands the native kernels a length that is a multiple of the
 * register width, so no unaligned vector access ever happens here.
 *
 * Kernels (the 256 forms process 32 bytes or 16 words per iteration, the 128
 * forms 16 bytes or 8 words):
 *   la256_xor / la128_xor       (uint8_t *dst, const uint8_t *src, size_t n)
 *   la256_xor_const / la128_xor_const (uint8_t *dst, uint8_t value, size_t n)
 *   la256_sum / la128_sum       (const uint8_t *src, size_t n) -> uint32
 *   la256_xor_fold / la128_xor_fold (const uint16_t *src, size_t nwords) -> uint16
 *
 * The machine code bytes embedded in the managed emitters are produced from
 * this file with the native GNU as on a LoongArch64 host (default flags; the
 * assembler accepts x-prefixed LASX and v-prefixed LSX forms out of the box)
 * followed by objcopy -O binary --only-section=.text.
 */

    .text

/* ---- LASX (256-bit) ---- */

    .globl la256_xor
la256_xor:
1:  addi.d $t8, $zero, 32
    bltu $a2, $t8, 2f
    xvld $xr0, $a0, 0
    xvld $xr1, $a1, 0
    xvxor.v $xr0, $xr0, $xr1
    xvst $xr0, $a0, 0
    addi.d $a0, $a0, 32
    addi.d $a1, $a1, 32
    addi.d $a2, $a2, -32
    b 1b
2:  jr $ra

    .globl la256_xor_const
la256_xor_const:
    xvreplgr2vr.b $xr3, $a1
1:  addi.d $t8, $zero, 32
    bltu $a2, $t8, 2f
    xvld $xr0, $a0, 0
    xvxor.v $xr0, $xr0, $xr3
    xvst $xr0, $a0, 0
    addi.d $a0, $a0, 32
    addi.d $a2, $a2, -32
    b 1b
2:  jr $ra

/* Sum of bytes: interleave with zero vectors to widen b -> h -> w, keeping
 * four w-lane accumulators; the lanes are folded with scalar extraction once
 * after the loop. */
    .globl la256_sum
la256_sum:
    xvreplgr2vr.b $xr24, $zero
    xvreplgr2vr.w $xr8, $zero
    xvreplgr2vr.w $xr9, $zero
    xvreplgr2vr.w $xr10, $zero
    xvreplgr2vr.w $xr11, $zero
1:  addi.d $t8, $zero, 32
    bltu $a1, $t8, 2f
    xvld $xr0, $a0, 0
    xvilvl.b $xr2, $xr24, $xr0
    xvilvh.b $xr3, $xr24, $xr0
    xvilvl.h $xr4, $xr24, $xr2
    xvilvh.h $xr5, $xr24, $xr2
    xvilvl.h $xr6, $xr24, $xr3
    xvilvh.h $xr7, $xr24, $xr3
    xvadd.w $xr8, $xr8, $xr4
    xvadd.w $xr9, $xr9, $xr5
    xvadd.w $xr10, $xr10, $xr6
    xvadd.w $xr11, $xr11, $xr7
    addi.d $a0, $a0, 32
    addi.d $a1, $a1, -32
    b 1b
2:  xvadd.w $xr8, $xr8, $xr10
    xvadd.w $xr9, $xr9, $xr11
    xvpickve2gr.w $t0, $xr8, 0
    xvpickve2gr.w $t1, $xr8, 1
    xvpickve2gr.w $t2, $xr8, 2
    xvpickve2gr.w $t3, $xr8, 3
    xvpickve2gr.w $t4, $xr8, 4
    xvpickve2gr.w $t5, $xr8, 5
    xvpickve2gr.w $t6, $xr8, 6
    xvpickve2gr.w $t7, $xr8, 7
    add.d $a0, $t0, $t1
    add.d $a0, $a0, $t2
    add.d $a0, $a0, $t3
    add.d $a0, $a0, $t4
    add.d $a0, $a0, $t5
    add.d $a0, $a0, $t6
    add.d $a0, $a0, $t7
    xvpickve2gr.w $t0, $xr9, 0
    xvpickve2gr.w $t1, $xr9, 1
    xvpickve2gr.w $t2, $xr9, 2
    xvpickve2gr.w $t3, $xr9, 3
    xvpickve2gr.w $t4, $xr9, 4
    xvpickve2gr.w $t5, $xr9, 5
    xvpickve2gr.w $t6, $xr9, 6
    xvpickve2gr.w $t7, $xr9, 7
    add.d $a0, $a0, $t0
    add.d $a0, $a0, $t1
    add.d $a0, $a0, $t2
    add.d $a0, $a0, $t3
    add.d $a0, $a0, $t4
    add.d $a0, $a0, $t5
    add.d $a0, $a0, $t6
    add.d $a0, $a0, $t7
    jr $ra

/* XOR-fold of native 16-bit words. */
    .globl la256_xor_fold
la256_xor_fold:
    xvreplgr2vr.h $xr4, $zero
1:  addi.d $t8, $zero, 16
    bltu $a1, $t8, 2f
    xvld $xr0, $a0, 0
    xvxor.v $xr4, $xr4, $xr0
    addi.d $a0, $a0, 32
    addi.d $a1, $a1, -16
    b 1b
2:  /* PICKVE2GR has no .h form: extract each w lane and split the two h
     * halves with bstrpick. */
    xvpickve2gr.w $t0, $xr4, 0
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $t1, $t2
    xvpickve2gr.w $t0, $xr4, 1
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    xvpickve2gr.w $t0, $xr4, 2
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    xvpickve2gr.w $t0, $xr4, 3
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    xvpickve2gr.w $t0, $xr4, 4
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    xvpickve2gr.w $t0, $xr4, 5
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    xvpickve2gr.w $t0, $xr4, 6
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    xvpickve2gr.w $t0, $xr4, 7
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    jr $ra

/* ---- LSX (128-bit) ---- */

    .globl la128_xor
la128_xor:
1:  addi.d $t8, $zero, 16
    bltu $a2, $t8, 2f
    vld $vr0, $a0, 0
    vld $vr1, $a1, 0
    vxor.v $vr0, $vr0, $vr1
    vst $vr0, $a0, 0
    addi.d $a0, $a0, 16
    addi.d $a1, $a1, 16
    addi.d $a2, $a2, -16
    b 1b
2:  jr $ra

    .globl la128_xor_const
la128_xor_const:
    vreplgr2vr.b $vr3, $a1
1:  addi.d $t8, $zero, 16
    bltu $a2, $t8, 2f
    vld $vr0, $a0, 0
    vxor.v $vr0, $vr0, $vr3
    vst $vr0, $a0, 0
    addi.d $a0, $a0, 16
    addi.d $a2, $a2, -16
    b 1b
2:  jr $ra

    .globl la128_sum
la128_sum:
    vreplgr2vr.b $vr24, $zero
    vreplgr2vr.w $vr8, $zero
    vreplgr2vr.w $vr9, $zero
    vreplgr2vr.w $vr10, $zero
    vreplgr2vr.w $vr11, $zero
1:  addi.d $t8, $zero, 16
    bltu $a1, $t8, 2f
    vld $vr0, $a0, 0
    vilvl.b $vr2, $vr24, $vr0
    vilvh.b $vr3, $vr24, $vr0
    vilvl.h $vr4, $vr24, $vr2
    vilvh.h $vr5, $vr24, $vr2
    vilvl.h $vr6, $vr24, $vr3
    vilvh.h $vr7, $vr24, $vr3
    vadd.w $vr8, $vr8, $vr4
    vadd.w $vr9, $vr9, $vr5
    vadd.w $vr10, $vr10, $vr6
    vadd.w $vr11, $vr11, $vr7
    addi.d $a0, $a0, 16
    addi.d $a1, $a1, -16
    b 1b
2:  vadd.w $vr8, $vr8, $vr10
    vadd.w $vr9, $vr9, $vr11
    vpickve2gr.w $t0, $vr8, 0
    vpickve2gr.w $t1, $vr8, 1
    vpickve2gr.w $t2, $vr8, 2
    vpickve2gr.w $t3, $vr8, 3
    add.d $a0, $t0, $t1
    add.d $a0, $a0, $t2
    add.d $a0, $a0, $t3
    vpickve2gr.w $t0, $vr9, 0
    vpickve2gr.w $t1, $vr9, 1
    vpickve2gr.w $t2, $vr9, 2
    vpickve2gr.w $t3, $vr9, 3
    add.d $a0, $a0, $t0
    add.d $a0, $a0, $t1
    add.d $a0, $a0, $t2
    add.d $a0, $a0, $t3
    jr $ra

    .globl la128_xor_fold
la128_xor_fold:
    vreplgr2vr.h $vr4, $zero
1:  addi.d $t8, $zero, 8
    bltu $a1, $t8, 2f
    vld $vr0, $a0, 0
    vxor.v $vr4, $vr4, $vr0
    addi.d $a0, $a0, 16
    addi.d $a1, $a1, -8
    b 1b
2:  vpickve2gr.w $t0, $vr4, 0
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $t1, $t2
    vpickve2gr.w $t0, $vr4, 1
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    vpickve2gr.w $t0, $vr4, 2
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    vpickve2gr.w $t0, $vr4, 3
    bstrpick.w $t1, $t0, 15, 0
    bstrpick.w $t2, $t0, 31, 16
    xor $a0, $a0, $t1
    xor $a0, $a0, $t2
    jr $ra

/* Execution-coherence probes for the managed emitters; not used in
 * production. Each returns a0 + constant. */
    .globl la_probe_add7
la_probe_add7:
    addi.d $a0, $a0, 7
    jr $ra

    .globl la_probe_add11
la_probe_add11:
    addi.d $a0, $a0, 11
    jr $ra
