# ZZZ 브라우저

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Deutsch](README.de.md) | [한국어](README.ko.md) | [繁體中文](README.zh-TW.md)

ZZZ는 .NET Framework 4.8, WPF 및 Microsoft WebView2로 만든 가벼운 오픈 소스 Windows 브라우저입니다. Chromium을 별도로 포함하지 않고 시스템에 설치된 WebView2 Runtime을 사용합니다. 휴대용으로 사용할 때는 브라우저 데이터를 실행 파일과 같은 위치에 보관할 수 있습니다.

현재 버전: **2.0.5**

## 다운로드 및 시스템 요구 사항

최신 버전은 [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest)에서 다운로드할 수 있습니다.

| 파일 | 플랫폼 |
|---|---|
| `ZZZ-v2.0.5-win-x64.exe` | Windows x64 네이티브 버전 |
| `ZZZ-v2.0.5-win-x86.exe` | Windows 10 x86 32비트 호환 버전(Windows 10 on Arm에서는 x86 에뮬레이션) |
| `ZZZ-v2.0.5-win-arm64.exe` | Windows ARM64 네이티브 버전 |

설치 과정은 필요하지 않습니다. Windows 10 또는 Windows 11, .NET Framework 4.8 및 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)이 필요합니다.

## 주요 기능

- 다국어 인터페이스, 탭 탐색, 최근에 닫은 페이지 복원, 가로·세로 분할 보기
- 방문 기록 일치 항목과 실시간 검색 제안을 제공하는 통합 주소·검색 표시줄
- 그룹 및 이름 편집, HTML 가져오기·내보내기를 지원하는 북마크
- 단색, 이미지 또는 GIF 배경을 선택할 수 있는 가벼운 네이티브 시작 페이지
- 페이지 내 찾기, 인쇄, PDF·MHT 저장, 전체 화면 및 창별 독립 확대/축소
- F9 읽기 모드, 앱 전체 흑백 모드, 편집 가능한 북마크 이름 및 개인정보를 배려한 정보 페이지
- 일반 탭만 기록하는 비활성화 가능한 원자적 세션 복원과 웹 프로세스 시작 전 최초 이용 약관 확인
- 웹 페이지 번역, 사용자 스크립트, User-Agent 전환 및 밝은·어두운 웹 렌더링
- EasyList 구독, 사용자 지정 ABP 규칙 및 오른쪽 클릭 광고 요소 선택을 지원하는 광고 차단
- 진행률, MIME 유형 및 저장 위치를 표시하는 다운로드 관리자와 외부 도구 연동
- AppData, 실행 파일 옆 또는 사용자 지정 폴더 중에서 선택할 수 있는 브라우저 데이터 저장 위치

## 개인정보 보호

각 비공개 탭은 격리된 WebView2 프로필을 사용하며 방문 기록, 세션, 캐시, 쿠키 및 온라인 검색 제안을 저장하지 않습니다. 탭을 닫으면 임시 데이터가 삭제되며, 비정상 종료 시 감시 프로세스와 다음 실행 시 정리 절차가 삭제를 다시 시도합니다. 사용자가 직접 저장한 다운로드 파일과 북마크는 유지됩니다.

일반 탐색에서도 DNT, GPC, Public Suffix List 기반 타사 쿠키 차단, WebRTC 제한 및 사이트 권한 관리를 사용할 수 있습니다.

## 휴대용 모드

**설정 → 백업 → 데이터 및 쿠키 저장 위치**에서 휴대용 모드를 선택하고 저장한 뒤 다시 시작하세요. 브라우저를 옮길 때는 `ZZZ.exe`, `Data` 폴더 및 `zzz-data-location.json`을 함께 복사해야 합니다.

## 빌드

```powershell
dotnet build ZZZ.sln -c Release
```

x64 출력 파일은 `ZZZ\bin\Release\net48\ZZZ.exe`에 생성됩니다. x86 32비트 호환 버전은 다음 명령으로 빌드할 수 있습니다.

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=x86 -p:OutputPath=outputs\win-x86\
```

WebView2는 ARM32 Runtime 또는 Loader를 제공하지 않습니다. Windows 10 on Arm에서 32비트 호환성이 필요하면 x86 에뮬레이션 버전을 사용하고, 네이티브 버전은 ARM64로 빌드하세요.

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=ARM64 -p:OutputPath=outputs\win-arm64\
```

## 지원 및 라이선스

- 문제 및 제안: [GitHub Issues](https://github.com/zengjiangy/ZZZ/issues)
- 라이선스: [MIT License](LICENSE)
- 타사 구성 요소: [Third-party notices](THIRD-PARTY-NOTICES.md)
