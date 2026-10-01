using Kiosk.Core.Devices;
using Kiosk.Core.Services;
using Xunit;

namespace Kiosk.Core.Tests;

public class CashPaymentTests
{
    private static CashPaymentSession Start(Fixture f)
    {
        var (s, err) = CashPaymentSession.Start(f.Repo, f.Payments, f.Acceptor, f.Dispenser, f.Printer, f.Audit,
            Fixture.Kim, f.UnpaidIds(Fixture.Kim), "CASH-1");
        Assert.Null(err);
        return s!;
    }

    private static char LastCommand(Fixture f) => (char)f.Sent[^1][1];

    [Fact]
    public void 금액이_채워지면_수납완료되고_거스름돈을_방출한다()
    {
        var f = new Fixture();
        var s = Start(f);                                   // 28,300원
        f.Acceptor.Feed(BillAcceptorDriver.EscrowFrame(10000));
        f.Acceptor.Feed(BillAcceptorDriver.EscrowFrame(10000));
        f.Acceptor.Feed(BillAcceptorDriver.EscrowFrame(10000));

        Assert.Equal(CashState.Completed, s.State);
        Assert.Equal(30_000, s.Received);
        Assert.Empty(f.Repo.GetUnpaid(Fixture.Kim));
        // 거스름돈 1,700원 = 1,000원 1장 + 500원 1개 + 100원 2개
        Assert.Equal(29, f.Dispenser.Inventory[1000]);
        Assert.Equal(19, f.Dispenser.Inventory[500]);
        Assert.Equal(48, f.Dispenser.Inventory[100]);
    }

    [Fact]
    public void 허용하지_않는_권종은_반환한다()
    {
        var f = new Fixture();
        var s = Start(f);
        f.Acceptor.Feed(BillAcceptorDriver.EscrowFrame(2000));

        Assert.Equal('R', LastCommand(f));
        Assert.Equal(0, s.Received);
    }

    [Fact]
    public void 거스름돈을_못_내주면_지폐를_반환한다()
    {
        var f = new Fixture();
        foreach (var k in f.Dispenser.Inventory.Keys.ToList()) f.Dispenser.Inventory[k] = 0;
        var s = Start(f);
        f.Acceptor.Feed(BillAcceptorDriver.EscrowFrame(50000));

        Assert.Equal('R', LastCommand(f));
        Assert.Equal(0, s.Received);
        Assert.Equal(CashState.Collecting, s.State);
    }

    [Fact]
    public void 중간에_취소하면_넣은_금액을_환불한다()
    {
        var f = new Fixture();
        var s = Start(f);
        f.Acceptor.Feed(BillAcceptorDriver.EscrowFrame(10000));
        int before = f.Dispenser.Inventory[10000];

        s.Cancel();

        Assert.Equal(CashState.Cancelled, s.State);
        Assert.Equal(before - 1, f.Dispenser.Inventory[10000]);
        Assert.Single(f.Repo.GetUnpaid(Fixture.Kim));             // 미수납 그대로
    }

    [Fact]
    public void 환불_방출이_실패하면_직원호출_상태가_된다()
    {
        var f = new Fixture();
        var s = Start(f);
        f.Acceptor.Feed(BillAcceptorDriver.EscrowFrame(10000));
        f.Dispenser.Jammed = true;

        s.Cancel();

        Assert.Equal(CashState.NeedsStaff, s.State);
    }
}
