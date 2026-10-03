# DiscordChatHUD Client — MIT 공개 소스 준비본

Windows에서 Discord 채팅·미디어와 GTA Online 사업장 현황을 표시하는 사용자용 클라이언트입니다.
HUD·설정창·자동 감지 프로그램(Watcher)의 소스와 빌드 절차가 포함됩니다.
중계 서버와 관리자 프로그램은 이 공개 패키지에 포함하지 않습니다.

프로젝트 자체 코드와 자체 아이콘은 MIT입니다. 외부 라이브러리와 글꼴에는 별도 라이선스가 적용됩니다.

## 현재 상태

Client 333에서 공개용으로 분리한 소스 준비본이며, 구분을 위해 후보 버전을 334로 설정했습니다.
운영 서버 주소와 실제 사용자·채널·역할 ID를 제거했고, 기존 게임 이미지는 자체 문자 아이콘으로 교체했습니다.
예시 ID는 합성값이며 기존 재고·보급·타이머 기록은 초기화했습니다.
이 소스 준비본은 기존 운영 서버에 바로 연결하는 일반 사용자용 업데이트가 아닙니다.
Git 이력, 로그, 개인 설정, 인증키, 기존 실행파일은 소스 ZIP에 포함하지 않습니다.
MIT 공개 저장소 게시를 완료했습니다. 인증서 서명과 SignPath 승인은 아직 완료되지 않았습니다.

## 빌드와 검증

Windows x64, .NET 10 SDK, PowerShell이 필요합니다. 최초 NuGet 복원에는 인터넷 연결이 필요합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\Build.ps1
powershell -ExecutionPolicy Bypass -File .\Verify.ps1
```

`artifacts/client`에 HUD와 설정창, `artifacts/watcher`에 자동 감지 프로그램을 만듭니다.
Watcher도 같은 공개 소스에서 먼저 빌드한 뒤 클라이언트에 포함합니다. 사전 컴파일된 Watcher는 소스 ZIP에 없습니다.
자체 아이콘은 `tools/Generate-Assets.ps1`에서 생성합니다.
빌드·검증 스크립트는 실제 HUD를 실행하거나 기존 설치를 변경하지 않습니다.

## 서버 연결

클라이언트 실행폴더의 `relay-client.json`에 운영자가 제공한 HTTPS 서버 주소를 넣습니다.
공개본의 기본 `relay.example.invalid`는 작동하는 서버가 아닙니다.
Discord 로그인에는 연결한 중계 서버의 역할·채널 접근 권한이 필요합니다. 이 패키지에 서버 구현이나 로그인 인증 정보는 없습니다.
실제 사용 중인 설정폴더를 공유하므로 개발 빌드 시험은 별도의 Windows 시험 계정에서 진행하는 것을 권장합니다.

## 직접 게이트웨이 개발 모드

일반 빌드는 중계 로그인 전용입니다. `RelayOnly=false` 개발 빌드는 별도 봇 설정과 Discord 권한이 필요합니다.
세션 인원 표시 대상의 직접 게이트웨이 기본값은 없습니다. 필요한 경우 프로세스 시작 전에
`HUD_SESSION_PRIMARY_ID`, `HUD_SESSION_PRIMARY_NAME`, `HUD_SESSION_SECONDARY_ID`, `HUD_SESSION_SECONDARY_NAME` 환경변수로 지정합니다.
일반 중계 클라이언트에서는 서버가 보내는 세션 정보를 사용합니다.

## 업데이트 배포

공개본의 업데이트 주소는 `updates.example.invalid`입니다. 실제 배포 주소가 아닙니다.
자체 업데이트를 운영하려면 `Services/AppUpdate.cs`의 주소와 `Services/UpdatePublicKey.xml`을 자신의 것으로 교체하고
기존 서명 검증 형식에 맞는 배포 메타데이터를 생성해야 합니다. 제공된 XML은 검증용 공개키이며 개인키가 아닙니다.
개인 서명키는 저장소 밖에서 관리합니다. 이 자체 검증은 Windows Authenticode 인증서 서명과 다릅니다.
복구 정책 주소는 `Services/RecoveryPolicyClient.cs`에서 자신의 서비스에 맞게 구성합니다.
이 소스 패키지는 운영 서비스의 배포 스크립트·서명키를 포함하지 않습니다.

## 데이터와 제거

[PRIVACY.md](PRIVACY.md)에 연결 서비스와 데이터 흐름이 설명되어 있습니다.
설정에서 자동 실행을 해제하고 HUD·설정창·자동 감지를 종료한 뒤 설치폴더를 삭제할 수 있습니다.
계정 설정·로그를 함께 지우려면 설정 저장 경로와 `%LOCALAPPDATA%/DiscordChatHUD*`의 자신의 데이터를 확인 후 제거합니다.
중계 서버에 저장된 계정 데이터 삭제는 해당 서버 운영자에게 요청해야 합니다.

## 코드 서명 준비

[CODE_SIGNING.md](CODE_SIGNING.md)에 남은 신청 준비가 있습니다.
