using UotanStudio.Media.Decoding.MediaFoundation;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class ExportSlotRingTests
{
    [Fact]
    public void Rent_WhenGateOpenAndAcquireSucceeds_ReturnsSlot()
    {
        using var ring = CreateRing(2, out var slots);
        Assert.Same(slots[0], ring.Rent());
    }

    [Fact]
    public void Rent_WhenAcquireProbeRejects_ReturnsNullAndCountsDrop()
    {
        using var ring = CreateRing(2, out _, acquirable: false);
        Assert.Null(ring.Rent());
        Assert.Equal(1, ring.DroppedFrames);
        Assert.Equal(0, ring.SubmittedFrames);
    }

    [Fact]
    public void Rent_WhenEveryGateClosed_ReturnsNullAndCountsDrop()
    {
        using var ring = CreateRing(1, out var slots);
        var slot = Assert.IsAssignableFrom<Slot>(ring.Rent());
        ring.CloseGate(slot);
        Assert.Null(ring.Rent());
        Assert.Equal(1, ring.DroppedFrames);
        Assert.Equal(1, ring.SubmittedFrames);
    }

    [Fact]
    public void MarkPresented_AfterHandoff_MakesSlotRentableAgain()
    {
        using var ring = CreateRing(1, out var slots);
        var slot = Assert.IsAssignableFrom<Slot>(ring.Rent());
        ring.CloseGate(slot);
        Assert.Null(ring.Rent());
        ring.MarkPresented(slot);
        Assert.Same(slot, ring.Rent());
        Assert.Equal(1, ring.DroppedFrames);
    }

    [Fact]
    public void Rent_AfterFailedHandoffGateStaysOpen_RentsAgainWithoutCountingDrop()
    {
        using var ring = CreateRing(1, out var slots);
        var slot = Assert.IsAssignableFrom<Slot>(ring.Rent());
        Assert.Same(slot, ring.Rent());
        Assert.Equal(0, ring.DroppedFrames);
        Assert.Equal(0, ring.SubmittedFrames);
    }

    [Fact]
    public void MarkPresented_AfterDispose_IsNoopAndRentReturnsNull()
    {
        var ring = CreateRing(1, out var slots);
        var slot = Assert.IsAssignableFrom<Slot>(ring.Rent());
        ring.CloseGate(slot);
        ring.Dispose();
        ring.MarkPresented(slot);
        Assert.Null(ring.Rent());
        Assert.True(slots[0].Destroyed);
    }

    [Fact]
    public void Constructor_WhenCreateSlotThrows_DestroysCreatedSlots()
    {
        var destroyed = new List<Slot>();
        var slots = new[] { new Slot(), new Slot() };
        Assert.Throws<InvalidOperationException>(() => _ = new ExportSlotRing<Slot>(2,
            index => index == 0 ? slots[0] : throw new InvalidOperationException(),
            slot => destroyed.Add(slot),
            _ => true));
        Assert.Equal([slots[0]], destroyed);
    }

    private static ExportSlotRing<Slot> CreateRing(int capacity, out Slot[] slots, bool acquirable = true)
    {
        var created = Enumerable.Range(0, capacity).Select(_ => new Slot { Acquirable = acquirable }).ToArray();
        slots = created;
        return new ExportSlotRing<Slot>(capacity, index => created[index], slot => slot.Destroyed = true, slot => slot.Acquirable);
    }

    private sealed class Slot
    {
        internal bool Acquirable { get; init; } = true;
        internal bool Destroyed { get; set; }
    }
}