using Kiosk.Core;
using Kiosk.Core.Data;
using Kiosk.Data.MySql;

namespace Kiosk.WinForms;

internal static class Program
{
    /// <summary>
    /// 환경변수 KIOSK_DB에 MySQL 연결 문자열이 있으면 MySQL을, 없으면 가상 데이터(메모리)를 쓴다.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var clock = new SystemClock();
        var cs = Environment.GetEnvironmentVariable("KIOSK_DB");
        IKioskRepository repo;
        InMemoryKioskRepository? memory = null;
        if (string.IsNullOrWhiteSpace(cs))
        {
            memory = new InMemoryKioskRepository();
            SampleData.Seed(memory, DateOnly.FromDateTime(DateTime.Today));
            repo = memory;
        }
        else
        {
            repo = MySqlKioskRepository.Create(cs);
        }
        Application.Run(new MainForm(repo, memory, clock));
    }
}
