namespace Kiosk.Core.Domain;

// 금액은 모두 원 단위 정수(long)로 다룬다. 소수점 오차가 생길 일이 없고 DB(BIGINT)와 그대로 맞는다.

/// <summary>환자. 화면·로그에는 이름을 마스킹해서만 보여 준다.</summary>
public sealed record Patient(string PatientNo, string Name, DateOnly BirthDate, string PhoneLast4);

public enum ReceivableKind { Treatment, Certificate }

public enum ReceivableStatus { Unpaid, Paid, Cancelled }

/// <summary>수납 항목 한 줄. 단가와 수량만 저장하고 금액은 항상 계산해서 쓴다.</summary>
public sealed record ReceivableItem(string Code, string Name, long UnitPrice, int Quantity)
{
    public long Amount => UnitPrice * Quantity;
}

/// <summary>미수납(또는 수납 완료) 건. 진료비나 제증명 발급비가 여기에 해당한다.</summary>
public sealed class Receivable
{
    public required long Id { get; init; }
    public required string PatientNo { get; init; }
    public required ReceivableKind Kind { get; init; }
    public required string Department { get; init; }
    public required DateOnly VisitDate { get; init; }
    public required IReadOnlyList<ReceivableItem> Items { get; init; }
    public ReceivableStatus Status { get; set; } = ReceivableStatus.Unpaid;
    public long? PaymentId { get; set; }

    /// <summary>총액은 저장값이 아니라 항목에서 다시 계산한다(화면이 보낸 금액을 믿지 않음).</summary>
    public long Total => Items.Sum(i => i.Amount);
}

public enum PaymentMethod { Card, Cash }

public enum PaymentStatus { Pending, Approved, Failed, Cancelled }

public sealed class Payment
{
    public required long Id { get; init; }
    /// <summary>같은 요청이 두 번 들어와도 결제가 한 번만 일어나도록 하는 키.</summary>
    public required string IdempotencyKey { get; init; }
    public required string PatientNo { get; init; }
    public required PaymentMethod Method { get; init; }
    public required long Amount { get; init; }
    public required IReadOnlyList<long> ReceivableIds { get; init; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? ApprovalNo { get; set; }
    public string? FailReason { get; set; }
    public long CashReceived { get; set; }
    public long Change { get; set; }
}

public sealed record CertificateType(string Code, string Name, long Fee);

public sealed record IssuedCertificate(string DocumentNo, string PatientNo, string TypeCode, DateOnly VisitDate, DateTimeOffset IssuedAt);

public enum QueueCategory { Payment, Admission, Certificate }

public sealed record QueueTicket(DateOnly Date, QueueCategory Category, int Number, DateTimeOffset IssuedAt)
{
    /// <summary>화면·번호표에 찍히는 표시 번호. 예: 수납 A-007</summary>
    public string Display => $"{Prefix(Category)}-{Number:000}";

    public static string Prefix(QueueCategory c) => c switch
    {
        QueueCategory.Payment => "A",
        QueueCategory.Admission => "B",
        QueueCategory.Certificate => "C",
        _ => "Z",
    };
}

public enum WaitingStatus { Waiting, InTreatment, Done }

public sealed record WaitingEntry(string PatientNo, string PatientName, string Department, string Doctor, DateTimeOffset ReceptionAt, WaitingStatus Status);

public static class Privacy
{
    /// <summary>이름 가운데 글자를 가린다. 홍길동 → 홍*동, 김수 → 김*</summary>
    public static string MaskName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.Length == 1) return "*";
        if (name.Length == 2) return name[0] + "*";
        return name[0] + new string('*', name.Length - 2) + name[^1];
    }
}
