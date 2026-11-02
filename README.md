# A3 Offline Coding Studio

인터넷 없는 Windows PC에서 A3 가공 SW의 코드와 승인한 관련 자료를 바탕으로 대화하고, 코드 수정안을 확인·적용·되돌리는 전용 앱입니다.

이 저장소 `A3_LD_Process_Local_LLM`은 앱의 모든 후속 개발·검증·배포 준비를 관리합니다. 앱이 다루는 대상은 별도의 `kjuws10-rgb/A3_LD_Process_SW_IPS` Git checkout입니다. 장비 코드와 내부 관련 자료, 모델 파일은 이 공개 저장소에 포함하지 않습니다.

## 사용 흐름

1. 준비된 실행 폴더에서 `A3.OfflineStudio.exe`를 실행합니다. 함께 제공된 `Project`와 `model-update.zip`이 있으면 자동 연결·반입합니다.
2. 한국어로 설명이나 코드 수정을 요청합니다. Word·Excel·텍스트 자료는 여러 파일을 한 번에 추가할 수 있습니다.
3. 파일·줄 출처와 수정 미리보기를 확인한 뒤 적용합니다. 원본 변경 충돌을 확인하고 백업·되돌리기를 제공합니다.

모델은 llama.cpp CPU 실행기로 같은 PC에서 구동합니다. 외부망에서 준비한 Qwen 모델 또는 자체 GGUF 업데이트 ZIP을 USB로 반입하며, 파일 해시와 실행 상태를 확인하고 이전 모델로 복원할 수 있습니다. 모델 재학습은 별도의 외부 준비 단계입니다.

## 개발·검증

```powershell
dotnet build tools/A3.OfflineStudio/A3.OfflineStudio.csproj -c Release
dotnet run --project tools/A3.OfflineStudio.Tests -c Release
dotnet run --project tools/A3.OfflineStudio.IntegrationTests -c Release
```

앱은 .NET10 Windows 프로젝트이며 장비 솔루션과 독립적으로 빌드됩니다. 자체 포함 실행 파일로 배포할 수 있고, 오프라인 빌드용 SDK·참조 패키지를 준비하는 도구도 제공합니다.

- [사용·외부망 준비·반입 안내](docs/OFFLINE_STUDIO.md)
- [검증 결과와 확인한 한계](docs/OFFLINE_STUDIO_VERIFICATION.md)
- [준비 도구](tools/A3.OfflineStudio/packaging)

현재 버전은 단일 PC·CPU 환경을 대상으로 합니다. 모델은 실수할 수 있으며, 설명·수정안 생성은 하드웨어에 따라 수십 초~수분이 걸립니다. 다중 PC LAN 서비스·GPU 가속·학습·이미지/스캔 PDF 이해는 포함하지 않습니다. 장비의 실제 동작 검증과 기존 대상 소스의 컴파일 오류 처리는 별개입니다.
