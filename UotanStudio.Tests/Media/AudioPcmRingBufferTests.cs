using System.Buffers.Binary;
using UotanStudio.Media.Audio;
using Xunit;

namespace UotanStudio.Tests.Media;

public class AudioPcmRingBufferTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MixedWritesAndReads_MatchDropOldestQueue(int channels)
    {
        const int capacity = 17;
        var ring = new AudioPcmRingBuffer(channels, capacity);
        var expected = new Queue<float>();
        var random = new Random(71);
        for (int operation = 0; operation < 1000; operation++)
        {
            if (random.Next(3) != 0)
            {
                int frames = random.Next(0, 40);
                float volume = operation % 3 == 0 ? 0.5f : 1f;
                var pcm = new byte[frames * channels * 2];
                for (int i = 0; i < frames * channels; i++)
                {
                    short value = (short)random.Next(short.MinValue, short.MaxValue + 1);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
                    expected.Enqueue(value / 32768f * volume);
                }
                while (expected.Count > capacity * channels) expected.Dequeue();
                ring.Write(pcm, volume);
            }
            else
            {
                var output = new byte[random.Next(0, 25) * channels * 4];
                output.AsSpan().Fill(0xFF);
                ring.Read(output);
                for (int i = 0; i < output.Length / 4; i++)
                    Assert.Equal(expected.Count > 0 ? expected.Dequeue() : 0f,
                        BinaryPrimitives.ReadSingleLittleEndian(output.AsSpan(i * 4)));
            }
        }
    }

    [Fact]
    public void SteadyWritesAndReads_DoNotAllocate()
    {
        var ring = new AudioPcmRingBuffer(2);
        var pcm = new byte[4096];
        var output = new byte[8192];
        for (int i = 0; i < 100; i++) { ring.Write(pcm, 0.5f); ring.Read(output); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { ring.Write(pcm, 0.5f); ring.Read(output); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void MisalignedData_IsRejectedWithoutChangingQueue()
    {
        var ring = new AudioPcmRingBuffer(2);
        Assert.Throws<ArgumentException>(() => ring.Write(new byte[3], 1));
        Assert.Throws<ArgumentException>(() => ring.Read(new byte[7]));
        var output = new byte[8];
        ring.Read(output);
        Assert.All(output, value => Assert.Equal(0, value));
    }
}