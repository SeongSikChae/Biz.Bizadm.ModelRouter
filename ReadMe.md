# Biz.Bizadm.ModelRouter

Azure AI Foundry의 OpenAI 호환 API를 프록시하는 ASP.NET Core 서버입니다. 클라이언트에는 OpenAI 호환 엔드포인트를 제공하고, 요청을 설정된 Foundry 엔드포인트로 전달합니다.

## 주요 기능

- `chat/completions` 요청을 Azure AI Foundry로 전달
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
- 선택적 HTTPS (`UseSsl`), TLS 1.2/1.3, HTTP/1·2·3
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

- .NET 10 SDK 또는 런타임
- Azure AI Foundry의 OpenAI 호환 엔드포인트 및 API 키
- 서버 설정 YAML 파일
- `UseSsl: true`인 경우 PKCS#12/PFX 인증서 파일

## 설정

서버는 `--config`로 지정한 YAML 파일을 읽습니다. 샘플은 [`Configuration.sample.yml`](src/Biz.Bizadm.ModelRouter.Server/Configuration.sample.yml)을 참고하세요.

```yaml
ContentRootPath: /ModelRouter
Port: 80
# UseSsl: false
# ServerCertificate: server.pfx
# ServerCertificatePassword: replace-with-certificate-password
# CertificateChain: ""
# IncludeCipherSuites: "TLS_AES_256_GCM_SHA384,TLS_CHACHA20_POLY1305_SHA256,TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256"
OpenAIEndpoint: https://YOUR-RESOURCE.services.ai.azure.com/openai
OpenAIKey: replace-with-foundry-api-key

ReasoningByModel:
  YOUR-MODEL:
    Effort: medium
    ChatToolsMode: BridgeToResponses
    SupportsNone: true
```

| 항목 | 필수 | 설명 |
| --- | --- | --- |
| `ContentRootPath` | 예 | 콘텐츠 루트. 인증서 등 상대 경로의 기준 |
| `Port` | 예 | 수신 포트. `0.0.0.0:{Port}`에서 listen |
| `UseSsl` | 아니오 | HTTPS 사용 여부. 기본값 `false` |
| `ServerCertificate` | `UseSsl`일 때 | PKCS#12/PFX 경로 (`ContentRootPath` 기준 상대 또는 절대) |
| `ServerCertificatePassword` | `UseSsl`일 때 | 인증서 암호 |
| `CertificateChain` | 아니오 | 체인에 포함할 인증서 SimpleName (쉼표 구분) |
| `IncludeCipherSuites` | 아니오 | 허용 TLS 암호 스위트 (`TlsCipherSuite` enum 이름, 쉼표 구분) |
| `OpenAIEndpoint` | 예 | Foundry OpenAI 엔드포인트. `/v1` 이전까지 (`.../openai`) |
| `OpenAIKey` | 예 | Foundry API 키 (`api-key` 헤더로 전달) |
| `ReasoningByModel` | 아니오 | 모델별 Effort / ChatToolsMode / SupportsNone |
| `ReasoningEffortByModel` | 아니오 | 하위 호환 effort 문자열 맵 (tools 시 BridgeToResponses) |

`OpenAIEndpoint`는 `/v1` 이전까지만 지정합니다. `/v1` 이후가 포함되어 있으면 서버가 자동으로 제거한 뒤 `/v1/chat/completions`, `/v1/responses`, `/v1/models` 경로를 추가합니다.

### 모델별 reasoning / tools

`ReasoningByModel` 키는 Foundry에 등록한 배포명(요청 `model` 값)과 맞춥니다. 허용 effort·chat+tools 제약은 기반 모델에 따르며, YAML의 `SupportsNone` / `ChatToolsMode`로 라우터 정책만 지정합니다.

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

> **주의:** API 라우트 자체에는 인증이 없습니다. `UseSsl`이 꺼져 있으면 HTTP로 수신하므로, 외부에 노출할 때는 TLS·인증을 제공하는 프록시/게이트웨이 뒤에 두거나 `UseSsl: true`로 HTTPS를 켜세요.

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
