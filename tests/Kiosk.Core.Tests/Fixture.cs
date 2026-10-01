using Kiosk.Core;
using Kiosk.Core.Data;
using Kiosk.Core.Devices;
using Kiosk.Core.Domain;
using Kiosk.Core.Services;

namespace Kiosk.Core.Tests;

/// <summary>테스트마다 새로 만드는 가상 환경.</summary>
internal sealed class Fixture
{
    public const string Hong = "10000001";
    public const string Kim = "10000002";

    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(9)));
    public InMemoryKioskRepository Repo { get; } = new();
    public SimulatedCardTerminal Card { get; } = new();
    public ConsolePrinter Printer { get; } = new();
    public SimulatedChangeDispenser Dispenser { get; } = new();
    public List<byte[]> Sent { get; } = new();
    public BillAcceptorDriver Acceptor { get; }
    public AuditLog Audit { get; }
    public PaymentService Payments { get; }
    public DateOnly Today => DateOnly.FromDateTime(Clock.Now.Date);

    public Fixture()
    {
        SampleData.Seed(Repo, Today);
        Audit = new AuditLog(Clock);
        Acceptor = new BillAcceptorDriver(b => Sent.Add(b));
        Payments = new PaymentService(Repo, Card, Printer, Audit);
    }

    public List<long> UnpaidIds(string patientNo) => Repo.GetUnpaid(patientNo).Select(r => r.Id).ToList();
}
