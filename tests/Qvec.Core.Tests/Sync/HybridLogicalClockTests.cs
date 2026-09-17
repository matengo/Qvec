using Qvec.Core.Sync;

namespace Qvec.Core.Tests.Sync;

public class HybridLogicalClockTests
{
    [Fact]
    public void Pack_RoundTripsPhysicalAndCounter()
    {
        long hlc = HybridLogicalClock.Pack(1_700_000_000_123, 42);

        Assert.Equal(1_700_000_000_123, HybridLogicalClock.PhysicalMs(hlc));
        Assert.Equal(42, HybridLogicalClock.Counter(hlc));
    }

    [Fact]
    public void Pack_OrdersByPhysicalThenCounter()
    {
        long a = HybridLogicalClock.Pack(1000, HybridLogicalClock.MaxCounter);
        long b = HybridLogicalClock.Pack(1001, 0);
        long c = HybridLogicalClock.Pack(1001, 1);

        Assert.True(a < b);
        Assert.True(b < c);
    }

    [Fact]
    public void Next_WhenWallClockAdvanced_UsesWallClockAndResetsCounter()
    {
        long last = HybridLogicalClock.Pack(1000, 5);

        long next = HybridLogicalClock.Next(last, nowMs: 2000);

        Assert.Equal(2000, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(0, HybridLogicalClock.Counter(next));
    }

    [Fact]
    public void Next_WhenWallClockUnchanged_IncrementsCounter()
    {
        long last = HybridLogicalClock.Pack(2000, 0);

        long next = HybridLogicalClock.Next(last, nowMs: 2000);

        Assert.Equal(2000, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(1, HybridLogicalClock.Counter(next));
    }

    [Fact]
    public void Next_WhenWallClockWentBackwards_KeepsLastPhysicalAndIncrements()
    {
        long last = HybridLogicalClock.Pack(2000, 3);

        long next = HybridLogicalClock.Next(last, nowMs: 1500);

        Assert.Equal(2000, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(4, HybridLogicalClock.Counter(next));
        Assert.True(next > last);
    }

    [Fact]
    public void Next_WhenCounterWouldOverflow_BumpsPhysicalByOneMs()
    {
        long last = HybridLogicalClock.Pack(2000, HybridLogicalClock.MaxCounter);

        long next = HybridLogicalClock.Next(last, nowMs: 2000);

        Assert.Equal(2001, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(0, HybridLogicalClock.Counter(next));
    }

    [Fact]
    public void Next_FromZero_UsesWallClock()
    {
        long next = HybridLogicalClock.Next(0, nowMs: 1234);

        Assert.Equal(1234, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(0, HybridLogicalClock.Counter(next));
    }

    [Fact]
    public void Next_WithRealClock_IsStrictlyGreaterThanLast()
    {
        long last = HybridLogicalClock.Next(0);
        long next = HybridLogicalClock.Next(last);

        Assert.True(next > last);
        Assert.True(HybridLogicalClock.PhysicalMs(next) > 1_600_000_000_000, "physical part should be a plausible Unix ms timestamp");
    }

    [Fact]
    public void Receive_WhenRemoteIsAhead_AdoptsRemotePhysicalAndIncrementsItsCounter()
    {
        long last = HybridLogicalClock.Pack(1000, 0);
        long remote = HybridLogicalClock.Pack(3000, 7);

        long next = HybridLogicalClock.Receive(last, remote, nowMs: 2000);

        Assert.Equal(3000, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(8, HybridLogicalClock.Counter(next));
        Assert.True(next > remote);
    }

    [Fact]
    public void Receive_WhenLocalIsAhead_IncrementsLocalCounter()
    {
        long last = HybridLogicalClock.Pack(3000, 2);
        long remote = HybridLogicalClock.Pack(1000, 9);

        long next = HybridLogicalClock.Receive(last, remote, nowMs: 2000);

        Assert.Equal(3000, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(3, HybridLogicalClock.Counter(next));
    }

    [Fact]
    public void Receive_WhenAllPhysicalEqual_UsesMaxCounterPlusOne()
    {
        long last = HybridLogicalClock.Pack(2000, 2);
        long remote = HybridLogicalClock.Pack(2000, 9);

        long next = HybridLogicalClock.Receive(last, remote, nowMs: 2000);

        Assert.Equal(2000, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(10, HybridLogicalClock.Counter(next));
    }

    [Fact]
    public void Receive_WhenWallClockIsAhead_UsesWallClockAndResetsCounter()
    {
        long last = HybridLogicalClock.Pack(1000, 2);
        long remote = HybridLogicalClock.Pack(1500, 9);

        long next = HybridLogicalClock.Receive(last, remote, nowMs: 5000);

        Assert.Equal(5000, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(0, HybridLogicalClock.Counter(next));
    }

    [Fact]
    public void Receive_WhenCounterWouldOverflow_BumpsPhysicalByOneMs()
    {
        long last = HybridLogicalClock.Pack(2000, HybridLogicalClock.MaxCounter);
        long remote = HybridLogicalClock.Pack(2000, 1);

        long next = HybridLogicalClock.Receive(last, remote, nowMs: 2000);

        Assert.Equal(2001, HybridLogicalClock.PhysicalMs(next));
        Assert.Equal(0, HybridLogicalClock.Counter(next));
    }

    [Fact]
    public void Next_IsStrictlyMonotone_OverManyTicksWithFrozenClock()
    {
        long hlc = 0;
        long previous = -1;
        for (int i = 0; i < 70_000; i++)
        {
            hlc = HybridLogicalClock.Next(hlc, nowMs: 1000);
            Assert.True(hlc > previous);
            previous = hlc;
        }

        // 70 000 ticks in one frozen millisecond must have spilled into the next millisecond.
        Assert.Equal(1001, HybridLogicalClock.PhysicalMs(hlc));
    }
}
