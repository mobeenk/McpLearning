# Learn MCP with .NET and local Ollama

Two small .NET 10 console projects, using the official MCP C# SDK:

- **McpServer** provides `add_numbers`, `get_store_hours`, and `search_web`.
- **OllamaChat** is the host application. It contains an MCP client and talks to your local Ollama model.

No API key, cloud LLM, database, or frontend is needed.
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

Ollama must be running at `http://localhost:11434`. If it is not running, start
the Ollama desktop app, or run `ollama serve` in another terminal.
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

One-shot mode and model selection:

```powershell
dotnet run --project .\OllamaChat -- --prompt 'What is 17 plus 25?'
dotnet run --project .\OllamaChat -- --model 'mcp-learning-llama3.2'
```

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
     |-- HTTP /api/chat --> Ollama model
     |   Sends conversation + discovered tool schemas
     |<-- Either an ordinary answer OR structured tool_calls
     |
     |   If the model requests a tool:
     |-- MCP client -- tools/call --> McpServer executes C# method
     |<-- Tool result
     |-- HTTP /api/chat --> Ollama (conversation now includes tool result)
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

MCP standardizes discovery and invocation between client and server. Ollama's
HTTP API is a separate connection, not the MCP transport. This demo translates
MCP tool schemas into Ollama tool definitions in plain C# so the bridge is visible.
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
   to Ollama, inspect `tool_calls`, invoke MCP, append results, and ask again.
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

The self-test requires no Ollama. It checks discovery, argument schemas, real
tool calls, negative/decimal addition, store data, and invalid argument errors.
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
