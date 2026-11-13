---
status: done
category: provider
feature: anthropic-지원
phase: 설계
issue: "#1"
---

# Anthropic 입력·공급자 지원 설계

선행: [0001-anthropic-지원-방안.md](0001-anthropic-지원-방안.md) (done)

## 1. 결정 목록 (합의)

| # | 주제 | 채택 |
| --- | --- | --- |
| D1 | 설정 구조 | `Providers` 맵의 공급자마다 `Models` 목록을 둔다. **`DefaultProvider`는 두지 않는다**(회귀 변경) |
| D2 | 기존 `OpenAI:` 섹션 | **설정 클래스에서 속성 자체를 삭제**하고 `Providers`로 교체한다. 레거시 감지·안내 코드는 두지 않는다. 기존 설정은 수동으로 옮기며, 남아 있으면 YamlDotNet이 알 수 없는 속성으로 거부한다 |
| D3 | `GET /v1/models` | **upstream을 호출하지 않고** 모든 `Providers[*].Models`로 로컬 목록을 만든다(회귀 변경). 요청에 `anthropic-version` 헤더가 있으면 Anthropic 형식으로 응답한다 |
| D4 | reasoning 매핑 | effort 단계를 매핑한다(4.5 표). thinking을 켤 때는 `adaptive`를 쓰고, OpenAI로 변환할 때 응답의 thinking 블록은 제거한다 |
| D5 | Anthropic 호출 방식 | SDK 없이 HTTP로 직접 호출한다. 프록시라서 원문 JSON을 보존하거나 변환해야 하기 때문이다 |
| D6 | 미등록 모델 | 어느 공급자의 `Models`에도 없으면 **404**를 입력 프로토콜 형식의 오류로 반환한다(회귀 추가) |
| D7 | 모델 별칭 | `Models[].Name`은 클라이언트에 보이는 이름이고 **전체에서 고유**해야 한다. `Models[].UpstreamModel`은 upstream에 보낼 실제 모델 ID/배포명이며 **생략하면 Name**과 같고, 공급자 사이에서 **겹쳐도 된다**(회귀 추가) |
| D8 | `POST /v1/messages/count_tokens` | 범위에 포함(회귀 추가). Anthropic 라우트는 upstream으로 전달하고(model만 `UpstreamModel`로 바꾸고 effort 접미사는 떼기만 함), OpenAI 라우트는 upstream을 호출하지 않고 **로컬 근사치**를 반환한다 |
| D9 | 변환 시 클라이언트 effort | Anthropic 입력을 OpenAI 계열로 보낼 때 클라이언트 effort를 **model 접미사로 바꿔** 기존 `ForwardChatCompletionsAsync`에 넘긴다(검토 회귀). 기존 우선순위(접미사 > 정책)·`NormalizeEffort`·Bridge가 그대로 적용되므로 **클라이언트 effort가 정책보다 우선**한다. 원래 model에 접미사가 있으면 그것이 우선이다 |
| D10 | thinking + tool 다중 턴 | OpenAI 클라이언트가 Anthropic 라우트로 tool 루프를 돌 때 thinking 블록을 돌려줄 수 없어 400이 날 수 있다(추정). **실호출로 확인한 뒤 결정**한다(U8). 재현되면 thinking 블록을 tool_use id 기준으로 서버에 짧게 캐시해 다시 붙이는 작업을 추가 단위로 계획한다(검토 회귀) |
| D11 | 변환 불가 입력 | Anthropic 입력을 OpenAI 계열로 변환할 때 `input_schema`가 없는 서버 도구(`web_search_*`, `bash_*`, `text_editor_*` 등)가 있으면 **Anthropic 형식 400**을 반환한다. `thinking.type=enabled`(`budget_tokens`)는 effort 지정 없음으로 보고 정책에 맡긴다(검토 회귀) |
| D12 | 엔드투엔드 테스트 | 테스트 프로젝트에 `Microsoft.AspNetCore.TestHost`를 추가해 실제 라우팅·DI·미들웨어를 거쳐 가짜 upstream으로 검증한다. 버전은 패키지 선택 규칙(TFM 호환·최신 패치·비취약)으로 정한다(검토 회귀) |

## 2. 설정 스키마

```yaml
Providers:
  foundry:
    Type: AzureFoundry            # Anthropic | AzureFoundry | OpenAI (필수, 생략 시 기동 오류)
    Endpoint: "https://RES.services.ai.azure.com"
    Key: "..."                    # 기동 시 KMS 암호화(DEKENC:)
    Models:
      - Name: foundry-claude-sonnet-5-5   # 클라이언트용 이름 (전체 고유)
        UpstreamModel: claude-sonnet-5-5  # 실제 upstream 이름 (생략 시 Name, 중복 허용)
        Api: Anthropic                    # AzureFoundry만 선택. 생략 시 OpenAI
      - Name: gpt-6-luna
  anthropic:
    Type: Anthropic
    Endpoint: "https://api.anthropic.com"
    Key: "..."
    Models:
      - Name: claude-sonnet-5-5           # 주 공급자는 원래 ID 그대로 (클라이언트의 이름 기반 기능 판별 유지)
      - Name: claude-opus-5-5
  openai:
    Type: OpenAI
    Endpoint: "https://api.openai.com"
    Key: "..."
    Models:
      - Name: gpt-6.1-sol
ReasoningByModel: ...            # 기존 유지 (OpenAI 프로토콜 라우트에 적용, 키는 클라이언트용 Name)
```

### 2.1 검증 규칙
- `Providers`는 1개 이상 있어야 한다.
- 각 공급자의 `Type`, `Endpoint`, `Key`는 필수이고, `Models`는 1개 이상 있어야 한다(등록된 모델만 라우팅하므로).
- `Endpoint`는 `http`/`https` 절대 URI여야 한다(첫 요청이 아니라 기동 시 오류가 나도록).
- `Type`은 nullable로 두어 **생략하면 기동 오류**로 처리한다. enum 0번 값으로 조용히 바뀌지 않게 하기 위해서다. 정의되지 않은 값도 거부한다.
- `Api` 기본값은 Type에 따라 정한다. Anthropic→`Anthropic`, OpenAI→`OpenAI`, AzureFoundry→`OpenAI`.
- `Api`가 Type과 맞지 않으면 기동 오류로 처리한다(`Type: OpenAI`+`Api: Anthropic`, `Type: Anthropic`+`Api: OpenAI`).
- `Models[].Name`은 필수다. 여러 공급자에 걸쳐 중복되면 대소문자 구분 없이 비교해 기동 오류로 처리한다.
- `Models[].UpstreamModel`은 선택이며 중복 검사를 하지 않는다. 비어 있으면 `Name`을 쓴다.
- `Name`이 effort 접미사(`-none`~`-max`)로 끝나면 기동 오류로 처리한다. 접미사 분리와 충돌하기 때문이다.
- 새 설정 클래스에는 setter 없는 계산 속성을 두지 않는다. `WriteConfiguration`이 설정 전체를 다시 직렬화하므로 재기동 시 역직렬화가 깨지지 않게 하기 위해서다.

### 2.2 엔드포인트 정규화
| Type/Api | 기준 URL | 경로 |
| --- | --- | --- |
| OpenAI | `Endpoint` (`/v1` 이후 제거, 기존 `NormalizeOpenAIEndpoint`) | `/v1/chat/completions`, `/v1/responses` |
| AzureFoundry + OpenAI | 리소스 루트 + `/openai` (`Endpoint`가 `/openai`·`/anthropic`·`/v1…`로 끝나면 제거한 뒤 붙임) | 위와 같음 |
| AzureFoundry + Anthropic | 리소스 루트 + `/anthropic` | `/v1/messages`, `/v1/messages/count_tokens` |
| Anthropic | `Endpoint` (`/v1` 이후 제거) | `/v1/messages`, `/v1/messages/count_tokens` |

### 2.3 인증 헤더 (요청마다 설정)
| 대상 | 헤더 |
| --- | --- |
| OpenAI | `Authorization: Bearer {Key}` |
| AzureFoundry (OpenAI) | `api-key: {Key}` (기존과 같음) |
| AzureFoundry (Anthropic) | `x-api-key: {Key}`, `anthropic-version` — *U4에서 Foundry 문서·실호출로 헤더 이름과 `count_tokens` 지원 여부를 확인한다. `count_tokens`를 지원하지 않으면 이 라우트의 count_tokens는 OpenAI 라우트와 같은 로컬 근사치로 대체한다* |
| Anthropic | `x-api-key: {Key}`, `anthropic-version` |

`anthropic-version`은 클라이언트가 보낸 값을 쓰고, 없으면 `2023-06-01`을 쓴다. Anthropic 프로토콜끼리 통과할 때는 `anthropic-beta`도 그대로 전달한다.

### 2.4 HttpClient 수명
- 공급자별로 캐시하는 것은 엔드포인트·URI·정책 같은 **불변 상태만**이다.
- `HttpClient`는 호출마다 `IHttpClientFactory.CreateClient`로 받는다. 팩토리의 핸들러 교체 주기를 유지해 DNS 변경을 반영하기 위해서다.

## 3. 구조

```
Controllers
  ChatCompletionsController  POST /v1/chat/completions   (기존, 라우팅 추가)
  ResponsesController        POST /v1/responses          (기존, Anthropic 라우트면 400)
  MessagesController         POST /v1/messages           (신규)
                             POST /v1/messages/count_tokens (신규, D8)
  ModelsController           GET  /v1/models[/{model}]   (설정 Models 로컬 응답·형식 분기)
        │
        ▼
Routing/ModelRouteResolver   model → ModelRoute(ProviderName, Provider, Api, Name, UpstreamModel) | null(미등록 → 404)
        │
        ├─ Api=OpenAI    → ProviderClientRegistry.GetOpenAi(route) : AzureFoundryChatClient (기존 로직, 공급자별)
        └─ Api=Anthropic → ProviderClientRegistry.GetAnthropic(route) : AnthropicMessagesClient (신규)
        │
Conversion/
  AnthropicToOpenAiRequest   messages → chat/completions 요청
  OpenAiToAnthropicResponse  chat 응답 → messages 응답 (비스트리밍)
  OpenAiToAnthropicStream    chat SSE  → messages SSE
  OpenAiToAnthropicRequest   chat/completions → messages 요청
  AnthropicToOpenAiResponse  messages 응답 → chat 응답
  AnthropicToOpenAiStream    messages SSE → chat SSE
  ErrorConverter             오류 본문·스트림 오류 이벤트 상호 변환 (상태 코드 유지)
  TokenEstimator             count_tokens 근사치 (D8)
```

### 3.1 흐름별 처리
| 입력 | Api | 처리 |
| --- | --- | --- |
| `/v1/chat/completions` | OpenAI | 기존 `ForwardChatCompletionsAsync`를 그대로 쓴다(ReasoningByModel, Bridge, tool id 매핑 포함) |
| `/v1/chat/completions` | Anthropic | 요청을 `OpenAiToAnthropicRequest`로 변환해 messages로 호출하고, 응답은 `AnthropicToOpenAi*`로 변환한다 |
| `/v1/messages` | Anthropic | 본문을 그대로 전달한다(model 접미사 effort만 적용). 응답도 그대로 전달한다 |
| `/v1/messages` | OpenAI | `AnthropicToOpenAiRequest`로 변환한 뒤(effort는 D9에 따라 model 접미사로) **기존 `ForwardChatCompletionsAsync`에 넘겨** 정책을 재사용하고, 응답은 `OpenAiToAnthropic*`로 변환한다. 서버 도구가 있으면 400(D11) |
| `/v1/responses` | Anthropic | 400을 반환한다(방안 비범위) |
| `/v1/messages/count_tokens` | Anthropic | upstream count_tokens로 전달한다. `anthropic-version`/`anthropic-beta`도 전달하고, 응답은 그대로 돌려준다 |
| `/v1/messages/count_tokens` | OpenAI | 로컬 근사치를 반환한다. `system`, 메시지의 text, `tool_use.input`(JSON 문자열), `tool_result`의 text, `tools` 각 항목의 name·description·input_schema(JSON)의 문자 수를 더해 4로 나누고 올림한다. 이미지·thinking 블록은 세지 않는다. 응답은 `200 {"input_tokens": N}`이다. 한국어 등은 과소추정될 수 있다 |
| 모든 입력 | 라우트 있음 | D7: upstream으로 보내기 직전에 `model`을 `UpstreamModel`로 바꾼다(effort 접미사는 기존 규칙대로 뗀다). 별칭이 없고 접미사도 없으며 요청 model이 `Name`과 대소문자까지 같으면 본문을 바이트 단위로 보존한다. 대소문자만 다르면 `UpstreamModel` 표기로 다시 직렬화한다. `ReasoningByModel`은 **클라이언트용 `Name`**으로 조회한다 |
| 모든 입력 | 라우트 없음 | 404를 반환한다. OpenAI 입력은 `{error:{message,type:"invalid_request_error",code:"model_not_found"}}`, Anthropic 입력은 `{type:"error",error:{type:"not_found_error",message}}` 형식이다. model이 없거나 본문을 파싱할 수 없을 때도 같다. **기존 동작 변경**: 지금은 파싱할 수 없는 본문을 그대로 upstream에 넘기지만 앞으로는 404다 |

응답의 `model` 필드는 upstream 응답(또는 청크)에 있는 값을 쓴다. 없을 때만 클라이언트가 보낸 model을 쓴다.

### 3.2 `AzureFoundryChatClient` 변경
- 생성자를 `(HttpClient, ProviderConfiguration, Configuration, ILogger)`로 바꾸고 엔드포인트를 공급자별로 받는다. 내부 로직은 바꾸지 않는다.
- `ReasoningByModel` lookup을 요청마다 다시 만들지 않도록, `ModelReasoningPolicy`와 `BuildReasoningLookup`을 `internal`로, `_reasoningByModel` 필드를 `IReadOnlyDictionary`로 바꾸고 미리 만든 lookup을 받는 `internal` 생성자를 추가한다. `ProviderClientRegistry`는 lookup을 한 번 만들어 이 생성자를 쓰고, 위 4인자 생성자(테스트용)는 내부에서 lookup을 만들어 위임한다.
- 공급자 인증 헤더는 `HttpClient` 기본 헤더 대신 요청마다 붙인다.
- `Program.ConfigureProviderAuthentication`을 요청 단위 헬퍼로 옮긴다.
- `ForwardModelsAsync`/`ForwardModelAsync`는 삭제한다(D3).
- 클래스 이름은 범위 밖이므로 바꾸지 않는다.

### 3.3 응답 쓰기 공통 규칙
- 모든 컨트롤러 경로에서 `IHttpResponseBodyFeature.DisableBuffering()`을 호출한다(기존과 같음).
- 상태 코드와 Content-Type을 복사한다.
- 헤더 `Retry-After`를 복사한다. Anthropic 통과 경로는 `retry-after`, `request-id`, `anthropic-ratelimit-*`도 복사한다.
- 로그: `Program`의 Serilog Debug 오버라이드에 `AnthropicMessagesClient`를 추가한다.

## 4. 변환 매핑

### 4.1 요청: chat/completions → messages
| OpenAI | Anthropic |
| --- | --- |
| `messages[role=system/developer]` | `system`(텍스트를 이어 붙임) |
| `user` content 문자열·`text` part | `text` 블록 |
| `image_url` (data URL / http URL) | `image` (`base64` / `url` source) |
| `assistant.content` + `tool_calls[]` | `text` 블록 + `tool_use{id,name,input}`. `input`은 `arguments`를 JSON 파싱한 객체이고, 파싱에 실패하거나 객체가 아니면 `{"raw_arguments": "<원문>"}` |
| 빈 content (`null`, `""`, 빈 text part) | **text 블록을 만들지 않는다**(Anthropic이 빈 text 블록을 거부). 블록이 0개인 메시지는 생략한다 |
| 배열 형태의 `system`/`tool` content | text part를 이어 붙인다 |
| `role=tool` (`tool_call_id`) | 다음 `user` 메시지의 `tool_result` 블록. 연속된 tool 메시지는 user 메시지 하나로 합침 |
| tool id(`tool_calls[].id`, `tool_call_id`) | `^[a-zA-Z0-9_-]+$`에 맞으면 그대로 쓰고, 맞지 않으면 요청 단위로 짧은 id에 매핑한다. 응답·스트림에서 원래 id로 되돌린다(id 생성·역매핑 메커니즘만 기존 `ToolCallIdMapper`와 같고, 바꾸는 대상은 허용 패턴 밖의 id뿐이다) |
| `tools[].function{name,description,parameters}` | `tools[]{name,description,input_schema}`. `parameters`가 없으면 `{"type":"object","properties":{}}`. `type`이 `function`이 아닌 tool은 `ConversionException`(400) |
| `tool_choice` `auto`/`none`/`required`/`{function}` | `auto`/`none`/`any`/`{type:tool,name}` (모델이 거부하면 upstream 오류를 그대로 전달) |
| `parallel_tool_calls:false` | `tool_choice.disable_parallel_tool_use:true`. `tool_choice`가 없으면 `{type:auto, disable_parallel_tool_use:true}`. 이 필드는 `auto`/`any`/`tool`에만 붙이고 `none`에는 붙이지 않는다 |
| `tools`가 없거나 빈 배열 | `tool_choice`·`parallel_tool_calls`를 버린다(Anthropic은 tools 없는 tool_choice를 거부) |
| `max_tokens`/`max_completion_tokens` | `max_tokens` (없으면 기본 16000) |
| `stop` (문자열 또는 배열) | `stop_sequences` (배열) |
| `stream`, `stream_options.include_usage` | `stream` (usage는 항상 받아 include_usage일 때 전달) |
| `user` | `metadata.user_id` |
| `temperature`/`top_p` | **제거**한다(최신 Claude 모델이 400을 반환) |
| `reasoning_effort` | 4.5 표 |
| `response_format: {type:json_schema, json_schema:{schema}}` | `output_config.format: {type:"json_schema", schema}` |
| `response_format: json_object`, 그 밖의 필드(`n`, `logprobs`, `seed` 등) | 제거 |

### 4.2 요청: messages → chat/completions
4.1의 역방향이다. 아래 항목은 별도로 처리한다.
- `system`(문자열 또는 블록 배열) → `system` 메시지 1개
- user 메시지 안의 `tool_result`(`is_error` 포함) → **먼저** `role=tool` 메시지들로 내보내고, 나머지 text/image 블록은 **그다음** user 메시지 하나로 내보낸다. tool_result 안에서는 text만 이어 붙이고 그 밖의 블록(image, document, search_result 등)은 버린다
- `thinking`/`redacted_thinking` 블록, `cache_control` → 제거
- effort → **D9**: `output_config.effort`(또는 `thinking.type=disabled`이면 `none`)를 4.5 표로 바꿔 model 접미사로 붙인다. 원래 model에 접미사가 있으면 그대로 둔다. `thinking.type=enabled`/`adaptive`만 있으면 접미사를 붙이지 않는다(D11)
- `tools`에 `input_schema`가 없는 항목(서버 도구)이 있으면 변환하지 않고 400(D11)
- `metadata.user_id` → `user`. 64자를 넘으면 SHA-256 hex(64자)로 바꾼다(Claude Code의 긴 user_id가 길이 제한에 걸리지 않게, 같은 사용자는 같은 값)
- `max_tokens` → `max_completion_tokens`
- `stop_sequences` → `stop`(GPT reasoning 모델이 `stop`을 거부하는지는 실호출로 확인하고, 거부하면 ReadMe 한계에 적는다)
- `tool_choice.disable_parallel_tool_use:true` → `parallel_tool_calls:false`
- `output_config.format{type:json_schema, schema}` → `response_format{type:json_schema, json_schema:{name:"response", schema}}`
- `tool_result.is_error:true` → tool 메시지 content 앞에 `"[error] "`를 붙인다
- `document` 블록과 알 수 없는 블록 타입 → `ConversionException`(400). 조용히 버리면 내용이 사라지기 때문이다
- thinking 블록을 빼고 나서 블록이 0개가 된 assistant 메시지 → 생략
- `temperature`/`top_p`/`top_k` → 제거(OpenAI reasoning 모델이 거부하므로 4.1과 대칭)

### 4.3 응답·스트림
| OpenAI `finish_reason` | Anthropic `stop_reason` |
| --- | --- |
| `stop` | `end_turn`, `stop_sequence: null` (OpenAI 응답은 어느 stop 시퀀스에 걸렸는지 알려주지 않으므로 항상 `end_turn`) |
| `length` | `max_tokens` |
| `tool_calls` | `tool_use` |
| `content_filter` | `refusal` |

Anthropic → OpenAI 방향에서 `pause_turn`은 `stop`, `model_context_window_exceeded`는 `length`로 매핑한다.

| usage 방향 | 매핑 |
| --- | --- |
| Anthropic → OpenAI | `prompt_tokens = input_tokens + cache_read_input_tokens + cache_creation_input_tokens`, `completion_tokens = output_tokens`, `prompt_tokens_details.cached_tokens = cache_read_input_tokens`, `total_tokens = prompt + completion` |
| OpenAI → Anthropic | `input_tokens = prompt_tokens - cached_tokens`, `cache_read_input_tokens = cached_tokens`, `output_tokens = completion_tokens` |

- **비스트리밍 chat 응답 → messages 응답**: `{id:"msg_…", type:"message", role:"assistant", model, content:[…], stop_reason, stop_sequence:null, usage}`. `content`는 text(비어 있으면 생략) 다음 `tool_use`를 둔다. `tool_calls[].function.arguments`는 4.1과 같은 규칙으로 `input` 객체로 바꾼다(실패 시 `raw_arguments`). `message.refusal`이 있으면 text 블록으로 넣고 `stop_reason=refusal`. 블록이 0개면 `content: []`.
- **chat SSE → messages SSE**
  - 첫 청크에서 `message_start`를 보낸다(`usage`는 0으로 시작). 텍스트 delta는 `content_block_start(text)`와 `text_delta`로, `tool_calls[i]` delta는 `content_block_start(tool_use, input:{})`와 `input_json_delta`로 바꾼다. 블록이 바뀌면 `content_block_stop`을 보낸다. 빈 text delta는 블록을 열지 않는다.
  - **tool 인자는 스트리밍하지 않고 index별로 버퍼에 모은다.** id·name은 처음 보는 `tool_calls[].index`에서 기록하고, 같은 index에 다시 와도 무시한다. `finish_reason`(또는 `[DONE]`) 시점에 열려 있는 text 블록을 닫고, index 순서대로 tool마다 `content_block_start(tool_use)` + `input_json_delta`(모은 인자 전체, 비어 있으면 `{}`) + `content_block_stop`을 보낸다. 교차 전송에도 인자가 온전하고 블록이 중복되지 않는다. text는 계속 스트리밍한다.
  - `choices`가 빈 청크(Azure `prompt_filter_results`, usage 청크)는 usage만 읽고 건너뛴다. 비스트리밍 응답도 `choices`가 비어 있으면 `content: []`로 처리한다.
  - `message_start`는 `choices`가 비어 있지 않은 첫 청크(또는 오류 청크·종료 직전)에서 보낸다. 청크의 `model`이 비어 있거나 `""`이면 클라이언트가 보낸 model을 쓴다.
  - `finish_reason`과 usage(위 OpenAI → Anthropic 공식)는 `message_delta`로, 끝은 `message_stop`으로 보낸다.
  - `data: {"error":{...}}` 청크(기존 Bridge의 중간 실패 포함)를 만나면 `event: error`(Anthropic 오류 형식)를 보내고 **즉시 종료**한다. 이후 `[DONE]`이 와도 `message_stop`을 보내지 않는다.
  - 업스트림이 `[DONE]` 없이 끊기거나 data가 JSON이 아니면 `event: error`를 보내고 종료한다.
- **messages SSE → chat SSE**
  - `text_delta`는 `delta.content`로 바꾼다. `tool_use` 시작은 `delta.tool_calls[index]{id,type,function{name, arguments:""}}`로, `input_json_delta`는 `arguments` 조각으로 바꾼다. 빈 문자열이 아닌 인자 조각이 하나도 없던 tool은 블록 종료 시 `arguments:"{}"` 조각을 보낸다(빈 `partial_json`은 내보내지 않는다).
  - `thinking_delta`와 `signature_delta`는 버린다. `ping`은 버린다.
  - `message_delta`는 `finish_reason` 청크로, include_usage이면 usage 청크를 추가하고, `message_stop`은 `data: [DONE]`으로 바꾼다.
  - `error` 이벤트는 `data: {"error":{...}}` 청크를 보낸 뒤 `[DONE]`으로 끝낸다(기존 Bridge와 같은 형태).
  - upstream이 `message_stop` 없이 끊기거나 data가 JSON이 아니면 `stream ended before a terminal event` 오류 청크와 `[DONE]`을 보낸다.
- SSE 읽기: 줄 끝은 `\n`과 `\r\n`을 모두 받는다. 한 이벤트에 `data:` 줄이 여러 개면 `\n`으로 이어 붙인다. 빈 줄이 이벤트 경계다.
- id: Anthropic `msg_…` ↔ `chatcmpl-…`는 그대로 쓰거나 새로 만든다. tool id는 4.1의 매핑 규칙을 따른다.

### 4.4 오류
- 상태 코드는 유지한다.
- Anthropic `{type:"error",error:{type,message}}`와 OpenAI `{error:{message,type,code}}`를 서로 변환한다.
- OpenAI → Anthropic 방향의 `error.type`은 상태 코드로 정한다: 400→`invalid_request_error`, 401→`authentication_error`, 403→`permission_error`, 404→`not_found_error`, 413→`request_too_large`, 429→`rate_limit_error`, 529→`overloaded_error`, 그 밖의 4xx→`invalid_request_error`, 그 밖의 5xx→`api_error`.
- Anthropic → OpenAI 방향은 Anthropic `error.type`을 그대로 `type`에 넣고 `code`는 null로 둔다.
- **스트림 중간 오류**(HTTP 상태는 이미 200)는 청크의 `error.code`/`type`으로 Anthropic `error.type`을 정한다: `rate_limit_exceeded`/`429`/TPM 관련 → `rate_limit_error`, `server_overloaded`/`overloaded` → `overloaded_error`, `invalid_request_error` → `invalid_request_error`, 그 밖(`server_error`, `incomplete_response` 등) → `api_error`.
- **캐스트 실패**: 변환기는 `messages`(배열), 각 요소(객체), `role`(문자열)을 명시적으로 검사하고, 객체가 아닌 메시지 요소는 건너뛰지 않고 `ConversionException`으로 처리한다. 그 밖에 JSON 값의 타입이 예상과 달라 생기는 `InvalidOperationException`/`FormatException`/`NullReferenceException`도 `ConversionException`으로 감싼다. 양방향 요청 변환기와 `TokenEstimator`에 모두 적용한다.
- 본문을 파싱할 수 없으면 원문을 message에 넣는다.
- **구조 오류**: model은 있지만 변환에 필요한 구조가 잘못된 요청(`messages` 없음·배열 아님, `role`이 문자열 아님, content 타입 오류 등)은 변환기가 `ConversionException`을 던지고, 컨트롤러가 **입력 프로토콜 형식의 400**(`invalid_request_error`)으로 응답한다. D11의 서버 도구 거부도 같은 예외 타입을 쓴다.

### 4.5 effort 값 매핑
| 방향 | 입력 값 | 결과 |
| --- | --- | --- |
| OpenAI → Anthropic | `none` | `thinking` 생략, `output_config.effort=low` |
| OpenAI → Anthropic | `minimal` | `thinking:{type:adaptive}`, `output_config.effort=low` |
| OpenAI → Anthropic | `low`/`medium`/`high`/`xhigh`/`max` | `thinking:{type:adaptive}`, `output_config.effort=같은 값` |
| Anthropic → OpenAI (접미사) | `output_config.effort` `low`~`max` | 같은 값의 접미사(`-low`~`-max`). `ReasoningByModel`과 `NormalizeEffort`가 적용된다 |
| Anthropic → OpenAI (접미사) | `thinking.type=disabled` (effort 없음) | `-none` 접미사(`SupportsNone:false`면 기존 규칙대로 `low`) |
| 모든 방향 | 그 밖의 값 | 무시하고 정책에 맡긴다 |

model 접미사와 본문 effort가 함께 있으면 **접미사가 우선**한다(기존 규칙).

알려진 한계: 대상 모델이 허용하지 않는 effort(예: GPT에 `max`, 일부 Claude에 `xhigh`)는 upstream이 400을 반환할 수 있다. 실호출 확인 항목에 넣고 ReadMe에 적는다.

### 4.6 알려진 한계 (비범위, ReadMe에 기재)
- 기존 `BridgeToResponses`는 chat `tool_choice`를 Responses로 그대로 복사하고 `stop`을 옮기지 않는다(`ChatResponsesBridge.cs:28`). 그래서 Anthropic 입력의 `tool_choice{type:tool}`·`stop_sequences`가 Bridge 경로에서 거부되거나 사라질 수 있다. 기존 OpenAI 입력에도 있는 문제이므로 이번 범위에서 고치지 않는다.
- `ReasoningByModel` 키는 클라이언트용 `Name` 기준이다. 별칭을 도입하면서 키를 배포명 그대로 두면 정책이 적용되지 않는다. 기동 시 어느 `Name`과도 맞지 않는 키는 경고 로그를 남긴다.
- Foundry **프로젝트** 엔드포인트(`.../api/projects/x`)는 지원하지 않는다. 리소스 엔드포인트를 적어야 한다.
- Claude Code는 보조 작업(제목·요약)에 소형 모델을 따로 호출한다. 그 모델을 `Models`에 등록하거나 `ANTHROPIC_DEFAULT_HAIKU_MODEL`을 등록된 이름으로 지정해야 404가 나지 않는다.
- 기존 AzureFoundry 설정에 `/openai` 없이 리소스 루트만 적었다면, 이전에는 `{root}/v1/...`로 호출했지만 이제 `{root}/openai/v1/...`로 호출한다. 수동 이전 안내에 적는다.

## 5. 모델 목록 (D3)
- upstream을 호출하지 않는다. 모든 `Providers[*].Models[].Name`을 설정 순서대로 나열한다(중복은 검증에서 이미 거부).
- 요청에 `anthropic-version` 헤더가 있으면 Anthropic 형식(`{data:[{type:"model",id,display_name,created_at}],has_more:false,first_id,last_id}`)으로, 없으면 OpenAI 형식(`{object:"list",data:[{id,object:"model",created,owned_by}]}`)으로 응답한다.
- `owned_by`에는 공급자 키 이름을 넣는다. `created`/`created_at`에는 고정값(0 / Unix epoch)을 넣고, `display_name`은 id와 같게 한다.
- `/v1/models/{model}`: `Name`과 **정확히 일치**(대소문자 무시, effort 접미사를 떼지 않음)하면 위 형식의 단일 객체를 반환하고, 아니면 D6의 404를 반환한다.

## 6. 보안정보
- `ProtectSecrets`는 모든 `Providers[*].Key`에 기존 `EncryptIfNeeded`/`Decrypt`를 적용한다.
- `Ssl.ServerCertificatePassword` 처리는 그대로 둔다.
- 재작성 후 YAML이 다시 역직렬화·검증되는지(`Api`/`UpstreamModel`이 null이면 생략되는지 포함) 테스트한다.

## 7. 테스트 범위
- 설정 검증: 2.1의 각 규칙, `Type` 누락, 알 수 없는 `Type`/`Api` 값, 남은 레거시 `OpenAI:` 섹션 거부, 직렬화→역직렬화→검증 왕복
- 라우팅: 이름 매칭(대소문자, effort 접미사 제거 뒤 매칭), 미등록 모델 null → 404
- count_tokens: Anthropic 라우트 전달(URL·헤더·model 교체), OpenAI 라우트 근사치(알려진 입력에 대한 기대값), 미등록 404
- 별칭: 같은 `UpstreamModel`을 쓰는 두 공급자로 각각 라우팅되는지, upstream 본문의 `model`이 바뀌는지, 별칭이 없으면 본문이 그대로인지
- effort(D9): 정책 있음/없음, Bridge, `SupportsNone:false`에서 `thinking.disabled`
- 변환: 4.1~4.5 요청·응답·SSE 단위 테스트(텍스트, tool, 이미지, thinking 제거, usage 양방향, stop 매핑, tool_result 순서, tool id 매핑, 잘못된 arguments, 서버 도구 400)
- 스트림 오류: Bridge 오류 청크, Anthropic `error` 이벤트, 양방향 비정상 종료
- 엔드투엔드(D12): TestHost로 실제 라우팅·DI를 거쳐 모든 흐름을 가짜 upstream으로 확인, 응답 헤더(`Retry-After`, Anthropic 통과 시 `request-id`·`anthropic-ratelimit-*`) 복사, **Anthropic 입력 + tools + stream → OpenAI 라우트(BridgeToResponses)** 3단 변환과 tool id 복원
- 구조 오류 400, JSON이 아닌 SSE data, 교차 tool delta, 같은 index에 id 반복, 빈 `choices`, parameters 없는 tool, 빈 content, 배열 content, 스트림 오류 type 매핑
- 엔드투엔드: OpenAI 입력 + tools + stream → Anthropic 라우트에서 tool id 복원
- 실호출(D10, 사용자 API 키 필요): Anthropic 라우트로 OpenAI 형식 tool 2턴 루프, Foundry Claude 헤더·count_tokens
- 기존 테스트(Bridge, ProviderSupport)가 통과하는지 확인. `ProviderSupportTests`·`ChatResponsesBridgeUsageTests`는 새 설정·생성자에 맞춰 수정한다
