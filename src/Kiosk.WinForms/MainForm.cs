using Kiosk.Core;
using Kiosk.Core.Data;
using Kiosk.Core.Devices;
using Kiosk.Core.Domain;
using Kiosk.Core.Flow;
using Kiosk.Core.Services;
using KioskScreen = Kiosk.Core.Flow.Screen;   // System.Windows.Forms.Screen과 이름이 겹쳐 별칭 사용

namespace Kiosk.WinForms;

/// <summary>
/// 키오스크 화면(왼쪽)과 장비 시뮬레이터(오른쪽).
/// 화면은 KioskSession의 상태만 보고 그린다. 업무 규칙은 전부 Kiosk.Core에 있다.
/// 디자이너 파일 없이 코드로 화면을 만든다(리뷰·diff가 쉽도록).
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Font Big = new("맑은 고딕", 20F, FontStyle.Bold);
    private static readonly Font Normal = new("맑은 고딕", 13F);
    private static readonly Font Small = new("맑은 고딕", 10F);

    private readonly IKioskRepository _repo;
    private readonly InMemoryKioskRepository? _memory;
    private readonly IClock _clock;
    private readonly KioskSession _session;
    private readonly SimulatedCardTerminal _card = new();
    private readonly ConsolePrinter _printer = new();
    private readonly SimulatedChangeDispenser _dispenser = new();
    private readonly BillAcceptorDriver _acceptor;
    private readonly AuditLog _audit;
    private readonly PaymentService _payments;
    private readonly CertificateService _certificates;
    private readonly QueueService _queue;
    private readonly WaitingBoardService _board;
    private CashPaymentSession? _cash;
    private Label? _cashStatus;

    // 왼쪽: 키오스크 화면
    private readonly Label _message = new() { Dock = DockStyle.Top, Height = 70, Font = Big, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Panel _screen = new() { Dock = DockStyle.Fill, Padding = new Padding(20) };

    // 오른쪽: 장비 시뮬레이터·출력물
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F) };
    private readonly ComboBox _nextCard = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly CheckBox _paperOut = new() { Text = "프린터 용지 없음", AutoSize = true };
    private readonly CheckBox _dbFail = new() { Text = "다음 수납 저장 실패", AutoSize = true };
    private readonly ListBox _boardList = new() { Dock = DockStyle.Fill, Font = Small };

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    public MainForm(IKioskRepository repo, InMemoryKioskRepository? memory, IClock clock)
    {
        _repo = repo;
        _memory = memory;
        _clock = clock;
        _audit = new AuditLog(clock, Path.Combine(AppContext.BaseDirectory, "audit.jsonl"));
        _acceptor = new BillAcceptorDriver(_ => { });          // 실제 장비라면 SerialPort.Write
        _payments = new PaymentService(repo, _card, _printer, _audit);
        _certificates = new CertificateService(repo, _printer, clock, _audit);
        _queue = new QueueService(repo, _printer, clock);
        _board = new WaitingBoardService(repo);
        _session = new KioskSession(repo, clock);
        _session.Changed += Render;

        Text = "병원 무인수납 키오스크 시뮬레이터";
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Normal;

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_screen);
        split.Panel1.Controls.Add(_message);
        split.Panel2.Controls.Add(BuildSimulatorPanel());
        Controls.Add(split);
        // 폼 크기가 정해진 뒤에 분할 위치를 정한다(생성 직후에 넣으면 범위 오류가 날 수 있음)
        Load += (_, _) => split.SplitterDistance = 820;

        // 화면 어디를 눌러도 '사용 중'으로 본다(시간 초과 연장)
        _screen.Click += (_, _) => _session.Touch();
        _tick.Tick += (_, _) => { _session.Tick(); RefreshBoard(); };
        _tick.Start();

        Render();
    }

    // ───────────── 오른쪽: 시뮬레이터 ─────────────
    private Control BuildSimulatorPanel()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 70));

        var devices = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, Font = Small };
        devices.Controls.Add(new Label { Text = "■ 장비 시뮬레이터", AutoSize = true, Font = new Font(Small, FontStyle.Bold) });
        _nextCard.Items.AddRange(new object[] { "카드: 승인", "카드: 거절", "카드: 응답 없음" });
        _nextCard.SelectedIndex = 0;
        devices.Controls.Add(_nextCard);
        devices.Controls.Add(_paperOut);
        _dbFail.Enabled = _memory is not null;
        devices.Controls.Add(_dbFail);
        var call = new Button { Text = "수납 창구 호출", AutoSize = true };
        call.Click += (_, _) => Log($"[창구 호출] {_queue.CallNext(QueueCategory.Payment) ?? "대기자 없음"}");
        devices.Controls.Add(call);
        root.Controls.Add(devices, 0, 0);

        var boardBox = new GroupBox { Text = "진료대기 현황(정형외과)", Dock = DockStyle.Fill, Font = Small };
        boardBox.Controls.Add(_boardList);
        root.Controls.Add(boardBox, 0, 1);

        root.Controls.Add(new Label { Text = "■ 출력물 · 이벤트", AutoSize = true, Font = new Font(Small, FontStyle.Bold) }, 0, 2);
        root.Controls.Add(_output, 0, 3);
        return root;
    }

    private void ApplySimulatorSettings()
    {
        _card.NextResults.Clear();
        _card.NextResults.Enqueue(_nextCard.SelectedIndex switch
        {
            1 => CardResultKind.Declined,
            2 => CardResultKind.Timeout,
            _ => CardResultKind.Approved,
        });
        _printer.PaperOut = _paperOut.Checked;
        if (_memory is not null && _dbFail.Checked)
        {
            _memory.FailNextCompletePayment = true;
            _dbFail.Checked = false;
        }
    }

    private void FlushPrinter()
    {
        foreach (var p in _printer.Printed) Log(p);
        _printer.Printed.Clear();
    }

    private void Log(string text) => _output.AppendText(text.Replace("\n", Environment.NewLine) + Environment.NewLine);

    private void RefreshBoard()
    {
        _boardList.BeginUpdate();
        _boardList.Items.Clear();
        foreach (var l in _board.Board("정형외과")) _boardList.Items.Add(l);
        _boardList.Items.Add($"수납 대기 {_queue.Waiting(QueueCategory.Payment)}명");
        _boardList.EndUpdate();
    }

    // ───────────── 왼쪽: 화면 그리기 ─────────────
    private void Render()
    {
        _message.Text = _session.Message;
        _screen.SuspendLayout();
        _screen.Controls.Clear();
        switch (_session.Screen)
        {
            case KioskScreen.Idle: RenderIdle(); break;
            case KioskScreen.Identify: RenderIdentify(); break;
            case KioskScreen.Menu: RenderMenu(); break;
            case KioskScreen.Bills: RenderBills(); break;
            case KioskScreen.Paying: RenderPaying(); break;
            case KioskScreen.Done: RenderDone(); break;
        }
        _screen.ResumeLayout();
    }

    private Button BigButton(string text, EventHandler onClick, int width = 340, int height = 90)
    {
        var b = new Button { Text = text, Font = Big, Width = width, Height = height, Margin = new Padding(12) };
        b.Click += (s, e) => { _session.Touch(); onClick(s, e); };
        return b;
    }

    private FlowLayoutPanel Flow(FlowDirection dir = FlowDirection.TopDown) =>
        new() { Dock = DockStyle.Fill, FlowDirection = dir, WrapContents = true, AutoScroll = true };

    private void RenderIdle()
    {
        var f = Flow();
        f.Controls.Add(BigButton("화면을 눌러 시작", (_, _) => _session.Start(), 600, 200));
        f.Controls.Add(BigButton("수납 번호표 뽑기", (_, _) => IssueTicket(), 600, 100));
        _screen.Controls.Add(f);
    }

    private void IssueTicket()
    {
        var (t, ahead) = _queue.Issue(QueueCategory.Payment);
        FlushPrinter();
        _session.Finish($"번호표 {t.Display} — 앞에 {ahead}명");
    }

    private void RenderIdentify()
    {
        var no = new TextBox { Font = Big, Width = 300, PlaceholderText = "환자번호" };
        var birth = new TextBox { Font = Big, Width = 300, PlaceholderText = "생년월일 6자리", UseSystemPasswordChar = true, MaxLength = 6 };
        TextBox target = no;
        no.Enter += (_, _) => target = no;
        birth.Enter += (_, _) => target = birth;

        var pad = new TableLayoutPanel { ColumnCount = 3, RowCount = 4, AutoSize = true };
        string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "지움", "0", "확인" };
        for (int i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            var b = new Button { Text = k, Font = Big, Width = 110, Height = 80 };
            b.Click += (_, _) =>
            {
                _session.Touch();
                if (k == "지움") { if (target.Text.Length > 0) target.Text = target.Text[..^1]; }
                else if (k == "확인") _session.Identify(no.Text, birth.Text);
                else if (target.Text.Length < target.MaxLength) target.Text += k;
            };
            pad.Controls.Add(b, i % 3, i / 3);
        }

        var f = Flow();
        f.Controls.Add(no);
        f.Controls.Add(birth);
        f.Controls.Add(pad);
        f.Controls.Add(new Label { Text = "가상 환자: 10000001 / 800512, 10000002 / 921103", AutoSize = true, Font = Small });
        f.Controls.Add(BigButton("처음으로", (_, _) => _session.Reset(), 200, 60));
        _screen.Controls.Add(f);
        no.Focus();
    }

    private void RenderMenu()
    {
        var f = Flow(FlowDirection.LeftToRight);
        f.Controls.Add(BigButton("진료비 수납", (_, _) => _session.ShowBills()));
        f.Controls.Add(BigButton("제증명 발급", (_, _) => RenderCertificate()));
        f.Controls.Add(BigButton("번호표", (_, _) => IssueTicket()));
        f.Controls.Add(BigButton("처음으로", (_, _) => _session.Reset()));
        _screen.Controls.Add(f);
    }

    private void RenderBills()
    {
        var patientNo = _session.Patient!.PatientNo;
        var bills = _repo.GetUnpaid(patientNo);
        var list = new CheckedListBox { Width = 740, Height = 260, Font = Normal, CheckOnClick = true };
        foreach (var b in bills)
        {
            int idx = list.Items.Add(new BillItem(b));
            list.SetItemChecked(idx, _session.Selected.Contains(b.Id));
        }
        var total = new Label { Font = Big, AutoSize = true };
        void UpdateTotal()
        {
            var (_, sum, err) = _payments.Prepare(patientNo, _session.Selected);
            total.Text = err ?? $"결제 금액 {sum:N0}원";
        }
        list.ItemCheck += (_, e) =>
        {
            var item = (BillItem)list.Items[e.Index];
            bool willCheck = e.NewValue == CheckState.Checked;
            if (willCheck != _session.Selected.Contains(item.Bill.Id)) _session.Toggle(item.Bill.Id);
            UpdateTotal();
        };
        UpdateTotal();

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(BigButton("카드 결제", async (_, _) => await PayByCard(patientNo), 230, 80));
        buttons.Controls.Add(BigButton("현금 결제", (_, _) => StartCash(patientNo), 230, 80));
        buttons.Controls.Add(BigButton("뒤로", (_, _) => _session.BackToMenu(), 230, 80));

        var f = Flow();
        f.Controls.Add(list);
        f.Controls.Add(total);
        f.Controls.Add(buttons);
        _screen.Controls.Add(f);
    }

    private async Task PayByCard(string patientNo)
    {
        ApplySimulatorSettings();
        var ids = _session.Selected.ToList();
        var key = _session.BeginPayment();
        // 단말기 응답을 기다리는 동안 화면이 멈추지 않게 백그라운드에서 처리
        var r = await Task.Run(() => _payments.PayByCard(patientNo, ids, key));
        FlushPrinter();
        Log($"[결제] {r.Outcome} {r.Message}");
        _session.EndPayment(r.Outcome == PayOutcome.Approved, r.Message);
    }

    private void StartCash(string patientNo)
    {
        ApplySimulatorSettings();
        var ids = _session.Selected.ToList();
        var key = _session.BeginPayment();
        var (cash, err) = CashPaymentSession.Start(_repo, _payments, _acceptor, _dispenser, _printer, _audit, patientNo, ids, key);
        if (cash is null) { _session.EndPayment(false, err!); return; }
        _cash = cash;
        cash.Changed += OnCashChanged;          // 한 번만 구독(화면을 다시 그려도 중복되지 않게)
        RenderCash();
    }

    private void OnCashChanged(CashPaymentSession cash)
    {
        UpdateCashStatus(cash);
        FlushPrinter();
        if (cash.State != CashState.Collecting) FinishCash(cash);
    }

    private void UpdateCashStatus(CashPaymentSession cash)
    {
        if (_cashStatus is not null)
            _cashStatus.Text = $"결제 금액 {cash.Total:N0}원 / 넣은 금액 {cash.Received:N0}원 / 남은 금액 {cash.Remaining:N0}원";
        _message.Text = string.IsNullOrEmpty(cash.LastMessage) ? "지폐를 넣어 주세요." : cash.LastMessage;
    }

    private void RenderPaying()
    {
        if (_cash is not null) { RenderCash(); return; }
        var f = Flow();
        f.Controls.Add(new Label { Text = "카드 단말기에 카드를 꽂아 주세요.", Font = Big, AutoSize = true });
        _screen.Controls.Add(f);
    }

    private void RenderCash()
    {
        var cash = _cash!;
        _screen.Controls.Clear();
        _cashStatus = new Label { Font = Normal, AutoSize = true };
        UpdateCashStatus(cash);

        var notes = new FlowLayoutPanel { AutoSize = true };
        foreach (var note in new long[] { 1000, 5000, 10000, 50000, 2000 })
        {
            // 실제로는 지폐 인식기가 RS-232C로 보내는 프레임. 여기서는 버튼이 그 바이트를 만들어 드라이버에 넣는다.
            var b = new Button { Text = $"{note:N0}원 투입", Width = 150, Height = 60, Font = Normal };
            b.Click += (_, _) => { _session.Touch(); _acceptor.Feed(BillAcceptorDriver.EscrowFrame(note)); };
            notes.Controls.Add(b);
        }

        var f = Flow();
        f.Controls.Add(_cashStatus);
        f.Controls.Add(new Label { Text = "▼ 지폐 투입 시뮬레이터 (2,000원권은 거부 대상)", AutoSize = true, Font = Small });
        f.Controls.Add(notes);
        f.Controls.Add(BigButton("결제 취소", (_, _) => cash.Cancel(), 230, 80));
        _screen.Controls.Add(f);
    }

    private void FinishCash(CashPaymentSession cash)
    {
        cash.Changed -= OnCashChanged;
        cash.Detach();
        _cash = null;
        _cashStatus = null;
        Log($"[현금] {cash.State} {cash.LastMessage}");
        if (cash.State == CashState.Completed) _session.EndPayment(true, cash.LastMessage);
        else if (cash.State == CashState.NeedsStaff) _session.Finish(cash.LastMessage);
        else _session.EndPayment(false, cash.LastMessage);
    }

    private void RenderCertificate()
    {
        var patientNo = _session.Patient!.PatientNo;
        _screen.Controls.Clear();
        _message.Text = "발급할 서류를 선택하세요.";
        var type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, Font = Normal };
        foreach (var t in CertificateService.Types) type.Items.Add($"{t.Code} · {t.Name} ({t.Fee:N0}원)");
        type.SelectedIndex = 1;
        var date = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 200, Font = Normal, Value = _clock.Now.Date };
        var copies = new NumericUpDown { Minimum = 1, Maximum = CertificateService.MaxCopies, Value = 1, Width = 80, Font = Normal };

        var f = Flow();
        f.Controls.Add(new Label { Text = "서류", AutoSize = true });
        f.Controls.Add(type);
        f.Controls.Add(new Label { Text = "진료일", AutoSize = true });
        f.Controls.Add(date);
        f.Controls.Add(new Label { Text = "매수", AutoSize = true });
        f.Controls.Add(copies);
        f.Controls.Add(BigButton("신청 · 카드 결제", async (_, _) =>
        {
            var code = CertificateService.Types[type.SelectedIndex].Code;
            await IssueCertificate(patientNo, code, DateOnly.FromDateTime(date.Value), (int)copies.Value);
        }, 360, 80));
        f.Controls.Add(BigButton("뒤로", (_, _) => _session.BackToMenu(), 200, 80));
        _screen.Controls.Add(f);
    }

    private async Task IssueCertificate(string patientNo, string code, DateOnly visitDate, int copies)
    {
        var (fee, err) = _certificates.Request(patientNo, code, visitDate, copies);
        if (err is not null) { _message.Text = err; return; }
        if (fee is not null)
        {
            ApplySimulatorSettings();
            var key = _session.BeginPayment();
            var r = await Task.Run(() => _payments.PayByCard(patientNo, new[] { fee.Id }, key));
            _printer.Printed.Clear();                 // 발급비 영수증은 생략하고 증명서만 보여 준다
            Log($"[발급비] {r.Outcome} {r.Message}");
            if (r.Outcome != PayOutcome.Approved) { _session.EndPayment(false, r.Message); _session.BackToMenu(); return; }
        }
        var (issued, issueErr) = _certificates.Issue(patientNo, code, visitDate, copies, fee?.Id);
        FlushPrinter();
        _session.Finish(issueErr ?? $"발급 완료: {string.Join(", ", issued.Select(i => i.DocumentNo))}");
    }

    private void RenderDone()
    {
        var f = Flow();
        f.Controls.Add(new Label { Text = "10초 후 처음 화면으로 돌아갑니다.", AutoSize = true });
        f.Controls.Add(BigButton("처음으로", (_, _) => _session.Reset(), 300, 90));
        _screen.Controls.Add(f);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Stop();
        _cash?.Detach();
        base.OnFormClosed(e);
    }

    /// <summary>CheckedListBox에 표시할 수납 건.</summary>
    private sealed record BillItem(Receivable Bill)
    {
        public override string ToString() => $"{Bill.VisitDate:MM-dd}  {Bill.Department}  {Bill.Total:N0}원";
    }
}
