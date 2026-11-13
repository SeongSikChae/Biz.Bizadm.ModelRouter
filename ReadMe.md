# Biz.Bizadm.ModelRouter

Anthropic, Azure AI Foundry, OpenAI API를 프록시하는 ASP.NET Core 서버입니다. 클라이언트에는 OpenAI 호환 엔드포인트와 Anthropic Messages API 엔드포인트를 함께 제공하고, 요청 `model` 이름으로 설정된 공급자를 골라 전달합니다.

```
Anthropic | OpenAI 입력 ──(model 이름으로 라우팅)──> Anthropic | AzureFoundry | OpenAI
```

## 주요 기능

- 입력: OpenAI 호환(`/v1/chat/completions`, `/v1/responses`) 및 Anthropic Messages API(`/v1/messages`, `/v1/messages/count_tokens`)
- upstream: 여러 공급자(`Anthropic`, `AzureFoundry`, `OpenAI`)를 동시에 등록하고 요청 `model`로 라우팅
  - Azure AI Foundry는 OpenAI 호환 모델(`/openai/v1/...`)과 Claude 모델(`/anthropic/v1/messages`)을 모델별로 선택
  - 모델 별칭: 클라이언트용 `Name`과 실제 upstream `UpstreamModel`을 분리 (같은 모델을 여러 공급자에서 사용 가능)
  - 등록되지 않은 모델은 입력 프로토콜 형식의 404
- 입력과 upstream 프로토콜이 다르면 교차 변환 (요청·응답·SSE 스트림·오류)
  - OpenAI 입력 → Anthropic 모델: chat/completions ↔ messages
  - Anthropic 입력 → OpenAI 계열 모델: messages ↔ chat/completions (기존 `ReasoningByModel`·`BridgeToResponses` 정책 그대로 적용)
- `/v1/models`는 설정의 `Models`로 응답 (`anthropic-version` 헤더가 있으면 Anthropic 형식)
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
- 콘솔 및 날짜별 파일 로그 기록 (`X-Request-Id` 응답 헤더 포함)
- 선택적 HTTPS (`Ssl.UseSsl`), TLS 1.2/1.3, HTTP/1·2·3
- 설정 보안정보(`Ssl.ServerCertificatePassword`, `Providers[*].Key`) 기동 시 암호화 저장 (`Biz.Bizadm.KMS`, OS 자격 증명 금고 KEK)
- Windows Service 및 systemd 호스팅 지원

## API

| 메서드 | 경로 | 설명 |
| --- | --- | --- |
| `POST` | `/v1/chat/completions` | Chat Completions. OpenAI 계열 모델은 그대로, Anthropic 모델은 변환해 전달 |
| `POST` | `/v1/responses` | Responses. OpenAI 계열 모델만 지원 (Anthropic 모델이면 400) |
| `POST` | `/v1/messages` | Anthropic Messages. Anthropic 모델은 그대로, OpenAI 계열 모델은 변환해 전달 |
| `POST` | `/v1/messages/count_tokens` | Anthropic 모델은 upstream으로 전달, OpenAI 계열 모델은 로컬 근사치(텍스트 문자 수 / 4) |
| `GET` | `/v1/models` | 설정의 `Models` 목록 (upstream 호출 없음) |
| `GET` | `/v1/models/{model}` | 지정한 모델 조회 (`Name` 정확 일치, 없으면 404) |

같은 프로토콜로 전달할 때는 성공·오류 상태 코드, Content-Type, 본문을 upstream 응답 그대로 전달합니다. 교차 변환할 때는 상태 코드를 유지하고 본문·오류·SSE를 입력 프로토콜 형식으로 바꿉니다. `Retry-After` 헤더는 항상 복사하고, Anthropic 통과 시 `request-id`, `anthropic-ratelimit-*`도 복사합니다. 모든 생성 엔드포인트는 스트리밍 응답을 지원합니다.

라우터가 직접 만드는 오류:

| 상황 | 상태 | 본문 |
| --- | --- | --- |
| 요청 `model`이 어느 `Models[].Name`에도 없음, `model` 없음, 본문이 JSON이 아님 | 404 | OpenAI 입력 `error.code: model_not_found`, Anthropic 입력 `error.type: not_found_error` |
| 변환할 수 없는 요청(서버 도구, `document` 블록, 구조 오류 등) | 400 | 입력 프로토콜 형식 `invalid_request_error` |

## 요구 사항

- .NET 10.0.13 이상 SDK 또는 ASP.NET Core 런타임
- 사용할 공급자(Anthropic, Azure AI Foundry, OpenAI)의 엔드포인트 및 API 키
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
Providers:
  foundry:
    Type: AzureFoundry
    Endpoint: https://YOUR-RESOURCE.services.ai.azure.com
    Key: replace-with-foundry-api-key
    Models:
      - Name: YOUR-MODEL                 # Foundry OpenAI 호환 배포 (Api 생략 = OpenAI)
      - Name: foundry-claude-sonnet-5-5  # 클라이언트용 이름
        UpstreamModel: claude-sonnet-5-5 # Foundry Claude 배포명
        Api: Anthropic
  anthropic:
    Type: Anthropic
    Endpoint: https://api.anthropic.com
    Key: replace-with-anthropic-api-key
    Models:
      - Name: claude-sonnet-5-5
  # openai:
  #   Type: OpenAI
  #   Endpoint: https://api.openai.com
  #   Key: replace-with-openai-api-key
  #   Models:
  #     - Name: gpt-6.1-sol

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
| `Providers` | 예 | 공급자 이름 → 공급자 설정. 1개 이상 |
| `Providers.<이름>.Type` | 예 | `Anthropic`, `AzureFoundry`, `OpenAI` |
| `Providers.<이름>.Endpoint` | 예 | http(s) 절대 URI. Anthropic `https://api.anthropic.com`, OpenAI `https://api.openai.com`, AzureFoundry는 **리소스 루트**(`https://RES.services.ai.azure.com`, 끝의 `/openai`·`/anthropic`·`/v1…`는 제거 후 경로를 붙임) |
| `Providers.<이름>.Key` | 예 | API 키. OpenAI는 `Authorization: Bearer`, AzureFoundry OpenAI 경로는 `api-key`, Anthropic과 AzureFoundry Anthropic 경로는 `x-api-key`. 기동 시 암호화되어 저장 |
| `Providers.<이름>.Models` | 예 | 이 공급자로 보낼 모델 목록. 1개 이상 |
| `Models[].Name` | 예 | 클라이언트가 요청 `model`로 보내는 이름. 모든 공급자에서 고유(대소문자 무시), effort 접미사(`-high` 등)로 끝나면 안 됨 |
| `Models[].UpstreamModel` | 아니오 | upstream에 보낼 실제 모델 ID/배포명. 생략 시 `Name`. 공급자 간 중복 허용 |
| `Models[].Api` | 아니오 | AzureFoundry에서만 `OpenAI`(기본) 또는 `Anthropic`. 다른 Type은 Type과 같은 값만 허용 |
| `Kms` | 예 | 보안정보 암호화 설정 섹션 |
| `Kms.DekFilePath` | 예 | KEK로 wrap된 DEK 파일 경로 (`ContentRootPath` 기준). 없으면 생성 |
| `Kms.KekSalt` | 아니오 | KEK PBKDF2 salt (Base64). 생략 시 생성되어 저장 |
| `Kms.KekIterations` | 아니오 | KEK PBKDF2 반복 횟수. 기본값 `600000` |
| `Kms.CredentialStore` | 아니오 | `GCM_CREDENTIAL_STORE` 값 (예: Linux 헤드리스의 `gpg`) |
| `ReasoningByModel` | 아니오 | 모델별 Effort / ChatToolsMode / SupportsNone |

`Endpoint`는 `/v1` 이전까지만 지정합니다. `/v1` 이후가 포함되어 있으면 서버가 제거한 뒤 경로를 붙입니다.

| Type / Api | 호출 경로 |
| --- | --- |
| OpenAI | `{Endpoint}/v1/chat/completions`, `/v1/responses` |
| AzureFoundry + OpenAI | `{리소스 루트}/openai/v1/chat/completions`, `/v1/responses` |
| AzureFoundry + Anthropic | `{리소스 루트}/anthropic/v1/messages`, `/v1/messages/count_tokens` |
| Anthropic | `{Endpoint}/v1/messages`, `/v1/messages/count_tokens` |

Anthropic 경로에는 `anthropic-version`(클라이언트 값, 없으면 `2023-06-01`)을 붙이고, Anthropic 입력을 그대로 전달할 때는 `anthropic-beta`도 전달합니다.

#### 이전 설정에서 옮기기

이전 형식의 `OpenAI:` 섹션은 더 이상 지원하지 않으며, 남아 있으면 기동 시 YAML 오류가 납니다. 수동으로 `Providers`로 옮기세요.

```yaml
# 이전
OpenAI:
  Provider: AzureFoundry
  OpenAIEndpoint: https://RES.services.ai.azure.com/openai
  OpenAIKey: DEKENC:...
# 이후
Providers:
  foundry:
    Type: AzureFoundry
    Endpoint: https://RES.services.ai.azure.com/openai   # 그대로 둬도 됨(/openai는 제거 후 다시 붙음)
    Key: DEKENC:...                                     # 같은 DEK·KEK를 쓰면 암호문 그대로 사용 가능
    Models:
      - Name: YOUR-MODEL                                # 클라이언트가 쓰던 model 이름을 모두 등록
```

- 이제 **등록된 모델만** 라우팅합니다. 클라이언트가 쓰던 model 이름을 `Models`에 모두 등록하세요(미등록은 404).
- 이전에 AzureFoundry `OpenAIEndpoint`를 `/openai` 없이 리소스 루트로 적었다면 호출 경로가 `{루트}/v1/...`에서 `{루트}/openai/v1/...`로 바뀝니다.
- Foundry **프로젝트** 엔드포인트(`.../api/projects/x`)는 지원하지 않습니다. 리소스 엔드포인트를 적으세요.
- `ReasoningByModel` 키는 `Models[].Name` 기준입니다. 별칭을 도입하면서 키를 배포명 그대로 두면 정책이 적용되지 않으며, 기동 시 경고 로그가 남습니다.
- 본문이 JSON이 아니거나 `model`이 없는 요청은 upstream으로 넘기지 않고 404를 반환합니다.

`ReasoningEffortByModel`은 더 이상 지원하지 않으며, 남아 있으면 기동 시 YAML 오류가 납니다. 같은 동작은 다음과 같이 옮깁니다.

```yaml
# 이전
ReasoningEffortByModel:
  YOUR-MODEL: medium
# 이후
ReasoningByModel:
  YOUR-MODEL:
    Effort: medium
    ChatToolsMode: BridgeToResponses
    SupportsNone: true
```

키는 `Models[].Name` 기준입니다. 맞는 `Name`이 없으면 정책이 적용되지 않고 기동 시 경고 로그가 남습니다.

### 보안정보 암호화

기동 시 `Ssl.ServerCertificatePassword`(값이 있을 때)와 모든 `Providers[*].Key`가 평문이면 DEK로 암호화해 `DEKENC:...` 형식으로 설정 파일을 다시 저장하고, 이미 암호문이면 파일을 바꾸지 않고 복호화만 해서 사용합니다.

- KEK 패스워드는 OS 자격 증명 금고의 `biz-bizadm-modelrouter-server://kek` / `default` 엔트리에 보관합니다. 엔트리가 없고 DEK 파일도 없으면 32바이트 랜덤 값으로 생성합니다.
- 엔트리 없이 DEK 파일만 있으면 복호화할 수 없으므로 기동에 실패합니다. 서버를 옮길 때는 금고 엔트리, DEK 파일, `Kms.KekSalt`를 함께 옮기거나 보안정보를 평문으로 다시 넣고 DEK 파일을 지운 뒤 기동하세요.
- 설정 파일을 다시 저장할 때 YAML이 재직렬화되어 주석은 제거됩니다. 파일 권한(ACL/Unix mode)은 유지됩니다.
- 이전 형식(루트의 `UseSsl`, `Provider`, `OpenAIEndpoint`, `OpenAIKey`, `OpenAI` 섹션)은 지원하지 않으므로 `Ssl`, `Providers`, `Kms` 섹션으로 옮겨야 합니다.

### 모델별 reasoning / tools

`ReasoningByModel` 키는 `Providers[*].Models[].Name`(요청 `model` 값)과 맞춥니다. 정책은 OpenAI 프로토콜 라우트(OpenAI, AzureFoundry OpenAI 경로)에 적용되며, Anthropic 입력이 OpenAI 계열 모델로 갈 때도 같은 정책을 거칩니다. 허용 effort·chat+tools 제약은 기반 모델에 따르며, YAML의 `SupportsNone` / `ChatToolsMode`로 라우터 정책만 지정합니다.

#### 모델명 effort 접미사

요청 `model` 끝에 `-none`, `-low`, `-medium`, `-high`, `-xhigh`, `-max`를 붙이면 YAML `Effort`보다 우선해 해당 effort를 주입합니다. upstream에는 접미사를 뺀 이름의 `UpstreamModel`을 전달합니다. Anthropic 모델에서는 접미사가 `thinking: {type: adaptive}` + `output_config.effort`가 되고, `-none`은 thinking 없이 `effort: low`가 됩니다.

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

### 프로토콜 교차 변환

| 항목 | OpenAI 입력 → Anthropic 모델 | Anthropic 입력 → OpenAI 계열 모델 |
| --- | --- | --- |
| system | `system`/`developer` 메시지 → `system` | `system` → system 메시지 |
| tool | `tools`/`tool_calls`/`tool` 메시지 ↔ `tools`/`tool_use`/`tool_result` | 역방향 (`tool_result.is_error`는 내용 앞에 `[error] `) |
| tool id | Anthropic 허용 문자(`^[a-zA-Z0-9_-]+$`) 밖의 id는 요청 단위로 매핑했다가 응답에서 복원 | 그대로 |
| effort | `reasoning_effort`(`none`→thinking 없이 `low`, `minimal`→`low`) → `thinking: adaptive` + `output_config.effort` | `output_config.effort`(`thinking.disabled`→`none`)를 **model 접미사**로 붙여 `ReasoningByModel`보다 우선 적용 |
| 제거 | `temperature`, `top_p`, `n`, `seed`, `logprobs`, `response_format: json_object` | `temperature`, `top_p`, `top_k`, thinking 블록, `cache_control` |
| 기타 | `user`→`metadata.user_id`, `json_schema`→`output_config.format` | `metadata.user_id`→`user`(64자 초과 시 SHA-256), `output_config.format`→`json_schema` |
| 응답 | thinking 블록 제거, `stop_reason`→`finish_reason`, usage 변환 | `finish_reason`→`stop_reason`(`stop`은 항상 `end_turn`), usage 변환 |
| 스트림 | Anthropic SSE → `chat.completion.chunk` (`include_usage`면 usage 청크) | chat 청크 → Anthropic SSE (tool 인자는 모았다가 종료 시 전송) |

알려진 한계:

- Anthropic 입력의 서버 도구(`web_search_*`, `bash_*`, `text_editor_*` 등 `input_schema` 없는 tool)와 `document` 블록은 OpenAI 계열 모델로 변환할 수 없어 400을 반환합니다.
- OpenAI 입력에서 Anthropic 모델로 tool 루프를 돌 때, OpenAI 형식은 thinking 블록을 되돌려 보낼 수 없으므로 모델에 따라 두 번째 요청부터 upstream이 거부할 수 있습니다(실호출 확인 전).
- 대상 모델이 허용하지 않는 effort(예: GPT에 `max`, 일부 Claude에 `xhigh`)는 upstream이 400을 반환할 수 있습니다.
- `BridgeToResponses` 경로는 chat `tool_choice`를 Responses로 그대로 복사하고 `stop`·`response_format`을 옮기지 않으므로, Anthropic 입력의 `tool_choice: {type: tool}`·`stop_sequences`·`output_config.format`이 이 경로에서 거부되거나 사라질 수 있습니다(기존 동작).
- 스트리밍 중 upstream 연결이 끊기면 입력 프로토콜 형식의 오류 이벤트(OpenAI: 오류 청크 + `[DONE]`, Anthropic: `event: error`)로 끝냅니다.
- OpenAI 계열 모델의 `count_tokens`는 텍스트 문자 수 / 4 근사치이며, 한국어 등은 과소추정될 수 있습니다.

### Claude Code·Cursor 연결

| 클라이언트 | 설정 | 사용 엔드포인트 |
| --- | --- | --- |
| Claude Code | `ANTHROPIC_BASE_URL=http(s)://<router>`, 모델은 `ANTHROPIC_MODEL` 또는 `/model`로 `Models[].Name` 지정. 라우터에 인증이 없으므로 `ANTHROPIC_AUTH_TOKEN`은 임의 값 | `/v1/messages`, `/v1/messages/count_tokens`, `/v1/models` |
| Cursor | Override OpenAI Base URL에 `http(s)://<router>/v1`, 모델 이름을 `Models[].Name`과 같게 추가 | `/v1/chat/completions` |

Claude Code는 제목·요약 같은 보조 작업에 소형 모델을 따로 호출합니다. 그 모델을 `Models`에 등록하거나 `ANTHROPIC_DEFAULT_HAIKU_MODEL`을 등록된 이름으로 지정해야 404가 나지 않습니다. 클라이언트가 모델 이름으로 기능(context window, thinking 지원 등)을 판별하므로, 주로 쓰는 공급자에는 원래 모델 ID를 `Name`으로 두고 보조 공급자에만 별칭을 주는 것을 권장합니다.

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

```bash
curl http://localhost:8080/v1/messages \
  -H "Content-Type: application/json" \
  -H "anthropic-version: 2023-06-01" \
  -d '{"model":"claude-sonnet-5-5","max_tokens":1024,"messages":[{"role":"user","content":"안녕하세요"}]}'
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
