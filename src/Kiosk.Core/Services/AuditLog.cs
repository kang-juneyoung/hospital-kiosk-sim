using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kiosk.Core.Services;

/// <summary>
/// 감사 로그. 한 줄 = JSON 하나(JSONL). 개인정보(이름)는 남기지 않고 환자번호와 금액·결과만 남긴다.
/// 장애가 나면 이 로그로 "결제는 됐는데 수납이 안 됐다" 같은 상황을 거슬러 확인한다.
/// </summary>
public sealed class AuditLog
{
    // 한글·+ 기호를 \uXXXX로 바꾸지 않고 그대로 남긴다(로그 파일 전용, 웹 페이지에 넣지 않으므로 안전).
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly IClock _clock;
    private readonly string? _path;
    private readonly object _lock = new();
    public List<string> Lines { get; } = new();

    public AuditLog(IClock clock, string? path = null)
    {
        _clock = clock;
        _path = path;
    }

    public void Write(string evt, object data)
    {
        var line = JsonSerializer.Serialize(new { at = _clock.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"), evt, data }, Json);
        lock (_lock)
        {
            Lines.Add(line);
            if (_path is not null) File.AppendAllText(_path, line + Environment.NewLine);
        }
    }
}
