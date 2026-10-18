using DofusCompanion.Application.Switching;

namespace DofusCompanion.Core.Tests;

public sealed class WindowCycleTests
{
    [Fact]
    public void Cycles_forward_and_back_in_first_seen_order()
    {
        var cycle = new WindowCycle();
        cycle.Update([10, 20, 30, 40]);

        Assert.Equal(20, cycle.Next(10));
        Assert.Equal(10, cycle.Next(40));
        Assert.Equal(40, cycle.Next(10, -1));
        Assert.Equal(20, cycle.Next(30, -1));
    }

    [Fact]
    public void Keeps_order_when_windows_close_and_open()
    {
        var cycle = new WindowCycle();
        cycle.Update([10, 20, 30]);
        cycle.Update([30, 50, 10]); // 20 closed, 50 opened, enumeration order changed

        Assert.Equal([10, 30, 50], cycle.Order);
    }

    [Fact]
    public void Unknown_window_goes_to_first_and_single_window_goes_nowhere()
    {
        var cycle = new WindowCycle();
        cycle.Update([10, 20]);
        Assert.Equal(10, cycle.Next(99));
        Assert.Equal(20, cycle.Next(99, -1));

        cycle.Update([10]);
        Assert.Equal(0, cycle.Next(10));
    }
}
