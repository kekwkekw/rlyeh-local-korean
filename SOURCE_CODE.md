# 소스와 재빌드 안내

설치 ZIP의 `sources/` 안에 해당 배포본의 소스를 함께 제공합니다. 별도 요청이나 비밀번호가 필요하지 않습니다.
공개 GitHub 저장소는 실기 검증 후 게시할 예정이며, 현재 존재하지 않는 공개 주소를 안내하지 않습니다.

| 구성요소 | 동봉 소스 | 버전/기준 |
|---|---|---|
| Rlyeh Local Korean | `rlyeh-localizer-0.1.0-rc3-source.zip` | 이번 ZIP의 코드·용어집·문장집·설치/패키징 도구 |
| hdTrans | `hdTrans-0.9.5-source.zip` | `acd67a792a99ac5da3d6622d4d3dd203e957acd1`, 수정 없음 |
| BepInEx | `BepInEx-be.785-source.zip` | `6abdba47eeebe08552282e7a58ef0f4a9ab60b62`, 수정 없음 |
| Il2CppInterop | `Il2CppInterop-1.5.3-rlyeh-source.zip` | `dbda1cb353b0f4253345dc45136d170b9e50a5a0` + Unity 6000.3 resolver 수정 |
| UnityDoorstop | `UnityDoorstop-4.5.0-source.zip` | v4.5.0, 수정 없음 |
| Dobby | `Dobby-1.0.5-source.zip` | BepInEx v1.0.5, 수정 없음 |
| Noto Serif CJK KR | `NotoSerifCJKkr-font-source.zip` | 원본 Bold OTF·OFL, 글꼴 자체 수정 없음 |

hdTrans는 원본 Windows 0.9.5 배포물의 x64 `hdTransEngine.dll`과 `dicts.hdsnap`을 사용합니다.
CLI/GUI/x86 DLL/J2K 호환 DLL은 이 플러그인에 필요하지 않아 동봉하지 않습니다.
hdTrans 소스 ZIP은 빌드에 필요한 규칙·사전 원본과 third_party 소스를 포함합니다.
실제 입력 버전 및 SHA-256은 `inputs.lock.json`, 설치 파일 해시는 `package.json`에 기록합니다.

## 플러그인

.NET SDK 9, 게임의 BepInEx core와 첫 실행에서 생성한 interop 참조로 빌드합니다.
게임 전용 DLL과 메타데이터는 자신의 설치본에서 생성하며 소스 ZIP에 포함하지 않습니다.

```powershell
.\build.ps1 -GameRoot 'C:\Games\rlyehshoujotaix_cl'
```

결과는 `runtime/Rlyeh.Localizer/bin/Release/net6.0/`의 DLL 두 개입니다.
진단 프로브는 일반 Release에 포함하지 않습니다.
수정한 라이브러리를 다시 빌드해 같은 경로에서 교체할 수 있으며 서명 키나 온라인 인증은 요구하지 않습니다.
설치 도구의 해시 검사는 파일 전달 오류 방지용입니다. 수정 배포 시 아래 도구로 새 manifest를 만드세요.

## Il2CppInterop 수정

동봉 소스는 수정이 적용된 상태입니다. `MODIFICATIONS.md`와 `runtime/Interop-Unity6000.3.patch`에 변경을 명시했습니다.
Unity 6000.3에서 literal-field 경로가 바뀐 경우 호출 대상의 구조를 확인해 선택하는 수정이며 게임 빌드 주소를 하드코딩하지 않습니다.
upstream [PR #291](https://github.com/BepInEx/Il2CppInterop/pull/291)을 참고했습니다.

소스 루트에서 `dotnet build Il2CppInterop.Runtime/Il2CppInterop.Runtime.csproj -c Release`를 실행합니다.
기준 소스의 프로젝트가 참조하는 NuGet 의존성을 복원해야 합니다. 소스에 포함된 upstream 빌드용 참조는 해당 프로젝트의 일부입니다.
`bin/Il2CppInterop.Runtime/net6.0/Il2CppInterop.Runtime.dll`과
`bin/Il2CppInterop.Common/netstandard2.0/Il2CppInterop.Common.dll`을 사용합니다.
Generator와 HarmonySupport는 BepInEx be.785가 동봉한 같은 1.5.3 버전을 유지합니다.

## 그 밖의 구성요소

hdTrans는 동봉 소스의 README·CMakePresets와 release workflow에 빌드/사전 생성 방법이 있습니다.
BepInEx와 Doorstop은 각 동봉 소스의 build/workflow를 따릅니다.
.NET mini-coreclr 6.0.7은 BepInEx 원본 배포물을 변경 없이 사용합니다.
[BepInEx dotnet-runtime 6.0.7](https://github.com/BepInEx/dotnet-runtime/releases/tag/6.0.7)에서 빌드 자료를 확인할 수 있습니다.

폰트 소스 ZIP의 OTF/OFL을 소스 저장소 `external/noto-serif-cjk-kr/`에 놓고
`tools/build-korean-font.ps1 -UnityEditor '<Unity 6000.3.5f2 Editor 경로>'`를 실행하면 동적 폰트 번들을 만듭니다.
Unity Editor와 게임 자체는 이 프로젝트의 오픈소스 라이선스 적용 대상이 아닙니다.

## 설치 ZIP 재구성

`tools/package.py --help`의 경로 인수를 사용합니다. 공식 BepInEx 원본, 수정 Interop 빌드,
공식 hdTrans 0.9.5 Windows ZIP, 폰트 번들, 위 소스 ZIP을 로컬 입력으로 준비합니다.
도구는 네트워크에 접속하거나 게임에 설치하지 않습니다. 코드 DLL을 먼저 Release로 빌드하세요.
출력 폴더는 새 경로를 사용하며, 소스 allowlist·라이선스·payload를 묶고 manifest와 SHA-256을 생성합니다.
파일을 바꾼 뒤에는 패키지를 다시 만들고 설치·복구 및 게임 실기를 반복해야 합니다.
