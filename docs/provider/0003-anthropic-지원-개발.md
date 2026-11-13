---
status: done
category: provider
feature: anthropic-지원
phase: 개발
issue: "#1"
---

# Anthropic 입력·공급자 지원 개발 계획

선행: [0001 방안](0001-anthropic-지원-방안.md), [0002 설계](0002-anthropic-지원-설계.md) (모두 done)

경로 약어: `S/` = `src/Biz.Bizadm.ModelRouter.Server/`, `T/` = `tests/Biz.Bizadm.ModelRouter.Tests/`

검증 공통: 각 단위가 끝나면 `dotnet build Biz.Bizadm.ModelRouter.slnx`(종료 코드 0)와 `dotnet test tests/Biz.Bizadm.ModelRouter.Tests`(실패 0)를 통과해야 한다. 단위가 끝날 때마다 커밋할지 확인한다.

---

## U1. 설정 스키마 교체

**대상**
- `S/Configuration.cs`: `Provider` enum 교체, `OpenAI` 속성과 `OpenAIConfiguration` 삭제, `ProviderConfiguration`·`ProviderModelConfiguration` 추가
- `S/Models/Provider/Azure/Foundry/AzureFoundryChatClient.cs`: `TrySplitModelEffortSuffix`를 `internal static`으로 노출(검증에서 사용)
- `T/ProviderSupportTests.cs`: 설정 테스트를 새 스키마로 다시 작성

**코드 초안**

```csharp
public enum ProviderType { AzureFoundry = 0, OpenAI = 1, Anthropic = 2 }
public enum ApiProtocol { OpenAI = 0, Anthropic = 1 }

public sealed class Configuration : IValidatableConfiguration
{
    // ... existing ContentRootPath, Port, Ssl, Kms, ReasoningByModel ...
    public Dictionary<string, ProviderConfiguration> Providers { get; set; } = null!;

    public void Validate()
    {
        if (Providers is null || Providers.Count == 0)
            throw new Exception("config field 'Providers' must contain at least one provider");

        HashSet<string> modelNames = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, ProviderConfiguration provider) in Providers)
        {
            ConfigurationValidator.Validate(provider); // ProviderConfiguration.Validate 호출
            foreach (ProviderModelConfiguration model in provider.Models!)
                if (!modelNames.Add(model.Name))
                    throw new Exception($"model '{model.Name}' is defined in more than one provider");
        }
        // ... existing Ssl, Kms validation ...
    }
}

public sealed class ProviderConfiguration : IValidatableConfiguration
{
    [Property(PropertyType.ENUM, required: true)] public ProviderType? Type { get; set; } // nullable: 누락 거부
    [Property(PropertyType.STRING, required: true)] public string Endpoint { get; set; } = null!;
    [Property(PropertyType.STRING, required: true)] public string Key { get; set; } = null!;
    public List<ProviderModelConfiguration>? Models { get; set; }

    public void Validate()
    {
        if (Type is null || !Enum.IsDefined(Type.Value))
            throw new ArgumentOutOfRangeException(nameof(Type), Type, "Provider 'Type' must be Anthropic, AzureFoundry or OpenAI.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new Exception($"provider 'Endpoint' must be an absolute http(s) URI: {Endpoint}");
        if (Models is null || Models.Count == 0)
            throw new Exception("provider must define at least one model in 'Models'");
        foreach (ProviderModelConfiguration model in Models)
        {
            if (string.IsNullOrWhiteSpace(model.Name))
                throw new Exception("model 'Name' must be provided");
            if (AzureFoundryChatClient.TrySplitModelEffortSuffix(model.Name, out _, out _))
                throw new Exception($"model name '{model.Name}' must not end with an effort suffix");
            ResolveApi(model); // Type/Api 불일치 시 throw
        }
    }

    public ApiProtocol ResolveApi(ProviderModelConfiguration model) => (Type!.Value, model.Api) switch
    {
        (ProviderType.AzureFoundry, var api) => api ?? ApiProtocol.OpenAI,
        (ProviderType.OpenAI, null or ApiProtocol.OpenAI) => ApiProtocol.OpenAI,
        (ProviderType.Anthropic, null or ApiProtocol.Anthropic) => ApiProtocol.Anthropic,
        _ => throw new Exception($"Api '{model.Api}' is not supported by provider type '{Type}'")
    };
}

public sealed class ProviderModelConfiguration
{
    public string Name { get; set; } = null!;   // 클라이언트용, 전체 고유
    public string? UpstreamModel { get; set; }  // 생략 시 Name, 중복 허용
    public ApiProtocol? Api { get; set; }

    public string ResolveUpstreamModel() => string.IsNullOrWhiteSpace(UpstreamModel) ? Name : UpstreamModel;
}
```

계산 속성(setter 없는 속성)은 두지 않는다. `WriteConfiguration` 재직렬화 때문이다(설계 2.1).

**검증**: `ProviderSupportTests`에 다음 테스트를 둔다.
- 정상 YAML 파싱, Foundry의 `Api` 기본값이 OpenAI인지
- 비어 있는 `Providers` 거부, `Models`가 없거나 빈 공급자 거부, `Name` 비어 있음 거부
- `Type` 누락 거부(속성 검증이 nullable enum을 잡지 못하면 `Validate()`의 직접 검사가 잡는지 확인), 알 수 없는 `Type`/`Api` 값 거부(`YamlException`)
- 상대 경로·`ftp:` 등 잘못된 `Endpoint` 거부
- 중복 모델 거부(대소문자 무시), 공급자가 달라도 `UpstreamModel`이 같으면 허용, `UpstreamModel`을 생략하면 `Name`과 같음, effort 접미사로 끝나는 `Name` 거부
- `OpenAI`+`Anthropic` 조합 거부, `Anthropic`+`OpenAI` 조합 거부
- 남은 레거시 `OpenAI:` 섹션 거부(`YamlException`)

빌드는 U2·U3가 끝나야 통과한다. 그래서 **U1~U3를 한 커밋 단위로 묶고**, 테스트도 U3가 끝난 뒤 함께 실행한다.

**선행 단위**: 없음

---

## U2. 보안정보 암호화·인증 헤더

**대상**
- `S/Program.cs`: `ProtectSecrets`, `CreateWebApplication`의 HttpClient·DI 등록(**기존 typed client `AddHttpClient<AzureFoundryChatClient>` 제거** — 삭제되는 `ConfigureProviderAuthentication`을 참조하고, 공급자별 생성 방식과 맞지 않음), `ConfigureProviderAuthentication` 삭제, Serilog Debug 오버라이드에 `AnthropicMessagesClient` 추가(타입은 U4에서 생기므로 문자열 카테고리로 등록), `ReasoningByModel` 키가 어느 `Name`과도 맞지 않으면 경고 로그(`ModelRouteResolver` 생성 시, DI 빌드 직후 `app.Services.GetRequiredService<ModelRouteResolver>()`로 미리 생성해 기동 시점에 남긴다. 레거시 `ReasoningEffortByModel`은 대상 아님)
- `S/Models/Provider/ProviderAuthentication.cs` (신규)
- `T/ConfigurationRoundTripTests.cs` (신규)

**코드 초안**

```csharp
// Program.ProtectSecrets
foreach (ProviderConfiguration provider in configuration.Providers.Values)
    provider.Key = EncryptIfNeeded(provider.Key);
if (rewrite) WriteConfiguration(configFileInfo, configuration);
foreach (ProviderConfiguration provider in configuration.Providers.Values)
    provider.Key = dekCipherService.Decrypt(provider.Key);

// Program.CreateWebApplication
builder.Services.AddHttpClient(ProviderClientRegistry.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<ProviderClientRegistry>();
builder.Services.AddSingleton<ModelRouteResolver>();
```

```csharp
internal static class ProviderAuthentication
{
    public const string DefaultAnthropicVersion = "2023-06-01";

    public static void Apply(HttpRequestMessage request, ProviderConfiguration provider, ApiProtocol api)
    {
        switch (provider.Type, api)
        {
            case (ProviderType.OpenAI, _):
                request.Headers.Authorization = new("Bearer", provider.Key); break;
            case (ProviderType.AzureFoundry, ApiProtocol.OpenAI):
                request.Headers.TryAddWithoutValidation("api-key", provider.Key); break;
            default: // Anthropic, AzureFoundry+Anthropic (U4에서 Foundry 헤더 확인)
                request.Headers.TryAddWithoutValidation("x-api-key", provider.Key); break;
        }
    }
}
```

`WriteConfiguration`의 직렬화 설정(OmitNull)을 테스트에서 재사용할 수 있게 `internal static ISerializer CreateConfigurationSerializer()`로 뽑는다.

**검증**
- `ConfigurationRoundTripTests`: 공급자 2개(`Api`/`UpstreamModel` 일부 null)를 직렬화하고 다시 역직렬화·검증했을 때 값이 같고, null 필드가 YAML에 나오지 않는지
- 인증 헤더는 U3 테스트에서 함께 확인한다(Anthropic/Foundry-Anthropic의 `x-api-key`, Foundry-OpenAI의 `api-key`, OpenAI의 Bearer)
- 수동 기동 확인: 평문 키 2개 이상인 설정으로 기동 → `DEKENC:`로 재작성 → 재기동 성공(로그 확인)

**선행 단위**: U1

---

## U3. 라우팅·공급자별 클라이언트

**대상**
- `S/Models/Provider/ModelRouteResolver.cs` (신규)
- `S/Models/Provider/ProviderClientRegistry.cs` (신규)
- `S/Models/Provider/ProviderEndpoints.cs` (신규, 설계 2.2 정규화)
- `S/Models/Provider/Azure/Foundry/AzureFoundryChatClient.cs`: 생성자, `SendAsync`의 인증, `upstreamModel` 인자, `ForwardModelsAsync`/`ForwardModelAsync` 삭제, `ModelReasoningPolicy`(`:789`)·`BuildReasoningLookup`(`:628`)을 `internal`로, `_reasoningByModel`(`:27`)을 `IReadOnlyDictionary`로
- `S/Controllers/ChatCompletionsController.cs`, `ResponsesController.cs`, `ModelsController.cs`: 라우트 해석, 로컬 모델 목록(OpenAI 형식)
- `S/Controllers/RequestBody.cs`, `UpstreamResponseWriter.cs`, `RouteErrors.cs` (신규 헬퍼)
- `T/ProviderSupportTests.cs`, `T/ChatResponsesBridgeUsageTests.cs`(새 생성자·`ProviderConfiguration`으로 교체), `T/ModelRouteResolverTests.cs` (신규)

**코드 초안**

```csharp
public sealed record ModelRoute(string ProviderName, ProviderConfiguration Provider, ApiProtocol Api,
    string Name, string UpstreamModel);

public sealed class ModelRouteResolver
{
    private readonly Dictionary<string, ModelRoute> _byModel; // OrdinalIgnoreCase

    /// <summary>미등록 모델이면 null을 반환하고, 호출한 쪽이 404를 응답한다.</summary>
    public ModelRoute? Resolve(string? requestedModel)
    {
        AzureFoundryChatClient.TrySplitModelEffortSuffix(requestedModel, out string? baseModel, out _);
        return _byModel.GetValueOrDefault(baseModel ?? string.Empty);
    }

    /// <summary>모델 단일 조회용. Name 정확 일치(대소문자 무시), 접미사를 떼지 않는다.</summary>
    public ModelRoute? FindExact(string? name) => _byModel.GetValueOrDefault(name ?? string.Empty);

    public IReadOnlyList<ModelRoute> Routes { get; } // 설정 순서
}
```

```csharp
public static class ProviderEndpoints
{
    public static string OpenAiBase(ProviderConfiguration p) => p.Type == ProviderType.AzureFoundry
        ? FoundryRoot(p.Endpoint) + "/openai"
        : AzureFoundryChatClient.NormalizeOpenAIEndpoint(p.Endpoint);

    public static string AnthropicBase(ProviderConfiguration p) => p.Type == ProviderType.AzureFoundry
        ? FoundryRoot(p.Endpoint) + "/anthropic"
        : AzureFoundryChatClient.NormalizeOpenAIEndpoint(p.Endpoint);

    internal static string FoundryRoot(string endpoint)
    {
        string root = AzureFoundryChatClient.NormalizeOpenAIEndpoint(endpoint);
        foreach (string suffix in new[] { "/openai", "/anthropic" })
            if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return root[..^suffix.Length];
        return root;
    }
}
```

```csharp
// AzureFoundryChatClient
public AzureFoundryChatClient(HttpClient httpClient, ProviderConfiguration provider,
    Configuration configuration, ILogger<AzureFoundryChatClient> logger)
{
    _provider = provider;
    _endpoint = ProviderEndpoints.OpenAiBase(provider);
    // ... existing uri / reasoning setup ...
}
// SendAsync 첫 줄
ProviderAuthentication.Apply(request, _provider, ApiProtocol.OpenAI);

public async Task<HttpResponseMessage> ForwardChatCompletionsAsync(Stream requestBody, string? contentType,
    CancellationToken cancellationToken, string? requestId = null, string? upstreamModel = null)
{
    // ... existing: parse, tool id rewrite, TrySplitModelEffortSuffix → model(=Name), suffixEffort ...
    _reasoningByModel.TryGetValue(model ?? string.Empty, out ModelReasoningPolicy? policy); // Name 기준 (기존 그대로)
    if (upstreamModel is not null && !string.Equals(upstreamModel, model, StringComparison.Ordinal))
    {
        root["model"] = upstreamModel;
        baseline = SerializeJsonObject(root);
    }
    // ... existing branching (effort 주입 / Bridge / PassThrough) ...
}
// ForwardResponsesAsync도 같은 방식
```

```csharp
// AzureFoundryChatClient: ModelReasoningPolicy를 internal로 바꾸고 lookup을 받는 internal 생성자 추가(설계 3.2)
internal AzureFoundryChatClient(HttpClient httpClient, ProviderConfiguration provider,
    IReadOnlyDictionary<string, ModelReasoningPolicy> reasoningByModel, ILogger<AzureFoundryChatClient> logger) { /* ... */ }
public AzureFoundryChatClient(HttpClient httpClient, ProviderConfiguration provider,
    Configuration configuration, ILogger<AzureFoundryChatClient> logger)
    : this(httpClient, provider, BuildReasoningLookup(configuration), logger) { }

// ProviderClientRegistry: 불변 상태(lookup 등)만 캐시하고 HttpClient는 호출마다 받는다
private readonly IReadOnlyDictionary<string, ModelReasoningPolicy> _reasoningByModel; // 생성 시 1회
public AzureFoundryChatClient GetOpenAi(ModelRoute route) =>
    new AzureFoundryChatClient(_httpClientFactory.CreateClient(HttpClientName), route.Provider, _reasoningByModel,
        _loggerFactory.CreateLogger<AzureFoundryChatClient>());
// GetAnthropic은 AnthropicMessagesClient가 생기는 U4에서 추가한다(U1~U3 커밋이 빌드되도록)
```

```csharp
// ChatCompletionsController (ResponsesController도 같은 방식)
byte[] body = await RequestBody.ReadAllAsync(Request.Body, cancellationToken);
string? model = RequestBody.TryGetModel(body);
ModelRoute? route = _resolver.Resolve(model);
if (route is null) { await RouteErrors.WriteModelNotFoundAsync(Response, ApiProtocol.OpenAI, model, cancellationToken); return; }
if (route.Api == ApiProtocol.OpenAI)
{
    using HttpResponseMessage upstream = await _registry.GetOpenAi(route).ForwardChatCompletionsAsync(
        new MemoryStream(body), Request.ContentType, cancellationToken, HttpContext.TraceIdentifier, route.UpstreamModel);
    await UpstreamResponseWriter.CopyAsync(HttpContext, upstream, cancellationToken);
    return;
}
// Anthropic 라우트는 U8에서 연결한다. 그 전에는 400을 반환한다.
```

- `UpstreamResponseWriter.CopyAsync`: `DisableBuffering`, 상태 코드, Content-Type, `Retry-After`(Anthropic 통과 경로는 `request-id`, `anthropic-ratelimit-*`도) 복사, 본문 복사(설계 3.3)
- `RouteErrors.WriteModelNotFoundAsync`: 설계 3.1의 404 본문을 입력 프로토콜 형식으로 작성
- `ModelsController`: `_resolver.Routes`로 OpenAI 형식 로컬 목록과 단일 조회를 응답한다. 단일 조회는 `Name` 정확 일치(대소문자 무시, 접미사를 떼지 않음)만 200이고 아니면 404다(설계 5절). Anthropic 형식 분기는 U8

**검증**
- `ModelRouteResolverTests`: 이름 매칭, 대소문자 무시, `-high` 접미사를 떼고 매칭, 미등록이거나 model이 null이면 null
- `ProviderEndpoints`: Foundry의 `.../openai`·`.../anthropic`·리소스 루트 입력이 모두 같은 루트가 되는지
- `ProviderSupportTests.ProviderAuthentication_IsUsedByAllForwardingPaths`: 새 생성자로 바꾸고, models 경로 2개를 빼서 3개 경로(chat, responses, bridge)의 헤더·호스트 단언을 유지
- 별칭: `Name: a`, `UpstreamModel: b`일 때 upstream 본문의 model이 `b`인지, `a-high`면 model은 `b`이고 effort는 high인지, `ReasoningByModel[a]` 정책이 적용되는지, 별칭이 없고 대소문자가 같으면 본문 바이트가 같은지, 별칭이 없는데 대소문자만 다르면 `Name` 표기로 바뀌는지
- 기존 `ChatResponsesBridge*` 테스트 통과
- 컨트롤러 404·모델 목록은 U8의 TestHost 테스트에서 확인한다

**선행 단위**: U1, U2

---

## U4. AnthropicMessagesClient (같은 프로토콜 통과)

**대상**
- `S/Models/Provider/Anthropic/AnthropicMessagesClient.cs` (신규)
- `S/Models/Provider/Anthropic/AnthropicReasoning.cs` (신규, 설계 4.5)
- `S/Models/Provider/ProviderClientRegistry.cs`: `GetAnthropic(ModelRoute)` 추가(`GetOpenAi`와 같은 방식)
- `T/AnthropicMessagesClientTests.cs` (신규)

**코드 초안**

```csharp
public sealed class AnthropicMessagesClient
{
    public Task<HttpResponseMessage> ForwardMessagesAsync(byte[] body, string? contentType,
        string? anthropicVersion, string? anthropicBeta, string upstreamModel, CancellationToken ct)
        => PostAsync(_messagesUri, RewriteModel(body, upstreamModel, applyEffort: true), contentType, anthropicVersion, anthropicBeta, ct);

    // D8: 접미사는 떼기만 하고 effort는 넣지 않는다
    public Task<HttpResponseMessage> ForwardCountTokensAsync(byte[] body, string? contentType,
        string? anthropicVersion, string? anthropicBeta, string upstreamModel, CancellationToken ct)
        => PostAsync(_countTokensUri, RewriteModel(body, upstreamModel, applyEffort: false), contentType, anthropicVersion, anthropicBeta, ct);

    private static byte[] RewriteModel(byte[] body, string upstreamModel, bool applyEffort)
    {
        if (!AnthropicJson.TryParse(body, out JsonObject? root)) return body;
        AzureFoundryChatClient.TrySplitModelEffortSuffix((string?)root["model"], out string? model, out string? effort);
        bool aliased = !string.Equals(model, upstreamModel, StringComparison.Ordinal);
        if (effort is null && !aliased) return body;
        root["model"] = upstreamModel;
        if (applyEffort && effort is not null) AnthropicReasoning.ApplyEffort(root, effort);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private async Task<HttpResponseMessage> PostAsync(Uri uri, byte[] payload, string? contentType,
        string? anthropicVersion, string? anthropicBeta, CancellationToken ct)
    {
        HttpRequestMessage request = new(HttpMethod.Post, uri) { Content = new ByteArrayContent(payload) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType ?? "application/json");
        request.Headers.TryAddWithoutValidation("anthropic-version", anthropicVersion ?? ProviderAuthentication.DefaultAnthropicVersion);
        if (anthropicBeta is not null) request.Headers.TryAddWithoutValidation("anthropic-beta", anthropicBeta);
        ProviderAuthentication.Apply(request, _provider, ApiProtocol.Anthropic);
        return await SendAsync(request, ct); // ResponseHeadersRead, 로그 형식은 AzureFoundryChatClient와 같게
    }
}

internal static class AnthropicReasoning
{
    // 설계 4.5 OpenAI → Anthropic 행
    public static void ApplyEffort(JsonObject root, string effort)
    {
        string normalized = effort.ToLowerInvariant();
        if (normalized is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max")) return;
        JsonObject outputConfig = root["output_config"] as JsonObject ?? new JsonObject();
        if (normalized == "none") { outputConfig["effort"] = "low"; root.Remove("thinking"); }
        else
        {
            outputConfig["effort"] = normalized == "minimal" ? "low" : normalized;
            root["thinking"] = new JsonObject { ["type"] = "adaptive" };
        }
        root["output_config"] = outputConfig;
    }
}
```

**검증**
- 가짜 handler로 URL을 확인한다. Anthropic은 `https://api.anthropic.com/v1/messages`와 `/v1/messages/count_tokens`, Foundry는 `https://res.services.ai.azure.com/anthropic/v1/messages`여야 한다.
- 헤더(`x-api-key`, `anthropic-version` 기본값과 전달값, `anthropic-beta`)를 확인한다.
- 접미사와 별칭이 없으면 본문이 바이트 단위로 같아야 한다.
- 별칭이면 본문의 `model`이 `UpstreamModel`로 바뀌어야 한다.
- `-high` 접미사가 있으면 `output_config.effort=high`와 `thinking.type=adaptive`, `-none`이면 thinking 없이 `effort=low`, count_tokens는 model만 바뀌고 `output_config`·`thinking`이 추가되지 않는지 확인한다.
- **실호출 확인(사용자 API 키 필요)**: Foundry 문서와 실호출로 Foundry Claude의 인증 헤더(`x-api-key`/`api-key`)와 `count_tokens` 지원 여부를 확인한다. 다르면 `ProviderAuthentication`과 설계 2.3을 고친다. 키가 없으면 이 항목은 `blocked`로 기록하고 보고한다.

**선행 단위**: U3

---

## U5. 요청 변환기 (양방향)

**대상**
- `S/Models/Conversion/OpenAiToAnthropicRequest.cs`
- `S/Models/Conversion/AnthropicToOpenAiRequest.cs`
- `S/Models/Conversion/AnthropicToolIdMapper.cs` (요청 단위 tool id 매핑. id 생성·역매핑 방식만 `ToolCallIdMapper`를 따르고, 허용 패턴 밖의 id만 바꾼다)
- `S/Models/Conversion/ConversionException.cs` (구조 오류·서버 도구 거부 → 컨트롤러가 입력 프로토콜 형식 400, 설계 4.4)
- `T/RequestConversionTests.cs`

**코드 초안**

```csharp
public static class OpenAiToAnthropicRequest
{
    public static JsonObject Convert(JsonObject chat, AnthropicToolIdMapper ids)
    {
        JsonObject result = new() { ["model"] = chat["model"]?.DeepClone() };
        JsonArray messages = new();
        List<string> system = new();

        foreach (JsonObject m in chat["messages"]!.AsArray().OfType<JsonObject>())
        {
            switch ((string?)m["role"])
            {
                case "system" or "developer": system.Add(TextOf(m["content"])); break;
                case "user": AppendUser(messages, ConvertUserContent(m["content"])); break;
                case "assistant": messages.Add(ConvertAssistant(m, ids)); break; // text + tool_use(input=ParseArguments)
                case "tool": AppendUser(messages, new JsonArray { ToolResult(m, ids) }); break; // 연속된 tool은 같은 user 메시지에 합친다
            }
        }
        if (system.Count > 0) result["system"] = string.Join("\n\n", system);
        result["messages"] = messages;
        result["max_tokens"] = (chat["max_completion_tokens"] ?? chat["max_tokens"])?.DeepClone() ?? 16000;
        // stop → stop_sequences(배열), user → metadata.user_id, tools → {name,description,input_schema},
        // tool_choice/parallel_tool_calls(설계 4.1), response_format(json_schema) → output_config.format, stream
        // temperature/top_p/n/logprobs/seed/json_object 등은 복사하지 않는다
        if ((string?)chat["reasoning_effort"] is { } effort) AnthropicReasoning.ApplyEffort(result, effort);
        return result;
    }

    // 전체를 try로 감싸 InvalidOperationException/FormatException/NullReferenceException → ConversionException
    // messages(배열)·요소(객체)·role(문자열) 명시 검사, 객체 아닌 요소는 ConversionException(OfType로 건너뛰지 않음)
    // tools: parameters 없으면 {"type":"object","properties":{}}, type != "function"이면 ConversionException
    // tools가 없거나 비면 tool_choice/parallel_tool_calls 버림, disable_parallel_tool_use는 auto/any/tool에만
    // response_format{type:json_schema, json_schema:{schema}} → output_config.format{type:"json_schema", schema}
    // 빈 content(null, "", 빈 text part)는 블록을 만들지 않는다. 블록이 0개인 메시지는 messages에 넣지 않는다.
    // system/tool content가 part 배열이면 text part를 이어 붙인다.
    // messages가 없거나 배열이 아님, role이 문자열이 아님, content 타입 오류 → ConversionException
    private static JsonObject ParseArguments(string? arguments)
    {
        try { if (JsonNode.Parse(arguments ?? "{}") is JsonObject obj) return obj; } catch (JsonException) { }
        return new JsonObject { ["raw_arguments"] = arguments };
    }
}
```

```csharp
public static class AnthropicToOpenAiRequest
{
    /// <summary>서버 도구가 있거나 구조가 잘못되면 ConversionException(→ 400, D11·설계 4.4)</summary>
    public static JsonObject Convert(JsonObject msg)
    {
        JsonArray messages = new();
        if (msg["system"] is { } sys) messages.Add(new JsonObject { ["role"] = "system", ["content"] = TextOf(sys) });
        foreach (JsonObject m in msg["messages"]!.AsArray().OfType<JsonObject>())
        {
            if ((string?)m["role"] == "assistant") messages.Add(ConvertAssistant(m)); // thinking 블록 제거, tool_use → tool_calls
            else
            {
                AppendToolMessages(messages, m);   // tool_result들을 먼저 role=tool로
                AppendUserRemainder(messages, m);  // 나머지 text/image를 그다음 user 하나로
            }
        }
        JsonObject chat = new() { ["model"] = WithEffortSuffix(msg), ["messages"] = messages };
        if (msg["max_tokens"] is { } maxTokens) chat["max_completion_tokens"] = maxTokens.DeepClone(); // null 속성 만들지 않음
        // stop_sequences → stop, tools(input_schema 없음 → 예외), tool_choice(any → required, tool → function),
        // disable_parallel_tool_use → parallel_tool_calls:false, output_config.format → response_format(json_schema),
        // metadata.user_id → user(64자 초과면 SHA-256 hex), temperature/top_p/top_k 제거
        // tool_result.is_error → "[error] " 접두, document/알 수 없는 블록 → ConversionException,
        // thinking 제거 후 빈 assistant 메시지는 생략
        // 변환 전체를 try로 감싸 InvalidOperationException/FormatException(캐스트 실패)도 ConversionException으로 바꾼다
        if (msg["stream"] is JsonValue s && s.TryGetValue(out bool stream) && stream)
        { chat["stream"] = true; chat["stream_options"] = new JsonObject { ["include_usage"] = true }; }
        return chat;
    }

    // D9: 설계 4.5 Anthropic → OpenAI 행. 원래 model에 접미사가 있으면 그대로 둔다
    private static string? WithEffortSuffix(JsonObject msg)
    {
        string? model = (string?)msg["model"];
        if (AzureFoundryChatClient.TrySplitModelEffortSuffix(model, out _, out _)) return model;
        string? effort = (string?)msg["output_config"]?["effort"]
            ?? ((string?)msg["thinking"]?["type"] == "disabled" ? "none" : null);
        return effort is "low" or "medium" or "high" or "xhigh" or "max" or "none" ? $"{model}-{effort}" : model;
    }
}
```

**검증**: `RequestConversionTests`에서 설계 4.1·4.2·4.5 표의 각 행을 1개 이상의 테스트로 확인한다. 특히 다음을 본다.
- 연속된 tool 메시지가 user 메시지 하나로 합쳐지는지, user 메시지에 tool_result와 text가 섞이면 role=tool이 먼저 오는지
- data URL 이미지가 base64로 바뀌는지
- `temperature`가 양방향 모두 제거되는지
- `reasoning_effort: none`이 thinking 없이 `low`, `minimal`이 `low`+adaptive로 바뀌는지
- Anthropic → OpenAI에서 effort가 model 접미사로 붙는지, 원래 접미사가 우선하는지, `thinking.enabled`만 있으면 접미사가 없는지
- thinking 블록이 제거되는지, `tool_result`의 `is_error`가 처리되는지
- 허용 문자 밖의 tool id가 매핑되는지, 잘못된 `arguments`가 `raw_arguments`가 되는지
- 서버 도구가 있으면 `ConversionException`이 나는지
- 빈 content: `content:null`/`""` + `tool_calls`인 assistant가 text 블록 없이 `tool_use`만 갖는지, 블록이 0개인 메시지가 빠지는지
- 배열 형태의 system/tool content가 이어 붙여지는지
- 구조 오류(`messages` 없음, `role` 숫자, `output_config.effort` 숫자 등)가 양방향 모두 `ConversionException`인지
- parameters 없는 tool에 빈 object schema가 들어가는지, function이 아닌 tool이 예외인지
- `json_schema` 양방향 구조 변환, `disable_parallel_tool_use` 역방향, `is_error` 접두, `document` 블록 예외, 긴 `user_id` 해시(64자, 같은 입력 → 같은 값)
- D9 통합: 변환 결과를 `ForwardChatCompletionsAsync`에 넣었을 때 정책 있음/없음·Bridge·`SupportsNone:false`(+`thinking.disabled` → low)에서 upstream effort가 기대값인지(가짜 handler)

**선행 단위**: U4 (`AnthropicReasoning` 재사용)

---

## U6. 비스트리밍 응답·오류 변환기

**대상**
- `S/Models/Conversion/AnthropicToOpenAiResponse.cs`
- `S/Models/Conversion/OpenAiToAnthropicResponse.cs`
- `S/Models/Conversion/StopReasonMap.cs`, `UsageMap.cs`
- `S/Models/Conversion/ErrorConverter.cs`
- `T/ResponseConversionTests.cs`

**코드 초안**

```csharp
public static class AnthropicToOpenAiResponse
{
    public static JsonObject Convert(JsonObject msg, AnthropicToolIdMapper ids)
    {
        StringBuilder text = new(); JsonArray toolCalls = new();
        foreach (JsonObject block in msg["content"]!.AsArray().OfType<JsonObject>())
        {
            switch ((string?)block["type"])
            {
                case "text": text.Append((string?)block["text"]); break;
                case "tool_use": toolCalls.Add(new JsonObject { ["id"] = ids.Restore((string)block["id"]!), ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = block["name"]!.DeepClone(), ["arguments"] = block["input"]!.ToJsonString() } }); break;
                // thinking / redacted_thinking: 버린다
            }
        }
        // choices[0].message{role=assistant, content, tool_calls}, finish_reason = StopReasonMap.ToOpenAi(stop_reason),
        // usage = UsageMap.ToOpenAi(usage), model = msg.model
    }
}

public static class OpenAiToAnthropicResponse // 설계 4.3 비스트리밍 행
{
    public static JsonObject Convert(JsonObject chat, AnthropicToolIdMapper ids)
    {
        JsonObject? choice = (chat["choices"] as JsonArray)?.FirstOrDefault() as JsonObject; // 빈 choices 안전
        JsonObject message = choice?["message"] as JsonObject ?? new JsonObject();
        JsonArray content = new();
        string? refusal = (string?)message["refusal"];
        string? text = refusal ?? TextOf(message["content"]);           // content null 허용
        if (!string.IsNullOrEmpty(text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (JsonObject call in message["tool_calls"]?.AsArray().OfType<JsonObject>() ?? [])
            content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = ids.Restore((string)call["id"]!),
                ["name"] = call["function"]?["name"]?.DeepClone(),
                ["input"] = OpenAiToAnthropicRequest.ParseArguments((string?)call["function"]?["arguments"]) }); // internal로 공유
        return new JsonObject
        {
            ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant",
            ["model"] = chat["model"]?.DeepClone(), ["content"] = content,
            ["stop_reason"] = refusal is not null ? "refusal" : StopReasonMap.ToAnthropic((string?)choice?["finish_reason"]),
            ["stop_sequence"] = null, ["usage"] = UsageMap.ToAnthropic(chat["usage"])
        };
    }
}

public static class StopReasonMap
{
    public static string ToOpenAi(string? r) => r switch
    { "max_tokens" or "model_context_window_exceeded" => "length", "tool_use" => "tool_calls", "refusal" => "content_filter", _ => "stop" };
    public static string ToAnthropic(string? r) => r switch
    { "length" => "max_tokens", "tool_calls" => "tool_use", "content_filter" => "refusal", _ => "end_turn" }; // stop_sequence는 판정 불가(설계 4.3)
}

public static class UsageMap // 설계 4.3 usage 표
{
    public static JsonObject ToOpenAi(JsonNode? anthropicUsage);   // total_tokens 포함
    public static JsonObject ToAnthropic(JsonNode? openAiUsage);   // input = prompt - cached
}

public static class ErrorConverter
{
    public static byte[] AnthropicToOpenAi(byte[] body);                 // {type:error,error:{type,message}} → {error:{message,type,code:null}}
    public static byte[] OpenAiToAnthropic(byte[] body, int statusCode); // error.type은 상태 코드로(설계 4.4)
    public static string AnthropicToOpenAiStreamChunk(JsonObject errorEvent);      // "data: {\"error\":...}\n\n"
    public static string OpenAiToAnthropicStreamEvent(JsonObject errorChunk); // "event: error\ndata: ...\n\n", type은 청크의 code/type으로(설계 4.4 스트림 중간 오류)
    // 파싱할 수 없으면 원문 문자열을 message에 넣는다
}
```

**검증**
- text, tool_use(id 복원 포함), thinking 제거, usage 양방향 공식(캐시 토큰·total_tokens 포함)
- `OpenAiToAnthropicResponse`: 필수 필드(`id` msg_ 접두, `type`, `role`, `stop_sequence:null`, `usage`), content null + tool_calls, 인자 JSON 파싱 실패 → `raw_arguments`, refusal → text 블록 + `stop_reason:refusal`, 블록 0개 → `content:[]`
- stop 매핑을 양방향으로 확인(`model_context_window_exceeded`, `pause_turn` 포함)
- 오류 본문 변환(상태 코드별 Anthropic `error.type`), 파싱할 수 없는 본문 처리
- 스트림 오류 청크의 `code`/`type`별 Anthropic `error.type`(`rate_limit_exceeded` → `rate_limit_error`, `server_error` → `api_error` 등)
- `choices`가 빈 응답이 `content:[]`로 변환되는지

**선행 단위**: U5

---

## U7. 스트리밍(SSE) 변환기

**대상**
- `S/Models/Conversion/SseReader.cs` (이벤트 단위 읽기. `\n`/`\r\n` 모두 허용, 여러 `data:` 줄은 `\n`으로 연결, 빈 줄이 경계)
- `S/Models/Conversion/AnthropicToOpenAiStream.cs`
- `S/Models/Conversion/OpenAiToAnthropicStream.cs`
- `T/StreamConversionTests.cs`

**코드 초안**

```csharp
public static class AnthropicToOpenAiStream
{
    public static async Task ConvertAsync(Stream upstream, Stream output, string fallbackModel, bool includeUsage,
        AnthropicToolIdMapper ids, CancellationToken ct)
    {
        string id = "chatcmpl-" + Guid.NewGuid().ToString("N"); string model = fallbackModel; int toolIndex = -1;
        Dictionary<int, (int Index, bool HasArgs)> blockToTool = new(); JsonObject? usage = null; bool terminated = false;
        await foreach (SseEvent e in SseReader.ReadAsync(upstream, ct))
        {
            if (!TryParseObject(e.Data, out JsonObject? data)) break; // JSON 아님 → 아래 비정상 종료 경로
            switch (e.Event ?? (string?)data["type"]) // event: 줄이 없으면 data.type으로 판정
            {
                case "message_start":
                    model = (string?)data["message"]?["model"] ?? fallbackModel;
                    usage = data["message"]?["usage"]?.DeepClone().AsObject();
                    await WriteChunkAsync(output, id, model, new JsonObject { ["role"] = "assistant" }, null, ct); break;
                case "content_block_start" when (string?)data["content_block"]?["type"] == "tool_use":
                    blockToTool[(int)data["index"]!] = (++toolIndex, false);
                    await WriteChunkAsync(output, id, model, ToolCallStart(toolIndex, data["content_block"]!, ids), null, ct); break; // arguments: ""
                case "content_block_delta":
                    // text_delta → {content}, input_json_delta → partial_json이 ""가 아닐 때만 arguments 조각(HasArgs=true)
                    // thinking_delta / signature_delta → 버린다
                    break;
                case "content_block_stop":
                    // tool 블록인데 HasArgs=false면 arguments:"{}" 조각을 보낸다
                    break;
                case "message_delta": usage = MergeUsage(usage, data["usage"]); // usage가 null이어도 새 객체를 돌려준다
                    await WriteChunkAsync(output, id, model, new JsonObject(), StopReasonMap.ToOpenAi((string?)data["delta"]?["stop_reason"]), ct); break;
                case "error":
                    await WriteRawAsync(output, ErrorConverter.AnthropicToOpenAiStreamChunk(data), ct);
                    await WriteRawAsync(output, "data: [DONE]\n\n", ct); return;
                case "message_stop":
                    if (includeUsage) await WriteUsageChunkAsync(output, id, model, UsageMap.ToOpenAi(usage), ct);
                    await WriteRawAsync(output, "data: [DONE]\n\n", ct); terminated = true; return;
                // ping: 버린다
            }
            await output.FlushAsync(ct);
        }
        if (!terminated) // message_stop 없이 끊김
        {
            await WriteRawAsync(output, StreamErrors.OpenAiChunk("stream ended before a terminal event"), ct);
            await WriteRawAsync(output, "data: [DONE]\n\n", ct);
        }
    }
}
```

```csharp
public static class OpenAiToAnthropicStream
{
    // 상태: text 블록 열림 여부, 블록 index, message_start 여부, usage, finish_reason, model,
    //       tools: SortedDictionary<int, (string Id, string Name, StringBuilder Args)>
    // model: 청크 model이 null/""이면 fallback(클라이언트 model)
    // message_start: choices가 비어 있지 않은 첫 청크(또는 오류·종료 직전)에서 보낸다
    // 첫 청크                 → message_start{message:{id:"msg_"+guid, type:"message", role:"assistant", model, content:[], usage:{input_tokens:0, output_tokens:0}}}
    // delta.content           → 빈 문자열이면 무시. 아니면 (필요하면 이전 블록 content_block_stop) content_block_start{text} + content_block_delta{text_delta}
    // choices가 빈 청크       → usage만 읽고 건너뛴다(Azure prompt_filter_results, usage 청크)
    // delta.tool_calls[i]     → 스트리밍하지 않고 tools[index]에 모은다. id/name은 처음 보는 index에서만 기록(id는 매핑 복원)
    // finish_reason / [DONE]  → text 블록 닫기 → index 순서로 tool마다 content_block_start(tool_use) +
    //                           input_json_delta(모은 인자, 비면 "{}") + content_block_stop (설계 4.3)
    // data가 JSON이 아님       → event: error 후 종료
    // finish_reason           → 저장
    // usage 청크              → UsageMap.ToAnthropic 저장
    // {"error":...} 청크      → ErrorConverter.OpenAiToAnthropicStreamEvent 후 즉시 종료(이후 [DONE] 무시)
    // [DONE]                  → content_block_stop, message_delta{delta:{stop_reason}, usage}, message_stop
    // [DONE] 없이 끊김         → event: error 후 종료
    // 이벤트 형식: "event: {type}\ndata: {json}\n\n"
}
```

**검증**
- 녹화한 SSE 텍스트(텍스트만, tool 2개 병렬, 인자 없는 tool, thinking 포함)를 입력해 출력 이벤트 순서와 내용을 단언한다.
- 오류: Bridge 형태의 `{"error":…}` + `[DONE]` 입력이 `event: error`로 끝나고 `message_stop`이 없는지, Anthropic `error` 이벤트가 오류 청크 + `[DONE]`이 되는지, 양방향 비정상 종료, 양방향 JSON이 아닌 data
- 교차 tool delta(index 0 → 1 → 0)에서 두 tool의 인자가 모두 온전한지
- 같은 index의 매 청크에 id가 반복돼도 tool_use 블록이 하나인지
- 인자 없는 tool(`partial_json:""`만 옴)이 `arguments:"{}"`로 끝나는지
- Azure 첫 청크(`choices:[]`, `model:""`) 뒤에도 `message_start.model`이 비어 있지 않은지
- `choices: []` 청크(첫 prompt_filter 청크, 마지막 usage 청크)에서 예외 없이 진행되는지
- `event:` 줄이 없는 Anthropic SSE가 `data.type`으로 처리되는지
- 빈 text delta가 블록을 열지 않는지
- `SseReader`: `\r\n` 줄 끝, 여러 줄 `data:`
- `include_usage`가 꺼져 있으면 usage 청크가 없어야 한다. usage 양방향 공식 확인
- 응답 model이 upstream 값인지(없을 때만 fallback)

**선행 단위**: U6

---

## U8. 컨트롤러 연결·엔드투엔드

**대상**
- `S/Controllers/MessagesController.cs` (신규, `messages`와 `messages/count_tokens`)
- `S/Controllers/ConvertedResponseWriter.cs` (신규, 실패 → `ErrorConverter`, SSE → 스트림 변환기, 그 밖 → 응답 변환기. `DisableBuffering`·`Retry-After` 규칙은 `UpstreamResponseWriter`와 같음)
- `S/Controllers/ChatCompletionsController.cs`: Anthropic 라우트 분기
- `S/Controllers/ResponsesController.cs`: Anthropic 라우트면 400
- `S/Controllers/ModelsController.cs`: Anthropic 형식 분기
- `S/Models/Conversion/TokenEstimator.cs` (신규, D8 근사치)
- `T/Biz.Bizadm.ModelRouter.Tests.csproj`: `Microsoft.AspNetCore.TestHost` 추가(D12, 패키지 선택 규칙으로 버전 선정)
- `T/EndToEndTests.cs` (신규, TestHost + 가짜 upstream `HttpMessageHandler`), `T/TokenEstimatorTests.cs` (신규)
- `S/Program.cs`: 테스트가 같은 DI·파이프라인을 쓸 수 있게 다음을 `internal static` 메서드로 뽑는다(동작 변경 없음)
  - `ConfigureServices(IServiceCollection, Configuration)`: 컨트롤러는 `AddControllers().AddApplicationPart(typeof(Program).Assembly)`로 등록한다. 테스트 프로세스에서는 진입 어셈블리가 testhost라 컨트롤러를 찾지 못하기 때문이다
  - `ConfigurePipeline(WebApplication)`: `RequestLoggingMiddleware`, `UseAuthorization`, `MapControllers`
  - Kestrel(`UseKestrel`·TLS), Serilog 파일 싱크, `AddSystemd`/`AddWindowsService`는 `CreateWebApplication`에 남겨 테스트에서 제외한다
  - 테스트는 `WebApplication.CreateBuilder` + `UseTestServer()` + 위 두 메서드를 쓰고, `services.AddHttpClient(ProviderClientRegistry.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => fakeHandler).SetHandlerLifetime(Timeout.InfiniteTimeSpan)`로 upstream을 가짜로 바꾼다(같은 handler 인스턴스가 교체 주기에 dispose되지 않게)
  - Anthropic 입력 → OpenAI 라우트의 응답 변환에는 항등 tool id mapper를 쓴다(기존 `ToolCallIdMapper`가 이미 원래 id로 복원하므로)

**코드 초안**

```csharp
[HttpPost("messages")]
public async Task Messages(CancellationToken ct)
{
    byte[] body = await RequestBody.ReadAllAsync(Request.Body, ct);
    string? model = RequestBody.TryGetModel(body);
    ModelRoute? route = _resolver.Resolve(model);
    if (route is null) { await RouteErrors.WriteModelNotFoundAsync(Response, ApiProtocol.Anthropic, model, ct); return; }
    string? version = Request.Headers["anthropic-version"], beta = Request.Headers["anthropic-beta"];

    if (route.Api == ApiProtocol.Anthropic)
    {
        using HttpResponseMessage up = await _registry.GetAnthropic(route).ForwardMessagesAsync(body, Request.ContentType, version, beta, route.UpstreamModel, ct);
        await UpstreamResponseWriter.CopyAsync(HttpContext, up, ct, anthropicPassThrough: true);
        return;
    }

    JsonObject msg = JsonNode.Parse(body)!.AsObject(); // model을 읽었으므로 객체임이 보장된다
    JsonObject chat;
    try { chat = AnthropicToOpenAiRequest.Convert(msg); }
    catch (ConversionException e) { await RouteErrors.WriteInvalidRequestAsync(Response, ApiProtocol.Anthropic, e.Message, ct); return; } // D11·설계 4.4
    using HttpResponseMessage upstream = await _registry.GetOpenAi(route).ForwardChatCompletionsAsync(
        new MemoryStream(Encoding.UTF8.GetBytes(chat.ToJsonString())), "application/json", ct, HttpContext.TraceIdentifier, route.UpstreamModel);
    await ConvertedResponseWriter.WriteAnthropicFromOpenAiAsync(HttpContext, upstream, fallbackModel: model!, ct);
}

[HttpPost("messages/count_tokens")]
public async Task CountTokens(CancellationToken ct)
{
    // ... body 읽기, Resolve, null이면 Anthropic 형식 404 ...
    // U4 실호출에서 Foundry Claude가 count_tokens를 지원하지 않는 것으로 확인되면
    // (route.Provider.Type == AzureFoundry)인 Anthropic 라우트도 아래 근사치 경로로 보낸다(설계 2.3)
    if (route.Api == ApiProtocol.Anthropic)
    {
        using HttpResponseMessage up = await _registry.GetAnthropic(route).ForwardCountTokensAsync(body, Request.ContentType, version, beta, route.UpstreamModel, ct);
        await UpstreamResponseWriter.CopyAsync(HttpContext, up, ct, anthropicPassThrough: true);
        return;
    }
    int estimate = TokenEstimator.EstimateAnthropicInput(JsonNode.Parse(body)!.AsObject());
    await Response.WriteAsJsonAsync(new { input_tokens = estimate }, ct);
}

public static class TokenEstimator
{
    // 요청 변환기와 같은 구조 검사·예외 감싸기(ConversionException → 컨트롤러가 Anthropic 형식 400)
    public static int EstimateAnthropicInput(JsonObject msg)
    {
        long chars = TextLength(msg["system"]);
        foreach (JsonObject tool in msg["tools"]?.AsArray().OfType<JsonObject>() ?? [])
            chars += Len((string?)tool["name"]) + Len((string?)tool["description"]) + Len(tool["input_schema"]?.ToJsonString());
        foreach (JsonObject m in msg["messages"]?.AsArray().OfType<JsonObject>() ?? [])
            chars += ContentLength(m["content"]); // text, tool_use.input JSON, tool_result text. image/thinking 제외
        return (int)Math.Min(int.MaxValue, (chars + 3) / 4);
    }
}
```

`ChatCompletionsController`의 Anthropic 분기는 위와 대칭이다(`OpenAiToAnthropicRequest` → `ForwardMessagesAsync` → `AnthropicToOpenAi*`, 요청 단위 `AnthropicToolIdMapper` 공유). 클라이언트가 보낸 `stream_options.include_usage`를 변환기에 전달한다.

```csharp
// ModelsController (upstream 호출 없음)
bool anthropicFormat = Request.Headers.ContainsKey("anthropic-version");
JsonObject list = anthropicFormat
    ? ModelListFormatter.Anthropic(_resolver.Routes)   // {data:[{type:"model",id,display_name:id,created_at:"1970-01-01T00:00:00Z"}],has_more:false,first_id,last_id}
    : ModelListFormatter.OpenAi(_resolver.Routes);     // {object:"list",data:[{id,object:"model",created:0,owned_by:providerName}]}
// GetModel: _resolver.FindExact(model)(Name 정확 일치, 접미사를 떼지 않음 — 설계 5절)가 null이면 404, 아니면 단일 객체
```

**검증**
- `EndToEndTests`(TestHost, 실제 라우팅·DI·미들웨어, 가짜 upstream): 설계 3.1의 모든 흐름(미등록 404, 파싱 불가 404, count_tokens 2가지, Responses+Anthropic 400, 서버 도구 400 포함)
  - 흐름마다 upstream URL, 변환된 요청 본문의 핵심 필드, 클라이언트 응답 형식(비스트리밍·SSE 각 1개 이상)
  - upstream 429 응답의 `Retry-After`가 클라이언트에 복사되는지, Anthropic 통과 경로에서 `request-id`·`anthropic-ratelimit-*`도 복사되는지
  - **Anthropic 라우트 tool 경로**: OpenAI 입력 + tools + stream → Anthropic 라우트에서 허용 패턴 밖 tool id가 매핑됐다가 클라이언트 SSE에서 원래 id로 복원되는지
  - **Bridge 경로**: Anthropic 입력 + tools + stream → `ChatToolsMode: BridgeToResponses`인 OpenAI 라우트. 가짜 upstream이 Responses SSE를 돌려주면 클라이언트가 messages SSE(`tool_use` 블록, 원래 tool id)를 받는지
  - 구조 오류 요청이 입력 프로토콜 형식 400인지(OpenAI 입력→Anthropic 라우트, Anthropic 입력→OpenAI 라우트)
  - 모든 컨트롤러 라우트가 404가 아닌지(ApplicationPart 확인)
  - 모델 목록 두 형식, upstream 호출 0회, 단일 조회 200·404
- `TokenEstimatorTests`: 알려진 입력(문자 수가 정해진 system·text·tool_use·tools)에 대한 기대값, 이미지·thinking 제외
- **실호출 확인(D10, 사용자 API 키 필요)**: effort 한계(설계 4.5), GPT reasoning 모델의 `stop` 거부 여부, 해시된 `user` 수용 여부도 함께 기록한다. Anthropic 라우트(또는 Foundry Claude)로 OpenAI 형식 tool 2턴 루프(첫 응답의 tool_calls + tool 결과로 두 번째 요청)를 보낸다. 400이면 결과를 사용자에게 보고하고 thinking 블록 캐시 단위를 추가로 계획한다. 키가 없으면 `blocked`로 기록하고 보고한다.

**선행 단위**: U7

---

## U9. 샘플 설정·문서

**대상**
- `S/Configuration.sample.yml`: `OpenAI:`를 `Providers` 예시로 교체
- `S/Biz.Bizadm.ModelRouter.Server.http`: `/v1/messages`, `/v1/messages/count_tokens` 예제 추가
- `ReadMe.md`: 주요 기능, API 표(`/v1/messages`, `/v1/messages/count_tokens`와 OpenAI 라우트의 근사치 동작·한계), 설정 표, 보안정보(`Providers[*].Key`), 수동 이전 안내(Foundry `Endpoint`를 리소스 루트로 적었던 경우 경로가 `/openai/v1/...`로 바뀜 포함), 미등록 모델·파싱 불가 본문 404(동작 변경), 모델 목록 로컬 응답, 변환 제약(비범위, 서버 도구 400, 구조 오류 400, thinking 블록 제거, effort 허용 범위, Bridge 경로의 `tool_choice`·`stop` 한계, `ReasoningByModel` 키는 `Name` 기준, Foundry 프로젝트 엔드포인트 미지원, Claude Code 소형 모델 등록 — 설계 4.5·4.6)

**코드 초안**

```yaml
Providers:
  foundry:
    Type: AzureFoundry # Anthropic | AzureFoundry | OpenAI (필수)
    Endpoint: "https://YOUR-RESOURCE.services.ai.azure.com" # Foundry 리소스 루트 (/openai, /anthropic 자동 부착)
    Key: "replace-with-provider-api-key"
    Models:
      - Name: foundry-claude-sonnet-5-5 # 클라이언트용 이름 (전체 고유)
        UpstreamModel: claude-sonnet-5-5 # 실제 upstream 이름 (생략 시 Name, 공급자 간 중복 허용)
        Api: Anthropic # AzureFoundry만 지정. 생략 시 OpenAI
      - Name: Bizadm-GPT-6-LUNA
```

**검증**
- 샘플 YAML이 `Deserializer`와 `ConfigurationValidator.Validate`를 통과하는지 테스트를 추가한다(`ProviderSupportTests`).
- 그 뒤 전체 빌드와 테스트를 실행한다.

**선행 단위**: U8

---

## 순서 요약

1. U1+U2+U3 (커밋 단위 1)
2. U4
3. U5
4. U6
5. U7
6. U8
7. U9
8. 완료 전 독립 검토 (vibe-harness 10절)
