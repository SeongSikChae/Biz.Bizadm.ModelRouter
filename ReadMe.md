# Biz.Bizadm.ModelRouter

Azure AI Foundry 또는 OpenAI API를 프록시하는 ASP.NET Core 서버입니다. 클라이언트에는 OpenAI 호환 엔드포인트를 제공하고, 요청을 설정된 공급자 엔드포인트로 전달합니다.

## 주요 기능

- `chat/completions` 요청을 Azure AI Foundry 또는 OpenAI로 전달
- Responses API를 직접 호출하고 응답을 그대로 전달
- 모델 목록 및 단일 모델 정보 조회
- tool call id 양방향 매핑 (요청 단위)
  - Foundry 64자 제한을 피하기 위해 요청의 `tool_calls[].id` / `tool_call_id` / `call_id`를 임의 short id로 치환
  - 동일 요청–응답에서 맵에 있는 id는 비스트리밍·SSE 응답 모두 클라이언트 원본 id로 복원
  - `BridgeToResponses` 포함: chat 요청 id를 먼저 치환한 뒤 Responses `call_id`로 전달하고, bridge가 만든 chat 응답/SSE에서 원본 id로 복원
  - `/v1/chat/completions` 직통과 `/v1/responses` 직통에도 동일하게 적용
- 모델별 reasoning / chat+tools 정책 (`ReasoningByModel`)
  - tools 없음: `Effort`를 `reasoning_effort`로 주입
  - 요청 모델명 접미사로 effort 지정 가능 (아래 [모델명 effort 접미사](#모델명-effort-접미사) 참고)
  - tools + `BridgeToResponses`: Responses API로 우회 후 chat.completions 형식으로 응답 변환 (Cursor/에이전트 권장)
  - tools + `ForceNone`: chat에 `reasoning_effort: none` (지연/비용 우선 저하 경로; `SupportsNone: false`면 bridge 폴백)
  - `/v1/responses`에는 `Effort`를 `reasoning.effort`로 주입
  - 하위 호환: `ReasoningEffortByModel` 문자열 맵은 tools 시 `BridgeToResponses`로 취급
- 콘솔 및 날짜별 파일 로그 기록 (`X-Request-Id` 응답 헤더 포함)
- 선택적 HTTPS (`Ssl.UseSsl`), TLS 1.2/1.3, HTTP/1·2·3
- 설정 보안정보(`Ssl.ServerCertificatePassword`, `OpenAI.OpenAIKey`) 기동 시 암호화 저장 (`Biz.Bizadm.KMS`, OS 자격 증명 금고 KEK)
- Windows Service 및 systemd 호스팅 지원

## API

| 메서드 | 경로 | 설명 |
| --- | --- | --- |
| `POST` | `/v1/chat/completions` | Chat Completions 요청 프록시 |
| `POST` | `/v1/responses` | Responses 요청 프록시 |
| `GET` | `/v1/models` | 모델 목록 조회 |
| `GET` | `/v1/models/{model}` | 지정한 모델 조회 |

성공·오류 상태 코드, 응답 Content-Type 및 응답 본문은 upstream 응답에서 전달됩니다. Chat Completions와 Responses 엔드포인트는 스트리밍 응답을 지원합니다.

## 요구 사항

- .NET 10.0.13 이상 SDK 또는 ASP.NET Core 런타임
- Azure AI Foundry 또는 OpenAI API 엔드포인트 및 API 키
- 서버 설정 YAML 파일
- `Ssl.UseSsl: true`인 경우 PKCS#12/PFX 인증서 파일
- OS 자격 증명 금고 (Windows Credential Manager, macOS Keychain, Linux Secret Service 또는 `Kms.CredentialStore`로 지정한 GCM 저장소)

### 보안 업데이트

[CVE-2026-88092](https://www.cve.org/CVERecord?id=CVE-2026-88092)는 ASP.NET Core의 HTTP 요청 스머글링 취약점입니다. ASP.NET Core 10.0.0~10.0.12가 영향을 받으므로 10.0.13 이상으로 업데이트하세요. 프레임워크 종속 배포는 서버의 ASP.NET Core 런타임을 업데이트하고 애플리케이션을 재시작해야 하며, self-contained 배포는 수정된 런타임으로 다시 빌드하고 재배포해야 합니다.

## 설정

서버는 `--config`로 지정한 YAML 파일을 읽습니다. 샘플은 [`Configuration.sample.yml`](src/Biz.Bizadm.ModelRouter.Server/Configuration.sample.yml)을 참고하세요.

```yaml
ContentRootPath: /ModelRouter
Port: 80
# Ssl:
#   UseSsl: true
#   ServerCertificate: server.pfx
#   ServerCertificatePassword: replace-with-certificate-password
#   CertificateChain: ""
#   IncludeCipherSuites: "TLS_AES_256_GCM_SHA384,TLS_CHACHA20_POLY1305_SHA256,TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256"
OpenAI:
  Provider: AzureFoundry # 생략 시 AzureFoundry; OpenAI 사용 시 OpenAI로 변경
  OpenAIEndpoint: https://YOUR-RESOURCE.services.ai.azure.com/openai
  OpenAIKey: replace-with-provider-api-key

# OpenAI 사용 예시:
# OpenAI:
#   Provider: OpenAI
#   OpenAIEndpoint: https://api.openai.com
#   OpenAIKey: replace-with-openai-api-key

Kms:
  DekFilePath: dek.bin
  # KekSalt: ""
  # KekIterations: 600000
  # CredentialStore: gpg

ReasoningByModel:
  YOUR-MODEL:
    Effort: medium
    ChatToolsMode: BridgeToResponses
    SupportsNone: true
```

| 항목 | 필수 | 설명 |
| --- | --- | --- |
| `ContentRootPath` | 예 | 콘텐츠 루트. 인증서·DEK 파일 등 상대 경로의 기준 |
| `Port` | 예 | 수신 포트. `0.0.0.0:{Port}`에서 listen |
| `Ssl` | 아니오 | HTTPS 설정 섹션. 생략 시 HTTP |
| `Ssl.UseSsl` | 아니오 | HTTPS 사용 여부. 기본값 `false` |
| `Ssl.ServerCertificate` | `UseSsl`일 때 | PKCS#12/PFX 경로 (`ContentRootPath` 기준 상대 또는 절대) |
| `Ssl.ServerCertificatePassword` | `UseSsl`일 때 | 인증서 암호. 기동 시 암호화되어 저장 |
| `Ssl.CertificateChain` | 아니오 | 체인에 포함할 인증서 SimpleName (쉼표 구분) |
| `Ssl.IncludeCipherSuites` | 아니오 | 허용 TLS 암호 스위트 (`TlsCipherSuite` enum 이름, 쉼표 구분) |
| `OpenAI` | 예 | 공급자 설정 섹션 |
| `OpenAI.Provider` | 아니오 | `AzureFoundry` 또는 `OpenAI`. 생략 시 `AzureFoundry` |
| `OpenAI.OpenAIEndpoint` | 예 | 선택한 공급자의 OpenAI 호환 API 기본 URL. Azure Foundry는 `.../openai`, OpenAI는 `https://api.openai.com` |
| `OpenAI.OpenAIKey` | 예 | 선택한 공급자의 API 키. Azure Foundry는 `api-key`, OpenAI는 `Authorization: Bearer`로 전달. 기동 시 암호화되어 저장 |
| `Kms` | 예 | 보안정보 암호화 설정 섹션 |
| `Kms.DekFilePath` | 예 | KEK로 wrap된 DEK 파일 경로 (`ContentRootPath` 기준). 없으면 생성 |
| `Kms.KekSalt` | 아니오 | KEK PBKDF2 salt (Base64). 생략 시 생성되어 저장 |
| `Kms.KekIterations` | 아니오 | KEK PBKDF2 반복 횟수. 기본값 `600000` |
| `Kms.CredentialStore` | 아니오 | `GCM_CREDENTIAL_STORE` 값 (예: Linux 헤드리스의 `gpg`) |
| `ReasoningByModel` | 아니오 | 모델별 Effort / ChatToolsMode / SupportsNone |
| `ReasoningEffortByModel` | 아니오 | 하위 호환 effort 문자열 맵 (tools 시 BridgeToResponses) |

`OpenAI.OpenAIEndpoint`는 `/v1` 이전까지만 지정합니다. `/v1` 이후가 포함되어 있으면 서버가 자동으로 제거한 뒤 `/v1/chat/completions`, `/v1/responses`, `/v1/models` 경로를 추가합니다. `OpenAI.Provider`를 생략하면 `AzureFoundry`로 처리됩니다.

### 보안정보 암호화

기동 시 `Ssl.ServerCertificatePassword`(값이 있을 때)와 `OpenAI.OpenAIKey`가 평문이면 DEK로 암호화해 `DEKENC:...` 형식으로 설정 파일을 다시 저장하고, 이미 암호문이면 파일을 바꾸지 않고 복호화만 해서 사용합니다.

- KEK 패스워드는 OS 자격 증명 금고의 `biz-bizadm-modelrouter-server://kek` / `default` 엔트리에 보관합니다. 엔트리가 없고 DEK 파일도 없으면 32바이트 랜덤 값으로 생성합니다.
- 엔트리 없이 DEK 파일만 있으면 복호화할 수 없으므로 기동에 실패합니다. 서버를 옮길 때는 금고 엔트리, DEK 파일, `Kms.KekSalt`를 함께 옮기거나 보안정보를 평문으로 다시 넣고 DEK 파일을 지운 뒤 기동하세요.
- 설정 파일을 다시 저장할 때 YAML이 재직렬화되어 주석은 제거됩니다. 파일 권한(ACL/Unix mode)은 유지됩니다.
- 이전 형식(루트의 `UseSsl`, `Provider`, `OpenAIEndpoint`, `OpenAIKey`)은 지원하지 않으므로 `Ssl`, `OpenAI`, `Kms` 섹션으로 옮겨야 합니다.

### 모델별 reasoning / tools

`ReasoningByModel` 키는 선택한 공급자에 등록한 모델명 또는 배포명(요청 `model` 값)과 맞춥니다. 허용 effort·chat+tools 제약은 기반 모델에 따르며, YAML의 `SupportsNone` / `ChatToolsMode`로 라우터 정책만 지정합니다.

#### 모델명 effort 접미사

요청 `model` 끝에 `-none`, `-low`, `-medium`, `-high`, `-xhigh`, `-max`를 붙이면 YAML `Effort`보다 우선해 해당 effort를 주입합니다. upstream에는 접미사를 뺀 베이스 배포명만 전달합니다.

예: 배포명이 `my-luna`일 때 `my-luna-high` → upstream `model=my-luna`, `reasoning_effort`/`reasoning.effort=high`

라우터가 인식하는 접미사는 위 6종입니다. 실제 API가 받는 값은 기반 모델마다 다르므로, `SupportsNone`과 아래 표를 맞춰 두세요. `SupportsNone: false`인데 `none`이 오면 라우터가 `low`로 보정합니다.

기반 모델별 허용 effort·tools 제약(OpenAI / Azure Foundry 문서 기준):

| 기반 모델 | 허용 effort | 기본값 | chat + function tools |
| --- | --- | --- | --- |
| `gpt-6-luna` | `none`, `low`, `medium`, `high`, `xhigh`, `max` | `medium` | chat에서는 `none`만 가능. reasoning+tools는 Responses |
| `gpt-6.1-sol` | `low`, `medium`, `high`, `xhigh`, `max` (`none`·`minimal` 불가) | `medium` | chat는 tools 없이만. tools는 Responses 필수 |
| `gpt-6-astra` | `low`, `medium`, `high`, `xhigh`, `max` (`none` 불가) | `medium` | chat는 tools 없이만. tools는 Responses 필수 |

권장 YAML 예:

```yaml
ReasoningByModel:
  my-luna:   # 기반: gpt-6-luna
    Effort: medium
    ChatToolsMode: BridgeToResponses
    SupportsNone: true
  my-sol:    # 기반: gpt-6.1-sol
    Effort: medium
    ChatToolsMode: BridgeToResponses
    SupportsNone: false
  my-astra:  # 기반: gpt-6-astra
    Effort: medium
    ChatToolsMode: BridgeToResponses
    SupportsNone: false
```

`ChatToolsMode` 값:

| 값 | 동작 |
| --- | --- |
| `BridgeToResponses` | tools가 있으면 Responses로 우회하고 chat.completions 응답으로 변환. Cursor/에이전트 권장 |
| `ForceNone` | chat에 `reasoning_effort: none`. 지연/비용 우선 저하 경로. `SupportsNone: false`면 bridge로 폴백 |
| `PassThrough` | none 강제 없음. `Effort`만 선택 주입 |

BridgeToResponses 스트림은 출력 청크 전에 rate-limit/`TPM` 실패가 오면 HTTP 429와 JSON 오류 본문으로 반환합니다. `max_output_tokens` 종료는 `finish_reason: length`, `content_filter` 종료는 `finish_reason: content_filter`로 전달됩니다. Azure가 `content_filters`를 실어 주면 choice에 `content_filters`(및 해당 시 `content_filter_results`)를 보존하고, 차단·필터 카테고리 요약을 로그에 남깁니다. 그 외 `response.failed`/`error` 및 매핑되지 않은 `response.incomplete`는 OpenAI 호환 SSE 오류 이벤트로 전달하며(사유가 있으면 message에 포함), 로그에 `Bridge SSE start`, `terminal`, `end`와 이벤트·청크 수를 기록합니다. 요청에 `stream_options.include_usage: true`가 있으면 성공 종료 시 `finish_reason` 청크 뒤에 `usage` 청크를 전달합니다. 비스트리밍에서도 `content_filter`/`max_output_tokens` incomplete는 위 finish_reason으로 변환하고, 나머지 `failed`/`incomplete`는 HTTP 502와 JSON 오류 본문으로 반환합니다.

Cursor `Show Context Usage` 표시는 전달된 토큰 `usage` 외에도 Cursor가 모델명으로 판별하는 context window에 의존합니다. Override에 등록한 모델명이 Cursor 내장 모델 카탈로그와 일치하는지 확인하세요; ModelRouter는 Cursor 카탈로그를 설정할 수 없습니다.

모델명은 대소문자를 구분하지 않습니다. `SupportsNone: false`인데 Effort가 `none`이면 허용된 최저 단계인 `low`로 보정합니다. 맵에 없거나 JSON을 파싱할 수 없으면 요청 바디를 그대로 전달합니다. `reasoning.summary` / `reasoning.mode` 등 다른 필드는 변경하지 않습니다.

> **주의:** API 라우트 자체에는 인증이 없습니다. `Ssl.UseSsl`이 꺼져 있으면 HTTP로 수신하므로, 외부에 노출할 때는 TLS·인증을 제공하는 프록시/게이트웨이 뒤에 두거나 `Ssl.UseSsl: true`로 HTTPS를 켜세요.

## 실행

저장소 루트에서 실행합니다. `--config`와 `--log`는 필수 인자이며, `--log`에는 로그 파일이 생성될 디렉터리를 지정합니다.

```powershell
dotnet run --project .\src\Biz.Bizadm.ModelRouter.Server\Biz.Bizadm.ModelRouter.Server.csproj -- --config C:\Biz\ModelRouter\config.yml --log C:\Biz\ModelRouter\logs
```

게시된 애플리케이션은 동일한 인자를 전달해 실행합니다.

```powershell
Biz.Bizadm.ModelRouter.Server.exe --config C:\Biz\ModelRouter\config.yml --log C:\Biz\ModelRouter\logs
```

로그는 콘솔과 지정한 디렉터리에 기록되며, 파일 로그는 일 단위로 회전하고 최대 12개 파일을 보관합니다.

## 예시 요청

```bash
curl http://localhost:8080/v1/models
```

```bash
curl http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"YOUR-MODEL","messages":[{"role":"user","content":"안녕하세요"}]}'
```

개발 환경에서는 OpenAPI 문서가 `/openapi/v1.json`에 노출됩니다. 프로덕션 환경에서는 OpenAPI 엔드포인트가 매핑되지 않습니다.

## 빌드

저장소 루트에서 다음 명령으로 솔루션을 빌드합니다.

```bash
dotnet build .\Biz.Bizadm.ModelRouter.slnx
```

## systemd 배포 (Linux)

unit 파일과 설치 스크립트는 게시 출력에 포함됩니다.

- `deploy/systemd/biz-bizadm-modelrouter.service`
- `deploy/systemd/install.sh`

기본 unit은 `/ModelRouter`를 작업 디렉터리로 사용합니다.

```bash
# 게시물 배포 후
sudo ./deploy/systemd/install.sh
```

서비스 상태·로그 확인:

```bash
systemctl status biz-bizadm-modelrouter
journalctl -u biz-bizadm-modelrouter -n 50 --no-pager
```

## 라이선스

MIT License. 자세한 내용은 [`LICENSE`](LICENSE)를 참조하세요.
