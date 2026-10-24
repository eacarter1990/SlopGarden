using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SlopGarden.Server.Ai;

namespace SlopGarden.Server.Tests;

public class ResponsesApiTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Url, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static OpenAiClient Client(FakeHandler h) => new(new Factory(h), NullLogger<OpenAiClient>.Instance);

    private static readonly JsonArray Messages = JsonNode.Parse("""
        [
          {"role":"system","content":"You are a gardener."},
          {"role":"user","content":"Is anything too dry?"},
          {"role":"assistant","content":"Checking.","tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_garden_overview","arguments":"{}"}}]},
          {"role":"tool","tool_call_id":"call_1","content":"{\"beds\":[]}"},
          {"role":"user","content":[{"type":"text","text":"[App: camera image]"},{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,AAAA"}}]}
        ]
        """)!.AsArray();

    private static readonly JsonArray Tools = JsonNode.Parse("""
        [{"type":"function","function":{"name":"run_actuator","description":"Run it","parameters":{"type":"object","properties":{"actuatorId":{"type":"string"}}}}}]
        """)!.AsArray();

    [Fact]
    public void Chat_messages_and_tools_translate_to_responses_items()
    {
        var input = ResponsesApi.ToInput(Messages);
        Assert.Equal("developer", input[0]!["role"]!.ToString());
        Assert.Equal("Is anything too dry?", input[1]!["content"]!.ToString());
        Assert.Equal("assistant", input[2]!["role"]!.ToString());
        Assert.Equal("function_call", input[3]!["type"]!.ToString());
        Assert.Equal("call_1", input[3]!["call_id"]!.ToString());
        Assert.Equal("get_garden_overview", input[3]!["name"]!.ToString());
        Assert.Equal("function_call_output", input[4]!["type"]!.ToString());
        Assert.Equal("call_1", input[4]!["call_id"]!.ToString());
        Assert.Equal("input_image", input[5]!["content"]![1]!["type"]!.ToString());
        Assert.Equal("data:image/jpeg;base64,AAAA", input[5]!["content"]![1]!["image_url"]!.ToString());

        var tool = ResponsesApi.ToTools(Tools)[0]!;
        Assert.Equal("run_actuator", tool["name"]!.ToString());
        Assert.Equal("object", tool["parameters"]!["type"]!.ToString());
        Assert.Null(tool["function"]);
    }

    [Fact]
    public async Task OpenAi_uses_the_responses_endpoint_without_reasoning_effort()
    {
        var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"status":"completed","output":[
                  {"type":"reasoning","summary":[]},
                  {"type":"function_call","call_id":"call_9","name":"run_actuator","arguments":"{\"actuatorId\":\"a1\"}"}],
                 "usage":{"input_tokens":120,"output_tokens":30}}
                """, Encoding.UTF8, "application/json"),
        });
        var turn = await Client(h).CompleteAsync(new ModelEndpoint("https://api.openai.com/v1", "sk", "gpt-6-luna", null, null), Messages, Tools, null, CancellationToken.None);

        var (url, body) = Assert.Single(h.Calls);
        Assert.Equal("https://api.openai.com/v1/responses", url.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Null(json["reasoning_effort"]);
        Assert.Null(json["messages"]);
        Assert.False(json["store"]!.GetValue<bool>());
        Assert.Equal("gpt-6-luna", json["model"]!.ToString());
        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal(("call_9", "run_actuator"), (call.Id, call.Name));
        Assert.Equal(120, turn.PromptTokens);
    }

    [Fact]
    public async Task Other_endpoints_keep_chat_completions()
    {
        var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"hi"}}]}""", Encoding.UTF8, "application/json"),
        });
        var turn = await Client(h).CompleteAsync(new ModelEndpoint("http://localhost:1234/v1", null, "qwen", null, null), Messages, Tools, null, CancellationToken.None);
        Assert.Equal("http://localhost:1234/v1/chat/completions", Assert.Single(h.Calls).Url.ToString());
        Assert.Equal("hi", turn.Content);
    }

    [Fact]
    public async Task Streaming_events_produce_text_deltas_and_tool_calls()
    {
        var sse = string.Join("\n\n", new[]
        {
            """{"type":"response.created","response":{"status":"in_progress"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning"}}""",
            """{"type":"response.output_text.delta","output_index":1,"delta":"Let me "}""",
            """{"type":"response.output_text.delta","output_index":1,"delta":"check."}""",
            """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","call_id":"call_7","name":"get_garden_overview","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"garden"}""",
            """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"Id\":null}"}""",
            """{"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":50,"output_tokens":9}}}""",
        }.Select(e => "event: x\ndata: " + e)) + "\n\n";
        var deltas = new List<string>();
        var turn = await ResponsesApi.ParseStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), d => { deltas.Add(d); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(["Let me ", "check."], deltas);
        Assert.Equal("Let me check.", turn.Content);
        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal("call_7", call.Id);
        Assert.Equal("{\"gardenId\":null}", call.Arguments);
        Assert.Equal(50, turn.PromptTokens);
        Assert.Equal("completed", turn.FinishReason);
    }

    [Fact]
    public async Task Stream_errors_surface_as_provider_errors()
    {
        const string sse = "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"quota exceeded\"}}}\n\n";
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => ResponsesApi.ParseStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("quota exceeded", ex.Message);
    }
}
