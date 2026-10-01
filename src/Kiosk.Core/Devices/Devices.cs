using System.Text;

namespace Kiosk.Core.Devices;

// ── 카드 단말기 ─────────────────────────────────────────────

public enum CardResultKind { Approved, Declined, Timeout }

public sealed record CardResult(CardResultKind Kind, string? ApprovalNo = null, string? Message = null);

public interface ICardTerminal
{
    /// <summary>승인 요청. 응답이 없으면 Timeout — 이때 승인이 됐는지 알 수 없으므로 망취소해야 한다.</summary>
    CardResult Approve(string transactionKey, long amount);

    /// <summary>승인 취소(망취소 포함). transactionKey로 취소하므로 승인번호를 몰라도 된다.</summary>
    bool Cancel(string transactionKey, long amount);
}

/// <summary>시연·테스트용 카드 단말기. 다음 응답을 미리 정해 둘 수 있다.</summary>
public sealed class SimulatedCardTerminal : ICardTerminal
{
    private int _seq = 1000;
    public Queue<CardResultKind> NextResults { get; } = new();
    public List<string> CancelledKeys { get; } = new();
    public List<(string Key, long Amount)> Approved { get; } = new();

    public CardResult Approve(string transactionKey, long amount)
    {
        var kind = NextResults.Count > 0 ? NextResults.Dequeue() : CardResultKind.Approved;
        switch (kind)
        {
            case CardResultKind.Approved:
                Approved.Add((transactionKey, amount));
                return new CardResult(kind, ApprovalNo: $"{++_seq:00000000}");
            case CardResultKind.Declined:
                return new CardResult(kind, Message: "한도 초과");
            default:
                // 응답은 못 받았지만 단말기 쪽에서는 승인됐을 수도 있는 상황을 흉내 낸다.
                Approved.Add((transactionKey, amount));
                return new CardResult(kind, Message: "단말기 응답 없음");
        }
    }

    public bool Cancel(string transactionKey, long amount)
    {
        CancelledKeys.Add(transactionKey);
        Approved.RemoveAll(a => a.Key == transactionKey);
        return true;
    }
}

// ── 지폐 인식기 (RS-232C) ───────────────────────────────────

/// <summary>지폐 인식기 명령 코드. 실제 장비 사양서의 코드는 장비마다 다르다.</summary>
public static class BillCmd
{
    public const byte Escrowed = (byte)'N';   // 장비→PC: 지폐가 임시 보관(에스크로)에 들어옴. DATA=금액(ASCII)
    public const byte Error = (byte)'E';      // 장비→PC: 오류. DATA=오류 코드(ASCII)
    public const byte Stack = (byte)'S';      // PC→장비: 보관함으로 넣기
    public const byte Return = (byte)'R';     // PC→장비: 지폐 반환
}

/// <summary>
/// 지폐 인식기 드라이버. 포트에서 읽은 바이트를 넣어 주면(Feed) 프레임을 해석해 이벤트로 올린다.
/// 실제 장비에서는 System.IO.Ports.SerialPort의 DataReceived에서 Feed를 호출한다.
/// </summary>
public sealed class BillAcceptorDriver
{
    private readonly SerialFrameParser _parser = new();
    private readonly Action<byte[]> _write;

    public event Action<long>? NoteEscrowed;
    public event Action<string>? DeviceError;
    public int RejectedFrames { get; private set; }

    public BillAcceptorDriver(Action<byte[]> write)
    {
        _write = write;
        _parser.FrameReceived += OnFrame;
        _parser.FrameRejected += _ => RejectedFrames++;
    }

    public void Feed(ReadOnlySpan<byte> bytes) => _parser.Feed(bytes);

    public void StackNote() => _write(new SerialFrame(BillCmd.Stack, Array.Empty<byte>()).Encode());
    public void ReturnNote() => _write(new SerialFrame(BillCmd.Return, Array.Empty<byte>()).Encode());

    private void OnFrame(SerialFrame f)
    {
        var text = Encoding.ASCII.GetString(f.Data);
        if (f.Command == BillCmd.Escrowed && long.TryParse(text, out var amount)) NoteEscrowed?.Invoke(amount);
        else if (f.Command == BillCmd.Error) DeviceError?.Invoke(text);
        else RejectedFrames++;
    }

    /// <summary>장비가 보낼 '지폐 들어옴' 프레임(시뮬레이터·테스트용).</summary>
    public static byte[] EscrowFrame(long amount) =>
        new SerialFrame(BillCmd.Escrowed, Encoding.ASCII.GetBytes(amount.ToString())).Encode();
}

// ── 영수증·제증명 프린터 ────────────────────────────────────

public interface IReceiptPrinter
{
    /// <summary>출력 실패(용지 없음 등)면 false. 결제는 되돌리지 않고 재출력 대상으로 남긴다.</summary>
    bool Print(string title, IReadOnlyList<string> lines);
}

public sealed class ConsolePrinter : IReceiptPrinter
{
    public bool PaperOut { get; set; }
    public List<string> Printed { get; } = new();

    public bool Print(string title, IReadOnlyList<string> lines)
    {
        if (PaperOut) return false;
        var sb = new StringBuilder();
        sb.AppendLine($"┌─ {title}");
        foreach (var l in lines) sb.AppendLine($"│ {l}");
        sb.Append("└─────────────");
        Printed.Add(sb.ToString());
        return true;
    }
}
