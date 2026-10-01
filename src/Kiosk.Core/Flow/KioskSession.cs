using Kiosk.Core.Data;
using Kiosk.Core.Domain;

namespace Kiosk.Core.Flow;

public enum Screen { Idle, Identify, Menu, Bills, Paying, Done }

/// <summary>
/// 키오스크 화면 흐름(상태 머신). WinForms·콘솔 화면은 이 클래스의 상태만 그린다.
/// 화면 코드에 업무 규칙을 두지 않으면, 규칙은 단위 테스트로 검증하고 화면은 얇게 유지할 수 있다.
///  - 아무 입력 없이 일정 시간이 지나면 처음 화면으로 돌아가고 환자 정보를 지운다(다음 사람이 보지 못하게).
///  - 결제 중에는 시간 초과로 끊지 않는다(단말기 응답을 기다리는 중일 수 있음).
///  - 본인 확인 3회 실패 시 잠시 잠근다.
/// </summary>
public sealed class KioskSession
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DoneTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LockTime = TimeSpan.FromSeconds(30);
    public const int MaxIdentifyFailures = 3;

    private readonly IKioskRepository _repo;
    private readonly IClock _clock;
    private DateTimeOffset _lastTouch;
    private int _failures;
    private DateTimeOffset? _lockedUntil;

    public Screen Screen { get; private set; } = Screen.Idle;
    public Patient? Patient { get; private set; }
    public HashSet<long> Selected { get; } = new();
    public string Message { get; private set; } = "화면을 눌러 시작하세요.";
    /// <summary>결제 시도마다 새로 만든다. 같은 시도의 재전송은 같은 키 → 이중 결제 방지.</summary>
    public string? PaymentKey { get; private set; }

    public event Action? Changed;

    public KioskSession(IKioskRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
        _lastTouch = clock.Now;
    }

    public void Touch() => _lastTouch = _clock.Now;

    public void Start()
    {
        Touch();
        Go(Screen.Identify, "환자번호와 생년월일 6자리를 입력하세요.");
    }

    public bool Identify(string patientNo, string birthYyMMdd)
    {
        Touch();
        if (_lockedUntil is { } until && _clock.Now < until)
        {
            Go(Screen.Identify, $"잠시 후 다시 시도하세요. ({(int)(until - _clock.Now).TotalSeconds}초)");
            return false;
        }
        var p = _repo.FindPatient(patientNo.Trim());
        if (p is null || p.BirthDate.ToString("yyMMdd") != birthYyMMdd.Trim())
        {
            _failures++;
            if (_failures >= MaxIdentifyFailures)
            {
                _lockedUntil = _clock.Now + LockTime;
                _failures = 0;
                Go(Screen.Identify, "입력이 여러 번 틀려 30초간 잠깁니다. 원무과 창구로 문의하세요.");
            }
            else Go(Screen.Identify, "환자 정보를 찾을 수 없습니다. 다시 입력하세요.");
            return false;
        }
        _failures = 0;
        Patient = p;
        Go(Screen.Menu, $"{Privacy.MaskName(p.Name)} 님, 원하시는 업무를 선택하세요.");
        return true;
    }

    public IReadOnlyList<Receivable> ShowBills()
    {
        Touch();
        RequirePatient();
        var bills = _repo.GetUnpaid(Patient!.PatientNo);
        Selected.Clear();
        foreach (var b in bills) Selected.Add(b.Id);   // 기본은 전체 선택
        Go(Screen.Bills, bills.Count == 0 ? "수납할 내역이 없습니다." : "수납할 내역을 확인하세요.");
        return bills;
    }

    public void Toggle(long receivableId)
    {
        Touch();
        if (!Selected.Remove(receivableId)) Selected.Add(receivableId);
        Changed?.Invoke();
    }

    public string BeginPayment()
    {
        Touch();
        RequirePatient();
        PaymentKey = Guid.NewGuid().ToString("N");
        Go(Screen.Paying, "결제 중입니다. 카드를 빼지 마세요.");
        return PaymentKey;
    }

    public void EndPayment(bool success, string message)
    {
        Touch();
        if (success) Go(Screen.Done, message);
        else Go(Screen.Bills, message);
    }

    public void BackToMenu()
    {
        Touch();
        RequirePatient();
        Go(Screen.Menu, "원하시는 업무를 선택하세요.");
    }

    public void Finish(string message)
    {
        Touch();
        Go(Screen.Done, message);
    }

    /// <summary>타이머가 주기적으로 부른다. 시간 초과면 처음으로.</summary>
    public void Tick()
    {
        if (Screen is Screen.Idle or Screen.Paying) return;
        var limit = Screen == Screen.Done ? DoneTimeout : IdleTimeout;
        if (_clock.Now - _lastTouch >= limit) Reset("화면을 눌러 시작하세요.");
    }

    public void Reset(string message = "화면을 눌러 시작하세요.")
    {
        Patient = null;
        Selected.Clear();
        PaymentKey = null;
        Go(Screen.Idle, message);
    }

    private void RequirePatient()
    {
        if (Patient is null) throw new InvalidOperationException("본인 확인 전입니다.");
    }

    private void Go(Screen s, string msg)
    {
        Screen = s;
        Message = msg;
        Changed?.Invoke();
    }
}
