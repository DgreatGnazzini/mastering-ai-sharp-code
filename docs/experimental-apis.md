# Preview and experimental APIs in use

Chapter 15. PriorAuthBot builds with warnings as errors, so using an API its authors mark as
experimental needs an explicit suppression. Each suppression is listed here with why it is needed
and when to look again. `UpgradeSafetyTests.EveryPreviewApiSuppressionIsInTheInventory` fails the
build when a suppression is added without a row.

Revisit every row when the package it belongs to changes version: the weekly latest-packages run
(`.github/workflows/latest-packages.yml`) shows when that is.

| Diagnostic | Package | Where | Why it is used | Revisit when |
|---|---|---|---|---|
| `OPENAI001` | OpenAI 2.15.0 | `src/PriorAuthBot.Core/AI/AiServiceCollectionExtensions.cs` (`CreateAzureOpenAI`) | The `OpenAIClient` constructor that takes an authentication policy is the documented way to call Azure OpenAI's v1 endpoint with an Entra ID token, and it is still marked experimental. | The OpenAI package marks the constructor stable, or Microsoft documents another way. If it is removed, the build breaks here first. |
| `ASPIRE010` | Aspire.Hosting 13.6.5 | `src/PriorAuthBot.AppHost/PriorAuthBot.AppHost.csproj` | Aspire warns when the Aspire CLI bundle is missing. `dotnet run` is enough for this book. | Aspire's next major version. |

Not on this list on purpose:

- **Microsoft Agent Framework's `MAAI001` APIs** (such as `AgentSessionStore`). Chapter 11 chose not to use them, and the build enforces it: with no suppression, any use is a build error.
- **ONNX Runtime GenAI** is versioned 0.x. Its APIs carry no experimental marker, but a 0.x version promises no compatibility between minor versions, so `Microsoft.ML.OnnxRuntimeGenAI` is treated as a preview package as a whole: it is pinned exactly, and its upgrades go through the model upgrade playbook (`docs/upgrade-playbook.md`), not the routine package bump.
