using Kiosk.Core.Data;
using Kiosk.Core.Devices;
using Kiosk.Core.Domain;

namespace Kiosk.Core.Services;

public enum CashState { Collecting, Completed, Cancelled, NeedsStaff }

/// <summary>
/// 현금 수납 한 건. 지폐가 들어올 때마다(에스크로) 받을지 돌려줄지 정한다.
///  - 허용 권종이 아니거나, 이 지폐를 받으면 거스름돈을 못 내주는 경우 → 반환(ReturnNote)
///  - 받으면 보관함으로(StackNote), 합계가 금액 이상이 되면 수납 완료 + 거스름돈 방출
///  - 중간 취소 → 넣은 금액을 방출기로 환불. 환불 실패면 직원 호출 상태로 남긴다.
/// </summary>
public sealed class CashPaymentSession
{
    public static readonly long[] AcceptedNotes = { 1000, 5000, 10000, 50000 };

    private readonly IKioskRepository _repo;
    private readonly BillAcceptorDriver _acceptor;
    private readonly IChangeDispenser _dispenser;
    private readonly IReceiptPrinter _printer;
    private readonly AuditLog _audit;
    private readonly Payment _payment;
    private readonly IReadOnlyList<Receivable> _targets;

    public long Total { get; }
    public long Received { get; private set; }
    public long Remaining => Math.Max(0, Total - Received);
    public CashState State { get; private set; } = CashState.Collecting;
    public string LastMessage { get; private set; } = "";

    public event Action<CashPaymentSession>? Changed;

    private CashPaymentSession(IKioskRepository repo, BillAcceptorDriver acceptor, IChangeDispenser dispenser,
        IReceiptPrinter printer, AuditLog audit, Payment payment, IReadOnlyList<Receivable> targets)
    {
        _repo = repo; _acceptor = acceptor; _dispenser = dispenser; _printer = printer; _audit = audit;
        _payment = payment; _targets = targets; Total = payment.Amount;
        _acceptor.NoteEscrowed += OnNoteEscrowed;
    }

    public static (CashPaymentSession? Session, string? Error) Start(
        IKioskRepository repo, PaymentService pricing, BillAcceptorDriver acceptor, IChangeDispenser dispenser,
        IReceiptPrinter printer, AuditLog audit, string patientNo, IReadOnlyCollection<long> receivableIds, string idempotencyKey)
    {
        var (targets, total, error) = pricing.Prepare(patientNo, receivableIds);
        if (error is not null) return (null, error);
        Payment p;
        try { p = repo.CreatePendingPayment(idempotencyKey, patientNo, PaymentMethod.Cash, total, targets.Select(t => t.Id).ToList()); }
        catch (ConcurrencyException) { return (null, "이미 처리 중인 요청입니다."); }
        audit.Write("cash.start", new { p.Id, patientNo, total });
        return (new CashPaymentSession(repo, acceptor, dispenser, printer, audit, p, targets), null);
    }

    private void OnNoteEscrowed(long note)
    {
        if (State != CashState.Collecting) { _acceptor.ReturnNote(); return; }

        if (!AcceptedNotes.Contains(note))
        {
            Reject(note, "사용할 수 없는 지폐입니다.");
            return;
        }
        long after = Received + note;
        long change = Math.Max(0, after - Total);
        if (change > 0 && !_dispenser.CanDispense(change))
        {
            Reject(note, $"거스름돈 {change:N0}원을 드릴 수 없어 지폐를 반환합니다. 더 작은 지폐를 넣어 주세요.");
            return;
        }

        _acceptor.StackNote();
        Received = after;
        _audit.Write("cash.note", new { _payment.Id, note, Received });
        LastMessage = $"{note:N0}원 투입. 남은 금액 {Remaining:N0}원";

        if (Received >= Total) Complete(change);
        Changed?.Invoke(this);
    }

    private void Reject(long note, string message)
    {
        _acceptor.ReturnNote();
        _audit.Write("cash.note_rejected", new { _payment.Id, note, message });
        LastMessage = message;
        Changed?.Invoke(this);
    }

    private void Complete(long change)
    {
        try
        {
            _repo.CompletePayment(_payment.Id, null, Received, change);
        }
        catch (Exception ex)
        {
            // 현금은 이미 보관함에 들어갔다 → 넣은 돈 전액 환불 시도
            _audit.Write("cash.complete_failed", new { _payment.Id, error = ex.Message });
            Refund("수납 저장 실패");
            return;
        }
        bool dispensed = change == 0 || _dispenser.Dispense(change);
        State = dispensed ? CashState.Completed : CashState.NeedsStaff;
        _audit.Write("cash.completed", new { _payment.Id, Received, change, dispensed });
        bool printed = _printer.Print("진료비 영수증", PaymentService.ReceiptLines(_targets, Total, null, Received, change));
        LastMessage = dispensed
            ? $"수납이 완료되었습니다. 거스름돈 {change:N0}원을 받아 가세요." + (printed ? "" : " (영수증 출력 실패)")
            : $"수납은 완료되었으나 거스름돈 {change:N0}원 방출에 실패했습니다. 직원을 불러 드리겠습니다.";
    }

    public void Cancel()
    {
        if (State != CashState.Collecting) return;
        Refund("사용자 취소");
        Changed?.Invoke(this);
    }

    private void Refund(string reason)
    {
        bool ok = Received == 0 || _dispenser.Dispense(Received);
        try { _repo.MarkPayment(_payment.Id, PaymentStatus.Cancelled, $"{reason}, 환불 {(ok ? "완료" : "실패")} {Received:N0}원"); }
        catch (Exception ex) { _audit.Write("cash.mark_failed", new { _payment.Id, error = ex.Message }); }
        _audit.Write("cash.refund", new { _payment.Id, reason, Received, ok });
        State = ok ? CashState.Cancelled : CashState.NeedsStaff;
        LastMessage = ok
            ? (Received > 0 ? $"취소되었습니다. 넣으신 {Received:N0}원을 돌려드렸습니다." : "취소되었습니다.")
            : $"환불 {Received:N0}원 방출에 실패했습니다. 직원을 불러 드리겠습니다.";
    }

    public void Detach() => _acceptor.NoteEscrowed -= OnNoteEscrowed;
}
