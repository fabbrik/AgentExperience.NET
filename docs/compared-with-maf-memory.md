# Compared with MAF memory

Microsoft Agent Framework (MAF) already offers several ways to give an agent memory. This page sets out, factually,
what each one stores, what decides that something is stored, what reaches the model, and what it trusts, so you can
pick the right one, or several. It says nothing about any product's quality: each does a different job.

The MAF types are described from the `Microsoft.Agents.AI` 1.22.0 API documentation (the XML docs shipped in the
package, and the [.NET API reference](https://learn.microsoft.com/dotnet/api/microsoft.agents.ai) on Microsoft Learn),
and the Mem0 provider from Microsoft Learn's
[Mem0 integration page](https://learn.microsoft.com/agent-framework/integrations/by-component/context-providers/mem0).
Checked on 2026-10-14; where this page and those disagree, they are right. AgentExperience.NET itself is a
`0.1.0-preview` (not production ready, public APIs may change between previews), while the MAF types above ship in
MAF's 1.x packages.

## At a glance

| | What it stores | What decides it is stored | What reaches the model | Trust and verification |
| --- | --- | --- | --- | --- |
| Session chat history ([`ChatHistoryProvider`](https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.chathistoryprovider), such as [`InMemoryChatHistoryProvider`](https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.inmemorychathistoryprovider)) | The conversation's messages, for one session (`InMemoryChatHistoryProvider` keeps them in the session's `StateBag`) | Every message of the conversation | The session's earlier messages, in order, on each invocation (unless the service keeps the conversation itself) | The conversation as it happened; nothing is checked |
| [`ChatHistoryMemoryProvider`](https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.chathistorymemoryprovider) | Chat messages, as vectors in a `VectorStore` collection, under a storage scope | Every message that passes its filters (by default external request messages and all response messages) | Before each invocation, the most similar past messages (3 by default), prefixed with a context prompt; or a search tool the model calls | Retrieved messages are accepted as-is, without validation or sanitization (its own security notes) |
| [`TextSearchProvider`](https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.textsearchprovider) | Nothing durable: it searches external knowledge through your delegate (optionally keeping recent messages in session state to build the search input) | Not applicable: what is searchable is your knowledge source | Before each invocation, the formatted search results; or a search tool the model calls | Results may carry adversarial content; its docs ask the search implementation to validate input and control access |
| [`FileMemoryProvider`](https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.filememoryprovider) | Files, through an `AgentFileStore` (in memory, local disk, blob storage, ...) | The model, by calling its `file_memory_write` and related tools | The memory tools and instructions for using them; file content when the model reads, lists or searches | Whatever the model wrote |
| Mem0 provider (Python; .NET: only an old preview package) | Memories that Mem0 extracts from conversations, in the Mem0 service, under user, agent or application scopes | Mem0's extraction | Relevant memories retrieved in later runs | A third-party service: Learn asks you to review its data handling, retention and terms. Python package `agent-framework-mem0`, pre-release; Learn says it is not currently available for Agent Framework .NET, and NuGet has only an old `Microsoft.Agents.AI.Mem0` preview |
| AgentExperience.NET | Sanitized runs (attempts, tool calls, error classes) and a lesson per verified run, in PostgreSQL (or in memory, for development) | Your own check (`Verify`): a test run, an exit code, a person's sign-off; never the model's word. A failed run is stored quarantined and never reused | Before each invocation, one labelled *Historical Reference* block: what earlier runs on a similar task tried, what failed and what worked. Never raw tool results | Stored only on host-produced evidence; sanitized at capture; scoped per tenant, application and project; the block is labelled untrusted reference material, which is hygiene, not a control |

## MAF's session chat history

An agent's `ChatHistoryProvider` (`ChatClientAgentOptions.ChatHistoryProvider`) fetches the conversation's earlier
messages for each invocation and is told about the new ones; `InMemoryChatHistoryProvider` keeps them in the session's
`StateBag`, with optional message reduction. It is the conversation itself, not memory across conversations: a new
session starts empty. When the service keeps the conversation (a conversation ID), the provider is not used.

## MAF's `ChatHistoryMemoryProvider`

Stores chat history in a vector store and retrieves related messages to add to a later conversation. Messages are
stored after each invocation and retrieved by semantic similarity before the next one (`SearchBehavior.BeforeAIInvoke`,
the default), or exposed as a search tool the model may call (`OnDemandFunctionCalling`). Storage and search are
scoped (`ChatHistoryMemoryProviderScope`), and `MaxResults` defaults to 3. Its documentation notes that retrieved
messages are injected into the model's context as-is, and that stored messages may contain personal data. It
remembers what was *said*, whatever its outcome.

## MAF's `TextSearchProvider`

Retrieval-augmented generation over external knowledge: your `searchAsync` delegate returns text results (with an
optional source name and link), and the provider injects them before each invocation or offers them as an on-demand
search tool. It does not learn from the agent's runs; with `RecentMessageMemoryLimit` above 0 it keeps recent messages
only to build better search input. It answers "what does our knowledge base say?", not "what worked last time?".

## MAF's `FileMemoryProvider`

Gives the agent tools to write, read, list, search, edit and delete memory files, persisted through a pluggable
`AgentFileStore`. The model decides what to remember and when to look, guided by built-in (or your own) instructions.
It suits notes the model curates for itself; nothing checks that what it writes is true.

## The Mem0 provider

Learn describes it as extracting durable memories from agent conversations and retrieving relevant ones in later runs,
with separate storage and retrieval scopes (no retrieval scope means memories are stored but never recalled). It is a
Python package installed with `--pre`; Learn's .NET tab says the integration is not currently available for Agent
Framework .NET (NuGet lists only an early `Microsoft.Agents.AI.Mem0` preview, `1.0.0-preview.251028.1`).

## AgentExperience.NET

Records what the agent tried in each run (its attempts and tool calls, sanitized before anything is kept), and stores
a lesson only when your own code says the run worked, from evidence your host produced: a tool call's captured outcome,
your own check, or a person's approval ([Built-in verifiers](guide/deployment.md#built-in-verifiers)). Before a later
run on a similar task, it retrieves the applicable lessons (by words, and by meaning when an embedding generator is
registered), re-checks each one, and adds them as one labelled block ([Injection](guide/injection.md)). With reuse
evidence turned on, a lesson that later runs confirm gains confidence and one they contradict loses it
([Confidence](guide/confidence.md)); it is off by default. What it
cannot do is stated in [Known limits](known-limits.md).

## Use which when

- **The agent should remember the person or the conversation** (preferences, facts they told it, earlier turns):
  `ChatHistoryMemoryProvider`, or another memory integration on
  [Learn's context-provider list](https://learn.microsoft.com/agent-framework/integrations/by-component/context-providers/)
  (the Mem0 provider there is Python only for now; .NET has only an old preview package). Within one conversation,
  the session chat history already does this.
- **The agent should answer from documents or data you already have:** `TextSearchProvider`, or a RAG integration
  such as Azure AI Search.
- **The model should keep its own notes:** `FileMemoryProvider`.
- **The agent uses tools on recurring tasks, and your code can tell whether a run worked:** AgentExperience.NET. Its
  lessons say which approach passed your checks and which failed, so the next run can start from what worked.

AgentExperience.NET is the **wrong** choice when:

- **the agent has no tools.** A lesson is built from the tool calls a run made; without them there is little to learn
  beyond the outcome;
- **nothing checks the outcome.** Without a `Verify` that your own code decides, runs are captured but nothing is
  stored, by design;
- **the goal is conversational personalisation.** It stores what was tried and whether it passed, not what a person
  said or prefers;
- **the tasks are one-off and never recur.** A lesson helps only a later run on a similar task;
- **you need a stable, production-ready package today.** It is a `0.1.0-preview`, with the limits stated in
  [Known limits](known-limits.md); MAF's own providers ship in its 1.x packages.

## They compose

Several `AIContextProvider`s can run on one agent: list them in `ChatClientAgentOptions.AIContextProviders`.
`ExperienceContextProvider` is an `AIContextProvider`, and
`UseAgentExperience` adds it beside whatever the agent already has (see
[Injection on the agent, or on its options](guide/deployment.md#injection-on-the-agent-or-on-its-options)). An agent can
recall the conversation through `ChatHistoryMemoryProvider`, ground answers through `TextSearchProvider`, and get
verified lessons from this library at once; each adds its own context, and none reads the others' stores. See
[Context providers](https://learn.microsoft.com/agent-framework/concepts/agents/conversations/context-providers) on
Microsoft Learn for how MAF runs them.
