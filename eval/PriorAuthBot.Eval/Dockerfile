# syntax=docker/dockerfile:1
# The evaluation suite as an image (chapter 14), for the jobs that run inside the deployment's
# virtual network: the smoke test against a new revision, and the eval suite against the Azure
# OpenAI deployment a new revision will use. Build from the repository root:
#   docker build -f eval/PriorAuthBot.Eval/Dockerfile -t priorauthbot-eval .

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config MasteringAI.slnx ./
COPY src/PriorAuthBot.Core/ src/PriorAuthBot.Core/
COPY samples/mcp/DeductibleDynamics.Formulary/ samples/mcp/DeductibleDynamics.Formulary/
COPY eval/PriorAuthBot.Eval/ eval/PriorAuthBot.Eval/
RUN dotnet publish eval/PriorAuthBot.Eval/PriorAuthBot.Eval.csproj --configuration Release --runtime linux-x64 \
      --self-contained false --output /repo/eval/bin

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra@sha256:b64d614c3c1c440abe6d57f510c5a4cfc0ccd56a9b96f96c1503efe4a7ac3e56 AS final

# The suite finds its data from the repository root, marked by MasteringAI.slnx.
WORKDIR /repo
COPY MasteringAI.slnx ./
COPY eval/datasets/ eval/datasets/
COPY eval/baselines.json eval/
COPY samples/documents/ samples/documents/
COPY samples/mock-apis/DeductibleDynamics.MockHr/employees.json samples/mock-apis/DeductibleDynamics.MockHr/
COPY src/PriorAuthBot.Api/appsettings.json src/PriorAuthBot.Api/
COPY --from=build /repo/eval/bin/ eval/bin/

ENV PRIORAUTHBOT_EVAL_CACHE=/tmp/eval-cache \
    PRIORAUTHBOT_TEST_REPORTS=/tmp/eval-reports
USER $APP_UID
ENTRYPOINT ["dotnet", "eval/bin/PriorAuthBot.Eval.dll"]
