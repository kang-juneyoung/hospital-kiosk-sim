namespace Kiosk.Core;

/// <summary>현재 시각을 주입받는다. 테스트에서 날짜 변경·세션 시간 초과를 재현하기 위함.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public sealed class ManualClock : IClock
{
    public ManualClock(DateTimeOffset start) => Now = start;
    public DateTimeOffset Now { get; private set; }
    public void Advance(TimeSpan by) => Now = Now.Add(by);
}
