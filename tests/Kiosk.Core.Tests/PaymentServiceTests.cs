using Kiosk.Core.Data;
using Kiosk.Core.Devices;
using Kiosk.Core.Domain;
using Kiosk.Core.Services;
using Xunit;

namespace Kiosk.Core.Tests;

public class PaymentServiceTests
{
    [Fact]
    public void 카드_승인되면_선택한_수납건이_모두_수납완료된다()
    {
        var f = new Fixture();
        var ids = f.UnpaidIds(Fixture.Hong);

        var r = f.Payments.PayByCard(Fixture.Hong, ids, "K1");

        Assert.Equal(PayOutcome.Approved, r.Outcome);
        Assert.Equal(31_000, r.Payment!.Amount);
        Assert.Empty(f.Repo.GetUnpaid(Fixture.Hong));
        Assert.True(r.ReceiptPrinted);
    }

    [Fact]
    public void 금액은_항목에서_서버가_다시_계산한다()
    {
        var f = new Fixture();
        var (_, total, error) = f.Payments.Prepare(Fixture.Hong, f.UnpaidIds(Fixture.Hong));

        Assert.Null(error);
        Assert.Equal(12_800 + 4_300 + 2_150 * 2 + 9_600, total);
    }

    [Fact]
    public void 다른_환자의_수납건은_결제할_수_없다()
    {
        var f = new Fixture();
        var kimBill = f.UnpaidIds(Fixture.Kim)[0];

        var r = f.Payments.PayByCard(Fixture.Hong, new[] { kimBill }, "K1");

        Assert.Equal(PayOutcome.Invalid, r.Outcome);
        Assert.Empty(f.Card.Approved);
    }

    [Fact]
    public void 같은_키로_두번_요청해도_승인은_한번만_일어난다()
    {
        var f = new Fixture();
        var ids = f.UnpaidIds(Fixture.Hong);

        var first = f.Payments.PayByCard(Fixture.Hong, ids, "SAME");
        var second = f.Payments.PayByCard(Fixture.Hong, ids, "SAME");

        Assert.Equal(PayOutcome.Approved, first.Outcome);
        Assert.Equal(PayOutcome.Approved, second.Outcome);
        Assert.Equal(first.Payment!.Id, second.Payment!.Id);
        Assert.Single(f.Card.Approved);
    }

    [Fact]
    public void 승인_거절이면_미수납이_그대로_남는다()
    {
        var f = new Fixture();
        f.Card.NextResults.Enqueue(CardResultKind.Declined);

        var r = f.Payments.PayByCard(Fixture.Hong, f.UnpaidIds(Fixture.Hong), "K1");

        Assert.Equal(PayOutcome.Declined, r.Outcome);
        Assert.Equal(2, f.Repo.GetUnpaid(Fixture.Hong).Count);
        Assert.Equal(PaymentStatus.Failed, f.Repo.FindPaymentByKey("K1")!.Status);
    }

    [Fact]
    public void 단말기_응답이_없으면_망취소하고_결제를_취소상태로_남긴다()
    {
        var f = new Fixture();
        f.Card.NextResults.Enqueue(CardResultKind.Timeout);

        var r = f.Payments.PayByCard(Fixture.Hong, f.UnpaidIds(Fixture.Hong), "K-TO");

        Assert.Equal(PayOutcome.Cancelled, r.Outcome);
        Assert.Contains("K-TO", f.Card.CancelledKeys);
        Assert.Empty(f.Card.Approved);                       // 단말기 쪽 승인도 남지 않음
        Assert.Equal(2, f.Repo.GetUnpaid(Fixture.Hong).Count);
        Assert.Equal(PaymentStatus.Cancelled, f.Repo.FindPaymentByKey("K-TO")!.Status);
    }

    [Fact]
    public void 승인후_DB저장이_실패하면_승인을_취소한다()
    {
        var f = new Fixture();
        f.Repo.FailNextCompletePayment = true;

        var r = f.Payments.PayByCard(Fixture.Hong, f.UnpaidIds(Fixture.Hong), "K-DB");

        Assert.Equal(PayOutcome.Cancelled, r.Outcome);
        Assert.Contains("K-DB", f.Card.CancelledKeys);
        Assert.Empty(f.Card.Approved);
        Assert.Equal(2, f.Repo.GetUnpaid(Fixture.Hong).Count);
        Assert.Contains(f.Audit.Lines, l => l.Contains("pay.compensated"));
    }

    [Fact]
    public void 창구에서_먼저_수납된_건이_있으면_결제하지_않는다()
    {
        var f = new Fixture();
        var ids = f.UnpaidIds(Fixture.Hong);
        f.Repo.MarkPaidElsewhere(ids[0]);

        var r = f.Payments.PayByCard(Fixture.Hong, ids, "K1");

        Assert.Equal(PayOutcome.Invalid, r.Outcome);
        Assert.Empty(f.Card.Approved);
    }

    [Fact]
    public void 저장_직전에_다른곳에서_수납되면_트랜잭션이_전부_거부한다()
    {
        var f = new Fixture();
        var ids = f.UnpaidIds(Fixture.Hong);
        var p = f.Repo.CreatePendingPayment("K1", Fixture.Hong, PaymentMethod.Card, 31_000, ids);
        f.Repo.MarkPaidElsewhere(ids[1]);

        Assert.Throws<ConcurrencyException>(() => f.Repo.CompletePayment(p.Id, "A1", 0, 0));
        Assert.Equal(ReceivableStatus.Unpaid, f.Repo.GetReceivable(ids[0])!.Status);   // 반쯤 바뀌지 않음
        Assert.Equal(PaymentStatus.Pending, f.Repo.FindPaymentByKey("K1")!.Status);
    }

    [Fact]
    public void 영수증_출력_실패는_결제를_되돌리지_않는다()
    {
        var f = new Fixture();
        f.Printer.PaperOut = true;

        var r = f.Payments.PayByCard(Fixture.Hong, f.UnpaidIds(Fixture.Hong), "K1");

        Assert.Equal(PayOutcome.Approved, r.Outcome);
        Assert.False(r.ReceiptPrinted);
        Assert.Empty(f.Repo.GetUnpaid(Fixture.Hong));
    }
}
