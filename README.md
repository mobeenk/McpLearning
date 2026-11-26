# Learn MCP with .NET and local Ollama

Two small .NET 10 console projects, using the official MCP C# SDK:

- **McpServer** provides `add_numbers`, `get_store_hours`, and `search_web`.
- **OllamaChat** is the host application. It contains an MCP client and talks to Ollama or an OpenAI-compatible chat-completions service.

The default local configuration needs no API key or cloud LLM. No database or frontend is needed.
The default model is `mcp-learning-llama3.2`, a separate local configuration
that reuses your already-installed `llama3.2:3b` weights.
NuGet packages require internet during the initial restore. The model runs locally,
but web searches require internet and send the search phrase to Bing.
Avoid putting sensitive information in search requests.

## Start

Open PowerShell:

```powershell
Set-Location 'D:\AI Course\McpLearning'
ollama create mcp-learning-llama3.2 -f .\Modelfile
dotnet build .\McpLearning.slnx
dotnet exec --runtimeconfig .\McpServer\bin\Debug\net10.0\McpServer.runtimeconfig.json --depsfile .\McpServer\bin\Debug\net10.0\McpServer.deps.json .\McpServer\bin\Debug\net10.0\Microsoft.Playwright.dll install chromium
dotnet run --project .\OllamaChat
```

By default, Ollama must be running at `http://localhost:11434`. If it is not
running, start the Ollama desktop app, or run `ollama serve` in another terminal.
The client automatically starts and stops the MCP server subprocess.
Do not run the server manually for this demo.
The `ollama create` command only needs running once, or after editing the
Modelfile. It does not alter your original model or download new weights.
The `dotnet exec` command downloads a separate Chromium browser for web search;
run it once after the build, and again if the Playwright package is upgraded.

### Why a separate model configuration?

Your installed Llama 3.2 template explicitly demands a function call whenever
tools are supplied, even for a greeting. That defeats optional tool use.
[Modelfile](Modelfile) replaces that template with instructions that permit
either a genuine tool call or a normal answer. This is Ollama prompt formatting,
not a change to MCP, not model training, and not keyword routing in C#.
Ollama still parses the model's output into native `tool_calls`.

Try these prompts:

| Prompt | Intended behavior |
| --- | --- |
| `What is 17 plus 25?` | Model requests `add_numbers`; answer is 42. |
| `When does Learning Bookshop open on Saturday?` | Model requests `get_store_hours`; answer uses fictional demo hours. |
| `Search the web for Kubernetes and tell me what it is.` | Model can request `search_web` and answer using search snippets and links. |
| `Say hello in one sentence.` | Model answers directly; no tool call. |
| `What is MCP? Explain simply.` | Model answers from its knowledge; no tool needed. |

Commands: `/tools` shows the schemas, `/reset` clears conversation history,
and `/exit` exits. Ctrl+C cancels. Use `/reset` when comparing prompts independently;
the model can answer from a tool result already present in the conversation.

### LLM settings

To customize settings, copy `OllamaChat\appsettings.example.json` to
`OllamaChat\appsettings.json` and edit the copy. Without the copy, the example
provides the default local Ollama configuration. Set `provider` to `Ollama` or
`OpenAI`; only the selected section is used. Ollama uses `endpoint`, `model`,
`timeoutSeconds`, `temperature`, and `numPredict` (maximum generated tokens).

For an OpenAI-compatible service, set `provider` to `OpenAI` and fill in the
`OpenAI` section. Its `endpoint` is the full HTTPS chat-completions URL;
`apiKey` is sent as `Authorization: Bearer <key>`, and `maxTokens` is sent as
`max_tokens`. The model and service must support OpenAI-compatible tool calls.

`appsettings.json` is ignored by Git to reduce accidental commits of your key,
but the key remains plaintext on disk and is copied into build and publish
outputs. Restrict access to those files and never share or commit them. The
remote provider receives conversation history, tool schemas, and tool results.

The selected settings file is copied to the build output; the application loads
that copy relative to its executable, regardless of the working directory.
Rebuild after changing source settings. Invalid or missing selected settings
cause a startup error. `--self-test` uses mock LLM responses and does not require
the settings file or a running LLM.

One-shot mode and a temporary model override:

```powershell
dotnet run --project .\OllamaChat -- --prompt 'What is 17 plus 25?'
dotnet run --project .\OllamaChat -- --model 'mcp-learning-llama3.2'
```

`--model` overrides the model in the settings file for that run.
Do not select an embedding-only model or a model without tool support.
You can also try `--model 'llama3.2:3b'` to observe the stock template's
unnecessary tool calls. Small local models can sometimes choose the wrong tool or skip one. Tool
descriptions and the system prompt guide them; MCP itself does not decide.
This application deliberately does not use keyword matching or pretend that
plain text resembling a function call is a real tool call.

## What actually happens?

```text
You type a prompt
     |
     v
OllamaChat (host)
     |-- MCP client -- stdio / JSON-RPC --> McpServer
     |             initialize + tools/list <-- tool descriptions and schemas
     |
     |-- HTTP /api/chat or /v1/chat/completions --> LLM
     |   Sends conversation + discovered tool schemas
     |<-- Either an ordinary answer OR structured tool_calls
     |
     |   If the model requests a tool:
     |-- MCP client -- tools/call --> McpServer executes C# method
     |<-- Tool result
     |-- HTTP chat request --> LLM (conversation now includes tool result)
     |<-- Final answer
     v
Answer displayed
```

**Important:** The LLM does not directly connect to MCP or execute C#.
It proposes a structured tool call with a name and arguments. The **host**
validates the tool name, executes the request through its **MCP client**, then
gives the result back to the LLM. Here, the host automatically permits all three
demo tools, including sending search phrases to Bing without further approval.
A real host should apply appropriate permissions and ask for
approval for sensitive or destructive tools.

MCP standardizes discovery and invocation between client and server. The LLM
HTTP API is a separate connection, not the MCP transport. This demo translates
MCP tool schemas and results into the selected provider's format in plain C#.
MCP also supports resources and reusable prompt templates; we intentionally
focus on tools. Your chat prompt is not an MCP prompt template.

## Read the code in this order

1. [McpServer/DemoTools.cs](McpServer/DemoTools.cs): descriptions, parameter schemas,
   and the actual tool methods. The opening hours are explicitly fictional.
2. [McpServer/Program.cs](McpServer/Program.cs): register tools and stdio transport.
   Server logs use **stderr** because stdout is reserved for MCP messages.
3. [OllamaChat/Program.cs](OllamaChat/Program.cs): launch the server, discover tools,
   and accept prompts.
4. [OllamaChat/OllamaConversation.cs](OllamaChat/OllamaConversation.cs): send schemas
   to the configured provider, inspect `tool_calls`, invoke MCP, append results, and ask again.
   The loop is limited to six model requests to prevent endless calls.
5. [OllamaChat/SelfTest.cs](OllamaChat/SelfTest.cs): exercise the actual MCP connection
   independently of the model.

The client locates the server's build output relative to its own build output,
using the same Debug/Release configuration. Keep the solution's folder layout;
this is a learning project, not a standalone published application.

## Verify

```powershell
dotnet run --project .\OllamaChat -- --self-test
dotnet run --project .\OllamaChat -- --prompt 'What is 17 plus 25?'
dotnet run --project .\OllamaChat -- --prompt 'When does Learning Bookshop open on Saturday?'
dotnet run --project .\OllamaChat -- --prompt 'Search the web for Kubernetes and tell me what it is.'
dotnet run --project .\OllamaChat -- --prompt 'Say hello in one sentence.'
```

The self-test requires no LLM service. It checks provider requests with mocked
HTTP responses, discovery, argument schemas, real MCP tool calls,
negative/decimal addition, store data, and invalid argument errors.
It does not contact Bing; the search prompt above is a separate live check.
The search uses a hidden browser and returns search result snippets, not full
web pages. Bing may block automated searches, require consent or a CAPTCHA,
or change its results page; these cases produce a tool error. Search results are
untrusted external content and may be inaccurate or incomplete. If a search
fails, the chat reports the error and clears the current conversation instead
of presenting an answer as though the search succeeded.
For live checks, look for `Tool requested`, `tools/call`, the tool result,
and the final answer. The greeting should have no `tools/call` line.
Logs show prompts' tool arguments and results locally, so avoid sensitive input.

## Experiment

Add another public static method to `DemoTools`, annotate it with
`[McpServerTool(Name = "...")]` and `[Description("...")]`, then rebuild and
restart the client. It is discovered automatically; no tool-routing switch
needs updating. Keep new tools read-only while learning.

## Official references

- [MCP introduction](https://modelcontextprotocol.io/docs/getting-started/intro)
- [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [Ollama tool calling](https://docs.ollama.com/capabilities/tool-calling)
