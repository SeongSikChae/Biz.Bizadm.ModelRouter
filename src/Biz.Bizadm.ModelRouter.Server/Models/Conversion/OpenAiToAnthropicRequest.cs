using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Anthropic;
using static Biz.Bizadm.ModelRouter.Server.Models.Conversion.JsonNodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>chat/completions 요청을 Anthropic messages 요청으로 변환한다(설계 4.1).</summary>
    public static class OpenAiToAnthropicRequest
    {
        private const int DefaultMaxTokens = 16000;

        public static JsonObject Convert(JsonObject chat, AnthropicToolIdMapper ids)
            => ConversionException.Guard(() => ConvertCore(chat, ids));

        private static JsonObject ConvertCore(JsonObject chat, AnthropicToolIdMapper ids)
        {
            JsonObject result = new JsonObject { ["model"] = chat["model"]?.DeepClone() };
            JsonArray messages = new JsonArray();
            List<string> system = new List<string>();

            foreach (JsonNode? node in RequireArray(chat, "messages"))
            {
                JsonObject message = RequireObject(node, "messages[] item");
                string role = GetString(message, "role") ?? throw new ConversionException("messages[].role must be provided.");
                switch (role)
                {
                    case "system":
                    case "developer":
                        string text = TextOf(message["content"]);
                        if (text.Length > 0)
                            system.Add(text);
                        break;
                    case "user":
                        Append(messages, "user", ConvertUserContent(message["content"]));
                        break;
                    case "assistant":
                        Append(messages, "assistant", ConvertAssistant(message, ids));
                        break;
                    case "tool":
                        Append(messages, "user", new JsonArray { ConvertToolResult(message, ids) });
                        break;
                    default:
                        throw new ConversionException($"Unsupported message role '{role}'.");
                }
            }

            if (system.Count > 0)
                result["system"] = string.Join("\n\n", system);
            result["messages"] = messages;
            result["max_tokens"] = (chat["max_completion_tokens"] ?? chat["max_tokens"])?.DeepClone() ?? DefaultMaxTokens;

            switch (chat["stop"])
            {
                case JsonValue stopValue when stopValue.TryGetValue(out string? stop):
                    result["stop_sequences"] = new JsonArray(stop);
                    break;
                case JsonArray stops:
                    result["stop_sequences"] = stops.DeepClone();
                    break;
            }

            if (GetString(chat, "user") is { Length: > 0 } user)
                result["metadata"] = new JsonObject { ["user_id"] = user };

            if (GetBool(chat, "stream"))
                result["stream"] = true;

            ConvertTools(chat, result);

            if (chat["response_format"] is JsonObject responseFormat
                && GetString(responseFormat, "type") == "json_schema"
                && responseFormat["json_schema"]?["schema"] is JsonNode schema)
            {
                result["output_config"] = new JsonObject
                {
                    ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = schema.DeepClone() }
                };
            }

            if (GetString(chat, "reasoning_effort") is { } effort)
                AnthropicReasoning.ApplyEffort(result, effort);

            return result;
        }

        /// <summary>같은 role이 이어지면 앞 메시지에 블록을 합친다. 블록이 없으면 넣지 않는다.</summary>
        private static void Append(JsonArray messages, string role, JsonArray blocks)
        {
            if (blocks.Count == 0)
                return;

            if (messages.Count > 0
                && messages[^1] is JsonObject last
                && GetString(last, "role") == role
                && last["content"] is JsonArray existing)
            {
                foreach (JsonNode? block in blocks.ToArray())
                {
                    blocks.Remove(block);
                    existing.Add(block);
                }
                return;
            }

            messages.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
        }

        private static JsonArray ConvertUserContent(JsonNode? content)
        {
            JsonArray blocks = new JsonArray();
            switch (content)
            {
                case null:
                    break;
                case JsonValue value when value.TryGetValue(out string? text):
                    AddText(blocks, text);
                    break;
                case JsonArray parts:
                    foreach (JsonNode? part in parts)
                    {
                        JsonObject obj = RequireObject(part, "content part");
                        switch (GetString(obj, "type"))
                        {
                            case "text":
                                AddText(blocks, GetString(obj, "text"));
                                break;
                            case "image_url":
                                blocks.Add(ConvertImage(obj["image_url"]));
                                break;
                            default:
                                throw new ConversionException($"Unsupported content part type '{GetString(obj, "type")}'.");
                        }
                    }
                    break;
                default:
                    throw new ConversionException("user content must be a string or an array.");
            }

            return blocks;
        }

        private static JsonObject ConvertImage(JsonNode? imageUrl)
        {
            string url = (imageUrl is JsonValue ? imageUrl.GetValue<string>() : GetString(imageUrl, "url"))
                ?? throw new ConversionException("image_url.url must be provided.");

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = url.IndexOf(',');
                int semicolon = url.IndexOf(';');
                if (comma < 0 || semicolon < 0 || semicolon > comma || !url[semicolon..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
                    throw new ConversionException("Only base64 data URLs are supported for images.");

                return new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = url[5..semicolon],
                        ["data"] = url[(comma + 1)..]
                    }
                };
            }

            return new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "url", ["url"] = url }
            };
        }

        private static JsonArray ConvertAssistant(JsonObject message, AnthropicToolIdMapper ids)
        {
            JsonArray blocks = new JsonArray();
            AddText(blocks, TextOf(message["content"]));

            if (message["tool_calls"] is JsonArray toolCalls)
            {
                foreach (JsonNode? node in toolCalls)
                {
                    JsonObject call = RequireObject(node, "tool_calls[] item");
                    string id = GetString(call, "id") ?? throw new ConversionException("tool_calls[].id must be provided.");
                    JsonNode? function = call["function"];
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = ids.ToProvider(id),
                        ["name"] = GetString(function, "name") ?? throw new ConversionException("tool_calls[].function.name must be provided."),
                        ["input"] = ParseArguments(GetString(function, "arguments"))
                    });
                }
            }

            return blocks;
        }

        private static JsonObject ConvertToolResult(JsonObject message, AnthropicToolIdMapper ids)
        {
            string id = GetString(message, "tool_call_id") ?? throw new ConversionException("tool message must have tool_call_id.");
            return new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = ids.ToProvider(id),
                ["content"] = TextOf(message["content"])
            };
        }

        private static void ConvertTools(JsonObject chat, JsonObject result)
        {
            if (chat["tools"] is not JsonArray tools || tools.Count == 0)
                return; // tools가 없으면 tool_choice/parallel_tool_calls도 버린다

            JsonArray converted = new JsonArray();
            foreach (JsonNode? node in tools)
            {
                JsonObject tool = RequireObject(node, "tools[] item");
                string? type = GetString(tool, "type");
                if (type is not null && type != "function")
                    throw new ConversionException($"Tool type '{type}' is not supported for Anthropic models.");

                JsonNode? function = tool["function"] ?? throw new ConversionException("tools[].function must be provided.");
                JsonObject anthropicTool = new JsonObject
                {
                    ["name"] = GetString(function, "name") ?? throw new ConversionException("tools[].function.name must be provided.")
                };
                if (GetString(function, "description") is { } description)
                    anthropicTool["description"] = description;
                anthropicTool["input_schema"] = function["parameters"]?.DeepClone()
                    ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                converted.Add(anthropicTool);
            }

            result["tools"] = converted;

            JsonObject? toolChoice = chat["tool_choice"] switch
            {
                null => null,
                JsonValue value when value.TryGetValue(out string? mode) => mode switch
                {
                    "auto" => new JsonObject { ["type"] = "auto" },
                    "none" => new JsonObject { ["type"] = "none" },
                    "required" => new JsonObject { ["type"] = "any" },
                    _ => throw new ConversionException($"Unsupported tool_choice '{mode}'.")
                },
                JsonObject obj when GetString(obj, "type") == "function" => new JsonObject
                {
                    ["type"] = "tool",
                    ["name"] = GetString(obj["function"], "name") ?? throw new ConversionException("tool_choice.function.name must be provided.")
                },
                _ => throw new ConversionException("Unsupported tool_choice.")
            };

            bool disableParallel = chat["parallel_tool_calls"] is JsonValue parallel
                && parallel.TryGetValue(out bool allowed)
                && !allowed;
            if (disableParallel)
            {
                toolChoice ??= new JsonObject { ["type"] = "auto" };
                if (GetString(toolChoice, "type") != "none")
                    toolChoice["disable_parallel_tool_use"] = true;
            }

            if (toolChoice is not null)
                result["tool_choice"] = toolChoice;
        }

        private static void AddText(JsonArray blocks, string? text)
        {
            if (!string.IsNullOrEmpty(text))
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
    }
}
