using Kiosk.Core.Data;
using MySqlConnector;

namespace Kiosk.Data.MySql;

/// <summary>
/// MySQL 저장소를 만든다. 연결 문자열 예:
/// Server=localhost;Port=3306;Database=kiosk;User ID=kiosk;Password=...;
/// 연결은 요청마다 열고 닫는다(풀링은 드라이버가 해 준다).
/// </summary>
public static class MySqlKioskRepository
{
    public static IKioskRepository Create(string connectionString) =>
        new AdoKioskRepository(() => new MySqlConnection(connectionString));
}
