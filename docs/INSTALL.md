# 설치와 복구

이 문서의 경로 `C:\Games\rlyehshoujotaix_cl`은 예시입니다. 실제 `rlyehshoujotaix_cl.exe`가 있는 폴더를 사용하세요.

## 설치

1. 게임을 종료하고 ZIP 전체를 게임 폴더 밖에 풉니다. `payload`만 수동 복사하면 자동 백업 기록이 생기지 않습니다.
2. 기존 다른 번역 패치가 있다면 게임 밖에 백업하고 먼저 제거합니다. 설치 도구는 다른 플러그인/patcher DLL을 발견하면 중단하며 자동 삭제하지 않습니다.
3. `VERIFY.cmd`로 파일 무결성을 검사합니다.
4. `INSTALL.cmd`를 실행하고 게임 폴더를 입력합니다. 설치 도구는 변경될 파일의 이전 바이트를 `게임폴더/RlyehPatchBackups/<시각-ID>/before/`에 저장하고 `manifest.json`을 기록합니다.
5. 평소 사용하던 DMM 런처로 실행합니다. 첫 실행은 BepInEx interop 생성 때문에 오래 걸릴 수 있습니다. 번역에 필요한 hdTrans DLL과 사전은 모두 동봉합니다.

PowerShell에서 직접 실행할 수도 있습니다. Windows PowerShell 5.1과 PowerShell 7을 지원 대상으로 합니다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Mode VerifyPackage
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Mode Plan -GameRoot 'C:\Games\rlyehshoujotaix_cl'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Mode Install -GameRoot 'C:\Games\rlyehshoujotaix_cl'
```

`Plan`은 대상 파일을 비교하고 변경 개수만 출력합니다. 대상 게임 폴더에 쓰지 않습니다.
실행 정책 옵션은 해당 프로세스에만 적용되며 시스템 정책을 변경하지 않습니다.
새 패키지를 풀 때 이전 패키지 폴더 위에 덮어 풀지 마세요.

## 설정과 동봉 사전

최초 실행 후 `BepInEx/config/local.rlyeh.korean.cfg`가 생성됩니다.

```ini
[Local]
Dictionary =
TranslateUi = true

[Diagnostics]
TranslationDelayMs = 0
TraceDisplay = false
TraceCatalog = false
```

기존 설정이 있으면 보존합니다. `Dictionary`가 비어 있어야
`BepInEx/plugins/Rlyeh.Localizer/native/hdtrans-data/dicts.hdsnap`을 사용합니다.
값을 지정하면 그 로컬 파일을 사용하며, 상대 경로는 플러그인 폴더 기준입니다.
원문 유지/오류가 발생해도 온라인 번역으로 전환하지 않습니다.
용어집과 사전·엔진이 달라지면 별도 캐시를 사용합니다. 업데이트는 게임을 종료하고 진행합니다.

## 복구

게임을 종료하고 설치 출력에 나온 `manifest.json`을 지정합니다.
백업은 원래 게임 폴더 안의 위치를 유지해야 합니다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Mode Restore -Receipt 'C:\Games\rlyehshoujotaix_cl\RlyehPatchBackups\<시각-ID>\manifest.json'
```

해당 설치에서 추가한 파일은 제거하고, 덮어쓴 파일은 이전 바이트로 돌립니다.
사용자가 설치 후 관리 파일을 수정했다면 복구 전에 중단합니다. 변경본을 먼저 별도 보존하세요.
실행 중 생성된 캐시·설정·interop·로그는 유지하며 게임 실행 파일과 자산을 수정하지 않습니다.
여러 버전을 설치했다면 최신 백업부터 역순으로 복구합니다. 설치가 중간에 실패한 경우에도 이미 기록된 receipt로 복구할 수 있습니다.

## 문제 확인

`BepInEx/LogOutput.log`에서 `Chainloader startup complete`, `Using bundled hdTrans dictionary`,
`hdTrans ready 0.9.5`, `Dynamic font ready`, `TMP Korean font ready`를 확인합니다.
현재 Unity 6000.3.5f2에서 기존 `Class::Init ... substitute` 경고는 알려진 대체 경로입니다.
새 게임 빌드의 Unity/IL2CPP 구조가 달라지면 재검증이 필요합니다.
문제 신고에는 오류 부분만 사용하고 계정·인증 정보가 섞인 전체 로그를 그대로 공개하지 마세요.
