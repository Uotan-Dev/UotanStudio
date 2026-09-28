using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class SmaliAssembler
{
    private sealed record CatchClause(int Start, int End, int TypeIndex, int Target);

    private sealed partial class Context
    {
        private void EncodeCatches(MethodState state, Dictionary<string, int> labels, SortedSet<int> boundaries, HashSet<int> instructions)
        {
            var code = state.Code;
            code.Tries.Clear();
            code.Handlers.Clear();
            if (state.Catches.Count == 0)
            {
                return;
            }
            var clauses = new List<CatchClause>();
            var events = new SortedDictionary<int, List<(int Index, bool Start)>>();
            foreach (var line in state.Catches)
            {
                var all = line.StartsWith(".catchall ", StringComparison.Ordinal);
                var text = Token(line, all ? ".catchall" : ".catch");
                var open = text.IndexOf('{');
                var close = text.IndexOf('}');
                if (open < 0 || close <= open)
                {
                    throw new FormatException("A catch directive requires a braced label range.");
                }
                var type = -1;
                var descriptor = text[..open].Trim();
                if (all)
                {
                    if (descriptor.Length != 0)
                    {
                        throw new FormatException("A catch-all cannot specify a type.");
                    }
                }
                else
                {
                    descriptor = SmaliTypes.Single(descriptor, allowVoid: false);
                    if (!descriptor.StartsWith('L'))
                    {
                        throw new FormatException("A catch type must be a class descriptor.");
                    }
                    type = Type(descriptor);
                }
                var range = text[(open + 1)..close].Split("..", StringSplitOptions.TrimEntries);
                if (range.Length != 2)
                {
                    throw new FormatException("A catch range requires start and end labels.");
                }
                var start = ResolveLabel(range[0]);
                var end = ResolveLabel(range[1]);
                var target = ResolveLabel(text[(close + 1)..].Trim());
                if (start >= end || target >= code.InsnsSize || !instructions.Contains(target))
                {
                    throw new FormatException("A catch range must be nonempty and its handler must address an instruction.");
                }
                var index = clauses.Count;
                clauses.Add(new CatchClause(start, end, type, target));
                AddEvent(start, index, true);
                AddEvent(end, index, false);
            }

            var active = new SortedSet<int>();
            var previous = 0;
            foreach (var (position, changes) in events)
            {
                if (previous < position && active.Count != 0)
                {
                    var handler = new CatchHandler();
                    var targets = new Dictionary<int, int>();
                    foreach (var index in active)
                    {
                        var clause = clauses[index];
                        if (targets.TryGetValue(clause.TypeIndex, out var existing))
                        {
                            if (clause.TypeIndex < 0 && existing != clause.Target)
                            {
                                throw new FormatException("Overlapping catch-all clauses assign different handlers.");
                            }
                            continue;
                        }
                        targets.Add(clause.TypeIndex, clause.Target);
                        if (clause.TypeIndex < 0)
                        {
                            handler.CatchAllAddress = clause.Target;
                        }
                        else
                        {
                            handler.Entries.Add(new CatchHandlerEntry { TypeIndex = clause.TypeIndex, Address = clause.Target });
                        }
                    }
                    var handlerIndex = code.Handlers.FindIndex(candidate => SameHandler(candidate, handler));
                    if (handlerIndex < 0)
                    {
                        handlerIndex = code.Handlers.Count;
                        code.Handlers.Add(handler);
                    }
                    AppendRange(previous, position, handlerIndex);
                }
                foreach (var (index, start) in changes)
                {
                    if (start)
                    {
                        active.Add(index);
                    }
                    else
                    {
                        active.Remove(index);
                    }
                }
                previous = position;
            }
            ValidateHandlerOffsets(code);

            int ResolveLabel(string label)
            {
                if (!label.StartsWith(':') || !labels.TryGetValue(label[1..], out var address)
                    || !boundaries.Contains(address))
                {
                    throw new FormatException($"Undefined or invalid catch label '{label}'.");
                }
                return address;
            }

            void AddEvent(int position, int index, bool start)
            {
                if (!events.TryGetValue(position, out var changes))
                {
                    events.Add(position, changes = []);
                }
                changes.Add((index, start));
            }

            void AppendRange(int start, int end, int handler)
            {
                var last = code.Tries.LastOrDefault();
                if (last is not null && last.EndAddress == start && last.HandlerIndex == handler
                    && (long)end - last.StartAddress <= ushort.MaxValue)
                {
                    last.InsnCount = (ushort)(end - last.StartAddress);
                    return;
                }
                while (start < end)
                {
                    var limit = (int)Math.Min(end, (long)start + ushort.MaxValue);
                    var next = boundaries.GetViewBetween(start + 1, limit).Max;
                    if (next <= start || code.Tries.Count == ushort.MaxValue)
                    {
                        throw new FormatException("The exception table exceeds the DEX try-item limits.");
                    }
                    code.Tries.Add(new TryBlock { StartAddress = start, InsnCount = (ushort)(next - start), HandlerIndex = handler });
                    start = next;
                }
            }
        }

        private static bool SameHandler(CatchHandler left, CatchHandler right)
            => left.CatchAllAddress == right.CatchAllAddress
               && left.Entries.Select(entry => (entry.TypeIndex, entry.Address))
                   .SequenceEqual(right.Entries.Select(entry => (entry.TypeIndex, entry.Address)));

        private static void ValidateHandlerOffsets(CodeItem code)
        {
            var offset = Leb128.Uleb128Size((uint)code.Handlers.Count);
            foreach (var handler in code.Handlers)
            {
                if (offset > ushort.MaxValue)
                {
                    throw new FormatException("A catch-handler offset exceeds the DEX 16-bit limit.");
                }
                var size = handler.HasCatchAll ? -handler.Entries.Count : handler.Entries.Count;
                offset += Leb128.Sleb128Size(size);
                foreach (var entry in handler.Entries)
                {
                    offset = checked(offset + Leb128.Uleb128Size((uint)entry.TypeIndex) + Leb128.Uleb128Size((uint)entry.Address));
                }
                if (handler.HasCatchAll)
                {
                    offset = checked(offset + Leb128.Uleb128Size((uint)handler.CatchAllAddress));
                }
            }
        }

    }
}