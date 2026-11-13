---
status: done
category: provider
feature: anthropic-지원
phase: 방안
issue: "#1"
---

# Anthropic 입력·공급자 지원 방안

## 1. 문제

현재 ModelRouter의 흐름은 다음 한 가지다.

```
OpenAI 호환 입력 ──> AzureFoundry | OpenAI (설정의 OpenAI.Provider 하나)
```

- 입력은 `/v1/chat/completions`, `/v1/responses`, `/v1/models`(OpenAI 형식)뿐이다.
- upstream은 설정 파일에 한 개만 지정하며, `AzureFoundryChatClient`가 모든 전달을 담당한다.
- Claude Code 등 Anthropic Messages API를 쓰는 클라이언트는 연결할 수 없고, Anthropic API·Foundry에 배포한 Claude 모델로도 보낼 수 없다.

## 2. 목표

```
Anthropic | OpenAI 입력 ──> Anthropic | AzureFoundry | OpenAI
```

| 입력 \ upstream 프로토콜 | Anthropic Messages (Anthropic API, Foundry Claude) | OpenAI 호환 (OpenAI, Foundry GPT 등) |
| --- | --- | --- |
| Anthropic `/v1/messages` | 그대로 전달 | Anthropic → chat/completions 변환 |
| OpenAI `/v1/chat/completions` | chat/completions → Anthropic 변환 | 기존 동작 |

## 3. 결정 사항 (합의)

| # | 주제 | 선택지 | 채택 |
| --- | --- | --- | --- |
| 1 | 프로토콜 변환 범위 | 전체 교차 변환 / 같은 프로토콜만 통과 / Anthropic 입력만 변환 | **전체 교차 변환** |
| 2 | upstream 구성 | 다중 공급자 + 모델 라우팅 / 단일 공급자 유지 | **다중 공급자 + 요청 `model` 이름 기반 라우팅** |
| 3 | Anthropic 입력 → AzureFoundry | Foundry Anthropic API / Foundry OpenAI 호환 변환 / 둘 다 | **Foundry Anthropic API**(`.../anthropic/v1/messages`) |
| 4 | 문서 카테고리 | provider / api | **provider** |

### 3.1 결정 1·2·3의 결합 해석

- 공급자는 종류(`Anthropic` | `AzureFoundry` | `OpenAI`)와 엔드포인트·키를 가진다. 여러 개를 동시에 등록한다.
- 요청 `model`로 라우트를 찾고, 라우트가 공급자와 upstream 프로토콜을 정한다.
  - `Anthropic` 공급자 → Anthropic Messages 프로토콜
  - `OpenAI` 공급자 → OpenAI 호환 프로토콜
  - `AzureFoundry` 공급자 → Foundry는 Claude(Anthropic API)와 GPT(OpenAI 호환)를 모두 호스팅하므로 라우트(모델)별로 프로토콜을 지정한다. Foundry Claude 모델은 결정 3에 따라 Anthropic API로 보낸다.
- 입력 프로토콜과 upstream 프로토콜이 같으면 통과, 다르면 변환한다(결정 1).

## 4. 채택 방안 개요

1. **입력 계층**: Anthropic Messages API 컨트롤러(`POST /v1/messages`, 스트리밍 포함)를 추가한다. 기존 OpenAI 컨트롤러는 유지한다.
2. **라우팅 계층**: 요청 `model` → (공급자, upstream 프로토콜). 설정에 등록한 모델만 허용하고, 매칭되는 라우트가 없으면 404를 반환한다(기본 공급자 없음 — 설계 회귀로 변경).
3. **공급자 계층**: 기존 `AzureFoundryChatClient`(OpenAI 호환 경로)는 유지하고, Anthropic Messages 클라이언트를 추가한다(Anthropic API: `x-api-key`+`anthropic-version`, Foundry Claude: Foundry 키 헤더).
4. **변환 계층**: Anthropic Messages ↔ OpenAI chat/completions 요청·응답·SSE 스트림 변환기를 추가한다.
   - 대상: system, 텍스트, 이미지, tools/tool_choice, tool_use/tool_result, stop_reason/finish_reason, usage, 스트리밍 이벤트.
   - reasoning: Anthropic `thinking` ↔ OpenAI `reasoning_effort` 매핑(세부는 설계).
5. **설정**: 다중 공급자·라우트 섹션을 추가한다. 키는 기존과 같이 KMS로 암호화한다. 기존 `OpenAI` 섹션은 제거하고 수동으로 이전한다(세부는 설계).

## 5. 비범위 (합의)

- `/v1/responses` 입력을 Anthropic upstream으로 변환 — 범위 밖, Anthropic 라우트면 오류 반환
- Anthropic Batches, Files API (`/v1/messages/count_tokens`는 이후 합의로 범위에 포함 — 설계 D8)
- 프롬프트 캐싱(`cache_control`) 등 한쪽에만 있는 기능의 상호 변환 — 같은 프로토콜 통과 시에만 보존, 변환 시 제거
- API 라우트 인증 추가 (현재 무인증 유지)

## 6. 유지할 기존 동작

- OpenAI 입력 → AzureFoundry/OpenAI 경로의 `ReasoningByModel`, effort 접미사, `BridgeToResponses`, tool call id 매핑
- KMS 보안정보 암호화, 로깅, TLS

의도된 동작 변경(설계에서 합의): 레거시 `OpenAI:` 설정 섹션 제거, 미등록 모델·model 없음·파싱 불가 본문은 upstream 전달 대신 404, `/v1/models`는 upstream 대신 설정 `Models`로 응답.
