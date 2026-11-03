---
status: done
category: test
feature: mstest-전환
phase: 개발
---

# MSTest 전환 개발 계획

규칙 `dotnet/project-creation.md`는 테스트 프로젝트를 MSTest(기본)로 둔다. `tests/Biz.Bizadm.ModelRouter.Tests`는 xUnit 2.9.3을 사용하므로 MSTest(VSTest)로 전환한다.

- 범위: 테스트 프레임워크만. Directory.*.props, 프로젝트 이름, MTP 러너, 프로덕션 코드는 바꾸지 않는다.
- 기준선: `dotnet test Biz.Bizadm.ModelRouter.slnx` 통과 30 / 전체 30.

## 1. csproj 패키지 교체

- 대상: `tests/Biz.Bizadm.ModelRouter.Tests/Biz.Bizadm.ModelRouter.Tests.csproj`
- 코드 초안:

```xml
<ItemGroup>
  <PackageReference Include="coverlet.collector" Version="10.1.0"> <!-- ... existing ... --> </PackageReference>
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
  <PackageReference Include="MSTest" Version="4.5.2" />
</ItemGroup>

<ItemGroup>
  <Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" />
</ItemGroup>
```

- 검증: 패키지 복원 성공, `--vulnerable --include-transitive`, `--deprecated` 신규 문제 없음
- 선행 단위: 없음

## 2. 테스트 코드 변환

- 대상: `ProviderSupportTests.cs`, `ChatResponsesBridgeUsageTests.cs`, `ChatResponsesBridgeContentFilterTests.cs`
- 매핑: `[Fact]` → `[TestMethod]`, `[Theory]`+`[InlineData]` → `[TestMethod]`+`[DataRow]`, 클래스에 `[TestClass]`. `Assert.Equal` → `AreEqual`, `True/False/Null/NotNull` → `IsTrue/IsFalse/IsNull/IsNotNull`, `Throws<T>` → `ThrowsExactly<T>`, `Single` → `ContainsSingle`, 컬렉션 `Contains/DoesNotContain`은 predicate를 첫 인자로, `All` → `foreach`.
- 코드 초안:

```csharp
[TestClass]
public sealed class ProviderSupportTests
{
    [TestMethod]
    [DataRow("AzureFoundry", Provider.AzureFoundry)]
    [DataRow("OpenAI", Provider.OpenAI)]
    public void ParsesProvider(string value, Provider expected)
    {
        // ... existing ...
        Assert.AreEqual(expected, actual);
    }
}

JsonObject usageChunk = ParseFrame(Assert.ContainsSingle(
    frame => frame.Contains("\"choices\":[]", StringComparison.Ordinal), frames));
Assert.DoesNotContain(frame => frame.Contains("\"usage\":", StringComparison.Ordinal), frames);

foreach (HttpRequestMessage request in requests)
{
    Assert.AreEqual("https://api.openai.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
}
```

- 검증: `dotnet build Biz.Bizadm.ModelRouter.slnx` exit 0, `dotnet test` 통과 30 / 전체 30, `tests`에 xUnit 흔적 0건
- 선행 단위: 1
