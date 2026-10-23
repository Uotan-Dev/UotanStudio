using System.Runtime.CompilerServices;
using size_t = System.UInt32;

namespace UotanStudio.Compress.Codecs.Lz4;

public static unsafe partial class Lz4DestinationCodec
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LZ4_compress_generic(
        LZ4_stream_t* cctx,
        byte* source,
        byte* dest,
        int inputSize,
        int* inputConsumed,
        int maxOutputSize,
        limitedOutput_directive outputDirective,
        tableType_t tableType,
        dict_directive dictDirective,
        dictIssue_directive dictIssue,
        int acceleration)
    {
        int result;
        var ip = (byte*)source;

        var startIndex = cctx->currentOffset;
        var @base = (byte*)source - startIndex;
        byte* lowLimit;

        LZ4_stream_t* dictCtx = (LZ4_stream_t*)cctx->dictCtx;
        var dictionary =
            dictDirective == dict_directive.usingDictCtx ? dictCtx->dictionary
                : cctx->dictionary;
        var dictSize =
            dictDirective == dict_directive.usingDictCtx ? dictCtx->dictSize : cctx->dictSize;
        var dictDelta = (dictDirective == dict_directive.usingDictCtx)
            ? startIndex - dictCtx->currentOffset : 0;

        var maybe_extMem = (dictDirective == dict_directive.usingExtDict)
            || (dictDirective == dict_directive.usingDictCtx);
        var prefixIdxLimit = startIndex - dictSize;
        var dictEnd = dictionary + dictSize;
        var anchor = (byte*)source;
        var iend = ip + inputSize;
        var mflimitPlusOne = iend - MFLIMIT + 1;
        var matchlimit = iend - LASTLITERALS;

        var dictBase = (dictDirective == dict_directive.usingDictCtx) ?
            dictionary + dictSize - dictCtx->currentOffset :
            dictionary + dictSize - startIndex;

        var op = (byte*)dest;
        var olimit = op + maxOutputSize;

        uint offset = 0;
        uint forwardH;

        if (outputDirective == limitedOutput_directive.fillOutput && maxOutputSize < 1)
        {
            return 0;
        }

        if ((uint)inputSize > (uint)LZ4_MAX_INPUT_SIZE) { return 0; }

        if ((tableType == tableType_t.byU16) && (inputSize >= LZ4_64Klimit)) { return 0; }

        if (tableType == tableType_t.byPtr)
            Assert(dictDirective == dict_directive.noDict);
        Assert(acceleration >= 1);

        lowLimit = (byte*)source
            - (dictDirective == dict_directive.withPrefix64k ? dictSize : 0);

        if (dictDirective == dict_directive.usingDictCtx)
        {
            cctx->dictCtx = null;
            cctx->dictSize = (uint)inputSize;
        }
        else
        {
            cctx->dictSize += (uint)inputSize;
        }

        cctx->currentOffset += (uint)inputSize;
        cctx->tableType = tableType;

        if (inputSize < LZ4_minLength) goto _last_literals;

        LZ4_putPosition(ip, cctx->hashTable, tableType, @base);
        ip++;
        forwardH = LZ4_hashPosition(ip, tableType);

        for (; ; )
        {
            byte* match;
            byte* token;
            byte* filledIp;

            if (tableType == tableType_t.byPtr)
            {
                var forwardIp = ip;
                var step = 1;
                var searchMatchNb = acceleration << LZ4_skipTrigger;
                do
                {
                    var h = forwardH;
                    ip = forwardIp;
                    forwardIp += step;
                    step = (searchMatchNb++ >> LZ4_skipTrigger);

                    if ((forwardIp > mflimitPlusOne)) goto _last_literals;

                    Assert(ip < mflimitPlusOne);

                    match = LZ4_getPositionOnHash(h, cctx->hashTable, tableType, @base);
                    forwardH = LZ4_hashPosition(forwardIp, tableType);
                    LZ4_putPositionOnHash(ip, h, cctx->hashTable, tableType, @base);
                }
                while ((match + LZ4_DISTANCE_MAX < ip) || (Mem.Peek4(match) != Mem.Peek4(ip)));
            }
            else
            {

                var forwardIp = ip;
                var step = 1;
                var searchMatchNb = acceleration << LZ4_skipTrigger;
                do
                {
                    var h = forwardH;
                    var current = (uint)(forwardIp - @base);
                    var matchIndex = LZ4_getIndexOnHash(h, cctx->hashTable, tableType);
                    Assert(matchIndex <= current);
                    Assert(forwardIp - @base < (2 * GB - 1));
                    ip = forwardIp;
                    forwardIp += step;
                    step = (searchMatchNb++ >> LZ4_skipTrigger);

                    if ((forwardIp > mflimitPlusOne)) goto _last_literals;

                    Assert(ip < mflimitPlusOne);

                    if (dictDirective == dict_directive.usingDictCtx)
                    {
                        if (matchIndex < startIndex)
                        {
                            Assert(tableType == tableType_t.byU32);
                            matchIndex = LZ4_getIndexOnHash(
                                h, dictCtx->hashTable, tableType_t.byU32);
                            match = dictBase + matchIndex;
                            matchIndex += dictDelta;
                            lowLimit = dictionary;
                        }
                        else
                        {
                            match = @base + matchIndex;
                            lowLimit = (byte*)source;
                        }
                    }
                    else if (dictDirective == dict_directive.usingExtDict)
                    {
                        if (matchIndex < startIndex)
                        {
                            Assert(startIndex - matchIndex >= MINMATCH);
                            match = dictBase + matchIndex;
                            lowLimit = dictionary;
                        }
                        else
                        {
                            match = @base + matchIndex;
                            lowLimit = (byte*)source;
                        }
                    }
                    else
                    {
                        match = @base + matchIndex;
                    }

                    forwardH = LZ4_hashPosition(forwardIp, tableType);
                    LZ4_putIndexOnHash(current, h, cctx->hashTable, tableType);

                    if ((dictIssue == dictIssue_directive.dictSmall)
                        && (matchIndex < prefixIdxLimit)) { continue; }

                    Assert(matchIndex < current);
                    if (((tableType != tableType_t.byU16)
                            || (LZ4_DISTANCE_MAX < LZ4_DISTANCE_ABSOLUTE_MAX))
                        && (matchIndex + LZ4_DISTANCE_MAX < current))
                    {
                        continue;
                    }

                    Assert((current - matchIndex) <= LZ4_DISTANCE_MAX);

                    if (Mem.Peek4(match) == Mem.Peek4(ip))
                    {
                        if (maybe_extMem) offset = current - matchIndex;
                        break;
                    }
                }
                while (true);
            }

            filledIp = ip;
            while (((ip > anchor) & (match > lowLimit)) && ((ip[-1] == match[-1])))
            {
                ip--;
                match--;
            }

            {
                var litLength = (uint)(ip - anchor);
                token = op++;
                if ((outputDirective == limitedOutput_directive.limitedOutput) &&
                    ((op + litLength + (2 + 1 + LASTLITERALS) + (litLength / 255) > olimit)))
                {
                    return 0;
                }

                if ((outputDirective == limitedOutput_directive.fillOutput) &&
                    ((op + (litLength + 240) / 255 + litLength + 2 + 1 + MFLIMIT - MINMATCH
                        > olimit)))
                {
                    op--;
                    goto _last_literals;
                }

                if (litLength >= RUN_MASK)
                {
                    var len = (int)(litLength - RUN_MASK);
                    *token = (byte)(RUN_MASK << ML_BITS);
                    for (; len >= 255; len -= 255) *op++ = 255;
                    *op++ = (byte)len;
                }
                else *token = (byte)(litLength << ML_BITS);

                Mem.WildCopy8(op, anchor, op + litLength);
                op += litLength;
            }

        _next_match:

            if ((outputDirective == limitedOutput_directive.fillOutput) &&
                (op + 2 + 1 + MFLIMIT - MINMATCH > olimit))
            {
                op = token;
                goto _last_literals;
            }

            if (maybe_extMem)
            {
                Assert(offset <= LZ4_DISTANCE_MAX && offset > 0);
                Mem.Poke2(op, (ushort)offset);
                op += 2;
            }
            else
            {
                Assert(ip - match <= LZ4_DISTANCE_MAX);
                Mem.Poke2(op, (ushort)(ip - match));
                op += 2;
            }

            {
                uint matchCode;

                if ((dictDirective == dict_directive.usingExtDict
                        || dictDirective == dict_directive.usingDictCtx)
                    && (lowLimit == dictionary))
                {
                    var limit = ip + (dictEnd - match);
                    Assert(dictEnd > match);
                    if (limit > matchlimit) limit = matchlimit;
                    matchCode = LZ4_count(ip + MINMATCH, match + MINMATCH, limit);
                    ip += (uint)matchCode + MINMATCH;
                    if (ip == limit)
                    {
                        var more = LZ4_count(limit, (byte*)source, matchlimit);
                        matchCode += more;
                        ip += more;
                    }
                }
                else
                {
                    matchCode = LZ4_count(ip + MINMATCH, match + MINMATCH, matchlimit);
                    ip += (uint)matchCode + MINMATCH;
                }

                if ((outputDirective != 0)
                    && ((op + (1 + LASTLITERALS) + (matchCode + 240) / 255 > olimit)))
                {
                    if (outputDirective == limitedOutput_directive.fillOutput)
                    {
                        var newMatchCode =
                            15 - 1 + ((uint)(olimit - op) - 1 - LASTLITERALS) * 255;
                        ip -= matchCode - newMatchCode;
                        Assert(newMatchCode < matchCode);
                        matchCode = newMatchCode;
                        if ((ip <= filledIp))
                        {
                            byte* ptr;
                            for (ptr = ip; ptr <= filledIp; ++ptr)
                            {
                                var h = LZ4_hashPosition(ptr, tableType);
                                LZ4_clearHash(h, cctx->hashTable, tableType);
                            }
                        }
                    }
                    else
                    {
                        Assert(outputDirective == limitedOutput_directive.limitedOutput);
                        return 0;
                    }
                }

                if (matchCode >= ML_MASK)
                {
                    *token += (byte)ML_MASK;
                    matchCode -= ML_MASK;
                    Mem.Poke4(op, 0xFFFFFFFF);
                    while (matchCode >= 4 * 255)
                    {
                        op += 4;
                        Mem.Poke4(op, 0xFFFFFFFF);
                        matchCode -= 4 * 255;
                    }

                    op += matchCode / 255;
                    *op++ = (byte)(matchCode % 255);
                }
                else
                    *token += (byte)(matchCode);
            }
            Assert(
                !(outputDirective == limitedOutput_directive.fillOutput
                    && op + 1 + LASTLITERALS > olimit));

            anchor = ip;

            if (ip >= mflimitPlusOne) break;

            LZ4_putPosition(ip - 2, cctx->hashTable, tableType, @base);

            if (tableType == tableType_t.byPtr)
            {
                match = LZ4_getPosition(ip, cctx->hashTable, tableType, @base);
                LZ4_putPosition(ip, cctx->hashTable, tableType, @base);
                if ((match + LZ4_DISTANCE_MAX >= ip) && (Mem.Peek4(match) == Mem.Peek4(ip)))
                {
                    token = op++;
                    *token = 0;
                    goto _next_match;
                }
            }
            else
            {

                var h = LZ4_hashPosition(ip, tableType);
                var current = (uint)(ip - @base);
                var matchIndex = LZ4_getIndexOnHash(h, cctx->hashTable, tableType);
                Assert(matchIndex < current);
                if (dictDirective == dict_directive.usingDictCtx)
                {
                    if (matchIndex < startIndex)
                    {
                        matchIndex = LZ4_getIndexOnHash(
                            h, dictCtx->hashTable, tableType_t.byU32);
                        match = dictBase + matchIndex;
                        lowLimit = dictionary;
                        matchIndex += dictDelta;
                    }
                    else
                    {
                        match = @base + matchIndex;
                        lowLimit = (byte*)source;
                    }
                }
                else if (dictDirective == dict_directive.usingExtDict)
                {
                    if (matchIndex < startIndex)
                    {
                        match = dictBase + matchIndex;
                        lowLimit = dictionary;
                    }
                    else
                    {
                        match = @base + matchIndex;
                        lowLimit = (byte*)source;
                    }
                }
                else
                {
                    match = @base + matchIndex;
                }

                LZ4_putIndexOnHash(current, h, cctx->hashTable, tableType);
                Assert(matchIndex < current);
                if (((dictIssue != dictIssue_directive.dictSmall)
                        || (matchIndex >= prefixIdxLimit))
                    && (((tableType == tableType_t.byU16)
                            && (LZ4_DISTANCE_MAX == LZ4_DISTANCE_ABSOLUTE_MAX))
                        || (matchIndex + LZ4_DISTANCE_MAX >= current))
                    && (Mem.Peek4(match) == Mem.Peek4(ip)))
                {
                    token = op++;
                    *token = 0;
                    if (maybe_extMem) offset = current - matchIndex;
                    goto _next_match;
                }
            }

            forwardH = LZ4_hashPosition(++ip, tableType);
        }

    _last_literals:
        {
            var lastRun = (size_t)(iend - anchor);
            if ((outputDirective != 0) &&
                (op + lastRun + 1 + ((lastRun + 255 - RUN_MASK) / 255) > olimit))
            {
                if (outputDirective == limitedOutput_directive.fillOutput)
                {
                    Assert(olimit >= op);
                    lastRun = (size_t)(olimit - op) - 1;
                    lastRun -= (lastRun + 256 - RUN_MASK) / 256;
                }
                else
                {
                    Assert(outputDirective == limitedOutput_directive.limitedOutput);
                    return 0;
                }
            }

            if (lastRun >= RUN_MASK)
            {
                var accumulator = (size_t)(lastRun - RUN_MASK);
                *op++ = (byte)(RUN_MASK << ML_BITS);
                for (; accumulator >= 255; accumulator -= 255) *op++ = 255;
                *op++ = (byte)accumulator;
            }
            else
            {
                *op++ = (byte)(lastRun << ML_BITS);
            }

            Mem.Copy(op, anchor, (int)lastRun);
            ip = anchor + lastRun;
            op += lastRun;
        }

        if (outputDirective == limitedOutput_directive.fillOutput)
        {
            *inputConsumed = (int)(((byte*)ip) - source);
        }

        result = (int)(((byte*)op) - dest);
        Assert(result > 0);
        return result;
    }

}