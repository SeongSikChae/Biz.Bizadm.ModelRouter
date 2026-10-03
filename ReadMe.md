# Biz.Bizadm.ModelRouter

Azure AI Foundry의 OpenAI 호환 API를 프록시하는 ASP.NET Core 서버입니다. 클라이언트에는 OpenAI 호환 엔드포인트를 제공하고, 요청을 설정된 Foundry 엔드포인트로 전달합니다. 특정 모델의 `chat/completions` 요청에 함수 도구가 포함된 경우에는 설정에 따라 Responses API로 변환·우회할 수 있습니다.

## 주요 기능

- `chat/completions` 요청을 Azure AI Foundry로 전달
- Responses API를 직접 호출하고 응답을 그대로 전달
- 모델 목록 및 단일 모델 정보 조회
- 모델별 reasoning 설정이 있는 함수 도구 요청을 Responses API로 변환
  - `none`: Chat Completions API에 `reasoning_effort: none`을 적용
  - 그 외 값: Responses API 요청으로 변환하고 일반 응답 또는 SSE 스트림을 Chat Completions 형식으로 변환
- 콘솔 및 날짜별 파일 로그 기록
- Windows Service 및 systemd 호스팅 지원

## API

| 메서드 | 경로 | 설명 |
| --- | --- | --- |
| `POST` | `/v1/chat/completions` | Chat Completions 요청 프록시 |
| `POST` | `/v1/responses` | Responses 요청 프록시 |
| `GET` | `/v1/models` | 모델 목록 조회 |
| `GET` | `/v1/models/{model}` | 지정한 모델 조회 |

성공·오류 상태 코드, 응답 Content-Type 및 응답 본문은 upstream 응답에서 전달됩니다. Chat Completions와 Responses 엔드포인트는 스트리밍 응답을 지원하며, reasoning 설정으로 변환되는 Chat Completions 스트림은 SSE 형식으로 변환됩니다.

## 요구 사항

- .NET 10 SDK 또는 런타임
- Azure AI Foundry의 OpenAI 호환 엔드포인트 및 API 키
- 서버 설정 YAML 파일
- 설정의 `ServerCertificate` 경로에 존재하는 PKCS#12/PFX 인증서 파일

## 설정

서버는 `--config`로 지정한 YAML 파일을 읽습니다. 다음은 설정 예시입니다.

```yaml
ContentRootPath: C:/Biz/ModelRouter
Port: 8080
ServerCertificate: certificates/server.pfx
ServerCertificatePassword: replace-with-certificate-password
OpenAIEndpoint: https://YOUR-RESOURCE.services.ai.azure.com
OpenAIKey: replace-with-foundry-api-key

# 선택 사항: 지정한 모델의 함수 도구 요청에 적용할 reasoning effort
ReasoningEffortByModel:
  Bizadm-GPT-6-LUNA: medium
  Bizadm-GPT-6.1-SOL: medium
```

`ServerCertificate`는 `ContentRootPath` 기준 상대 경로 또는 절대 경로로 지정합니다. `OpenAIEndpoint`에는 `openai/v1`을 포함하지 않은 기본 endpoint를 지정합니다. 서버가 `/openai/v1/chat/completions`, `/openai/v1/responses`, `/openai/v1/models` 경로를 추가합니다.

`ReasoningEffortByModel`은 선택 항목입니다. 모델명은 대소문자를 구분하지 않습니다. 해당 설정이 없거나 모델이 목록에 없으면 Chat Completions 요청은 변환 없이 전달됩니다. 설정값이 `none`이 아닌 경우 함수 도구가 포함된 요청에 한해 Responses API로 우회합니다.

> **주의:** 현재 서버 시작 시 인증서 파일을 읽지만, Kestrel의 HTTPS 설정은 비활성화되어 있습니다. 실제 수신은 설정한 포트의 HTTP이며, 인증서가 HTTPS를 활성화하는 것은 아닙니다. 외부 네트워크에서 사용할 때는 인증 및 TLS를 제공하는 신뢰할 수 있는 프록시/게이트웨이 뒤에 두고 접근을 제한하세요. API 라우트 자체에는 인증이 설정되어 있지 않습니다.

## 실행

저장소 루트에서 실행합니다. `--config`와 `--log`는 필수 인자이며, `--log`에는 로그 파일이 생성될 디렉터리를 지정합니다.

```powershell
dotnet run --project .\src\Biz.Bizadm.ModelRouter.Server\Biz.Bizadm.ModelRouter.Server.csproj -- --config C:\Biz\ModelRouter\config.yml --log C:\Biz\ModelRouter\logs
```

게시된 애플리케이션은 동일한 인자를 전달해 실행합니다.

```powershell
Biz.Bizadm.ModelRouter.Server.exe --config C:\Biz\ModelRouter\config.yml --log C:\Biz\ModelRouter\logs
```

서버는 `0.0.0.0:{Port}`에서 수신합니다. 로그는 콘솔과 지정한 디렉터리에 기록되며, 파일 로그는 일 단위로 회전하고 최대 12개 파일을 보관합니다.

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

## 라이선스

MIT License. 자세한 내용은 [`LICENSE`](LICENSE)를 참조하세요.
