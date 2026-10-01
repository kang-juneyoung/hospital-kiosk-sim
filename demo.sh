#!/usr/bin/env bash
# 시연 단축 명령 (맥·리눅스). Windows에서는 Visual Studio로 HospitalKiosk.sln을 열고 Kiosk.WinForms 실행.
#   ./demo.sh            전체 시나리오
#   ./demo.sh card       카드 수납        ./demo.sh cash      현금 수납
#   ./demo.sh timeout    응답 없음→망취소  ./demo.sh dbfail    승인 후 DB 실패→승인 취소
#   ./demo.sh race       이중 결제·동시 수납 ./demo.sh cert     제증명
#   ./demo.sh queue      번호표           ./demo.sh board     대기 현황판
#   ./demo.sh frames     RS-232C 프레임    ./demo.sh play      직접 눌러 보기
#   ./demo.sh test       단위 테스트
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
  echo ".NET 8 SDK가 필요합니다. 맥: brew install --cask dotnet-sdk" >&2
  exit 1
fi

case "${1:-all}" in
  test) dotnet test tests/Kiosk.Core.Tests ;;
  play) dotnet run --project src/Kiosk.Console -- interactive ;;
  *)    dotnet run --project src/Kiosk.Console -- "$@" ;;
esac
