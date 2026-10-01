using System.Text;
using Kiosk.Core;
using Kiosk.Core.Data;
using Kiosk.Core.Devices;
using Kiosk.Core.Domain;
using Kiosk.Core.Flow;
using Kiosk.Core.Services;

// 병원 무인수납 키오스크 시뮬레이터 — 콘솔 시연
// 사용법: dotnet run --project src/Kiosk.Console -- [시나리오]
//   all(기본) card cash declined timeout dbfail race cert queue board frames interactive

Console.OutputEncoding = Encoding.UTF8;
var scenario = args.FirstOrDefault() ?? "all";
var showAudit = args.Contains("--audit");

var scenarios = new Dictionary<string, (string Title, Action<Env> Run)>
{
    ["card"] = ("카드 수납 정상 흐름", Scenarios.Card),
    ["cash"] = ("현금 수납 — 지폐 투입·반환·거스름돈", Scenarios.Cash),
    ["declined"] = ("카드 승인 거절", Scenarios.Declined),
    ["timeout"] = ("단말기 응답 없음 → 망취소", Scenarios.Timeout),
    ["dbfail"] = ("승인 후 DB 저장 실패 → 승인 취소", Scenarios.DbFail),
    ["race"] = ("창구와 동시 수납 + 결제 버튼 두 번 눌림", Scenarios.Race),
    ["cert"] = ("제증명 신청 → 발급비 수납 → 발급", Scenarios.Certificate),
    ["queue"] = ("순번대기표 발행·호출·날짜 변경", Scenarios.Queue),
    ["board"] = ("진료대기 현황판(이름 마스킹)", Scenarios.Board),
    ["frames"] = ("RS-232C 프레임 — 조각·잡음·체크섬 오류", Scenarios.Frames),
};

if (scenario == "interactive") { Interactive.Run(new Env()); return; }

if (scenario == "help" || (scenario != "all" && !scenarios.ContainsKey(scenario)))
{
    Console.WriteLine("시나리오: all " + string.Join(' ', scenarios.Keys) + " interactive   (옵션: --audit 감사 로그 출력)");
    return;
}

foreach (var (key, (title, run)) in scenarios)
{
    if (scenario != "all" && scenario != key) continue;
    Console.WriteLine();
    Console.WriteLine($"══════ [{key}] {title} ══════");
    var env = new Env();
    run(env);
    if (showAudit)
    {
        Console.WriteLine("  ── 감사 로그 ──");
        foreach (var l in env.Audit.Lines) Console.WriteLine("  " + l);
    }
}

/// <summary>시나리오마다 새 환경(가상 DB·장비)을 만든다. 시나리오끼리 서로 영향을 주지 않게.</summary>
public sealed class Env
{
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 10, 30, 0, TimeSpan.FromHours(9)));
    public InMemoryKioskRepository Repo { get; } = new();
    public SimulatedCardTerminal Card { get; } = new();
    public ConsolePrinter Printer { get; } = new();
    public SimulatedChangeDispenser Dispenser { get; } = new();
    public List<byte[]> SentToBillAcceptor { get; } = new();
    public BillAcceptorDriver BillAcceptor { get; }
    public AuditLog Audit { get; }
    public PaymentService Payments { get; }
    public CertificateService Certificates { get; }
    public QueueService Queue { get; }
    public WaitingBoardService Board { get; }
    public DateOnly Today => DateOnly.FromDateTime(Clock.Now.Date);

    public Env()
    {
        SampleData.Seed(Repo, Today);
        Audit = new AuditLog(Clock);
        BillAcceptor = new BillAcceptorDriver(bytes => SentToBillAcceptor.Add(bytes));
        Payments = new PaymentService(Repo, Card, Printer, Audit);
        Certificates = new CertificateService(Repo, Printer, Clock, Audit);
        Queue = new QueueService(Repo, Printer, Clock);
        Board = new WaitingBoardService(Repo);
    }

    public void FlushPrinter()
    {
        foreach (var p in Printer.Printed)
            foreach (var line in p.Split('\n')) Console.WriteLine("    " + line);
        Printer.Printed.Clear();
    }
}

public static class Scenarios
{
    const string Hong = "10000001";

    static void Step(string s) => Console.WriteLine("  ▶ " + s);
    static void Show(string s) => Console.WriteLine("    " + s);

    static IReadOnlyList<Receivable> ListBills(Env e, string patientNo)
    {
        var bills = e.Repo.GetUnpaid(patientNo);
        foreach (var b in bills) Show($"[{b.Id}] {b.VisitDate:MM-dd} {b.Department,-6} {b.Total,9:N0}원");
        return bills;
    }

    public static void Card(Env e)
    {
        var s = new KioskSession(e.Repo, e.Clock);
        s.Start();
        Step("본인 확인: 10000001 / 800512");
        s.Identify(Hong, "800512");
        Show(s.Message);
        Step("미수납 내역 조회");
        var bills = s.ShowBills();
        ListBills(e, Hong);
        var (_, total, _) = e.Payments.Prepare(Hong, s.Selected);
        Show($"합계(서버 재계산) {total:N0}원");
        Step("카드 결제");
        var key = s.BeginPayment();
        var r = e.Payments.PayByCard(Hong, s.Selected.ToList(), key);
        s.EndPayment(r.Outcome == PayOutcome.Approved, r.Message);
        Show($"{r.Outcome}: {r.Message} (승인번호 {r.Payment?.ApprovalNo})");
        e.FlushPrinter();
        Show($"남은 미수납 {e.Repo.GetUnpaid(Hong).Count}건");
        Step("완료 화면에서 10초 동안 입력 없음 → 다음 사람을 위해 처음 화면으로");
        e.Clock.Advance(TimeSpan.FromSeconds(11));
        s.Tick();
        Show($"화면: {s.Screen}, 환자 정보: {(s.Patient is null ? "지워짐" : "남아 있음")}");
    }

    public static void Cash(Env e)
    {
        var ids = e.Repo.GetUnpaid("10000002").Select(b => b.Id).ToList();
        var (session, err) = CashPaymentSession.Start(e.Repo, e.Payments, e.BillAcceptor, e.Dispenser, e.Printer, e.Audit,
            "10000002", ids, Guid.NewGuid().ToString("N"));
        if (session is null) { Show(err!); return; }
        session.Changed += x => Show($"→ {x.LastMessage}");
        Show($"결제 금액 {session.Total:N0}원 (초진 18,700 + 물리치료 3,200×3)");

        Step("2,000원권 투입(허용하지 않는 권종)");
        e.BillAcceptor.Feed(BillAcceptorDriver.EscrowFrame(2000));
        Step("10,000원권 투입");
        e.BillAcceptor.Feed(BillAcceptorDriver.EscrowFrame(10000));
        Step("50,000원권 투입 — 방출기에 거스름돈이 부족한 상황(1만원·5천원권 소진, 1천원권 5장)");
        e.Dispenser.Inventory[10000] = 0;
        e.Dispenser.Inventory[5000] = 0;
        e.Dispenser.Inventory[1000] = 5;
        e.BillAcceptor.Feed(BillAcceptorDriver.EscrowFrame(50000));
        Step("10,000원권 2장 투입 → 금액 충족, 거스름돈 1,700원 방출");
        e.BillAcceptor.Feed(BillAcceptorDriver.EscrowFrame(10000));
        e.BillAcceptor.Feed(BillAcceptorDriver.EscrowFrame(10000));
        Show($"상태 {session.State}, 받은 금액 {session.Received:N0}원");
        Show($"장비로 보낸 명령: {string.Join(", ", e.SentToBillAcceptor.Select(b => (char)b[1] == 'S' ? "보관(S)" : "반환(R)"))}");
        e.FlushPrinter();
        session.Detach();
    }

    public static void Declined(Env e)
    {
        var ids = e.Repo.GetUnpaid(Hong).Select(b => b.Id).ToList();
        e.Card.NextResults.Enqueue(CardResultKind.Declined);
        Step("카드 결제(단말기가 거절 응답)");
        var r = e.Payments.PayByCard(Hong, ids, Guid.NewGuid().ToString("N"));
        Show($"{r.Outcome}: {r.Message}");
        Show($"미수납 그대로 {e.Repo.GetUnpaid(Hong).Count}건 — 다른 카드로 다시 결제 가능");
    }

    public static void Timeout(Env e)
    {
        var ids = e.Repo.GetUnpaid(Hong).Select(b => b.Id).ToList();
        e.Card.NextResults.Enqueue(CardResultKind.Timeout);
        Step("카드 결제(단말기 응답 없음 — 실제로는 승인됐을 수도 있음)");
        var r = e.Payments.PayByCard(Hong, ids, "KEY-TIMEOUT-1");
        Show($"{r.Outcome}: {r.Message}");
        Show($"망취소 보낸 키: {string.Join(',', e.Card.CancelledKeys)} / 단말기에 남은 승인: {e.Card.Approved.Count}건");
        Show($"결제 상태 {r.Payment?.Status} / 미수납 {e.Repo.GetUnpaid(Hong).Count}건 → 돈도 장부도 '안 된 상태'로 일치");
    }

    public static void DbFail(Env e)
    {
        var ids = e.Repo.GetUnpaid(Hong).Select(b => b.Id).ToList();
        e.Repo.FailNextCompletePayment = true;
        Step("카드 승인은 성공, 직후 수납 저장 중 DB 연결 끊김");
        var r = e.Payments.PayByCard(Hong, ids, "KEY-DBFAIL-1");
        Show($"{r.Outcome}: {r.Message}");
        Show($"승인 취소 보낸 키: {string.Join(',', e.Card.CancelledKeys)} / 단말기에 남은 승인: {e.Card.Approved.Count}건");
        Show($"미수납 {e.Repo.GetUnpaid(Hong).Count}건 그대로 — 환자는 돈이 빠져나가지 않았고 다시 수납 가능");
    }

    public static void Race(Env e)
    {
        var bills = e.Repo.GetUnpaid(Hong);
        var ids = bills.Select(b => b.Id).ToList();
        Step("같은 키로 결제 요청 두 번(버튼 두 번 눌림·네트워크 재시도)");
        var r1 = e.Payments.PayByCard(Hong, ids, "KEY-SAME");
        var r2 = e.Payments.PayByCard(Hong, ids, "KEY-SAME");
        Show($"1차 {r1.Outcome}, 2차 {r2.Outcome}: {r2.Message}");
        Show($"단말기 승인 횟수 {e.Card.Approved.Count}회 (이중 결제 없음)");
        e.Printer.Printed.Clear();

        var kim = e.Repo.GetUnpaid("10000002").Select(b => b.Id).ToList();
        Step("김영희 결제 화면을 보는 사이 창구에서 먼저 수납됨");
        e.Repo.MarkPaidElsewhere(kim[0]);
        var r3 = e.Payments.PayByCard("10000002", kim, "KEY-RACE");
        Show($"{r3.Outcome}: {r3.Message}");
        Step("화면 확인 후 결제 직전에 창구 수납이 끝난 경우(검사와 저장 사이) — 저장 단계 트랜잭션이 막음");
        var fresh = e.Repo.CreateReceivable("10000002", ReceivableKind.Treatment, "정형외과", e.Today,
            new[] { new ReceivableItem("MM101", "물리치료", 3_200, 1) });
        var pricing = e.Payments.Prepare("10000002", new[] { fresh.Id });
        Show($"화면 표시 금액 {pricing.Total:N0}원");
        var pending = e.Repo.CreatePendingPayment("KEY-RACE-2", "10000002", PaymentMethod.Card, pricing.Total, new[] { fresh.Id });
        e.Repo.MarkPaidElsewhere(fresh.Id);
        try { e.Repo.CompletePayment(pending.Id, "00001234", 0, 0); }
        catch (ConcurrencyException ex) { Show($"저장 거부: {ex.Message} → 서비스는 이때 승인 취소를 보냄(dbfail 시나리오와 같은 경로)"); }
    }

    public static void Certificate(Env e)
    {
        Step("진료확인서 2매 신청(진료일 오늘)");
        var (fee, err) = e.Certificates.Request(Hong, "VISIT", e.Today, 2);
        if (fee is null) { Show(err ?? "무료"); return; }
        Show($"발급비 {fee.Total:N0}원 수납 건 생성 [{fee.Id}]");
        Step("발급비 결제 전에 발급 시도");
        var early = e.Certificates.Issue(Hong, "VISIT", e.Today, 2, fee.Id);
        Show(early.Error!);
        Step("발급비 카드 결제(진료비와 같은 결제 경로)");
        var r = e.Payments.PayByCard(Hong, new[] { fee.Id }, Guid.NewGuid().ToString("N"));
        Show($"{r.Outcome}: {r.Message}");
        e.Printer.Printed.Clear();
        Step("발급");
        var (issued, _) = e.Certificates.Issue(Hong, "VISIT", e.Today, 2, fee.Id);
        Show("문서번호: " + string.Join(", ", issued.Select(i => i.DocumentNo)));
        e.FlushPrinter();
        Step("진료 기록이 없는 날짜로 신청");
        Show(e.Certificates.Request(Hong, "VISIT", e.Today.AddDays(-30), 1).Error!);
    }

    public static void Queue(Env e)
    {
        Step("수납 번호표 3장, 제증명 1장 발행");
        for (int i = 0; i < 3; i++) { var (t, ahead) = e.Queue.Issue(QueueCategory.Payment); Show($"{t.Display} (앞 대기 {ahead}명)"); }
        var (c, _) = e.Queue.Issue(QueueCategory.Certificate);
        Show($"{c.Display}");
        e.Printer.Printed.Clear();
        Step("수납 창구 호출 2번");
        Show($"호출 {e.Queue.CallNext(QueueCategory.Payment)}, {e.Queue.CallNext(QueueCategory.Payment)} / 수납 대기 {e.Queue.Waiting(QueueCategory.Payment)}명");
        Step("자정이 지나 다음 날 첫 번호표");
        e.Clock.Advance(TimeSpan.FromHours(14));
        var (n, ahead2) = e.Queue.Issue(QueueCategory.Payment);
        Show($"{n.Date:MM-dd} {n.Display} (앞 대기 {ahead2}명) — 날짜별로 1번부터 다시");
        e.Printer.Printed.Clear();
    }

    public static void Board(Env e)
    {
        Step("정형외과 대기 현황판");
        foreach (var line in e.Board.Board("정형외과")) Show(line);
    }

    public static void Frames(Env e)
    {
        var received = new List<long>();
        e.BillAcceptor.NoteEscrowed += received.Add;
        var frame = BillAcceptorDriver.EscrowFrame(10000);
        Show("10,000원 프레임: " + BitConverter.ToString(frame));
        Step("한 프레임이 3조각으로 나뉘어 도착");
        e.BillAcceptor.Feed(frame.AsSpan(0, 2));
        e.BillAcceptor.Feed(frame.AsSpan(2, 4));
        e.BillAcceptor.Feed(frame.AsSpan(6));
        Show($"인식된 지폐: {string.Join(",", received)}");
        Step("앞에 잡음 바이트 + 체크섬 깨진 프레임 + 정상 프레임이 한 번에 도착");
        var bad = BillAcceptorDriver.EscrowFrame(5000);
        bad[^1] ^= 0xFF;
        var burst = new byte[] { 0x55, 0xAA }.Concat(bad).Concat(BillAcceptorDriver.EscrowFrame(1000)).ToArray();
        e.BillAcceptor.Feed(burst);
        Show($"인식된 지폐: {string.Join(",", received)} / 버린 프레임·잡음 {e.BillAcceptor.RejectedFrames}건");
    }
}

/// <summary>키보드로 직접 눌러 보는 모드. WinForms 화면과 같은 KioskSession을 쓴다.</summary>
public static class Interactive
{
    public static void Run(Env e)
    {
        var s = new KioskSession(e.Repo, e.Clock);
        Console.WriteLine("병원 무인수납 키오스크 (가상 환자: 10000001/800512, 10000002/921103) — q 입력 시 종료");
        s.Start();
        while (true)
        {
            Console.WriteLine($"\n[{s.Screen}] {s.Message}");
            switch (s.Screen)
            {
                case Screen.Identify:
                {
                    var no = Ask("환자번호"); if (no is null) return;
                    var birth = Ask("생년월일 6자리"); if (birth is null) return;
                    s.Identify(no, birth);
                    break;
                }
                case Screen.Menu:
                {
                    var m = Ask("1) 수납  2) 제증명  3) 번호표  0) 처음으로"); if (m is null) return;
                    if (m == "1") s.ShowBills();
                    else if (m == "2") Cert(e, s);
                    else if (m == "3") { var (t, a) = e.Queue.Issue(QueueCategory.Payment); s.Finish($"번호표 {t.Display} 발행(앞 {a}명)"); e.FlushPrinter(); }
                    else s.Reset();
                    break;
                }
                case Screen.Bills:
                {
                    var bills = e.Repo.GetUnpaid(s.Patient!.PatientNo);
                    if (bills.Count == 0) { s.Finish("수납할 내역이 없습니다."); break; }
                    foreach (var b in bills) Console.WriteLine($"  [{(s.Selected.Contains(b.Id) ? "v" : " ")}] {b.Id} {b.VisitDate:MM-dd} {b.Department} {b.Total:N0}원");
                    var (_, total, err) = e.Payments.Prepare(s.Patient.PatientNo, s.Selected);
                    Console.WriteLine(err ?? $"  결제 금액 {total:N0}원");
                    var m = Ask("번호=선택 토글, p=카드결제, b=뒤로"); if (m is null) return;
                    if (m == "p")
                    {
                        var key = s.BeginPayment();
                        var r = e.Payments.PayByCard(s.Patient.PatientNo, s.Selected.ToList(), key);
                        s.EndPayment(r.Outcome == PayOutcome.Approved, r.Message);
                        e.FlushPrinter();
                    }
                    else if (m == "b") s.BackToMenu();
                    else if (long.TryParse(m, out var id)) s.Toggle(id);
                    break;
                }
                case Screen.Done:
                    if (Ask("엔터=처음으로") is null) return;
                    s.Reset();
                    s.Start();
                    break;
                default:
                    s.Start();
                    break;
            }
        }
    }

    static void Cert(Env e, KioskSession s)
    {
        var no = s.Patient!.PatientNo;
        var (fee, err) = e.Certificates.Request(no, "VISIT", e.Today, 1);
        if (err is not null) { s.Finish(err); return; }
        var r = e.Payments.PayByCard(no, new[] { fee!.Id }, Guid.NewGuid().ToString("N"));
        e.Printer.Printed.Clear();
        if (r.Outcome != PayOutcome.Approved) { s.Finish(r.Message); return; }
        var (issued, err2) = e.Certificates.Issue(no, "VISIT", e.Today, 1, fee.Id);
        e.FlushPrinter();
        s.Finish(err2 ?? $"진료확인서 발급 완료 ({issued[0].DocumentNo})");
    }

    static string? Ask(string prompt)
    {
        Console.Write($"  {prompt} > ");
        var line = Console.ReadLine();
        return line is null || line.Trim() == "q" ? null : line.Trim();
    }
}
