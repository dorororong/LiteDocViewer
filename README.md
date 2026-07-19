# LiteDoc Viewer

Windows용 경량 로컬 Markdown/HTML 문서 브라우저입니다. WPF, WebView2, Markdig로 만들어졌습니다.

## 주요 기능

- `.md`, `.markdown`, `.html`, `.htm`만 표시하는 폴더 트리
- GitHub 계열 스타일의 Markdown 렌더링과 상대 이미지 경로 지원
- JavaScript, CSS, 이미지 등 상대 리소스가 동작하는 HTML 미리보기
- 파일 변경 감지 후 자동 새로고침
- 밝은/어두운 모드와 마지막 폴더 기억
- `Ctrl+O` 폴더 열기, `F5` 새로고침
- 외부 링크는 Windows 기본 브라우저에서 열기
- 팝업과 다운로드를 앱 내부에서 차단

## 일반 사용자용 설치

GitHub Releases에서 `LiteDocViewer-Setup-x64.exe`를 내려받아 실행합니다.

설치 프로그램은 다음 필수 구성 요소를 자동으로 확인합니다.

- .NET 8 Desktop Runtime x64
- Microsoft Edge WebView2 Runtime

이미 설치되어 있으면 건너뛰고, 없을 때만 Microsoft 서버에서 내려받아 자동 설치합니다.
따라서 첫 설치 시 인터넷 연결과 관리자 권한이 필요할 수 있습니다.

현재 생성된 설치 파일은 다음 위치에 있습니다.

```text
artifacts\Installer\LiteDocViewer-Setup-x64.exe
```

## 개발 환경에서 실행

필요 조건: Windows 10/11, .NET 8 SDK, WebView2 Runtime

```powershell
dotnet restore
dotnet run
```

## 로컬 배포 빌드

Inno Setup 6 설치 후 다음 명령을 실행합니다.

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 1.0.0
```

결과물:

```text
artifacts\App\LiteDocViewer.exe
artifacts\Installer\LiteDocViewer-Setup-x64.exe
```

앱은 framework-dependent 방식이므로 작지만 .NET 8 Desktop Runtime이 필요합니다. 설치 프로그램이 이 의존성을 자동으로 처리합니다. WebView2 네이티브 로더는 단일 EXE에 포함되어 설치 후 별도 DLL 없이 실행됩니다.

## GitHub Releases 자동 배포

`.github/workflows/release.yml`이 포함되어 있습니다. GitHub 저장소에 소스를 올린 다음 버전 태그를 push하면 Windows 빌드와 Release 생성이 자동으로 실행됩니다.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

완료되면 저장소의 `Releases` 페이지에 다음 파일이 공개됩니다.

```text
LiteDocViewer-Setup-x64.exe
LiteDocViewer.exe
SHA256SUMS.txt
```

GitHub의 `Actions` 화면에서 `Build and release`를 수동 실행하면 Release를 만들지 않고 다운로드 가능한 테스트 산출물만 생성할 수도 있습니다.

## 배포 참고 사항

- GitHub에 올린 서명되지 않은 EXE는 Windows SmartScreen에서 `알 수 없는 게시자` 경고가 나타날 수 있습니다.
- 이 경고를 없애려면 별도의 Windows 코드 서명 인증서가 필요합니다.
- WebView2 Runtime은 Evergreen 방식으로 설치되고 이후 자동 업데이트됩니다.

WebView2 Runtime이 없는 PC에서는 LiteDoc Viewer 설치 프로그램이 자동으로 준비합니다.
