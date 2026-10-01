namespace Kiosk.Core.Devices;

/// <summary>
/// 시리얼(RS-232C) 장비와 주고받는 프레임.
/// 형식: STX(0x02) | CMD(1) | LEN(1) | DATA(LEN) | ETX(0x03) | BCC(1)
/// BCC = CMD부터 ETX까지 XOR. (STX는 계산에서 뺀다 — 실제 장비마다 범위가 다르므로 사양서로 반드시 확인)
/// </summary>
public sealed record SerialFrame(byte Command, byte[] Data)
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;

    public byte[] Encode()
    {
        if (Data.Length > 255) throw new ArgumentException("DATA는 255바이트를 넘을 수 없습니다.");
        var buf = new byte[Data.Length + 5];
        buf[0] = Stx;
        buf[1] = Command;
        buf[2] = (byte)Data.Length;
        Data.CopyTo(buf, 3);
        buf[3 + Data.Length] = Etx;
        buf[4 + Data.Length] = Bcc(buf.AsSpan(1, Data.Length + 3));
        return buf;
    }

    public static byte Bcc(ReadOnlySpan<byte> span)
    {
        byte x = 0;
        foreach (var b in span) x ^= b;
        return x;
    }
}

public enum FrameError { BadChecksum, BadEtx, Garbage }

/// <summary>
/// 바이트가 조각나서 들어와도 프레임을 복원한다. 시리얼 포트는 한 번에 한 프레임씩 읽힌다는 보장이 없다.
/// 잡음 바이트는 버리고 다음 STX부터 다시 찾는다.
/// </summary>
public sealed class SerialFrameParser
{
    private readonly List<byte> _buffer = new();

    public event Action<SerialFrame>? FrameReceived;
    public event Action<FrameError>? FrameRejected;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) _buffer.Add(b);
        while (TryParseOne()) { }
    }

    private bool TryParseOne()
    {
        int stx = _buffer.IndexOf(SerialFrame.Stx);
        if (stx < 0)
        {
            if (_buffer.Count > 0) { _buffer.Clear(); FrameRejected?.Invoke(FrameError.Garbage); }
            return false;
        }
        if (stx > 0) { _buffer.RemoveRange(0, stx); FrameRejected?.Invoke(FrameError.Garbage); }
        if (_buffer.Count < 3) return false;              // CMD, LEN 아직 안 옴
        int len = _buffer[2];
        int total = len + 5;
        if (_buffer.Count < total) return false;          // 나머지 아직 안 옴

        var frame = _buffer.GetRange(0, total).ToArray();
        if (frame[3 + len] != SerialFrame.Etx)
        {
            _buffer.RemoveAt(0);                           // 이 STX는 가짜였다. 다음 STX부터 다시
            FrameRejected?.Invoke(FrameError.BadEtx);
            return true;
        }
        _buffer.RemoveRange(0, total);
        byte expected = SerialFrame.Bcc(frame.AsSpan(1, len + 3));
        if (frame[4 + len] != expected)
        {
            FrameRejected?.Invoke(FrameError.BadChecksum);
            return true;
        }
        FrameReceived?.Invoke(new SerialFrame(frame[1], frame.AsSpan(3, len).ToArray()));
        return true;
    }
}
