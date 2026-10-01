using System.Data;
using System.Data.Common;
using Kiosk.Core.Domain;

namespace Kiosk.Core.Data;

/// <summary>
/// ADO.NET 저장소(MySQL 8 문법). 특정 드라이버에 묶이지 않도록 DbConnection만 받는다.
/// 실제 연결은 Kiosk.Data.MySql 프로젝트가 MySqlConnector로 만들어 넘긴다.
/// 모든 값은 파라미터로 넘긴다(문자열 이어 붙이기 금지 → SQL 인젝션 방지).
/// </summary>
public sealed class AdoKioskRepository : IKioskRepository
{
    private readonly Func<DbConnection> _connect;

    public AdoKioskRepository(Func<DbConnection> connect) => _connect = connect;

    // ── 공통 도우미 ──
    private DbConnection Open()
    {
        var c = _connect();
        c.Open();
        return c;
    }

    private static DbCommand Cmd(DbConnection c, string sql, DbTransaction? tx = null, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static DateOnly ToDate(object v) => DateOnly.FromDateTime(Convert.ToDateTime(v));
    private static DateTime FromDate(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    // ── 환자·수납 ──
    public Patient? FindPatient(string patientNo)
    {
        using var c = Open();
        using var r = Cmd(c, "SELECT patient_no, name, birth_date, phone_last4 FROM patient WHERE patient_no = @no",
            null, ("@no", patientNo)).ExecuteReader();
        if (!r.Read()) return null;
        return new Patient(r.GetString(0), r.GetString(1), ToDate(r.GetValue(2)), r.GetString(3));
    }

    public IReadOnlyList<Receivable> GetUnpaid(string patientNo)
    {
        using var c = Open();
        var ids = new List<long>();
        using (var r = Cmd(c, "SELECT id FROM receivable WHERE patient_no = @no AND status = 'Unpaid' ORDER BY visit_date, id",
                   null, ("@no", patientNo)).ExecuteReader())
            while (r.Read()) ids.Add(r.GetInt64(0));
        return ids.Select(id => LoadReceivable(c, null, id)!).ToList();
    }

    public Receivable? GetReceivable(long id)
    {
        using var c = Open();
        return LoadReceivable(c, null, id);
    }

    private static Receivable? LoadReceivable(DbConnection c, DbTransaction? tx, long id)
    {
        string patientNo, dept; ReceivableKind kind; DateOnly visit; ReceivableStatus status; long? paymentId;
        using (var r = Cmd(c, "SELECT patient_no, kind, department, visit_date, status, payment_id FROM receivable WHERE id = @id",
                   tx, ("@id", id)).ExecuteReader())
        {
            if (!r.Read()) return null;
            patientNo = r.GetString(0);
            kind = Enum.Parse<ReceivableKind>(r.GetString(1));
            dept = r.GetString(2);
            visit = ToDate(r.GetValue(3));
            status = Enum.Parse<ReceivableStatus>(r.GetString(4));
            paymentId = r.IsDBNull(5) ? null : r.GetInt64(5);
        }
        var items = new List<ReceivableItem>();
        using (var r = Cmd(c, "SELECT code, name, unit_price, quantity FROM receivable_item WHERE receivable_id = @id ORDER BY seq",
                   tx, ("@id", id)).ExecuteReader())
            while (r.Read()) items.Add(new ReceivableItem(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3)));

        return new Receivable
        {
            Id = id, PatientNo = patientNo, Kind = kind, Department = dept, VisitDate = visit,
            Items = items, Status = status, PaymentId = paymentId,
        };
    }

    public Receivable CreateReceivable(string patientNo, ReceivableKind kind, string department, DateOnly visitDate, IReadOnlyList<ReceivableItem> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Cmd(c, "INSERT INTO receivable (patient_no, kind, department, visit_date) VALUES (@no, @kind, @dept, @d)",
            tx, ("@no", patientNo), ("@kind", kind.ToString()), ("@dept", department), ("@d", FromDate(visitDate))).ExecuteNonQuery();
        long id = Convert.ToInt64(Cmd(c, "SELECT LAST_INSERT_ID()", tx).ExecuteScalar());
        for (int i = 0; i < items.Count; i++)
            Cmd(c, "INSERT INTO receivable_item (receivable_id, seq, code, name, unit_price, quantity) VALUES (@id, @seq, @code, @name, @price, @qty)",
                tx, ("@id", id), ("@seq", i + 1), ("@code", items[i].Code), ("@name", items[i].Name),
                ("@price", items[i].UnitPrice), ("@qty", items[i].Quantity)).ExecuteNonQuery();
        var created = LoadReceivable(c, tx, id)!;
        tx.Commit();
        return created;
    }

    public bool HasVisit(string patientNo, DateOnly visitDate)
    {
        using var c = Open();
        var n = Convert.ToInt64(Cmd(c, "SELECT COUNT(*) FROM visit WHERE patient_no = @no AND visit_date = @d",
            null, ("@no", patientNo), ("@d", FromDate(visitDate))).ExecuteScalar());
        return n > 0;
    }

    // ── 결제 ──
    public Payment? FindPaymentByKey(string idempotencyKey)
    {
        using var c = Open();
        long? id;
        using (var r = Cmd(c, "SELECT id FROM payment WHERE idempotency_key = @k", null, ("@k", idempotencyKey)).ExecuteReader())
            id = r.Read() ? r.GetInt64(0) : null;
        return id is null ? null : LoadPayment(c, null, id.Value);
    }

    private static Payment LoadPayment(DbConnection c, DbTransaction? tx, long id)
    {
        Payment p;
        using (var r = Cmd(c, "SELECT idempotency_key, patient_no, method, amount, status, approval_no, fail_reason, cash_received, change_amount FROM payment WHERE id = @id",
                   tx, ("@id", id)).ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException($"payment {id} 없음");
            p = new Payment
            {
                Id = id, IdempotencyKey = r.GetString(0), PatientNo = r.GetString(1),
                Method = Enum.Parse<PaymentMethod>(r.GetString(2)), Amount = r.GetInt64(3),
                ReceivableIds = new List<long>(),
                Status = Enum.Parse<PaymentStatus>(r.GetString(4)),
                ApprovalNo = r.IsDBNull(5) ? null : r.GetString(5),
                FailReason = r.IsDBNull(6) ? null : r.GetString(6),
                CashReceived = r.GetInt64(7), Change = r.GetInt64(8),
            };
        }
        var ids = (List<long>)p.ReceivableIds;
        using (var r = Cmd(c, "SELECT receivable_id FROM payment_target WHERE payment_id = @id ORDER BY receivable_id",
                   tx, ("@id", id)).ExecuteReader())
            while (r.Read()) ids.Add(r.GetInt64(0));
        return p;
    }

    public Payment CreatePendingPayment(string idempotencyKey, string patientNo, PaymentMethod method, long amount, IReadOnlyList<long> receivableIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        try
        {
            Cmd(c, "INSERT INTO payment (idempotency_key, patient_no, method, amount, status) VALUES (@k, @no, @m, @amt, 'Pending')",
                tx, ("@k", idempotencyKey), ("@no", patientNo), ("@m", method.ToString()), ("@amt", amount)).ExecuteNonQuery();
        }
        catch (DbException ex)
        {
            // uq_payment_key 위반 = 같은 요청이 이미 처리 중
            tx.Rollback();
            throw new ConcurrencyException("이미 같은 키의 결제가 있습니다: " + ex.Message);
        }
        long id = Convert.ToInt64(Cmd(c, "SELECT LAST_INSERT_ID()", tx).ExecuteScalar());
        foreach (var rid in receivableIds)
            Cmd(c, "INSERT INTO payment_target (payment_id, receivable_id) VALUES (@pid, @rid)",
                tx, ("@pid", id), ("@rid", rid)).ExecuteNonQuery();
        tx.Commit();
        return new Payment
        {
            Id = id, IdempotencyKey = idempotencyKey, PatientNo = patientNo, Method = method,
            Amount = amount, ReceivableIds = receivableIds.ToList(),
        };
    }

    public void CompletePayment(long paymentId, string? approvalNo, long cashReceived, long change)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        // 1) 대상 수납 건을 잠그고(FOR UPDATE) 상태 확인 — 창구와 동시에 수납되는 경우를 막는다.
        int targets = 0, unpaid = 0;
        using (var r = Cmd(c, @"SELECT r.status FROM payment_target t JOIN receivable r ON r.id = t.receivable_id
                                WHERE t.payment_id = @pid FOR UPDATE", tx, ("@pid", paymentId)).ExecuteReader())
            while (r.Read()) { targets++; if (r.GetString(0) == "Unpaid") unpaid++; }

        if (targets == 0 || unpaid != targets)
        {
            tx.Rollback();
            throw new ConcurrencyException("이미 수납된 항목이 있습니다.");
        }

        // 2) 수납 건과 결제를 한 트랜잭션으로 바꾼다.
        Cmd(c, @"UPDATE receivable r JOIN payment_target t ON r.id = t.receivable_id
                 SET r.status = 'Paid', r.payment_id = @pid WHERE t.payment_id = @pid", tx, ("@pid", paymentId)).ExecuteNonQuery();
        Cmd(c, @"UPDATE payment SET status = 'Approved', approval_no = @ap, cash_received = @cash, change_amount = @chg
                 WHERE id = @pid", tx, ("@ap", approvalNo), ("@cash", cashReceived), ("@chg", change), ("@pid", paymentId)).ExecuteNonQuery();
        tx.Commit();
    }

    public void MarkPayment(long paymentId, PaymentStatus status, string reason)
    {
        using var c = Open();
        Cmd(c, "UPDATE payment SET status = @s, fail_reason = @r WHERE id = @id",
            null, ("@s", status.ToString()), ("@r", reason), ("@id", paymentId)).ExecuteNonQuery();
    }

    // ── 제증명 ──
    public IssuedCertificate SaveCertificate(string patientNo, string typeCode, DateOnly visitDate, DateTimeOffset issuedAt)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        var day = issuedAt.Date;
        var n = Convert.ToInt64(Cmd(c, "SELECT COUNT(*) FROM certificate WHERE issued_at >= @from AND issued_at < @to FOR UPDATE",
            tx, ("@from", day), ("@to", day.AddDays(1))).ExecuteScalar());
        var doc = new IssuedCertificate($"CERT-{day:yyyyMMdd}-{n + 1:0000}", patientNo, typeCode, visitDate, issuedAt);
        Cmd(c, "INSERT INTO certificate (document_no, patient_no, type_code, visit_date, issued_at) VALUES (@doc, @no, @t, @v, @at)",
            tx, ("@doc", doc.DocumentNo), ("@no", patientNo), ("@t", typeCode), ("@v", FromDate(visitDate)), ("@at", issuedAt.DateTime)).ExecuteNonQuery();
        tx.Commit();
        return doc;
    }

    // ── 순번대기 ──
    public int NextQueueNumber(DateOnly date, QueueCategory category)
    {
        using var c = Open();
        // MySQL 관용구: LAST_INSERT_ID(식)으로 올린 값을 같은 연결에서 바로 읽는다. 행 잠금 한 번으로 원자적.
        Cmd(c, @"INSERT INTO queue_counter (queue_date, category, last_issued, last_called)
                 VALUES (@d, @cat, LAST_INSERT_ID(1), 0)
                 ON DUPLICATE KEY UPDATE last_issued = LAST_INSERT_ID(last_issued + 1)",
            null, ("@d", FromDate(date)), ("@cat", category.ToString())).ExecuteNonQuery();
        return Convert.ToInt32(Cmd(c, "SELECT LAST_INSERT_ID()").ExecuteScalar());
    }

    public int? CallNextQueueNumber(DateOnly date, QueueCategory category)
    {
        using var c = Open();
        int n = Cmd(c, @"UPDATE queue_counter SET last_called = LAST_INSERT_ID(last_called + 1)
                         WHERE queue_date = @d AND category = @cat AND last_called < last_issued",
            null, ("@d", FromDate(date)), ("@cat", category.ToString())).ExecuteNonQuery();
        if (n == 0) return null;
        return Convert.ToInt32(Cmd(c, "SELECT LAST_INSERT_ID()").ExecuteScalar());
    }

    public (int LastIssued, int LastCalled) GetQueueState(DateOnly date, QueueCategory category)
    {
        using var c = Open();
        using var r = Cmd(c, "SELECT last_issued, last_called FROM queue_counter WHERE queue_date = @d AND category = @cat",
            null, ("@d", FromDate(date)), ("@cat", category.ToString())).ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
    }

    public IReadOnlyList<WaitingEntry> GetWaiting(string department)
    {
        using var c = Open();
        var list = new List<WaitingEntry>();
        using var r = Cmd(c, @"SELECT w.patient_no, p.name, w.department, w.doctor, w.reception_at, w.status
                               FROM waiting w JOIN patient p ON p.patient_no = w.patient_no
                               WHERE w.department = @dept ORDER BY w.reception_at",
            null, ("@dept", department)).ExecuteReader();
        while (r.Read())
            list.Add(new WaitingEntry(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                new DateTimeOffset(Convert.ToDateTime(r.GetValue(4))), Enum.Parse<WaitingStatus>(r.GetString(5))));
        return list;
    }
}
