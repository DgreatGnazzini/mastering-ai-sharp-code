// PriorAuthBot's local environment (chapter 1). Everything runs on your machine: no cloud
// account, no keys. The Aspire dashboard shows the containers, the API, and their logs.
var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with the pgvector extension, for the vector store from chapter 6 on.
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "0.8.7-pg17-trixie")
    // Create the application database at first start, so the init script enables pgvector in it.
    .WithEnvironment("POSTGRES_DB", "priorauthbot")
    .WithBindMount("../../scripts/init-pgvector.sql", "/docker-entrypoint-initdb.d/init-pgvector.sql", isReadOnly: true)
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var database = postgres.AddDatabase("priorauthbot");

// Ollama with the two models the book uses. The first run downloads about 2.3 GB.
var ollama = builder.AddOllama("ollama")
    .WithImageTag("0.40.16")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var chatModel = ollama.AddModel("chat", "llama3.2:3b");
var embeddingModel = ollama.AddModel("embedding", "nomic-embed-text:v1.5");

// Chapter 4's vision model, for scans and intake forms: about 3.2 GB more. The API does not wait
// for it, so the rest of PriorAuthBot starts while it downloads.
ollama.AddModel("vision", "qwen2.5vl:3b");

// Chapter 10: the mock HR system, which the PTO balance tool calls on behalf of the user.
var hr = builder.AddProject<Projects.DeductibleDynamics_MockHr>("hr");

builder.AddProject<Projects.PriorAuthBot_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(chatModel)
    .WaitFor(embeddingModel)
    .WithEnvironment("AI__Provider", "Ollama")
    .WithEnvironment("AI__Ollama__Endpoint", ollama.GetEndpoint("http"))
    .WithEnvironment("Tools__Hr__BaseAddress", hr.GetEndpoint("http"))
    // Chapter 13: the audit log in its append-only table in the same database. Aspire also sets
    // OTEL_EXPORTER_OTLP_ENDPOINT, so traces, metrics, and logs appear in the dashboard.
    .WithEnvironment("Audit__Store", "Postgres")
    .WithEnvironment("Audit__CreateSchema", "true")
    .WaitFor(hr);

builder.Build().Run();
