using Kiosk.Core.Devices;
using Kiosk.Core.Domain;
using Kiosk.Core.Flow;
using Kiosk.Core.Services;
using Xunit;

namespace Kiosk.Core.Tests;

public class SerialFrameTests
{
    [Fact]
    public void BCC는_CMD부터_ETX까지_XOR이다()
    {
        var frame = new SerialFrame((byte)'N', "10000"u8.ToArray()).Encode();
        // 02 4E 05 31 30 30 30 30 03 | BCC
        Assert.Equal(0x02, frame[0]);
        Assert.Equal(0x03, frame[^2]);
        Assert.Equal(0x79, frame[^1]);
    }

    [Fact]
    public void 조각난_바이트도_한_프레임으로_복원한다()
    {
        var parser = new SerialFrameParser();
        var got = new List<SerialFrame>();
        parser.FrameReceived += got.Add;
        var bytes = new SerialFrame(0x41, new byte[] { 1, 2, 3 }).Encode();

        foreach (var b in bytes) parser.Feed(new[] { b });   // 1바이트씩

        Assert.Single(got);
        Assert.Equal(new byte[] { 1, 2, 3 }, got[0].Data);
    }

    [Fact]
    public void 체크섬이_틀린_프레임은_버리고_다음_프레임은_받는다()
    {
        var parser = new SerialFrameParser();
        var got = new List<SerialFrame>();
        var errors = new List<FrameError>();
        parser.FrameReceived += got.Add;
        parser.FrameRejected += errors.Add;
        var bad = new SerialFrame(0x41, new byte[] { 9 }).Encode();
        bad[^1] ^= 0xFF;
        var good = new SerialFrame(0x42, new byte[] { 7 }).Encode();

        parser.Feed(new byte[] { 0x55 }.Concat(bad).Concat(good).ToArray());

        Assert.Single(got);
        Assert.Equal(0x42, got[0].Command);
        Assert.Contains(FrameError.BadChecksum, errors);
        Assert.Contains(FrameError.Garbage, errors);
    }
}

public class CertificateAndQueueTests
{
    [Fact]
    public void 유료_증명서는_발급비_수납_전에는_발급되지_않는다()
    {
        var f = new Fixture();
        var svc = new CertificateService(f.Repo, f.Printer, f.Clock, f.Audit);
        var (fee, _) = svc.Request(Fixture.Hong, "VISIT", f.Today, 2);

        var before = svc.Issue(Fixture.Hong, "VISIT", f.Today, 2, fee!.Id);
        f.Payments.PayByCard(Fixture.Hong, new[] { fee.Id }, "CERT-FEE");
        var after = svc.Issue(Fixture.Hong, "VISIT", f.Today, 2, fee.Id);

        Assert.NotNull(before.Error);
        Assert.Equal(2_000, fee.Total);
        Assert.Null(after.Error);
        Assert.Equal(new[] { "CERT-20261001-0001", "CERT-20261001-0002" }, after.Issued.Select(i => i.DocumentNo));
    }

    [Fact]
    public void 진료기록이_없는_날짜는_신청할_수_없다()
    {
        var f = new Fixture();
        var svc = new CertificateService(f.Repo, f.Printer, f.Clock, f.Audit);

        var (_, error) = svc.Request(Fixture.Hong, "VISIT", f.Today.AddDays(-30), 1);

        Assert.NotNull(error);
    }

    [Fact]
    public void 번호표는_종류별로_따로_세고_날짜가_바뀌면_1번부터()
    {
        var f = new Fixture();
        var q = new QueueService(f.Repo, f.Printer, f.Clock);

        q.Issue(QueueCategory.Payment);
        var second = q.Issue(QueueCategory.Payment);
        var cert = q.Issue(QueueCategory.Certificate);
        f.Clock.Advance(TimeSpan.FromDays(1));
        var nextDay = q.Issue(QueueCategory.Payment);

        Assert.Equal("A-002", second.Ticket.Display);
        Assert.Equal(1, second.WaitingAhead);
        Assert.Equal("C-001", cert.Ticket.Display);
        Assert.Equal("A-001", nextDay.Ticket.Display);
    }

    [Fact]
    public void 호출은_발행된_번호를_넘지_않는다()
    {
        var f = new Fixture();
        var q = new QueueService(f.Repo, f.Printer, f.Clock);
        q.Issue(QueueCategory.Payment);

        Assert.Equal("A-001", q.CallNext(QueueCategory.Payment));
        Assert.Null(q.CallNext(QueueCategory.Payment));
        Assert.Equal(0, q.Waiting(QueueCategory.Payment));
    }

    [Fact]
    public void 대기현황판은_이름을_가리고_진료중을_먼저_보여준다()
    {
        var f = new Fixture();
        var board = new WaitingBoardService(f.Repo).Board("정형외과");

        Assert.StartsWith("진료중  김*희", board[0]);
        Assert.Contains("이*", board[1]);
        Assert.DoesNotContain(board, l => l.Contains("홍길동"));
    }

    [Theory]
    [InlineData("홍길동", "홍*동")]
    [InlineData("김수", "김*")]
    [InlineData("남궁민수", "남**수")]
    public void 이름_마스킹(string name, string expected) => Assert.Equal(expected, Privacy.MaskName(name));
}

public class KioskSessionTests
{
    [Fact]
    public void 본인확인_성공하면_메뉴로_간다()
    {
        var f = new Fixture();
        var s = new KioskSession(f.Repo, f.Clock);
        s.Start();

        Assert.True(s.Identify(Fixture.Hong, "800512"));
        Assert.Equal(Screen.Menu, s.Screen);
    }

    [Fact]
    public void 세번_틀리면_잠기고_잠금이_풀리면_다시_시도할_수_있다()
    {
        var f = new Fixture();
        var s = new KioskSession(f.Repo, f.Clock);
        s.Start();
        for (int i = 0; i < 3; i++) s.Identify(Fixture.Hong, "000000");

        Assert.False(s.Identify(Fixture.Hong, "800512"));      // 잠김 중에는 맞아도 거부
        f.Clock.Advance(KioskSession.LockTime);
        Assert.True(s.Identify(Fixture.Hong, "800512"));
    }

    [Fact]
    public void 입력없이_시간이_지나면_처음화면으로_가고_환자정보를_지운다()
    {
        var f = new Fixture();
        var s = new KioskSession(f.Repo, f.Clock);
        s.Start();
        s.Identify(Fixture.Hong, "800512");
        s.ShowBills();

        f.Clock.Advance(KioskSession.IdleTimeout);
        s.Tick();

        Assert.Equal(Screen.Idle, s.Screen);
        Assert.Null(s.Patient);
        Assert.Empty(s.Selected);
    }

    [Fact]
    public void 결제중에는_시간초과로_끊지_않는다()
    {
        var f = new Fixture();
        var s = new KioskSession(f.Repo, f.Clock);
        s.Start();
        s.Identify(Fixture.Hong, "800512");
        s.ShowBills();
        s.BeginPayment();

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        s.Tick();

        Assert.Equal(Screen.Paying, s.Screen);
    }

    [Fact]
    public void 결제_시도마다_새_키를_만든다()
    {
        var f = new Fixture();
        var s = new KioskSession(f.Repo, f.Clock);
        s.Start();
        s.Identify(Fixture.Hong, "800512");
        s.ShowBills();

        var k1 = s.BeginPayment();
        s.EndPayment(false, "거절");
        var k2 = s.BeginPayment();

        Assert.NotEqual(k1, k2);
    }
}
