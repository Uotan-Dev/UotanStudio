// Derived from K4os.Compression.LZ4 1.3.8 (f5a25b7d72e2e41550fe20662597169ff11c3b60).
// LZ4 fillOutput corrections follow liblz4 v1.9.4. See Lz4DestinationCodec.LICENSE.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using reg_t = System.UInt64;
using size_t = System.UInt32;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;

internal static unsafe partial class Lz4DestinationCodec
{
    private const int LZ4_MEMORY_USAGE = 14;
    private const int LZ4_MAX_INPUT_SIZE = 0x7E000000;
    private const int LZ4_DISTANCE_MAX = 65535;
    private const int LZ4_DISTANCE_ABSOLUTE_MAX = 65535;

    private const int LZ4_HASHLOG = LZ4_MEMORY_USAGE - 2;
    private const int LZ4_HASHTABLESIZE = 1 << LZ4_MEMORY_USAGE;
    private const int LZ4_HASH_SIZE_U32 = 1 << LZ4_HASHLOG;

    private const int ACCELERATION_DEFAULT = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LZ4_stream_t
    {
        public fixed uint hashTable[LZ4_HASH_SIZE_U32];
        public uint currentOffset;
        public bool dirty;
        public tableType_t tableType;
        public byte* dictionary;
        public LZ4_stream_t* dictCtx;
        public uint dictSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LZ4_streamDecode_t
    {
        public byte* externalDict;
        public uint extDictSize;
        public byte* prefixEnd;
        public uint prefixSize;
    }

    private const int MINMATCH = 4;

    private const int WILDCOPYLENGTH = 8;
    private const int LASTLITERALS = 5;
    private const int MFLIMIT = 12; // WILDCOPYLENGTH + MINMATCH;

    private const int MATCH_SAFEGUARD_DISTANCE = 2 * WILDCOPYLENGTH - MINMATCH;
    private const int FASTLOOP_SAFE_DISTANCE = 64;

    private const int LZ4_minLength = MFLIMIT + 1;

    private const int KB = 1 << 10;
    private const int MB = 1 << 20;
    private const uint GB = 1u << 30;

    private const int ML_BITS = 4;
    private const uint ML_MASK = (1U << ML_BITS) - 1;
    private const int RUN_BITS = 8 - ML_BITS;
    private const uint RUN_MASK = (1U << RUN_BITS) - 1;

    private const int OPTIMAL_ML = (int)((ML_MASK - 1) + MINMATCH);
    private const int LZ4_OPT_NUM = (1 << 12);

    private const int LZ4_64Klimit = 64 * KB + (MFLIMIT - 1);
    private const int LZ4_skipTrigger = 6;

    private enum limitedOutput_directive
    {
        notLimited = 0, limitedOutput = 1, fillOutput = 2
    }

    private enum tableType_t
    {
        clearedTable = 0, byPtr, byU32, byU16
    }

    private enum dict_directive
    {
        noDict = 0, withPrefix64k, usingExtDict, usingDictCtx
    }

    private enum dictIssue_directive
    {
        noDictIssue = 0, dictSmall
    }

    private enum endCondition_directive
    {
        endOnOutputSize = 0, endOnInputSize = 1
    }

    private enum earlyEnd_directive
    {
        full = 0, partial = 1
    }

    private enum variable_length_error
    {
        loop_error = -2, initial_error = -1, ok = 0
    }

    private enum dictCtx_directive { noDictCtx = 0, usingDictCtxHc }

    private const int LZ4HC_DICTIONARY_LOGSIZE = 16;
    private const int LZ4HC_MAXD = (1 << LZ4HC_DICTIONARY_LOGSIZE);
    private const int LZ4HC_MAXD_MASK = (LZ4HC_MAXD - 1);

    private const int LZ4HC_HASH_LOG = 15;
    private const int LZ4HC_HASHTABLESIZE = (1 << LZ4HC_HASH_LOG);
    private const int LZ4HC_HASH_MASK = (LZ4HC_HASHTABLESIZE - 1);

    private const int LZ4HC_CLEVEL_MIN = 3;
    private const int LZ4HC_CLEVEL_DEFAULT = 9;
    private const int LZ4HC_CLEVEL_OPT_MIN = 10;
    private const int LZ4HC_CLEVEL_MAX = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct LZ4_streamHC_t
    {
        public fixed uint hashTable[LZ4HC_HASHTABLESIZE];
        public fixed ushort chainTable[LZ4HC_MAXD];
        public byte* end; /* next block here to continue on current prefix */
        public byte* @base; /* All index relative to this position */
        public byte* dictBase; /* alternate @base for extDict */
        public uint dictLimit; /* below that point, need extDict */
        public uint lowLimit; /* below that point, no more dict */
        public uint nextToUpdate; /* index from which to continue dictionary update */
        public short compressionLevel;
        public bool favorDecSpeed; /* favor decompression speed if this flag set */
        public bool dirty; /* stream has to be fully reset if this flag is set */
        public LZ4_streamHC_t* dictCtx;
    }

    private enum repeat_state_e { rep_untested, rep_not, rep_confirmed }

    private enum HCfavor_e { favorCompressionRatio = 0, favorDecompressionSpeed }

    [StructLayout(LayoutKind.Sequential)]
    private struct LZ4HC_match_t
    {
        public int off;
        public int len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LZ4HC_optimal_t
    {
        public int price;
        public int off;
        public int mlen;
        public int litlen;
    }

    private enum lz4hc_strat_e { lz4hc, lz4opt }

    [StructLayout(LayoutKind.Sequential)]
    private struct cParams_t
    {
        public lz4hc_strat_e strat;
        public uint nbSearches;
        public uint targetLength;

        public cParams_t(lz4hc_strat_e strat, uint nbSearches, uint targetLength)
        {
            this.strat = strat;
            this.nbSearches = nbSearches;
            this.targetLength = targetLength;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4_sizeofStateHC() => sizeof(LZ4_streamHC_t);

    private static void LZ4_setCompressionLevel(
        LZ4_streamHC_t* LZ4_streamHCPtr, int compressionLevel)
    {
        if (compressionLevel < 1) compressionLevel = LZ4HC_CLEVEL_DEFAULT;
        if (compressionLevel > LZ4HC_CLEVEL_MAX) compressionLevel = LZ4HC_CLEVEL_MAX;
        LZ4_streamHCPtr->compressionLevel = (short)compressionLevel;
    }

    private static void LZ4_favorDecompressionSpeed(LZ4_streamHC_t* LZ4_streamHCPtr, int favor)
    {
        LZ4_streamHCPtr->favorDecSpeed = (favor != 0);
    }

    private static LZ4_streamHC_t* LZ4_initStreamHC(void* buffer, int size)
    {
        LZ4_streamHC_t* LZ4_streamHCPtr = (LZ4_streamHC_t*)buffer;
        if (buffer == null) return null;
        if (size < sizeof(LZ4_streamHC_t)) return null;

        LZ4_streamHCPtr->end = (byte*)-1;
        LZ4_streamHCPtr->@base = null;
        LZ4_streamHCPtr->dictCtx = null;
        LZ4_streamHCPtr->favorDecSpeed = false;
        LZ4_streamHCPtr->dirty = false;
        LZ4_setCompressionLevel(LZ4_streamHCPtr, LZ4HC_CLEVEL_DEFAULT);
        return LZ4_streamHCPtr;
    }

    private static LZ4_streamHC_t* LZ4_initStreamHC(LZ4_streamHC_t* stream) =>
        LZ4_initStreamHC(stream, sizeof(LZ4_streamHC_t));

    private static void LZ4_resetStreamHC_fast(
        LZ4_streamHC_t* LZ4_streamHCPtr, int compressionLevel)
    {
        if (LZ4_streamHCPtr->dirty)
        {
            LZ4_initStreamHC(LZ4_streamHCPtr);
        }
        else
        {
            LZ4_streamHCPtr->end -= LZ4_streamHCPtr->@base;
            LZ4_streamHCPtr->@base = null;
            LZ4_streamHCPtr->dictCtx = null;
        }

        LZ4_setCompressionLevel(LZ4_streamHCPtr, compressionLevel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HASH_FUNCTION(uint value) =>
        (value * 2654435761U) >> (MINMATCH * 8 - LZ4HC_HASH_LOG);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ushort DELTANEXTU16(ushort* table, uint pos) =>
        ref table[(ushort)(pos)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4HC_hashPtr(void* ptr) => HASH_FUNCTION(Mem.Peek4(ptr));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LZ4HC_Insert(LZ4_streamHC_t* hc4, byte* ip)
    {
        ushort* chainTable = hc4->chainTable;
        uint* hashTable = hc4->hashTable;
        byte* @base = hc4->@base;
        uint target = (uint)(ip - @base);
        uint idx = hc4->nextToUpdate;

        while (idx < target)
        {
            uint h = LZ4HC_hashPtr(@base + idx);
            uint delta = idx - hashTable[h];
            if (delta > LZ4_DISTANCE_MAX) delta = LZ4_DISTANCE_MAX;
            DELTANEXTU16(chainTable, idx) = (ushort)delta;
            hashTable[h] = idx;
            idx++;
        }

        hc4->nextToUpdate = target;
    }

    private static void LZ4HC_setExternalDict(LZ4_streamHC_t* ctxPtr, byte* newBlock)
    {
        if (ctxPtr->end >= ctxPtr->@base + ctxPtr->dictLimit + 4)
            LZ4HC_Insert(
                ctxPtr, ctxPtr->end - 3); /* Referencing remaining dictionary content */

        /* Only one memory segment for extDict, so any previous extDict is lost at this stage */
        ctxPtr->lowLimit = ctxPtr->dictLimit;
        ctxPtr->dictLimit = (uint)(ctxPtr->end - ctxPtr->@base);
        ctxPtr->dictBase = ctxPtr->@base;
        ctxPtr->@base = newBlock - ctxPtr->dictLimit;
        ctxPtr->end = newBlock;
        ctxPtr->nextToUpdate = ctxPtr->dictLimit; /* match referencing will resume from there */

        /* cannot reference an extDict and a dictCtx at the same time */
        ctxPtr->dictCtx = null;
    }

    private static void LZ4HC_clearTables(LZ4_streamHC_t* hc4)
    {
        Mem.Fill((byte*)hc4->hashTable, 0, sizeof(uint) * LZ4HC_HASHTABLESIZE);
        Mem.Fill((byte*)hc4->chainTable, 0xFF, sizeof(ushort) * LZ4HC_MAXD);
    }

    private static void LZ4HC_init_internal(LZ4_streamHC_t* hc4, byte* start)
    {
        var startingOffset = (hc4->end - hc4->@base);
        if (startingOffset < 0 || startingOffset > 1 * GB)
        {
            LZ4HC_clearTables(hc4);
            startingOffset = 0;
        }

        startingOffset += 64 * KB;
        hc4->nextToUpdate = (uint)startingOffset;
        hc4->@base = start - startingOffset;
        hc4->end = start;
        hc4->dictBase = start - startingOffset;
        hc4->dictLimit = (uint)startingOffset;
        hc4->lowLimit = (uint)startingOffset;
    }

    private static int LZ4_saveDictHC(
        LZ4_streamHC_t* LZ4_streamHCPtr, byte* safeBuffer, int dictSize)
    {
        LZ4_streamHC_t* streamPtr = LZ4_streamHCPtr;
        int prefixSize = (int)(streamPtr->end - (streamPtr->@base + streamPtr->dictLimit));
        if (dictSize > 64 * KB) dictSize = 64 * KB;
        if (dictSize < 4) dictSize = 0;
        if (dictSize > prefixSize) dictSize = prefixSize;
        Mem.Move(safeBuffer, streamPtr->end - dictSize, dictSize);
        uint endIndex = (uint)(streamPtr->end - streamPtr->@base);
        streamPtr->end = (byte*)safeBuffer + dictSize;
        streamPtr->@base = streamPtr->end - endIndex;
        streamPtr->dictLimit = endIndex - (uint)dictSize;
        streamPtr->lowLimit = endIndex - (uint)dictSize;
        if (streamPtr->nextToUpdate < streamPtr->dictLimit)
            streamPtr->nextToUpdate = streamPtr->dictLimit;
        return dictSize;
    }

    private static int LZ4_loadDictHC(LZ4_streamHC_t* LZ4_streamHCPtr, byte* dictionary, int dictSize)
    {
        LZ4_streamHC_t* ctxPtr = LZ4_streamHCPtr;
        Assert(LZ4_streamHCPtr != null);
        if (dictSize > 64 * KB)
        {
            dictionary += dictSize - 64 * KB;
            dictSize = 64 * KB;
        }

        /* need a full initialization, there are bad side-effects when using resetFast() */
        {
            int cLevel = ctxPtr->compressionLevel;
            LZ4_initStreamHC(LZ4_streamHCPtr);
            LZ4_setCompressionLevel(LZ4_streamHCPtr, cLevel);
        }
        LZ4HC_init_internal(ctxPtr, (byte*)dictionary);
        ctxPtr->end = (byte*)dictionary + dictSize;
        if (dictSize >= 4) LZ4HC_Insert(ctxPtr, ctxPtr->end - 3);
        return dictSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4HC_rotl32(uint x, int r) => ((x << r) | (x >> (32 - r)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LZ4HC_protectDictEnd(uint dictLimit, uint matchIndex) =>
        ((uint)((dictLimit - 1) - matchIndex) >= 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4HC_countBack(byte* ip, byte* match, byte* iMin, byte* mMin)
    {
        int back = 0;
        int min = (int)MAX(iMin - ip, mMin - match);
        Assert(min <= 0);
        Assert(ip >= iMin);
        Assert((size_t)(ip - iMin) < (1U << 31));
        Assert(match >= mMin);
        Assert((size_t)(match - mMin) < (1U << 31));
        while ((back > min)
            && (ip[back - 1] == match[back - 1]))
            back--;
        return back;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4HC_reverseCountPattern(byte* ip, byte* iLow, uint pattern)
    {
        byte* iStart = ip;

        while ((ip >= iLow + 4))
        {
            if (Mem.Peek4(ip - 4) != pattern) break;

            ip -= 4;
        }

        {
            byte* bytePtr = (byte*)(&pattern) + 3; /* works for any endianness */
            while ((ip > iLow))
            {
                if (ip[-1] != *bytePtr) break;

                ip--;
                bytePtr--;
            }
        }
        return (uint)(iStart - ip);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4HC_rotatePattern(size_t rotate, uint pattern)
    {
        size_t bitsToRotate = (rotate & (sizeof(uint) - 1)) << 3;
        if (bitsToRotate == 0)
            return pattern;
        return LZ4HC_rotl32(pattern, (int)bitsToRotate);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4HC_literalsPrice(int litlen)
    {
        int price = litlen;
        Assert(litlen >= 0);
        if (litlen >= (int)RUN_MASK)
            price += 1 + ((litlen - (int)RUN_MASK) / 255);
        return price;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4HC_sequencePrice(int litlen, int mlen)
    {
        int price = 1 + 2; /* token + 16-bit offset */
        Assert(litlen >= 0);
        Assert(mlen >= MINMATCH);

        price += LZ4HC_literalsPrice(litlen);

        if (mlen >= (int)(ML_MASK + MINMATCH))
            price += 1 + ((mlen - (int)(ML_MASK + MINMATCH)) / 255);

        return price;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4_hash4(uint sequence, tableType_t tableType)
    {
        var hashLog = tableType == tableType_t.byU16 ? LZ4_HASHLOG + 1 : LZ4_HASHLOG;
        return unchecked((sequence * 2654435761u) >> (MINMATCH * 8 - hashLog));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4_hash5(ulong sequence, tableType_t tableType)
    {
        var hashLog = tableType == tableType_t.byU16 ? LZ4_HASHLOG + 1 : LZ4_HASHLOG;
        return unchecked((uint)(((sequence << 24) * 889523592379ul) >> (64 - hashLog)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LZ4_clearHash(uint h, void* tableBase, tableType_t tableType)
    {
        switch (tableType)
        {
            case tableType_t.byPtr:
                ((byte**)tableBase)[h] = null;
                return;
            case tableType_t.byU32:
                ((uint*)tableBase)[h] = 0;
                return;
            case tableType_t.byU16:
                ((ushort*)tableBase)[h] = 0;
                return;
            default:
                Assert(false);
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LZ4_putIndexOnHash(
        uint idx, uint h, void* tableBase, tableType_t tableType)
    {
        switch (tableType)
        {
            case tableType_t.byU32:
                ((uint*)tableBase)[h] = idx;
                return;
            case tableType_t.byU16:
                Assert(idx < 65536);
                ((ushort*)tableBase)[h] = (ushort)idx;
                return;
            default:
                Assert(false);
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LZ4_putPositionOnHash(
        byte* p, uint h, void* tableBase, tableType_t tableType, byte* srcBase)
    {
        switch (tableType)
        {
            case tableType_t.byPtr:
                ((byte**)tableBase)[h] = p;
                return;
            case tableType_t.byU32:
                ((uint*)tableBase)[h] = (uint)(p - srcBase);
                return;
            case tableType_t.byU16:
                ((ushort*)tableBase)[h] = (ushort)(p - srcBase);
                return;
            default:
                Assert(false);
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4_getIndexOnHash(uint h, void* tableBase, tableType_t tableType)
    {
        Assert(LZ4_MEMORY_USAGE > 2);
        switch (tableType)
        {
            case tableType_t.byU32:
                Assert(h < (1U << (LZ4_MEMORY_USAGE - 2)));
                return ((uint*)tableBase)[h];
            case tableType_t.byU16:
                Assert(h < (1U << (LZ4_MEMORY_USAGE - 1)));
                return ((ushort*)tableBase)[h];
            default:
                Assert(false);
                return 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* LZ4_getPositionOnHash(
        uint h, void* tableBase, tableType_t tableType, byte* srcBase)
    {
        switch (tableType)
        {
            case tableType_t.byPtr: return ((byte**)tableBase)[h];
            case tableType_t.byU32: return ((uint*)tableBase)[h] + srcBase;
            default: return ((ushort*)tableBase)[h] + srcBase;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MIN(int a, int b) => a < b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MIN(uint a, uint b) => a < b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MAX(uint a, uint b) => a < b ? b : a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long MAX(long a, long b) => a < b ? b : a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long MIN(long a, long b) => a < b ? a : b;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4_count(byte* pIn, byte* pMatch, byte* pInLimit)
    {
        const int STEPSIZE = ALGORITHM_ARCH;

        var pStart = pIn;

        if (pIn < pInLimit - (STEPSIZE - 1))
        {
            var diff = Mem.PeekW(pMatch) ^ Mem.PeekW(pIn);
            if (diff != 0)
                return LZ4_NbCommonBytes(diff);

            pIn += STEPSIZE;
            pMatch += STEPSIZE;
        }

        while (pIn < pInLimit - (STEPSIZE - 1))
        {
            var diff = Mem.PeekW(pMatch) ^ Mem.PeekW(pIn);
            if (diff != 0)
                return (uint)(pIn + LZ4_NbCommonBytes(diff) - pStart);

            pIn += STEPSIZE;
            pMatch += STEPSIZE;
        }



        if (pIn < pInLimit - 3 && Mem.Peek4(pMatch) == Mem.Peek4(pIn))
        {
            pIn += 4;
            pMatch += 4;
        }



        if (pIn < pInLimit - 1 && Mem.Peek2(pMatch) == Mem.Peek2(pIn))
        {
            pIn += 2;
            pMatch += 2;
        }

        if (pIn < pInLimit && *pMatch == *pIn)
            pIn++;

        return (uint)(pIn - pStart);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4_hashPosition(void* p, tableType_t tableType)
    {

        if (tableType != tableType_t.byU16)
            return LZ4_hash5(Mem.PeekW(p), tableType);

        return LZ4_hash4(Mem.Peek4(p), tableType);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LZ4_putPosition(
        byte* p, void* tableBase, tableType_t tableType, byte* srcBase) =>
        LZ4_putPositionOnHash(p, LZ4_hashPosition(p, tableType), tableBase, tableType, srcBase);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* LZ4_getPosition(
        byte* p, void* tableBase, tableType_t tableType, byte* srcBase) =>
        LZ4_getPositionOnHash(LZ4_hashPosition(p, tableType), tableBase, tableType, srcBase);

}