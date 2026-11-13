---
status: done
category: config
feature: 레거시effort맵제거
phase: 개발
---

# 하위 호환 설정 `ReasoningEffortByModel` 제거 — 개발 계획

이슈: SeongSikChae/Biz.Bizadm.ModelRouter#2

## 배경

`ReasoningEffortByModel`(모델명 → effort 문자열)은 `BuildReasoningLookup`에서 아래 정책으로 바뀌어 들어간다.

```yaml
ReasoningByModel:
  <모델>:
    Effort: <값>
    ChatToolsMode: BridgeToResponses
    SupportsNone: true
```

레거시만 할 수 있는 동작이 없다. 키가 겹치면 이미 `ReasoningByModel`이 이긴다. 정책 설정 경로를 `ReasoningByModel` 하나로 줄인다.

## 결정

- 자동 변환은 하지 않는다. 사용자가 설정을 직접 옮긴다.
- 기동 시 설정은 `new Deserializer()`(기본 설정)로 읽는다. 기본 설정은 모르는 키를 만나면 `YamlException`을 던진다. 그래서 속성을 지우면 레거시 키가 남은 설정은 기동하지 못한다. 이전 `OpenAI:` 섹션과 같은 방식이며, 별도 검사 코드는 넣지 않는다.
- `ReasoningByModel` 해석(대소문자 무시, 키 Trim, `ChatToolsMode` 파싱, `SupportsNone` 기본값 true)은 바꾸지 않는다.
- `ModelRouteResolver`의 키 경고는 원래 `ReasoningByModel`만 보므로 손대지 않는다.
- 과거 계획 문서 `docs/provider/0003-anthropic-지원-개발.md`의 레거시 언급은 그 시점의 기록이라 고치지 않는다.

## 단위 1. 설정·조회 코드에서 레거시 맵 제거

**대상**
- `src/Biz.Bizadm.ModelRouter.Server/Configuration.cs`: `ReasoningEffortByModel` 속성과 XML 주석
- `src/Biz.Bizadm.ModelRouter.Server/Models/Provider/Azure/Foundry/AzureFoundryChatClient.cs`: `BuildReasoningLookup`
- `tests/Biz.Bizadm.ModelRouter.Tests/ProviderSupportTests.cs`: 레거시 키 거부 테스트 추가

**코드 초안**

```csharp
// Configuration.cs — 아래 블록 삭제
/// <summary>
/// 하위 호환: 모델명 → effort 문자열 맵.
/// ...
/// </summary>
public Dictionary<string, string>? ReasoningEffortByModel { get; set; }
```

```csharp
// AzureFoundryChatClient.cs
internal static Dictionary<string, ModelReasoningPolicy> BuildReasoningLookup(Configuration configuration)
{
    Dictionary<string, ModelReasoningPolicy> lookup =
        new Dictionary<string, ModelReasoningPolicy>(StringComparer.OrdinalIgnoreCase);

    // (삭제) if (configuration.ReasoningEffortByModel is { Count: > 0 }) { ... }

    if (configuration.ReasoningByModel is { Count: > 0 })
    {
        // ... existing ...
    }

    return lookup;
}
```

```csharp
// ProviderSupportTests.cs
[TestMethod]
public void DeserializeConfiguration_RejectsRemovedReasoningEffortByModel()
{
    string yaml = CreateYaml(ValidProviders) + "ReasoningEffortByModel:\n  foundry-claude: medium\n";
    Assert.ThrowsExactly<YamlException>(() => DeserializeConfiguration(yaml));
}
```

기존 `DeserializeConfiguration_RejectsLegacyOpenAISection`과 같은 방식이다.

`Configuration.cs`의 `ReasoningByModel` 주석에서 "(권장)"을 뺀다. 비교 대상이던 레거시가 없어지기 때문이다.

```csharp
/// 모델별 reasoning 정책.
```

**검증**
- `dotnet build` 경고·오류 0
- `dotnet test` 전체 통과(새 테스트 포함)
- `grep ReasoningEffortByModel`로 `*.cs`에 남은 참조가 없음을 확인(테스트의 거부 YAML 문자열 제외)

**선행 단위**: 없음

## 단위 2. 샘플 설정·ReadMe 정리와 수동 변환 안내

**대상**
- `src/Biz.Bizadm.ModelRouter.Server/Configuration.sample.yml`: 주석 처리된 레거시 예시 블록
- `ReadMe.md`: 기능 목록의 하위 호환 줄, 설정 표의 `ReasoningEffortByModel` 행, "이전 설정에서 옮기기" 절

**코드 초안**

```yaml
# Configuration.sample.yml 48행 — 맨 앞 "권장: " 삭제
# 모델별 reasoning / chat+tools 정책 (키는 Providers[*].Models[].Name, OpenAI 프로토콜 라우트에 적용)

# Configuration.sample.yml — 아래 3줄 삭제
# 하위 호환(비권장): 모델명 → effort 문자열만 지정. tools가 있으면 BridgeToResponses로 취급.
# ReasoningEffortByModel:
#   Bizadm-GPT-6-LUNA: "medium"
```

````markdown
<!-- ReadMe.md, 기능 목록 -->
- 하위 호환: `ReasoningEffortByModel` 문자열 맵은 tools 시 `BridgeToResponses`로 취급   ← 삭제

<!-- ReadMe.md, 설정 표 -->
| `ReasoningEffortByModel` | 아니오 | ... |   ← 삭제

<!-- ReadMe.md, "이전 설정에서 옮기기" 끝에 추가 -->
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
````

18행의 "기존 `ReasoningByModel`·`BridgeToResponses` 정책 그대로 적용" 문구는 레거시 맵을 말하지 않으므로 그대로 둔다.

**검증**
- `grep ReasoningEffortByModel`로 ReadMe에는 이전 안내 절에만 남고, `Configuration.sample.yml`에는 없음을 확인
- `dotnet test`에서 `SampleConfiguration_DeserializesAndValidates` 통과(샘플 설정 파싱·검증)

**선행 단위**: 단위 1
