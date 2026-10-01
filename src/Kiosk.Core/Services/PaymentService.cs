using Kiosk.Core.Data;
using Kiosk.Core.Devices;
using Kiosk.Core.Domain;

namespace Kiosk.Core.Services;

public enum PayOutcome { Approved, Declined, Cancelled, AlreadyPaid, Invalid }

public sealed record PayResult(PayOutcome Outcome, string Message, Payment? Payment = null, bool ReceiptPrinted = false);

/// <summary>
/// 카드 수납. 순서가 핵심이다.
///  1) 대상 수납 건을 다시 읽고 금액을 서버에서 다시 계산 (화면 금액을 믿지 않음)
///  2) 같은 키의 결제가 있으면 그 결과를 돌려줌 (버튼 두 번 눌림·재시도 → 이중 결제 방지)
///  3) Pending 결제를 먼저 저장 → 4) 단말기 승인 → 5) 한 트랜잭션으로 수납 완료
///  승인 응답이 없거나(타임아웃) 승인 후 DB 저장이 실패하면 단말기에 망취소를 보내 돈과 장부를 맞춘다.
/// </summary>
public sealed class PaymentService
{
    private readonly IKioskRepository _repo;
    private readonly ICardTerminal _terminal;
    private readonly IReceiptPrinter _printer;
    private readonly AuditLog _audit;

    public PaymentService(IKioskRepository repo, ICardTerminal terminal, IReceiptPrinter printer, AuditLog audit)
    {
        _repo = repo;
        _terminal = terminal;
        _printer = printer;
        _audit = audit;
    }

    /// <summary>선택한 수납 건 합계. 화면 표시와 결제 모두 이 값만 쓴다.</summary>
    public (IReadOnlyList<Receivable> Targets, long Total, string? Error) Prepare(string patientNo, IReadOnlyCollection<long> receivableIds)
    {
        if (receivableIds.Count == 0) return (Array.Empty<Receivable>(), 0, "수납할 항목을 선택하세요.");
        var targets = new List<Receivable>();
        foreach (var id in receivableIds.Distinct())
        {
            var r = _repo.GetReceivable(id);
            if (r is null || r.PatientNo != patientNo) return (Array.Empty<Receivable>(), 0, "선택한 항목을 찾을 수 없습니다.");
            if (r.Status != ReceivableStatus.Unpaid) return (Array.Empty<Receivable>(), 0, "이미 수납된 항목이 있습니다.");
            targets.Add(r);
        }
        long total = targets.Sum(t => t.Total);
        if (total <= 0) return (Array.Empty<Receivable>(), 0, "수납할 금액이 없습니다.");
        return (targets, total, null);
    }

    public PayResult PayByCard(string patientNo, IReadOnlyCollection<long> receivableIds, string idempotencyKey)
    {
        var existing = _repo.FindPaymentByKey(idempotencyKey);
        if (existing is not null)
        {
            _audit.Write("pay.duplicate", new { key = idempotencyKey, existing.Status });
            return existing.Status == PaymentStatus.Approved
                ? new PayResult(PayOutcome.Approved, "이미 처리된 결제입니다.", existing)
                : new PayResult(PayOutcome.Invalid, "이미 처리된 요청입니다. 처음부터 다시 시도하세요.", existing);
        }

        var (targets, total, error) = Prepare(patientNo, receivableIds);
        if (error is not null) return new PayResult(PayOutcome.Invalid, error);

        Payment payment;
        try { payment = _repo.CreatePendingPayment(idempotencyKey, patientNo, PaymentMethod.Card, total, targets.Select(t => t.Id).ToList()); }
        catch (ConcurrencyException) { return new PayResult(PayOutcome.Invalid, "이미 처리 중인 요청입니다."); }
        _audit.Write("pay.pending", new { payment.Id, patientNo, total, ids = payment.ReceivableIds });

        var card = _terminal.Approve(idempotencyKey, total);
        switch (card.Kind)
        {
            case CardResultKind.Declined:
                _repo.MarkPayment(payment.Id, PaymentStatus.Failed, card.Message ?? "승인 거절");
                _audit.Write("pay.declined", new { payment.Id, card.Message });
                return new PayResult(PayOutcome.Declined, $"카드 승인이 거절되었습니다. ({card.Message})", payment);

            case CardResultKind.Timeout:
                // 승인이 됐는지 모른다 → 망취소로 확실히 '안 된 상태'로 만든다.
                bool netCancelled = _terminal.Cancel(idempotencyKey, total);
                _repo.MarkPayment(payment.Id, PaymentStatus.Cancelled, "단말기 응답 없음 → 망취소");
                _audit.Write("pay.timeout_netcancel", new { payment.Id, netCancelled });
                return new PayResult(PayOutcome.Cancelled, "결제 응답이 없어 취소했습니다. 다시 시도해 주세요.", payment);
        }

        try
        {
            _repo.CompletePayment(payment.Id, card.ApprovalNo, 0, 0);
        }
        catch (Exception ex)
        {
            // 돈은 빠져나갔는데 수납 처리가 안 됐다 → 바로 승인 취소해서 맞춘다.
            bool cancelled = _terminal.Cancel(idempotencyKey, total);
            var reason = ex is ConcurrencyException ? "다른 곳에서 먼저 수납됨" : "수납 저장 실패";
            TryMark(payment.Id, PaymentStatus.Cancelled, $"{reason} → 승인 취소({(cancelled ? "성공" : "실패, 수동 확인 필요")})");
            _audit.Write("pay.compensated", new { payment.Id, reason, error = ex.Message, cancelled });
            var msg = ex is ConcurrencyException
                ? "이미 수납된 항목이 있어 결제를 취소했습니다."
                : "일시적인 오류로 결제를 취소했습니다. 원무과 창구로 문의해 주세요.";
            return new PayResult(ex is ConcurrencyException ? PayOutcome.AlreadyPaid : PayOutcome.Cancelled, msg, payment);
        }

        payment.Status = PaymentStatus.Approved;
        payment.ApprovalNo = card.ApprovalNo;
        _audit.Write("pay.approved", new { payment.Id, total, card.ApprovalNo });

        bool printed = _printer.Print("진료비 영수증", ReceiptLines(targets, total, card.ApprovalNo));
        if (!printed) _audit.Write("receipt.print_failed", new { payment.Id });
        return new PayResult(PayOutcome.Approved, printed ? "수납이 완료되었습니다." : "수납 완료. 영수증 출력에 실패했습니다(창구에서 재발행).", payment, printed);
    }

    private void TryMark(long paymentId, PaymentStatus status, string reason)
    {
        // DB가 죽어 있으면 이것도 실패할 수 있다. 그때는 감사 로그가 유일한 기록이다.
        try { _repo.MarkPayment(paymentId, status, reason); }
        catch (Exception ex) { _audit.Write("pay.mark_failed", new { paymentId, error = ex.Message }); }
    }

    public static IReadOnlyList<string> ReceiptLines(IEnumerable<Receivable> targets, long total, string? approvalNo, long cash = 0, long change = 0)
    {
        var lines = new List<string>();
        foreach (var r in targets)
        {
            lines.Add($"{r.VisitDate:yyyy-MM-dd} {r.Department}");
            foreach (var i in r.Items) lines.Add($"  {i.Name} x{i.Quantity}  {i.Amount:N0}원");
        }
        lines.Add($"합계 {total:N0}원");
        if (approvalNo is not null) lines.Add($"카드 승인번호 {approvalNo}");
        if (cash > 0) lines.Add($"받은 금액 {cash:N0}원 / 거스름돈 {change:N0}원");
        return lines;
    }
}
