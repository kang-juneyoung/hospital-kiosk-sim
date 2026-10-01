using Kiosk.Core.Data;
using Kiosk.Core.Devices;
using Kiosk.Core.Domain;

namespace Kiosk.Core.Services;

/// <summary>
/// 제증명 발급. 흐름: 신청(발급비 수납 건 생성) → 수납(PaymentService 재사용) → 발급(문서번호 부여 + 출력).
/// 발급비도 일반 진료비와 같은 결제 경로를 타게 해서, 결제·망취소 규칙을 한 곳에서만 관리한다.
/// </summary>
public sealed class CertificateService
{
    public static readonly IReadOnlyList<CertificateType> Types = new[]
    {
        new CertificateType("RECEIPT", "진료비 계산서·영수증 재발행", 0),
        new CertificateType("VISIT", "진료확인서", 1000),
        new CertificateType("DETAIL", "진료비 세부산정내역", 1000),
    };

    public const int MaxCopies = 5;

    private readonly IKioskRepository _repo;
    private readonly IReceiptPrinter _printer;
    private readonly IClock _clock;
    private readonly AuditLog _audit;

    public CertificateService(IKioskRepository repo, IReceiptPrinter printer, IClock clock, AuditLog audit)
    {
        _repo = repo; _printer = printer; _clock = clock; _audit = audit;
    }

    public static CertificateType? FindType(string code) => Types.FirstOrDefault(t => t.Code == code);

    /// <summary>신청. 무료 증명서는 수납 없이 바로 발급 가능하므로 null을 돌려준다.</summary>
    public (Receivable? Fee, string? Error) Request(string patientNo, string typeCode, DateOnly visitDate, int copies)
    {
        var type = FindType(typeCode);
        if (type is null) return (null, "발급할 수 없는 서류입니다.");
        if (copies < 1 || copies > MaxCopies) return (null, $"발급 매수는 1~{MaxCopies}매입니다.");
        if (!_repo.HasVisit(patientNo, visitDate)) return (null, "해당 날짜의 진료 기록이 없습니다.");
        if (type.Fee == 0) return (null, null);

        var fee = _repo.CreateReceivable(patientNo, ReceivableKind.Certificate, "제증명", visitDate,
            new[] { new ReceivableItem(type.Code, type.Name, type.Fee, copies) });
        _audit.Write("cert.requested", new { patientNo, typeCode, copies, fee.Id, fee.Total });
        return (fee, null);
    }

    /// <summary>발급. 유료 서류는 발급비 수납이 끝난 경우에만 출력한다.</summary>
    public (IReadOnlyList<IssuedCertificate> Issued, string? Error) Issue(string patientNo, string typeCode, DateOnly visitDate, int copies, long? feeReceivableId)
    {
        var type = FindType(typeCode);
        if (type is null) return (Array.Empty<IssuedCertificate>(), "발급할 수 없는 서류입니다.");
        if (type.Fee > 0)
        {
            var fee = feeReceivableId is null ? null : _repo.GetReceivable(feeReceivableId.Value);
            if (fee is null || fee.PatientNo != patientNo || fee.Status != ReceivableStatus.Paid)
                return (Array.Empty<IssuedCertificate>(), "발급비 수납이 확인되지 않았습니다.");
        }

        var issued = new List<IssuedCertificate>();
        for (int i = 0; i < copies; i++)
        {
            var doc = _repo.SaveCertificate(patientNo, typeCode, visitDate, _clock.Now);
            issued.Add(doc);
            bool printed = _printer.Print(type.Name, new[]
            {
                $"문서번호 {doc.DocumentNo}",
                $"환자번호 {patientNo}",
                $"진료일 {visitDate:yyyy-MM-dd}",
                $"발급일시 {doc.IssuedAt:yyyy-MM-dd HH:mm}",
            });
            _audit.Write("cert.issued", new { doc.DocumentNo, patientNo, typeCode, printed });
            if (!printed) return (issued, $"{issued.Count}매 중 마지막 장 출력에 실패했습니다. 문서번호로 창구에서 재출력할 수 있습니다.");
        }
        return (issued, null);
    }
}

/// <summary>순번대기표. 날짜가 바뀌면 번호는 1부터 다시 시작한다(저장소가 날짜별로 센다).</summary>
public sealed class QueueService
{
    private readonly IKioskRepository _repo;
    private readonly IReceiptPrinter _printer;
    private readonly IClock _clock;

    public QueueService(IKioskRepository repo, IReceiptPrinter printer, IClock clock)
    {
        _repo = repo; _printer = printer; _clock = clock;
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.Now.Date);

    public (QueueTicket Ticket, int WaitingAhead) Issue(QueueCategory category)
    {
        int no = _repo.NextQueueNumber(Today, category);
        var (_, called) = _repo.GetQueueState(Today, category);
        var ticket = new QueueTicket(Today, category, no, _clock.Now);
        int ahead = no - called - 1;
        _printer.Print("번호표", new[] { $"{CategoryName(category)}  {ticket.Display}", $"앞에 {ahead}명 대기", $"{ticket.IssuedAt:HH:mm} 발행" });
        return (ticket, ahead);
    }

    /// <summary>창구 직원 호출 버튼.</summary>
    public string? CallNext(QueueCategory category)
    {
        var n = _repo.CallNextQueueNumber(Today, category);
        return n is null ? null : $"{QueueTicket.Prefix(category)}-{n:000}";
    }

    public int Waiting(QueueCategory category)
    {
        var (issued, called) = _repo.GetQueueState(Today, category);
        return issued - called;
    }

    public static string CategoryName(QueueCategory c) => c switch
    {
        QueueCategory.Payment => "수납",
        QueueCategory.Admission => "입·퇴원",
        QueueCategory.Certificate => "제증명",
        _ => c.ToString(),
    };
}

/// <summary>진료대기 현황판. 이름은 반드시 마스킹하고, 진료가 끝난 환자는 보여 주지 않는다.</summary>
public sealed class WaitingBoardService
{
    private readonly IKioskRepository _repo;
    public WaitingBoardService(IKioskRepository repo) => _repo = repo;

    public IReadOnlyList<string> Board(string department, int max = 10)
    {
        var list = _repo.GetWaiting(department)
            .Where(w => w.Status != WaitingStatus.Done)
            .OrderBy(w => w.Status == WaitingStatus.InTreatment ? 0 : 1)
            .ThenBy(w => w.ReceptionAt)
            .Take(max)
            .ToList();
        int order = 0;
        return list.Select(w => w.Status == WaitingStatus.InTreatment
                ? $"진료중  {Privacy.MaskName(w.PatientName)}  ({w.Doctor})"
                : $"대기 {++order,2}  {Privacy.MaskName(w.PatientName)}  ({w.Doctor})")
            .ToList();
    }
}
