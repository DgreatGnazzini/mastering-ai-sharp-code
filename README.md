# Mastering AI with Modern C# — companion code

Code for **Mastering AI with Modern C#: Building Secure, Production-Ready LLM Applications with .NET**.

Across the book you build one application: **PriorAuthBot**, the internal policy and knowledge assistant for **Deductible Dynamics**, a fictional regional health plan. PriorAuthBot answers staff questions from internal documents, cites its sources, and only ever retrieves documents the signed-in user is allowed to see. Each chapter adds one layer.

## Get the code for a chapter

Every chapter has a tag. Check out the one you're reading:

```bash
git clone https://github.com/DgreatGnazzini/mastering-ai-sharp-code.git
cd mastering-ai-sharp-code
git checkout ch07   # code as it stands at the end of chapter 7
git diff ch06 ch07  # exactly what chapter 7 adds
```

`main` always holds the latest code. See [docs/CHAPTERS.md](docs/CHAPTERS.md) for the tag convention and [ERRATA.md](ERRATA.md) for fixes.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned in `global.json`)
- One model provider: Azure OpenAI, OpenAI, or [Ollama](https://ollama.com) for free local models
- Docker or Podman, for the .NET Aspire AppHost that runs PostgreSQL with pgvector, Qdrant, and Ollama

## Build and test

```bash
dotnet build MasteringAI.slnx
dotnet test MasteringAI.slnx
dotnet run --project src/PriorAuthBot.Api
```

## Configuration and secrets

Never put keys in `appsettings.json`. Store them with user secrets:

```bash
cd src/PriorAuthBot.Api
dotnet user-secrets set "AI:OpenAI:ApiKey" "<your-key>"
```

## Repository layout

| Path | Contents |
|---|---|
| `src/PriorAuthBot.Api` | ASP.NET Core host for the assistant |
| `src/PriorAuthBot.Core` | Chat, retrieval, security, and evaluation logic |
| `docs/chapters/` | A short README per chapter: what it adds and how to run it |
| `tests/PriorAuthBot.Tests` | Unit and regression tests |
| `samples/documents` | Deductible Dynamics' fictional policies, protocols, and procedures |
| `Directory.Packages.props` | Every package version used in the book, pinned |

## Package versions

All versions are pinned centrally in `Directory.Packages.props`. CI builds and tests on every push and every chapter tag on Windows, Linux, and macOS, nightly against the pinned versions, and weekly against the latest package versions so API changes surface before readers hit them.

## License

Code is released under the [MIT License](LICENSE). The book text is copyright © 2026 John Gnazzo, all rights reserved.
