# 그란 나이츠 히스토리 로컬 전쟁 패치

**Grand Knights History Local War 1.0.0** — Windows + PPSSPP에서 개인용 AI 국가전을 플레이하는 비공식 팬 패치입니다. v12.2 전장·계약 기능을 기반으로 합니다.

## 다운로드

[배포본 1.0.0 받기](GKH_Local_War_1.0.0.zip?raw=true)

[전체 한국어 설치 안내](READ_ME_KO.txt) · [배포 ZIP 해시](SHA256SUMS.txt) · [재배포 안내](DISTRIBUTION.md)

## 주요 기능

- AI 국가 투표와 인접 적 영토 침공, 전쟁 종료 후 다음 캠페인 자동 전환.
- 플레이어가 참전하지 않아도 진행되는 AI 전황과 영토 변화.
- 유니온·로그리스·아발론 국가 계약, 기간 1~60회 선택과 계약금 선불 지급.
- 강국 900G/회, 약국 1,100G/회, 중간 국가는 영토 수에 따라 금액 결정.
- 적 편성과 진군 지점의 공격·방어 배치 및 전투 경계 복원.
- 기본 적 조회 밀도는 1회 배치 / 2회 빈 배치 순환.

## 지원 환경

Windows PowerShell 5.1, .NET Framework, Windows용 PPSSPP,
제작에 사용한 데이터와 정확히 일치하는 **한국어판 ULJS00394 ISO**.
같은 게임 식별자여도 다른 번역·수정판은 패치 검증에서 거부될 수 있습니다.
Python이나 별도 C# 컴파일러는 필요하지 않습니다.

게임 ISO·PPSSPP·개인 세이브는 포함하지 않습니다.
서버와 PPSSPP는 같은 PC에서 실행하며 각자의 AI 전황을 사용합니다.
공용 멀티플레이, PSP 실기와 다른 운영체제는 이 배포판의 지원 범위에 없습니다.

## 처음 설치

1. 원본 게임에서 일반 저장을 만들고 PPSSPP를 종료합니다.
2. 배포 ZIP 전체를 압축 해제하고 `START.bat`를 실행합니다.
3. 메뉴 `[1]`에서 실제 `PSP/SAVEDATA` 폴더의 저장을 백업합니다.
4. 메뉴 `[2]`에서 ISO를 패치합니다. 원본 옆에 `*_CONTRACT_v12.iso`가 생성됩니다.
5. 메뉴 `[3]`으로 서버를 켜고 자체 테스트 PASS와 `Listening at http://127.0.0.1:80`을 확인합니다.
6. 새 ISO에서 일반 저장을 불러오고 전쟁 접속으로 전황을 동기화합니다.
7. 귀환 상태에서 메뉴 `[4]`로 국가·기간을 계약하고 게임에서 재접속합니다.
8. 상점이나 전투를 진행하기 전에 다시 접속하여 지급 확인을 마친 뒤 일반 저장합니다.

계약금 확인 전에 소지금을 변경하지 마세요.
게임의 이전 저장과 서버 기록을 혼용하면 계약·잔액이 어긋날 수 있으므로 같은 시점의 백업을 함께 사용하세요.

## 기존 v12.2 사용자

기존 `CONTRACT_v12.iso`는 그대로 사용합니다.
게임과 서버를 종료한 뒤 새 폴더로 `campaign_state.bin`, `local-test-token.txt`,
`unit-template-cache.bin`과 기존 `local-war-state/`를 옮기고 저장 백업을 다시 수행하세요.
자세한 이전 순서는 [READ_ME_KO.txt](READ_ME_KO.txt)에 있습니다.

## 소스와 검증

핵심 C# 소스는 [GkhCampaign.cs](GkhCampaign.cs), ISO 패치·로컬 서버 실행 도구는 [gkh_probe.ps1](gkh_probe.ps1)입니다.
서버 실행 시 PowerShell에서 컴파일하고 자체 테스트를 수행합니다.
시간과 적 밀도 설정은 [설정 안내](CONFIG_KO.txt)를 참고하세요.

전황·계약·중복 지급 방지·전장 경계의 기존 검증 범위는 [VERIFICATION_KO.txt](VERIFICATION_KO.txt)에 있습니다.
Windows 메뉴와 PPSSPP GUI는 제작 환경에서 직접 실행하지 못했습니다.
GitHub 배포 준비에서는 기존 배포 ZIP을 변경하지 않고 파일 해시와 포함 파일을 확인했습니다.

플레이어 투표는 표시되지만 다음 전장 선정에는 현재 AI 표만 반영됩니다.
적 부대는 플레이어 캐릭터를 바탕으로 만드는 미러 부대입니다.
오류 제보에는 패치/게임/PPSSPP 버전, 재현 순서, 오류 문구를 적어 주세요.

## 전체 소스 포함 패키지

[grand-knights-history-local-war-github-ready.zip](grand-knights-history-local-war-github-ready.zip?raw=true)에는 폴더 구조를 유지한 전체 소스·설치 도구·안내문과 배포 ZIP이 들어 있습니다.
