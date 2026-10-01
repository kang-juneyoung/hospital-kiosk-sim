using Kiosk.Core.Domain;

namespace Kiosk.Core.Data;

/// <summary>시연용 가상 데이터. 실제 환자·병원 정보가 아니다.</summary>
public static class SampleData
{
    public static void Seed(InMemoryKioskRepository repo, DateOnly today)
    {
        repo.AddPatient(new Patient("10000001", "홍길동", new DateOnly(1980, 5, 12), "1234"));
        repo.AddPatient(new Patient("10000002", "김영희", new DateOnly(1992, 11, 3), "5678"));
        repo.AddPatient(new Patient("10000003", "이수", new DateOnly(1975, 1, 30), "9012"));

        var yesterday = today.AddDays(-1);
        repo.AddVisit("10000001", today);
        repo.AddVisit("10000001", yesterday);
        repo.AddVisit("10000002", today);

        // 홍길동: 오늘 내과 + 어제 영상의학과 미수납
        repo.CreateReceivable("10000001", ReceivableKind.Treatment, "내과", today, new[]
        {
            new ReceivableItem("AA157", "재진 진찰료", 12_800, 1),
            new ReceivableItem("B1010", "일반혈액검사", 4_300, 1),
            new ReceivableItem("C5211", "주사료", 2_150, 2),
        });
        repo.CreateReceivable("10000001", ReceivableKind.Treatment, "영상의학과", yesterday, new[]
        {
            new ReceivableItem("G2101", "흉부 X-ray", 9_600, 1),
        });
        // 김영희: 오늘 정형외과
        repo.CreateReceivable("10000002", ReceivableKind.Treatment, "정형외과", today, new[]
        {
            new ReceivableItem("AA154", "초진 진찰료", 18_700, 1),
            new ReceivableItem("MM101", "물리치료", 3_200, 3),
        });

        var t = new DateTimeOffset(today.ToDateTime(new TimeOnly(9, 0)));
        repo.AddWaiting(new WaitingEntry("10000002", "김영희", "정형외과", "박의사", t.AddMinutes(5), WaitingStatus.InTreatment));
        repo.AddWaiting(new WaitingEntry("10000003", "이수", "정형외과", "박의사", t.AddMinutes(12), WaitingStatus.Waiting));
        repo.AddWaiting(new WaitingEntry("10000001", "홍길동", "정형외과", "박의사", t.AddMinutes(20), WaitingStatus.Waiting));
    }
}
