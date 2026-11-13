using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;
using static Biz.Bizadm.ModelRouter.Server.Models.Conversion.JsonNodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>Anthropic messages 요청을 chat/completions 요청으로 변환한다(설계 4.2·4.5·D9·D11).</summary>
    public static class AnthropicToOpenAiRequest
    {
        private const int MaxUserLength = 64;
        private static readonly HashSet<string> SuffixEfforts = new(StringComparer.Ordinal) { "none", "low", "medium", "high", "xhigh", "max" };

        public static JsonObject Convert(JsonObject msg)
            => ConversionException.Guard(() => ConvertCore(msg));

        private static JsonObject ConvertCore(JsonObject msg)
        {
            JsonArray messages = new JsonArray();

            string system = TextOf(msg["system"]);
            if (system.Length > 0)
                messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

            foreach (JsonNode? node in RequireArray(msg, "messages"))
            {
                JsonObject message = RequireObject(node, "messages[] item");
                string role = GetString(message, "role") ?? throw new ConversionException("messages[].role must be provided.");
                switch (role)
                {
                    case "assistant":
                        if (ConvertAssistant(message["content"]) is { } assistant)
                            messages.Add(assistant);
                        break;
                    case "user":
                        AppendUser(messages, message["content"]);
                        break;
                    case "system":
                        string text = TextOf(message["content"]);
                        if (text.Length > 0)
                            messages.Add(new JsonObject { ["role"] = "system", ["content"] = text });
                        break;
                    default:
                        throw new ConversionException($"Unsupported message role '{role}'.");
                }
            }

            JsonObject chat = new JsonObject
            {
                ["model"] = WithEffortSuffix(msg),
                ["messages"] = messages
            };

            if (msg["max_tokens"] is { } maxTokens)
                chat["max_completion_tokens"] = maxTokens.DeepClone();

            if (msg["stop_sequences"] is JsonArray stops && stops.Count > 0)
                chat["stop"] = stops.DeepClone();

            ConvertTools(msg, chat);

            if (msg["output_config"]?["format"] is JsonObject format
                && GetString(format, "type") == "json_schema"
                && format["schema"] is JsonNode schema)
            {
                chat["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "response", ["schema"] = schema.DeepClone() }
                };
            }

            if (GetString(msg["metadata"], "user_id") is { Length: > 0 } userId)
                chat["user"] = userId.Length > MaxUserLength
                    ? System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant()
                    : userId;

            if (GetBool(msg, "stream"))
            {
                chat["stream"] = true;
                chat["stream_options"] = new JsonObject { ["include_usage"] = true };
            }

            return chat;
        }

        /// <summary>D9: 클라이언트 effort를 model 접미사로 붙인다. 원래 접미사가 있으면 그대로 둔다.</summary>
        private static string? WithEffortSuffix(JsonObject msg)
        {
            string? model = GetString(msg, "model");
            if (model is null || AzureFoundryChatClient.TrySplitModelEffortSuffix(model, out _, out _))
                return model;

            string? effort = GetString(msg["output_config"], "effort")?.Trim().ToLowerInvariant();
            if (effort is null && GetString(msg["thinking"], "type") == "disabled")
                effort = "none";

            return effort is not null && SuffixEfforts.Contains(effort) ? $"{model}-{effort}" : model;
        }

        private static JsonObject? ConvertAssistant(JsonNode? content)
        {
            StringBuilder text = new StringBuilder();
            JsonArray toolCalls = new JsonArray();

            foreach (JsonObject block in Blocks(content))
            {
                switch (GetString(block, "type"))
                {
                    case "text":
                        text.Append(GetString(block, "text"));
                        break;
                    case "tool_use":
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = GetString(block, "id") ?? throw new ConversionException("tool_use.id must be provided."),
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = GetString(block, "name") ?? throw new ConversionException("tool_use.name must be provided."),
                                ["arguments"] = (block["input"] ?? new JsonObject()).ToJsonString()
                            }
                        });
                        break;
                    case "thinking":
                    case "redacted_thinking":
                        break;
                    default:
                        throw new ConversionException($"Content block type '{GetString(block, "type")}' is not supported for OpenAI models.");
                }
            }

            if (text.Length == 0 && toolCalls.Count == 0)
                return null;

            JsonObject assistant = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = text.Length > 0 ? text.ToString() : null
            };
            if (toolCalls.Count > 0)
                assistant["tool_calls"] = toolCalls;
            return assistant;
        }

        /// <summary>tool_result는 먼저 role=tool로, 나머지 text/image는 그다음 user 메시지 하나로 내보낸다.</summary>
        private static void AppendUser(JsonArray messages, JsonNode? content)
        {
            if (content is JsonValue value && value.TryGetValue(out string? plain))
            {
                if (!string.IsNullOrEmpty(plain))
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = plain });
                return;
            }

            JsonArray parts = new JsonArray();
            foreach (JsonObject block in Blocks(content))
            {
                switch (GetString(block, "type"))
                {
                    case "tool_result":
                        string result = TextOf(block["content"]);
                        if (GetBool(block, "is_error"))
                            result = "[error] " + result;
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = GetString(block, "tool_use_id") ?? throw new ConversionException("tool_result.tool_use_id must be provided."),
                            ["content"] = result
                        });
                        break;
                    case "text":
                        if (GetString(block, "text") is { Length: > 0 } text)
                            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                        break;
                    case "image":
                        parts.Add(ConvertImage(block["source"]));
                        break;
                    default:
                        throw new ConversionException($"Content block type '{GetString(block, "type")}' is not supported for OpenAI models.");
                }
            }

            if (parts.Count > 0)
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
        }

        private static JsonObject ConvertImage(JsonNode? source)
        {
            string url = GetString(source, "type") switch
            {
                "base64" => $"data:{GetString(source, "media_type")};base64,{GetString(source, "data")}",
                "url" => GetString(source, "url") ?? throw new ConversionException("image.source.url must be provided."),
                var type => throw new ConversionException($"Image source type '{type}' is not supported for OpenAI models.")
            };
            return new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } };
        }

        private static void ConvertTools(JsonObject msg, JsonObject chat)
        {
            if (msg["tools"] is not JsonArray tools || tools.Count == 0)
                return;

            JsonArray converted = new JsonArray();
            foreach (JsonNode? node in tools)
            {
                JsonObject tool = RequireObject(node, "tools[] item");
                string? name = GetString(tool, "name");
                if (tool["input_schema"] is not JsonNode schema)
                    throw new ConversionException($"Server tool '{name ?? GetString(tool, "type")}' is not supported for OpenAI models.");

                JsonObject function = new JsonObject
                {
                    ["name"] = name ?? throw new ConversionException("tools[].name must be provided.")
                };
                if (GetString(tool, "description") is { } description)
                    function["description"] = description;
                function["parameters"] = schema.DeepClone();
                converted.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }

            chat["tools"] = converted;

            if (msg["tool_choice"] is JsonObject toolChoice)
            {
                switch (GetString(toolChoice, "type"))
                {
                    case "auto":
                        chat["tool_choice"] = "auto";
                        break;
                    case "any":
                        chat["tool_choice"] = "required";
                        break;
                    case "none":
                        chat["tool_choice"] = "none";
                        break;
                    case "tool":
                        chat["tool_choice"] = new JsonObject
                        {
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = GetString(toolChoice, "name") }
                        };
                        break;
                    default:
                        throw new ConversionException($"Unsupported tool_choice type '{GetString(toolChoice, "type")}'.");
                }

                if (GetBool(toolChoice, "disable_parallel_tool_use"))
                    chat["parallel_tool_calls"] = false;
            }
        }

        private static IEnumerable<JsonObject> Blocks(JsonNode? content)
        {
            switch (content)
            {
                case null:
                    yield break;
                case JsonValue value when value.TryGetValue(out string? text):
                    if (!string.IsNullOrEmpty(text))
                        yield return new JsonObject { ["type"] = "text", ["text"] = text };
                    yield break;
                case JsonArray blocks:
                    foreach (JsonNode? block in blocks)
                        yield return RequireObject(block, "content block");
                    yield break;
                default:
                    throw new ConversionException("content must be a string or an array.");
            }
        }
    }
}
