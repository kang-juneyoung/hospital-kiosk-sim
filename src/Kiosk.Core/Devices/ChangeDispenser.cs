namespace Kiosk.Core.Devices;

/// <summary>거스름돈·환불 방출기.</summary>
public interface IChangeDispenser
{
    bool CanDispense(long amount);
    bool Dispense(long amount);
}

/// <summary>권종별 보유 수량을 가진 방출기 시뮬레이터. 큰 권종부터 내준다.</summary>
public sealed class SimulatedChangeDispenser : IChangeDispenser
{
    private static readonly long[] Units = { 10000, 5000, 1000, 500, 100 };
    public Dictionary<long, int> Inventory { get; } = new() { [10000] = 5, [5000] = 10, [1000] = 30, [500] = 20, [100] = 50 };
    public bool Jammed { get; set; }

    public bool CanDispense(long amount) => Plan(amount) is not null;

    public bool Dispense(long amount)
    {
        if (Jammed) return false;
        var plan = Plan(amount);
        if (plan is null) return false;
        foreach (var (unit, count) in plan) Inventory[unit] -= count;
        return true;
    }

    private List<(long Unit, int Count)>? Plan(long amount)
    {
        var result = new List<(long, int)>();
        long left = amount;
        foreach (var u in Units)
        {
            int n = (int)Math.Min(left / u, Inventory.GetValueOrDefault(u));
            if (n > 0) { result.Add((u, n)); left -= u * n; }
        }
        return left == 0 ? result : null;
    }
}
