using Kiosk.Core.Domain;

namespace Kiosk.Core.Data;

/// <summary>
/// 메모리 저장소. 시연과 단위 테스트에 쓴다. 모든 메서드를 하나의 lock으로 묶어
/// MySQL 트랜잭션과 같은 "전부 되거나 전부 안 되거나"를 흉내 낸다.
/// </summary>
public sealed class InMemoryKioskRepository : IKioskRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Patient> _patients = new();
    private readonly Dictionary<long, Receivable> _receivables = new();
    private readonly Dictionary<long, Payment> _payments = new();
    private readonly List<(string PatientNo, DateOnly Date)> _visits = new();
    private readonly List<IssuedCertificate> _certificates = new();
    private readonly Dictionary<(DateOnly, QueueCategory), (int Issued, int Called)> _queues = new();
    private readonly List<WaitingEntry> _waiting = new();
    private long _receivableSeq = 100, _paymentSeq = 1;

    /// <summary>시연용 고장 주입: 다음 CompletePayment에서 DB 오류를 낸다.</summary>
    public bool FailNextCompletePayment { get; set; }

    public IReadOnlyList<IssuedCertificate> Certificates { get { lock (_lock) return _certificates.ToList(); } }

    // ── 시드 데이터 등록 ──
    public void AddPatient(Patient p) { lock (_lock) _patients[p.PatientNo] = p; }
    public void AddVisit(string patientNo, DateOnly date) { lock (_lock) _visits.Add((patientNo, date)); }
    public void AddWaiting(WaitingEntry e) { lock (_lock) _waiting.Add(e); }

    /// <summary>창구에서 먼저 수납해 버린 상황 재현용.</summary>
    public void MarkPaidElsewhere(long receivableId) { lock (_lock) _receivables[receivableId].Status = ReceivableStatus.Paid; }

    public Patient? FindPatient(string patientNo)
    {
        lock (_lock) return _patients.GetValueOrDefault(patientNo);
    }

    public IReadOnlyList<Receivable> GetUnpaid(string patientNo)
    {
        lock (_lock)
            return _receivables.Values
                .Where(r => r.PatientNo == patientNo && r.Status == ReceivableStatus.Unpaid)
                .OrderBy(r => r.VisitDate).ThenBy(r => r.Id)
                .ToList();
    }

    public Receivable? GetReceivable(long id)
    {
        lock (_lock) return _receivables.GetValueOrDefault(id);
    }

    public Receivable CreateReceivable(string patientNo, ReceivableKind kind, string department, DateOnly visitDate, IReadOnlyList<ReceivableItem> items)
    {
        lock (_lock)
        {
            var r = new Receivable
            {
                Id = ++_receivableSeq, PatientNo = patientNo, Kind = kind,
                Department = department, VisitDate = visitDate, Items = items.ToList(),
            };
            _receivables[r.Id] = r;
            return r;
        }
    }

    public bool HasVisit(string patientNo, DateOnly visitDate)
    {
        lock (_lock) return _visits.Contains((patientNo, visitDate));
    }

    public Payment? FindPaymentByKey(string idempotencyKey)
    {
        lock (_lock) return _payments.Values.FirstOrDefault(p => p.IdempotencyKey == idempotencyKey);
    }

    public Payment CreatePendingPayment(string idempotencyKey, string patientNo, PaymentMethod method, long amount, IReadOnlyList<long> receivableIds)
    {
        lock (_lock)
        {
            if (_payments.Values.Any(p => p.IdempotencyKey == idempotencyKey))
                throw new ConcurrencyException("이미 같은 키의 결제가 있습니다.");   // DB의 UNIQUE 제약과 같은 역할
            var p = new Payment
            {
                Id = _paymentSeq++, IdempotencyKey = idempotencyKey, PatientNo = patientNo,
                Method = method, Amount = amount, ReceivableIds = receivableIds.ToList(),
            };
            _payments[p.Id] = p;
            return p;
        }
    }

    public void CompletePayment(long paymentId, string? approvalNo, long cashReceived, long change)
    {
        lock (_lock)
        {
            if (FailNextCompletePayment)
            {
                FailNextCompletePayment = false;
                throw new InvalidOperationException("DB 연결 끊김(시연용 고장 주입)");
            }
            var p = _payments[paymentId];
            var targets = p.ReceivableIds.Select(id => _receivables[id]).ToList();
            if (targets.Any(r => r.Status != ReceivableStatus.Unpaid))
                throw new ConcurrencyException("이미 수납된 항목이 있습니다.");
            // 검사가 끝난 뒤에만 바꾼다 → 중간에 실패해도 반쯤 바뀐 상태가 남지 않음
            foreach (var r in targets) { r.Status = ReceivableStatus.Paid; r.PaymentId = p.Id; }
            p.Status = PaymentStatus.Approved;
            p.ApprovalNo = approvalNo;
            p.CashReceived = cashReceived;
            p.Change = change;
        }
    }

    public void MarkPayment(long paymentId, PaymentStatus status, string reason)
    {
        lock (_lock)
        {
            var p = _payments[paymentId];
            p.Status = status;
            p.FailReason = reason;
        }
    }

    public IssuedCertificate SaveCertificate(string patientNo, string typeCode, DateOnly visitDate, DateTimeOffset issuedAt)
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(issuedAt.Date);
            int seq = _certificates.Count(c => DateOnly.FromDateTime(c.IssuedAt.Date) == today) + 1;
            var doc = new IssuedCertificate($"CERT-{today:yyyyMMdd}-{seq:0000}", patientNo, typeCode, visitDate, issuedAt);
            _certificates.Add(doc);
            return doc;
        }
    }

    public int NextQueueNumber(DateOnly date, QueueCategory category)
    {
        lock (_lock)
        {
            var key = (date, category);
            var s = _queues.GetValueOrDefault(key);
            s.Issued++;
            _queues[key] = s;
            return s.Issued;
        }
    }

    public int? CallNextQueueNumber(DateOnly date, QueueCategory category)
    {
        lock (_lock)
        {
            var key = (date, category);
            var s = _queues.GetValueOrDefault(key);
            if (s.Called >= s.Issued) return null;
            s.Called++;
            _queues[key] = s;
            return s.Called;
        }
    }

    public (int LastIssued, int LastCalled) GetQueueState(DateOnly date, QueueCategory category)
    {
        lock (_lock)
        {
            var s = _queues.GetValueOrDefault((date, category));
            return (s.Issued, s.Called);
        }
    }

    public IReadOnlyList<WaitingEntry> GetWaiting(string department)
    {
        lock (_lock) return _waiting.Where(w => w.Department == department).ToList();
    }
}
