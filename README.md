# hospital-kiosk-sim — 병원 무인수납 키오스크 시뮬레이터 (C# / .NET 8)

병원 키오스크의 **무인수납·제증명 발급·순번대기·진료대기** 흐름을 C#으로 구현한 개인 프로젝트입니다.
화면이 예쁜 것보다 **돈과 장부가 어긋나지 않는 것**, **장비가 이상해도 멈추지 않는 것**에 집중했습니다.

> 모든 환자·진료·금액 데이터는 가상입니다. 실제 병원·VAN·장비 사양과는 관계가 없습니다.

## 무엇을 보여 주나

| 기능 | 핵심 규칙 | 확인 방법 |
|---|---|---|
| 카드 수납 | 금액은 서버(Core)가 항목에서 다시 계산 · 결제 전 Pending 저장 · 한 트랜잭션으로 수납 완료 | `./demo.sh card` |
| 망취소 | 단말기 응답 없음 → 망취소 / 승인 후 DB 저장 실패 → 승인 취소 ([ADR 0001](docs/adr/0001-net-cancel.md)) | `./demo.sh timeout`, `dbfail` |
| 이중 결제 방지 | 거래 키 + DB UNIQUE · 창구와 동시 수납 시 전부 거부 ([ADR 0002](docs/adr/0002-idempotency.md)) | `./demo.sh race` |
| 현금 수납 | 지폐 인식기(RS-232C) 프레임 해석 · 권종 거부 · 거스름돈 부족 시 반환 · 취소 시 환불 | `./demo.sh cash` |
| 제증명 | 신청 → 발급비 수납(같은 결제 경로) → 문서번호 발급 · 진료 기록 없는 날짜 거부 | `./demo.sh cert` |
| 순번대기 | 종류별 번호(A/B/C) · 날짜 바뀌면 1번부터 · MySQL에서 원자적 증가 | `./demo.sh queue` |
| 진료대기 현황판 | 이름 마스킹(홍*동) · 진료중 먼저 | `./demo.sh board` |
| 화면 흐름 | 60초 무입력 시 처음으로 + 환자 정보 삭제 · 본인 확인 3회 실패 잠금 · 결제 중엔 끊지 않음 ([ADR 0003](docs/adr/0003-session-state-machine.md)) | 단위 테스트 |
| 장비 통신 | 조각난 바이트 복원 · 잡음·체크섬 오류 버림 ([ADR 0004](docs/adr/0004-device-layer.md)) | `./demo.sh frames` |

## 구조

```
src/
  Kiosk.Core/          업무 규칙 전부 (외부 패키지 없음)
    Domain/            환자·수납·결제·증명서·번호표 모델, 이름 마스킹
    Devices/           RS-232C 프레임 파서, 지폐 인식기 드라이버, 카드 단말기·프린터·거스름돈 방출기 + 시뮬레이터
    Data/              저장소 인터페이스, 메모리 구현, ADO.NET(MySQL) 구현, 가상 데이터
    Services/          카드 수납(망취소), 현금 수납, 제증명, 순번대기, 대기 현황판, 감사 로그(JSONL)
    Flow/              KioskSession — 화면 상태 머신
  Kiosk.Data.MySql/    MySqlConnector로 연결만 만들어 주는 얇은 프로젝트
  Kiosk.WinForms/      Windows 키오스크 화면 + 장비 시뮬레이터 패널 (KIOSK_DB 환경변수가 있으면 MySQL 사용)
  Kiosk.Console/       맥·리눅스용 시나리오 시연 + 직접 눌러 보는 모드
tests/Kiosk.Core.Tests/ xUnit 단위 테스트 31개
db/mysql/              schema.sql, seed.sql (MySQL 8)
docs/adr/              설계 결정 기록 4건
```

```
[화면: WinForms / 콘솔] ──> KioskSession(상태 머신)
            │
            ▼
   PaymentService · CashPaymentSession · CertificateService · QueueService
            │                          │
            ▼                          ▼
   IKioskRepository             ICardTerminal · BillAcceptorDriver(RS-232C) · IReceiptPrinter · IChangeDispenser
   (메모리 / MySQL)              (실제 장비 또는 시뮬레이터)
```

## 실행

.NET 8 SDK가 필요합니다. (맥: `brew install --cask dotnet-sdk`)

```bash
./demo.sh            # 전체 시나리오 (맥·리눅스·Windows Git Bash)
./demo.sh dbfail     # 하나만
./demo.sh play       # 콘솔에서 직접 눌러 보기 (가상 환자 10000001 / 800512)
./demo.sh test       # 단위 테스트
dotnet run --project src/Kiosk.Console -- card --audit   # 감사 로그까지 출력
```

Windows 화면: Visual Studio 2022로 `HospitalKiosk.sln`을 열고 `Kiosk.WinForms`를 시작 프로젝트로 실행하거나,
`dotnet run --project src/Kiosk.WinForms`. 오른쪽 패널에서 카드 거절·응답 없음, 프린터 용지 없음, DB 저장 실패를 골라 재현할 수 있습니다.

MySQL로 실행(선택):

```bash
docker run -d --name kiosk-mysql -e MYSQL_ROOT_PASSWORD=pass -e MYSQL_DATABASE=kiosk -p 3306:3306 mysql:8.0
mysql -h127.0.0.1 -uroot -ppass kiosk < db/mysql/schema.sql
mysql -h127.0.0.1 -uroot -ppass kiosk < db/mysql/seed.sql
set KIOSK_DB=Server=127.0.0.1;Database=kiosk;User ID=root;Password=pass;   # Windows cmd
```

## 검증 범위 (솔직하게)

- **확인함**: Core 빌드(경고를 오류로 처리), 단위 테스트 31개 통과, 콘솔 시나리오 10개 실행,
  `schema.sql`·`seed.sql`과 저장소의 핵심 SQL(번호표 원자적 증가, 수납 트랜잭션, UNIQUE 위반)을 MySQL 8.0에서 직접 실행.
- **CI로 확인**: WinForms 화면과 MySqlConnector 프로젝트 빌드는 GitHub Actions(Windows·Linux)에서 확인합니다.
- **하지 않은 것**: 실제 카드 단말기·VAN 연동, 실제 지폐 인식기 시리얼 연결(`System.IO.Ports`), 병원 HIS/EMR 연동,
  C# 저장소를 실제 MySQL에 붙인 통합 테스트, MSSQL·Oracle 대응.

## 만든 방법

C#·WinForms는 이 프로젝트로 처음 학습했습니다. 실무에서는 Python(FastAPI)·PostgreSQL로 키오스크 결제 연동을,
RS-232C로 지폐 장비 제어를 해 왔고, 그 경험에서 나온 규칙(서버 금액 재계산, 결제 예외 흐름, 장비 프레임 검증)을
C#으로 옮겼습니다. 코드는 AI 코딩 도구(Claude)와 함께 작성했고, 동작은 테스트와 시나리오 실행으로 확인했습니다.
