// Derived from K4os.Compression.LZ4 1.3.8 (f5a25b7d72e2e41550fe20662597169ff11c3b60).
// LZ4 fillOutput corrections follow liblz4 v1.9.4. See Lz4DestinationCodec.LICENSE.
using System.Runtime.CompilerServices;
using reg_t = System.UInt64;
using size_t = System.UInt32;

namespace UotanStudio.Firmware.Compression.Codecs.Lz4;

internal static unsafe partial class Lz4DestinationCodec
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4HC_countPattern(byte* ip, byte* iEnd, uint pattern32)
    {
        const int ARCH = sizeof(reg_t);
        byte* iStart = ip;
        reg_t pattern = pattern32;

        pattern |= pattern << 32;

        while ((ip < iEnd - (ARCH - 1)))
        {
            reg_t diff = Mem.PeekW(ip) ^ pattern;
            if (diff == 0)
            {
                ip += ARCH;
                continue;
            }

            ip += LZ4_NbCommonBytes(diff);
            return (uint)(ip - iStart);
        }

        reg_t patternByte = pattern;
        while ((ip < iEnd) && (*ip == (byte)patternByte))
        {
            ip++;
            patternByte >>= 8;
        }

        return (uint)(ip - iStart);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int
        LZ4HC_InsertAndGetWiderMatch(
            LZ4_streamHC_t* hc4,
            byte* ip,
            byte* iLowLimit,
            byte* iHighLimit,
            int longest,
            byte** matchpos,
            byte** startpos,
            int maxNbAttempts,
            bool patternAnalysis,
            bool chainSwap,
            dictCtx_directive dict,
            HCfavor_e favorDecSpeed)
    {
        ushort* chainTable = hc4->chainTable;
        uint* HashTable = hc4->hashTable;
        LZ4_streamHC_t* dictCtx = hc4->dictCtx;
        byte* @base = hc4->@base;
        uint dictLimit = hc4->dictLimit;
        byte* lowPrefixPtr = @base + dictLimit;
        uint ipIndex = (uint)(ip - @base);
        uint lowestMatchIndex = (hc4->lowLimit + (LZ4_DISTANCE_MAX + 1) > ipIndex)
            ? hc4->lowLimit : ipIndex - LZ4_DISTANCE_MAX;
        byte* dictBase = hc4->dictBase;
        int lookBackLength = (int)(ip - iLowLimit);
        int nbAttempts = maxNbAttempts;
        uint matchChainPos = 0;
        uint pattern = Mem.Peek4(ip);
        uint matchIndex;
        repeat_state_e repeat = repeat_state_e.rep_untested;
        size_t srcPatternLength = 0;

        LZ4HC_Insert(hc4, ip);
        matchIndex = HashTable[LZ4HC_hashPtr(ip)];

        while ((matchIndex >= lowestMatchIndex) && (nbAttempts != 0))
        {
            int matchLength = 0;
            nbAttempts--;
            Assert(matchIndex < ipIndex);
            if (favorDecSpeed != 0 && (ipIndex - matchIndex < 8))
            {
            }
            else if (matchIndex >= dictLimit)
            {
                byte* matchPtr = @base + matchIndex;
                Assert(matchPtr >= lowPrefixPtr);
                Assert(matchPtr < ip);
                Assert(longest >= 1);
                if (Mem.Peek2(iLowLimit + longest - 1)
                    == Mem.Peek2(matchPtr - lookBackLength + longest - 1))
                {
                    if (Mem.Peek4(matchPtr) == pattern)
                    {
                        int back = lookBackLength != 0 ? LZ4HC_countBack(
                            ip, matchPtr, iLowLimit, lowPrefixPtr) : 0;
                        matchLength = MINMATCH + (int)LZ4_count(
                            ip + MINMATCH, matchPtr + MINMATCH, iHighLimit);
                        matchLength -= back;
                        if (matchLength > longest)
                        {
                            longest = matchLength;
                            *matchpos = matchPtr + back;
                            *startpos = ip + back;
                        }
                    }
                }
            }
            else
            {
                byte* matchPtr = dictBase + matchIndex;
                if (Mem.Peek4(matchPtr) == pattern)
                {
                    byte* dictStart = dictBase + hc4->lowLimit;
                    int back = 0;
                    byte* vLimit = ip + (dictLimit - matchIndex);
                    if (vLimit > iHighLimit) vLimit = iHighLimit;
                    matchLength = (int)LZ4_count(ip + MINMATCH, matchPtr + MINMATCH, vLimit)
                        + MINMATCH;
                    if ((ip + matchLength == vLimit) && (vLimit < iHighLimit))
                        matchLength += (int)LZ4_count(
                            ip + matchLength, lowPrefixPtr, iHighLimit);
                    back = lookBackLength != 0 ? LZ4HC_countBack(
                        ip, matchPtr, iLowLimit, dictStart) : 0;
                    matchLength -= back;
                    if (matchLength > longest)
                    {
                        longest = matchLength;
                        *matchpos =
                            @base + matchIndex
                            + back;
                        *startpos = ip + back;
                    }
                }
            }

            if (chainSwap && matchLength == longest)
            {
                Assert(lookBackLength == 0);
                if (matchIndex + (uint)longest <= ipIndex)
                {
                    int kTrigger = 4;
                    uint distanceToNextMatch = 1;
                    int end = longest - MINMATCH + 1;
                    int step = 1;
                    int accel = 1 << kTrigger;
                    int pos;
                    for (pos = 0; pos < end; pos += step)
                    {
                        uint candidateDist = DELTANEXTU16(chainTable, matchIndex + (uint)pos);
                        step = (accel++ >> kTrigger);
                        if (candidateDist > distanceToNextMatch)
                        {
                            distanceToNextMatch = candidateDist;
                            matchChainPos = (uint)pos;
                            accel = 1 << kTrigger;
                        }
                    }

                    if (distanceToNextMatch > 1)
                    {
                        if (distanceToNextMatch > matchIndex) break;

                        matchIndex -= distanceToNextMatch;
                        continue;
                    }
                }
            }

            {
                uint distNextMatch = DELTANEXTU16(chainTable, matchIndex);
                if (patternAnalysis && distNextMatch == 1 && matchChainPos == 0)
                {
                    uint matchCandidateIdx = matchIndex - 1;
                    if (repeat == repeat_state_e.rep_untested)
                    {
                        if (((pattern & 0xFFFF) == (pattern >> 16))
                            & ((pattern & 0xFF) == (pattern >> 24)))
                        {
                            repeat = repeat_state_e.rep_confirmed;
                            srcPatternLength = LZ4HC_countPattern(
                                ip + sizeof(uint), iHighLimit, pattern) + sizeof(uint);
                        }
                        else
                        {
                            repeat = repeat_state_e.rep_not;
                        }
                    }

                    if ((repeat == repeat_state_e.rep_confirmed)
                        && (matchCandidateIdx >= lowestMatchIndex)
                        && LZ4HC_protectDictEnd(dictLimit, matchCandidateIdx))
                    {
                        bool extDict = matchCandidateIdx < dictLimit;
                        byte* matchPtr = (extDict ? dictBase : @base) + matchCandidateIdx;
                        if (Mem.Peek4(matchPtr) == pattern)
                        {
                            byte* dictStart = dictBase + hc4->lowLimit;
                            byte* iLimit = extDict ? dictBase + dictLimit : iHighLimit;
                            size_t forwardPatternLength = LZ4HC_countPattern(
                                matchPtr + sizeof(uint), iLimit, pattern) + sizeof(uint);
                            if (extDict && matchPtr + forwardPatternLength == iLimit)
                            {
                                uint rotatedPattern = LZ4HC_rotatePattern(
                                    forwardPatternLength, pattern);
                                forwardPatternLength += LZ4HC_countPattern(
                                    lowPrefixPtr, iHighLimit, rotatedPattern);
                            }

                            {
                                byte* lowestMatchPtr = extDict ? dictStart : lowPrefixPtr;
                                size_t backLength = LZ4HC_reverseCountPattern(
                                    matchPtr, lowestMatchPtr, pattern);
                                size_t currentSegmentLength;
                                if (!extDict && matchPtr - backLength == lowPrefixPtr
                                    && hc4->lowLimit < dictLimit)
                                {
                                    uint rotatedPattern = LZ4HC_rotatePattern(
                                        (uint)(-(int)backLength), pattern);
                                    backLength += LZ4HC_reverseCountPattern(
                                        dictBase + dictLimit, dictStart, rotatedPattern);
                                }

                                backLength = matchCandidateIdx - MAX(
                                    matchCandidateIdx - (uint)backLength, lowestMatchIndex);
                                Assert(
                                    matchCandidateIdx - backLength >= lowestMatchIndex);
                                currentSegmentLength = backLength + forwardPatternLength;
                                if ((currentSegmentLength >= srcPatternLength)
                                    && (forwardPatternLength <= srcPatternLength))
                                {
                                    uint newMatchIndex = matchCandidateIdx
                                        + (uint)forwardPatternLength
                                        - (uint)srcPatternLength;
                                    if (LZ4HC_protectDictEnd(dictLimit, newMatchIndex))
                                        matchIndex = newMatchIndex;
                                    else
                                    {
                                        Assert(
                                            newMatchIndex >= dictLimit - 3
                                            && newMatchIndex < dictLimit && !extDict);
                                        matchIndex = dictLimit;
                                    }
                                }
                                else
                                {
                                    uint newMatchIndex =
                                        matchCandidateIdx
                                        - (uint)backLength;
                                    if (!LZ4HC_protectDictEnd(dictLimit, newMatchIndex))
                                    {
                                        Assert(
                                            newMatchIndex >= dictLimit - 3
                                            && newMatchIndex < dictLimit && !extDict);
                                        matchIndex = dictLimit;
                                    }
                                    else
                                    {
                                        matchIndex = newMatchIndex;
                                        if (lookBackLength == 0)
                                        {
                                            size_t maxML = MIN(
                                                currentSegmentLength, srcPatternLength);
                                            if ((size_t)longest < maxML)
                                            {
                                                Assert(@base + matchIndex != ip);
                                                if ((size_t)(ip - @base) - matchIndex
                                                    > LZ4_DISTANCE_MAX) break;

                                                Assert(maxML < 2 * GB);
                                                longest = (int)maxML;
                                                *matchpos =
                                                    @base
                                                    + matchIndex;
                                                *startpos = ip;
                                            }

                                            {
                                                uint distToNextPattern = DELTANEXTU16(
                                                    chainTable, matchIndex);
                                                if (distToNextPattern > matchIndex)
                                                    break;

                                                matchIndex -= distToNextPattern;
                                            }
                                        }
                                    }
                                }
                            }
                            continue;
                        }
                    }
                }
            }

            matchIndex -= DELTANEXTU16(chainTable, matchIndex + matchChainPos);
        }

        if (dict == dictCtx_directive.usingDictCtxHc
            && nbAttempts != 0
            && ipIndex - lowestMatchIndex < LZ4_DISTANCE_MAX)
        {
            size_t dictEndOffset = (size_t)(dictCtx->end - dictCtx->@base);
            uint dictMatchIndex = dictCtx->hashTable[LZ4HC_hashPtr(ip)];
            Assert(dictEndOffset <= 1 * GB);
            matchIndex = dictMatchIndex + lowestMatchIndex - (uint)dictEndOffset;
            while (ipIndex - matchIndex <= LZ4_DISTANCE_MAX && nbAttempts-- != 0)
            {
                byte* matchPtr = dictCtx->@base + dictMatchIndex;

                if (Mem.Peek4(matchPtr) == pattern)
                {
                    int mlt;
                    int back = 0;
                    byte* vLimit = ip + (dictEndOffset - dictMatchIndex);
                    if (vLimit > iHighLimit) vLimit = iHighLimit;
                    mlt = (int)LZ4_count(ip + MINMATCH, matchPtr + MINMATCH, vLimit)
                        + MINMATCH;
                    back = lookBackLength != 0 ? LZ4HC_countBack(
                        ip, matchPtr, iLowLimit, dictCtx->@base + dictCtx->dictLimit) : 0;
                    mlt -= back;
                    if (mlt > longest)
                    {
                        longest = mlt;
                        *matchpos = @base + matchIndex + back;
                        *startpos = ip + back;
                    }
                }

                {
                    uint nextOffset = DELTANEXTU16(dictCtx->chainTable, dictMatchIndex);
                    dictMatchIndex -= nextOffset;
                    matchIndex -= nextOffset;
                }
            }
        }

        return longest;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4HC_InsertAndFindBestMatch(
        LZ4_streamHC_t* hc4,
        byte* ip, byte* iLimit,
        byte** matchpos,
        int maxNbAttempts,
        bool patternAnalysis,
        dictCtx_directive dict)
    {
        byte* uselessPtr = ip;
        return LZ4HC_InsertAndGetWiderMatch(
            hc4, ip, ip, iLimit, MINMATCH - 1, matchpos, &uselessPtr, maxNbAttempts,
            patternAnalysis, false, dict, HCfavor_e.favorCompressionRatio);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LZ4HC_match_t LZ4HC_FindLongerMatch(
        LZ4_streamHC_t* ctx,
        byte* ip, byte* iHighLimit,
        int minLen, int nbSearches,
        dictCtx_directive dict,
        HCfavor_e favorDecSpeed)
    {
        LZ4HC_match_t match;
        match.len = 0;
        match.off = 0;
        byte* matchPtr = null;
        int matchLength = LZ4HC_InsertAndGetWiderMatch(
            ctx, ip, ip, iHighLimit, minLen, &matchPtr, &ip, nbSearches,
            true,
            true, dict, favorDecSpeed);
        if (matchLength <= minLen) return match;

        if (favorDecSpeed != 0)
        {
            if ((matchLength > 18) & (matchLength <= 36))
                matchLength = 18;
        }

        match.len = matchLength;
        match.off = (int)(ip - matchPtr);
        return match;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4HC_encodeSequence(
        byte** ip,
        byte** op,
        byte** anchor,
        int matchLength,
        byte* match,
        limitedOutput_directive limit,
        byte* oend)
    {
        size_t length;
        byte* token = (*op)++;

        length = (size_t)(*ip - *anchor);
        if ((limit != 0) && ((*op + (length / 255) + length + (2 + 1 + LASTLITERALS)) > oend))
            return 1;

        if (length >= RUN_MASK)
        {
            size_t len = length - RUN_MASK;
            *token = (byte)(RUN_MASK << ML_BITS);
            for (; len >= 255; len -= 255) *(*op)++ = 255;
            *(*op)++ = (byte)len;
        }
        else
        {
            *token = (byte)(length << ML_BITS);
        }

        Mem.WildCopy8(*op, *anchor, (*op) + length);
        *op += length;

        Assert(
            (*ip - match)
            <= LZ4_DISTANCE_MAX);
        Mem.Poke2(*op, (ushort)(*ip - match));
        *op += 2;

        Assert(matchLength >= MINMATCH);
        length = (size_t)matchLength - MINMATCH;
        if ((limit != 0) && (*op + (length / 255) + (1 + LASTLITERALS) > oend))
            return 1;

        if (length >= ML_MASK)
        {
            *token += (byte)ML_MASK;
            length -= ML_MASK;
            for (; length >= 510; length -= 510)
            {
                *(*op)++ = 255;
                *(*op)++ = 255;
            }

            if (length >= 255)
            {
                length -= 255;
                *(*op)++ = 255;
            }

            *(*op)++ = (byte)length;
        }
        else
        {
            *token += (byte)(length);
        }

        *ip += matchLength;
        *anchor = *ip;

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4HC_compress_hashChain(
        LZ4_streamHC_t* ctx,
        byte* source,
        byte* dest,
        int* srcSizePtr,
        int maxOutputSize,
        int maxNbAttempts,
        limitedOutput_directive limit,
        dictCtx_directive dict)
    {
        int inputSize = *srcSizePtr;
        bool patternAnalysis = (maxNbAttempts > 128);

        byte* ip = (byte*)source;
        byte* anchor = ip;
        byte* iend = ip + inputSize;
        byte* mflimit = iend - MFLIMIT;
        byte* matchlimit = (iend - LASTLITERALS);

        byte* optr = (byte*)dest;
        byte* op = (byte*)dest;
        byte* oend = op + maxOutputSize;

        int ml0, ml, ml2, ml3;
        byte* start0;
        byte* ref0;
        byte* @ref = null;
        byte* start2 = null;
        byte* ref2 = null;
        byte* start3 = null;
        byte* ref3 = null;

        *srcSizePtr = 0;
        if (limit == limitedOutput_directive.fillOutput)
            oend -= LASTLITERALS;
        if (inputSize < LZ4_minLength)
            goto _last_literals;

        while (ip <= mflimit)
        {
            ml = LZ4HC_InsertAndFindBestMatch(
                ctx, ip, matchlimit, &@ref, maxNbAttempts, patternAnalysis, dict);
            if (ml < MINMATCH)
            {
                ip++;
                continue;
            }

            start0 = ip;
            ref0 = @ref;
            ml0 = ml;

        _Search2:
            if (ip + ml <= mflimit)
            {
                ml2 = LZ4HC_InsertAndGetWiderMatch(
                    ctx,
                    ip + ml - 2, ip + 0, matchlimit, ml, &ref2, &start2,
                    maxNbAttempts, patternAnalysis, false, dict,
                    HCfavor_e.favorCompressionRatio);
            }
            else
            {
                ml2 = ml;
            }

            if (ml2 == ml)
            {
                optr = op;
                if (LZ4HC_encodeSequence(&ip, &op, &anchor, ml, @ref, limit, oend) != 0)
                    goto _dest_overflow;

                continue;
            }

            if (start0 < ip)
            {
                if (start2 < ip + ml0)
                {
                    ip = start0;
                    @ref = ref0;
                    ml = ml0;
                }
            }

            if ((start2 - ip) < 3)
            {
                ml = ml2;
                ip = start2;
                @ref = ref2;
                goto _Search2;
            }

        _Search3:
            if ((start2 - ip) < OPTIMAL_ML)
            {
                int correction;
                int new_ml = ml;
                if (new_ml > OPTIMAL_ML) new_ml = OPTIMAL_ML;
                if (ip + new_ml > start2 + ml2 - MINMATCH)
                    new_ml = (int)(start2 - ip) + ml2 - MINMATCH;
                correction = new_ml - (int)(start2 - ip);
                if (correction > 0)
                {
                    start2 += correction;
                    ref2 += correction;
                    ml2 -= correction;
                }
            }

            if (start2 + ml2 <= mflimit)
            {
                ml3 = LZ4HC_InsertAndGetWiderMatch(
                    ctx,
                    start2 + ml2 - 3, start2, matchlimit, ml2, &ref3, &start3,
                    maxNbAttempts, patternAnalysis, false, dict,
                    HCfavor_e.favorCompressionRatio);
            }
            else
            {
                ml3 = ml2;
            }

            if (ml3 == ml2)
            {
                if (start2 < ip + ml) ml = (int)(start2 - ip);
                optr = op;
                if (LZ4HC_encodeSequence(&ip, &op, &anchor, ml, @ref, limit, oend) != 0)
                    goto _dest_overflow;

                ip = start2;
                optr = op;
                if (LZ4HC_encodeSequence(&ip, &op, &anchor, ml2, ref2, limit, oend) != 0)
                {
                    ml = ml2;
                    @ref = ref2;
                    goto _dest_overflow;
                }

                continue;
            }

            if (start3 < ip + ml + 3)
            {
                if (start3 >= (ip + ml))
                {
                    if (start2 < ip + ml)
                    {
                        int correction = (int)(ip + ml - start2);
                        start2 += correction;
                        ref2 += correction;
                        ml2 -= correction;
                        if (ml2 < MINMATCH)
                        {
                            start2 = start3;
                            ref2 = ref3;
                            ml2 = ml3;
                        }
                    }

                    optr = op;
                    if (LZ4HC_encodeSequence(&ip, &op, &anchor, ml, @ref, limit, oend) != 0)
                        goto _dest_overflow;

                    ip = start3;
                    @ref = ref3;
                    ml = ml3;

                    start0 = start2;
                    ref0 = ref2;
                    ml0 = ml2;
                    goto _Search2;
                }

                start2 = start3;
                ref2 = ref3;
                ml2 = ml3;
                goto _Search3;
            }

            if (start2 < ip + ml)
            {
                if ((start2 - ip) < OPTIMAL_ML)
                {
                    int correction;
                    if (ml > OPTIMAL_ML) ml = OPTIMAL_ML;
                    if (ip + ml > start2 + ml2 - MINMATCH)
                        ml = (int)(start2 - ip) + ml2 - MINMATCH;
                    correction = ml - (int)(start2 - ip);
                    if (correction > 0)
                    {
                        start2 += correction;
                        ref2 += correction;
                        ml2 -= correction;
                    }
                }
                else
                {
                    ml = (int)(start2 - ip);
                }
            }

            optr = op;
            if (LZ4HC_encodeSequence(&ip, &op, &anchor, ml, @ref, limit, oend) != 0)
                goto _dest_overflow;

            ip = start2;
            @ref = ref2;
            ml = ml2;

            start2 = start3;
            ref2 = ref3;
            ml2 = ml3;

            goto _Search3;
        }

    _last_literals:
        {
            size_t lastRunSize = (size_t)(iend - anchor);
            size_t litLength = (lastRunSize + 255 - RUN_MASK) / 255;
            size_t totalSize = 1 + litLength + lastRunSize;
            if (limit == limitedOutput_directive.fillOutput)
                oend += LASTLITERALS;
            if (limit != 0 && (op + totalSize > oend))
            {
                if (limit == limitedOutput_directive.limitedOutput)
                    return 0;

                lastRunSize = (size_t)(oend - op) - 1;
                litLength = (lastRunSize + 256 - RUN_MASK) / 256;
                lastRunSize -= litLength;
            }

            ip = anchor + lastRunSize;

            if (lastRunSize >= RUN_MASK)
            {
                size_t accumulator = lastRunSize - RUN_MASK;
                *op++ = (byte)(RUN_MASK << ML_BITS);
                for (; accumulator >= 255; accumulator -= 255) *op++ = 255;
                *op++ = (byte)accumulator;
            }
            else
            {
                *op++ = (byte)(lastRunSize << ML_BITS);
            }

            Mem.Copy(op, anchor, (int)lastRunSize);
            op += lastRunSize;
        }

        *srcSizePtr = (int)(((byte*)ip) - source);
        return (int)(((byte*)op) - dest);

    _dest_overflow:
        if (limit == limitedOutput_directive.fillOutput)
        {
            op = optr;
            EncodeOverflowMatch(&ip, &op, &anchor, ml, @ref, oend);
            goto _last_literals;
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4HC_compress_optimal(
        LZ4_streamHC_t* ctx,
        byte* source,
        byte* dst,
        int* srcSizePtr,
        int dstCapacity,
        int nbSearches,
        size_t sufficient_len,
        limitedOutput_directive limit,
        bool fullUpdate,
        dictCtx_directive dict,
        HCfavor_e favorDecSpeed)
    {
        const int TRAILING_LITERALS = 3;
        LZ4HC_optimal_t* opt = stackalloc LZ4HC_optimal_t[LZ4_OPT_NUM + TRAILING_LITERALS];

        byte* ip = (byte*)source;
        byte* anchor = ip;
        byte* iend = ip + *srcSizePtr;
        byte* mflimit = iend - MFLIMIT;
        byte* matchlimit = iend - LASTLITERALS;
        byte* op = (byte*)dst;
        byte* opSaved = (byte*)dst;
        byte* oend = op + dstCapacity;
        int overflowMatchLength = MINMATCH;
        byte* overflowReference = null;

        *srcSizePtr = 0;
        if (limit == limitedOutput_directive.fillOutput)
            oend -= LASTLITERALS;
        if (sufficient_len >= LZ4_OPT_NUM) sufficient_len = LZ4_OPT_NUM - 1;

        Assert(ip - anchor < LZ4_MAX_INPUT_SIZE);
        while (ip <= mflimit)
        {
            int llen = (int)(ip - anchor);
            int best_mlen, best_off;
            int cur, last_match_pos = 0;

            LZ4HC_match_t firstMatch = LZ4HC_FindLongerMatch(
                ctx, ip, matchlimit, MINMATCH - 1, nbSearches, dict, favorDecSpeed);
            if (firstMatch.len == 0)
            {
                ip++;
                continue;
            }

            if ((size_t)firstMatch.len > sufficient_len)
            {
                int firstML = firstMatch.len;
                byte* matchPos = ip - firstMatch.off;
                opSaved = op;
                if (LZ4HC_encodeSequence(&ip, &op, &anchor, firstML, matchPos, limit, oend) != 0
                )
                {
                    overflowMatchLength = firstML;
                    overflowReference = matchPos;
                    goto _dest_overflow;
                }

                continue;
            }

            {
                int rPos;
                for (rPos = 0; rPos < MINMATCH; rPos++)
                {
                    int cost = LZ4HC_literalsPrice(llen + rPos);
                    opt[rPos].mlen = 1;
                    opt[rPos].off = 0;
                    opt[rPos].litlen = llen + rPos;
                    opt[rPos].price = cost;
                }
            }
            {
                int mlen = MINMATCH;
                int matchML = firstMatch.len;
                int offset = firstMatch.off;
                Assert(matchML < LZ4_OPT_NUM);
                for (; mlen <= matchML; mlen++)
                {
                    int cost = LZ4HC_sequencePrice(llen, mlen);
                    opt[mlen].mlen = mlen;
                    opt[mlen].off = offset;
                    opt[mlen].litlen = llen;
                    opt[mlen].price = cost;
                }
            }
            last_match_pos = firstMatch.len;
            {
                int addLit;
                for (addLit = 1; addLit <= TRAILING_LITERALS; addLit++)
                {
                    opt[last_match_pos + addLit].mlen = 1;
                    opt[last_match_pos + addLit].off = 0;
                    opt[last_match_pos + addLit].litlen = addLit;
                    opt[last_match_pos + addLit].price =
                        opt[last_match_pos].price + LZ4HC_literalsPrice(addLit);
                }
            }

            for (cur = 1; cur < last_match_pos; cur++)
            {
                byte* curPtr = ip + cur;
                LZ4HC_match_t newMatch;

                if (curPtr > mflimit) break;

                if (fullUpdate)
                {
                    if ((opt[cur + 1].price <= opt[cur].price)
                        && (opt[cur + MINMATCH].price < opt[cur].price + 3))
                        continue;
                }
                else
                {
                    if (opt[cur + 1].price <= opt[cur].price) continue;
                }

                if (fullUpdate)
                    newMatch = LZ4HC_FindLongerMatch(
                        ctx, curPtr, matchlimit, MINMATCH - 1, nbSearches, dict, favorDecSpeed);
                else
                    newMatch = LZ4HC_FindLongerMatch(
                        ctx, curPtr, matchlimit, last_match_pos - cur, nbSearches, dict,
                        favorDecSpeed);
                if (newMatch.len == 0) continue;

                if (((size_t)newMatch.len > sufficient_len)
                    || (newMatch.len + cur >= LZ4_OPT_NUM))
                {
                    best_mlen = newMatch.len;
                    best_off = newMatch.off;
                    last_match_pos = cur + 1;
                    goto encode;
                }

                {
                    int baseLitlen = opt[cur].litlen;
                    int litlen;
                    for (litlen = 1; litlen < MINMATCH; litlen++)
                    {
                        int price = opt[cur].price - LZ4HC_literalsPrice(baseLitlen)
                            + LZ4HC_literalsPrice(baseLitlen + litlen);
                        int pos = cur + litlen;
                        if (price < opt[pos].price)
                        {
                            opt[pos].mlen = 1;
                            opt[pos].off = 0;
                            opt[pos].litlen = baseLitlen + litlen;
                            opt[pos].price = price;
                        }
                    }
                }

                {
                    int matchML = newMatch.len;
                    int ml = MINMATCH;

                    Assert(cur + newMatch.len < LZ4_OPT_NUM);
                    for (; ml <= matchML; ml++)
                    {
                        int pos = cur + ml;
                        int offset = newMatch.off;
                        int price;
                        int ll;
                        if (opt[cur].mlen == 1)
                        {
                            ll = opt[cur].litlen;
                            price = ((cur > ll) ? opt[cur - ll].price : 0)
                                + LZ4HC_sequencePrice(ll, ml);
                        }
                        else
                        {
                            ll = 0;
                            price = opt[cur].price + LZ4HC_sequencePrice(0, ml);
                        }

                        Assert((uint)favorDecSpeed <= 1);
                        if (pos > last_match_pos + TRAILING_LITERALS
                            || price <= opt[pos].price - (int)favorDecSpeed)
                        {
                            Assert(pos < LZ4_OPT_NUM);
                            if ((ml == matchML)
                                && (last_match_pos < pos))
                                last_match_pos = pos;
                            opt[pos].mlen = ml;
                            opt[pos].off = offset;
                            opt[pos].litlen = ll;
                            opt[pos].price = price;
                        }
                    }
                }
                {
                    int addLit;
                    for (addLit = 1; addLit <= TRAILING_LITERALS; addLit++)
                    {
                        opt[last_match_pos + addLit].mlen = 1;
                        opt[last_match_pos + addLit].off = 0;
                        opt[last_match_pos + addLit].litlen = addLit;
                        opt[last_match_pos + addLit].price = opt[last_match_pos].price
                            + LZ4HC_literalsPrice(addLit);
                    }
                }
            }

            Assert(last_match_pos < LZ4_OPT_NUM + TRAILING_LITERALS);
            best_mlen = opt[last_match_pos].mlen;
            best_off = opt[last_match_pos].off;
            cur = last_match_pos - best_mlen;

        encode:
            Assert(cur < LZ4_OPT_NUM);
            Assert(last_match_pos >= 1);
            {
                int candidate_pos = cur;
                int selected_matchLength = best_mlen;
                int selected_offset = best_off;
                while (true)
                {
                    int next_matchLength =
                        opt[candidate_pos].mlen;
                    int next_offset = opt[candidate_pos].off;
                    opt[candidate_pos].mlen = selected_matchLength;
                    opt[candidate_pos].off = selected_offset;
                    selected_matchLength = next_matchLength;
                    selected_offset = next_offset;
                    if (next_matchLength > candidate_pos)
                        break;

                    Assert(next_matchLength > 0);
                    candidate_pos -= next_matchLength;
                }
            }

            {
                int rPos = 0;
                while (rPos < last_match_pos)
                {
                    int ml = opt[rPos].mlen;
                    int offset = opt[rPos].off;
                    if (ml == 1)
                    {
                        ip++;
                        rPos++;
                        continue;
                    }

                    rPos += ml;
                    Assert(ml >= MINMATCH);
                    Assert((offset >= 1) && (offset <= LZ4_DISTANCE_MAX));
                    opSaved = op;
                    if (LZ4HC_encodeSequence(&ip, &op, &anchor, ml, ip - offset, limit, oend)
                        != 0)
                    {
                        overflowMatchLength = ml;
                        overflowReference = ip - offset;
                        goto _dest_overflow;
                    }
                }
            }
        }

    _last_literals:
        {
            size_t lastRunSize = (size_t)(iend - anchor);
            size_t litLength = (lastRunSize + 255 - RUN_MASK) / 255;
            size_t totalSize = 1 + litLength + lastRunSize;
            if (limit == limitedOutput_directive.fillOutput)
                oend += LASTLITERALS;
            if (limit != 0 && (op + totalSize > oend))
            {
                if (limit == limitedOutput_directive.limitedOutput)
                    return 0;

                lastRunSize = (size_t)(oend - op) - 1;
                litLength = (lastRunSize + 256 - RUN_MASK) / 256;
                lastRunSize -= litLength;
            }

            ip = anchor + lastRunSize;

            if (lastRunSize >= RUN_MASK)
            {
                size_t accumulator = lastRunSize - RUN_MASK;
                *op++ = (byte)(RUN_MASK << ML_BITS);
                for (; accumulator >= 255; accumulator -= 255) *op++ = 255;
                *op++ = (byte)accumulator;
            }
            else
            {
                *op++ = (byte)(lastRunSize << ML_BITS);
            }

            Mem.Copy(op, anchor, (int)lastRunSize);
            op += lastRunSize;
        }

        *srcSizePtr = (int)(((byte*)ip) - source);
        return (int)((byte*)op - dst);

    _dest_overflow:
        if (limit == limitedOutput_directive.fillOutput)
        {
            op = opSaved;
            EncodeOverflowMatch(&ip, &op, &anchor, overflowMatchLength, overflowReference, oend);
            goto _last_literals;
        }

        return 0;
    }

    private static void EncodeOverflowMatch(byte** input, byte** output, byte** anchor,
        int matchLength, byte* reference, byte* outputEnd)
    {
        var literals = (uint)(*input - *anchor);
        var literalCost = 1 + (literals + 240) / 255 + literals;
        var maximumLiteralPosition = outputEnd - 3;
        if (*output + literalCost > maximumLiteralPosition) return;
        var matchBytes = (uint)(maximumLiteralPosition - (*output + literalCost));
        var maximumMatchLength = MINMATCH + ML_MASK - 1 + matchBytes * 255;
        matchLength = (int)Math.Min((uint)matchLength, maximumMatchLength);
        if (outputEnd + LASTLITERALS - (*output + literalCost + 2) - 1 + matchLength >= MFLIMIT)
            LZ4HC_encodeSequence(input, output, anchor, matchLength, reference,
                limitedOutput_directive.notLimited, outputEnd);
    }

    private static readonly cParams_t[] clTable = [
        new cParams_t(lz4hc_strat_e.lz4hc, 2, 16),
        new cParams_t(lz4hc_strat_e.lz4hc, 2, 16),
        new cParams_t(lz4hc_strat_e.lz4hc, 2, 16),
        new cParams_t(lz4hc_strat_e.lz4hc, 4, 16),
        new cParams_t(lz4hc_strat_e.lz4hc, 8, 16),
        new cParams_t(lz4hc_strat_e.lz4hc, 16, 16),
        new cParams_t(lz4hc_strat_e.lz4hc, 32, 16),
        new cParams_t(lz4hc_strat_e.lz4hc, 64, 16),
        new cParams_t(lz4hc_strat_e.lz4hc, 128, 16),
        new cParams_t(lz4hc_strat_e.lz4hc, 256, 16),
        new cParams_t(lz4hc_strat_e.lz4opt, 96, 64),
        new cParams_t(lz4hc_strat_e.lz4opt, 512, 128),
        new cParams_t(lz4hc_strat_e.lz4opt, 16384, LZ4_OPT_NUM),
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4HC_compress_generic_internal(
        LZ4_streamHC_t* ctx,
        byte* src,
        byte* dst,
        int* srcSizePtr,
        int dstCapacity,
        int cLevel,
        limitedOutput_directive limit,
        dictCtx_directive dict)
    {
        if (limit == limitedOutput_directive.fillOutput && dstCapacity < 1)
            return 0;
        if ((uint)*srcSizePtr > (uint)LZ4_MAX_INPUT_SIZE)
            return 0;

        ctx->end += *srcSizePtr;
        if (cLevel < 1)
            cLevel =
                LZ4HC_CLEVEL_DEFAULT;
        cLevel = MIN(LZ4HC_CLEVEL_MAX, cLevel);
        {
            cParams_t cParam = clTable[cLevel];
            HCfavor_e favor = ctx->favorDecSpeed
                ? HCfavor_e.favorDecompressionSpeed
                : HCfavor_e.favorCompressionRatio;
            int result;

            if (cParam.strat == lz4hc_strat_e.lz4hc)
            {
                result = LZ4HC_compress_hashChain(
                    ctx,
                    src, dst, srcSizePtr, dstCapacity,
                    (int)cParam.nbSearches, limit, dict);
            }
            else
            {
                Assert(cParam.strat == lz4hc_strat_e.lz4opt);
                result = LZ4HC_compress_optimal(
                    ctx,
                    src, dst, srcSizePtr, dstCapacity,
                    (int)cParam.nbSearches, cParam.targetLength, limit,
                    cLevel == LZ4HC_CLEVEL_MAX,
                    dict, favor);
            }

            if (result <= 0) ctx->dirty = true;
            return result;
        }
    }

}