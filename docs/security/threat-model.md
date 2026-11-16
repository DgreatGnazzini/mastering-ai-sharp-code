# PriorAuthBot threat model

Chapter 12. Every threat maps to an entry in the [OWASP Top 10 for LLM Applications (2025)](https://genai.owasp.org/llm-top-10/), to the controls that address it, and to the tests that prove the controls work. `tests/PriorAuthBot.Tests/Security/ThreatModelTests.cs` reads the table below and fails if a control file or a test it names does not exist, or if a red-team case is not listed against any threat. Keep the table in that shape: one row per threat, files in backticks, tests by red-team case ID or test class name.

## System

PriorAuthBot answers Deductible Dynamics staff questions from internal documents (`/ask`), calls tools for them (`/assist`, chapter 10), and runs two agents (`/agent` and `/exceptions`, chapter 11). Users sign in with Entra ID. Documents carry access control lists from their source systems. Some documents, and some questions, contain PHI.

Trust boundaries:

1. **The user.** Authenticated, but their words can be an attack (direct injection).
2. **Documents.** Written by other staff and indexed automatically. Their text is data, and it can be an attack (indirect injection).
3. **Tools and MCP servers.** Their descriptions are vetted and pinned (chapter 10); their results are data, and can be an attack.
4. **The model provider.** Receives prompts. Covered by a BAA, or PHI is redacted before it leaves (chapter 4).
5. **The client that renders answers.** Anything it renders, it may fetch.

## Threats

| ID | Threat | OWASP | Controls | Tests |
|---|---|---|---|---|
| T1 | A user tells the model to drop its rules, change roles, or act without approval | LLM01 | `src/PriorAuthBot.Core/Security/PromptShieldChatClient.cs`, `src/PriorAuthBot.Core/Security/HeuristicPromptShield.cs`, `src/PriorAuthBot.Core/Security/ContentSafetyPromptShield.cs` | rt01-ignore-and-print, rt02-dan, GuardrailMiddlewareTests |
| T2 | A document in the index carries instructions for the model | LLM01 | `src/PriorAuthBot.Core/Security/UntrustedContent.cs`, `src/PriorAuthBot.Core/Rag/RagAnswerService.cs`, `src/PriorAuthBot.Core/Prompts/Templates/grounded-answer.v2.md` | rt04-holiday-password, GuardrailTests |
| T3 | A tool result or MCP server's output carries instructions for the model | LLM01 | `src/PriorAuthBot.Core/Security/PromptShieldChatClient.cs`, `src/PriorAuthBot.Core/Mcp/McpToolVetting.cs`, `src/PriorAuthBot.Core/Tools/PendingActions.cs` | rt08-tool-result, rt09-mcp-output |
| T4 | Instructions hidden in invisible Unicode, or in an image or scan | LLM01 | `src/PriorAuthBot.Core/Security/HiddenText.cs`, `src/PriorAuthBot.Core/AI/PhiGuardChatClient.cs` | rt06-parking-hidden, GuardrailTests |
| T5 | A document states a false fact, with no instruction in it (data poisoning) | LLM04 | `src/PriorAuthBot.Core/Rag/Citations.cs`, `samples/documents/README.md` (owners and ACLs at the source). Accepted risk A1 | rt07-per-diem-poisoned-fact |
| T6 | An answer reveals the system prompt | LLM07 | `src/PriorAuthBot.Core/Security/OutputGuard.cs` | rt01-ignore-and-print, rt03-paraphrased-extraction |
| T7 | An answer contains PHI or data the user may not see | LLM02 | `src/PriorAuthBot.Core/Retrieval/SecureRetriever.cs`, `src/PriorAuthBot.Core/Privacy/PhiRedactor.cs`, `eval/PriorAuthBot.Eval/LeakageChecker.cs` | rt10-phi-other-member, rt11-ssn-echo, SecureRetrieverTests |
| T8 | An answer carries data off site in an image or link | LLM05 | `src/PriorAuthBot.Core/Security/MarkdownSanitizer.cs`, `src/PriorAuthBot.Core/Security/OutputGuardChatClient.cs` | rt05-wellness-image, rt12-user-image, rt17-planted-link |
| T9 | An answer carries HTML or script into the client | LLM05 | `src/PriorAuthBot.Core/Security/MarkdownSanitizer.cs`, `src/PriorAuthBot.Api/ui/index.html` | rt13-script-tag, GuardrailTests |
| T10 | The model performs a write the user did not approve | LLM06 | `src/PriorAuthBot.Core/Tools/AssistantService.cs`, `src/PriorAuthBot.Core/Tools/PendingActions.cs` | rt02-dan, rt14-self-approve, AssistantServiceTests |
| T11 | An agent is talked into changing its approver or claiming a decision | LLM06 | `src/PriorAuthBot.Core/Agents/Exceptions/ExceptionWorkflow.cs` | rt15-exception-approver, rt16-exception-claims-approval, ExceptionWorkflowTests |
| T12 | A request runs up cost or exhausts the context window | LLM10 | `src/PriorAuthBot.Core/Security/SecurityOptions.cs`, `src/PriorAuthBot.Core/Agents/AgentBudget.cs`, `src/PriorAuthBot.Api/Endpoints.cs` | rt18-huge-question, AgentBudgetTests |
| T13 | An MCP server changes a tool's description after it was vetted | LLM03 | `src/PriorAuthBot.Core/Mcp/McpToolVetting.cs` | McpVettingTests |
| T14 | Retrieval returns chunks the user may not see | LLM08 | `src/PriorAuthBot.Core/Retrieval/SecureRetriever.cs` | SecureRetrieverTests, RetrievalBoundaryTests |
| T15 | An answer states something its sources do not | LLM09 | `src/PriorAuthBot.Core/Rag/Citations.cs`, `eval/PriorAuthBot.Eval/CitationFaithfulnessEvaluator.cs` | CitationFaithfulnessTests |

## Accepted risks

| ID | Risk | Why it is accepted | Compensating controls | Owner | Signed off |
|---|---|---|---|---|---|
| A1 | A document can state a false fact that PriorAuthBot repeats (T5). No shield detects it, because nothing in it addresses a model. | Only document owners can edit indexed sources, and every answer cites its source, so a wrong answer is traceable to one page and one editor. | Citations (chapter 8), source-system ACLs and edit history, the red-team case rt07 as a tripwire on the number of such breaches. | Policy content owners | _pending_ |
| A2 | The heuristic shield misses paraphrased attacks. | Azure AI Content Safety Prompt Shields is the production shield; the heuristics are the offline fallback and a second opinion. | Output checks run whatever the shield decides; writes need approval; retrieval is permission-filtered. | PriorAuthBot engineering | _pending_ |
| A3 | Text sent to the prompt shield leaves the trust boundary. | The Content Safety resource is in the same subscription, region, and BAA as the chat model. | PHI guard runs before the shield for any provider not approved for PHI. | Security | _pending_ |
