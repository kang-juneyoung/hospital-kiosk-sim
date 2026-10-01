using Kiosk.Core.Domain;

namespace Kiosk.Core.Data;

/// <summary>
/// 저장소 경계. 서비스는 이 인터페이스만 알고, MySQL인지 메모리인지는 모른다.
/// 운영: <see cref="AdoKioskRepository"/>(MySQL) / 시연·테스트: <see cref="InMemoryKioskRepository"/>.
/// </summary>
public interface IKioskRepository
{
    Patient? FindPatient(string patientNo);
    IReadOnlyList<Receivable> GetUnpaid(string patientNo);
    Receivable? GetReceivable(long id);
    Receivable CreateReceivable(string patientNo, ReceivableKind kind, string department, DateOnly visitDate, IReadOnlyList<ReceivableItem> items);
    bool HasVisit(string patientNo, DateOnly visitDate);

    Payment? FindPaymentByKey(string idempotencyKey);
    Payment CreatePendingPayment(string idempotencyKey, string patientNo, PaymentMethod method, long amount, IReadOnlyList<long> receivableIds);

    /// <summary>
    /// 한 트랜잭션으로 결제를 승인 상태로 바꾸고 대상 수납 건을 모두 Paid로 바꾼다.
    /// 그 사이 창구에서 먼저 수납된 건이 있으면 <see cref="ConcurrencyException"/>을 던지고 아무것도 바꾸지 않는다.
    /// </summary>
    void CompletePayment(long paymentId, string? approvalNo, long cashReceived, long change);
    void MarkPayment(long paymentId, PaymentStatus status, string reason);

    IssuedCertificate SaveCertificate(string patientNo, string typeCode, DateOnly visitDate, DateTimeOffset issuedAt);

    /// <summary>날짜·창구 종류별 번호를 원자적으로 하나 올려 돌려준다. 날짜가 바뀌면 1부터 다시.</summary>
    int NextQueueNumber(DateOnly date, QueueCategory category);
    /// <summary>다음 번호를 호출. 대기자가 없으면 null.</summary>
    int? CallNextQueueNumber(DateOnly date, QueueCategory category);
    (int LastIssued, int LastCalled) GetQueueState(DateOnly date, QueueCategory category);

    IReadOnlyList<WaitingEntry> GetWaiting(string department);
}

public sealed class ConcurrencyException(string message) : Exception(message);
