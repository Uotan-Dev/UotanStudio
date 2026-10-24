using UotanStudio.Comm.Transports.Usb.Core;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class FifoGateTests
{
    [Fact]
    public void Enter_SecondEntrantWhileGateHeld_StaysQueuedUntilRelease()
    {
        var gate = new FifoGate();
        var completed = false;
        using var held = gate.Enter();
        var second = new Thread(() =>
        {
            using (gate.Enter())
            {
                completed = true;
            }
        });

        second.Start();
        Assert.True(SpinWait.SpinUntil(() => gate.PendingCount >= 2, TimeSpan.FromSeconds(5)));
        Assert.False(completed);

        held.Dispose();
        Assert.True(second.Join(TimeSpan.FromSeconds(5)));
        Assert.True(completed);
    }

    [Fact]
    public void Enter_ThreeQueuedEntrants_RunInArrivalOrder()
    {
        var gate = new FifoGate();
        List<int> order = [];
        using var held = gate.Enter();
        var threads = new List<Thread>();
        for (var index = 1; index <= 2; index++)
        {
            var id = index;
            var thread = new Thread(() =>
            {
                using (gate.Enter())
                {
                    lock (order)
                    {
                        order.Add(id);
                    }
                }
            });
            thread.Start();
            Assert.True(SpinWait.SpinUntil(() => gate.PendingCount >= id + 1, TimeSpan.FromSeconds(5)));
            threads.Add(thread);
        }

        held.Dispose();
        Assert.All(threads, thread => Assert.True(thread.Join(TimeSpan.FromSeconds(5))));
        Assert.Equal([1, 2], order);
    }
}