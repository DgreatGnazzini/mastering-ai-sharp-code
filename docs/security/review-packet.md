# PriorAuthBot security review packet

Chapter 12. This is the document a security reviewer signs. It points at evidence rather than repeating it: the threat model, the controls in code, and the CI reports that show the controls working on every pull request.

## 1. What the system does

PriorAuthBot is Deductible Dynamics' internal assistant. It answers staff questions from policy documents the user may read, calls three of its own tools and one vetted MCP server for the user, and runs a PTO carryover exception workflow that waits for the requester and their manager. It runs on Azure (chapter 14) with Azure OpenAI under the company's BAA.

## 2. Data

| Data | Where it comes from | Where it goes | Controls |
|---|---|---|---|
| User questions | Signed-in staff | The model; logs as counts only | PHI guard (chapter 4), rate limit (chapter 2), length limit (chapter 12) |
| Policy documents | Source systems, with their ACLs | The vector store; prompts, filtered by the user's groups | ACL metadata on every chunk, deny by default (chapter 6) |
| PHI in documents | Incident and case documents | Only users in the document's groups | Query-time filtering, the leak gate (chapter 9), output redaction of ungrounded identifiers (chapter 12) |
| Tool results | HR API (on behalf of the user), help desk, formulary MCP server | The model, as untrusted content | Per-user tools (chapter 10), shield on every result (chapter 12) |
| Answers | The model | The user's client | Output guard: system prompt check, identifiers, Markdown (chapter 12) |

## 3. Threat model

[threat-model.md](threat-model.md): 15 threats, each mapped to OWASP, controls, and tests. A test fails if a mapping goes stale.

## 4. Evidence

| Evidence | Where |
|---|---|
| Unit and integration tests | CI job "Build and test", every pull request, three operating systems |
| Leak, tool, and agent gates | CI job "Build and test", step "Eval gate, offline"; reports `eval-*-offline.md` |
| Red-team results with a real model | CI job "RAG eval", report `eval-redteam-ollama.md` (artifact `rag-eval-reports`) |
| Red-team results without the guardrails | The same reports, "Without guardrails" column: what the guardrails stop |
| Vulnerable packages | CI step "Check for vulnerable packages", nightly |

## 5. Red-team summary

Fill this from the latest `eval-redteam-*.md` reports when the packet is signed. The gate fails any run with a breach on an absolute case, so the absolute row is 0 whenever the packet is current.

| | Offline, with guardrails | Offline, without | Ollama, with guardrails | Ollama, without |
|---|---|---|---|---|
| Attacks | 18 | 18 | 18 | 18 |
| Blocked before the model | | | | |
| Breaches, absolute cases | 0 | | 0 | |
| Breaches, measured cases | | | | |

## 6. Accepted risks

See [threat-model.md](threat-model.md#accepted-risks). Each needs an owner's name and a date before this packet is signed.

## 7. Sign-off

| Role | Name | Date | Decision |
|---|---|---|---|
| Application owner | | | |
| Security | | | |
| Privacy officer | | | |
