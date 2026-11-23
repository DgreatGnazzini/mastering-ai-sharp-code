# syntax=docker/dockerfile:1
# PriorAuthBot's API image (chapter 14). Build from the repository root:
#   docker build -f src/PriorAuthBot.Api/Dockerfile --build-arg REVISION=$(git rev-parse --short HEAD) -t priorauthbot-api .
#
# Both base images are pinned by digest, so a rebuild of the same commit gets the same base, and the
# SBOM and the vulnerability scan describe what actually ships. Dependabot (.github/dependabot.yml)
# proposes the new digest when Microsoft publishes a patched image.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
WORKDIR /src

# Restore first, from the project files alone, so a code change does not invalidate the package layer.
COPY global.json Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/PriorAuthBot.Core/PriorAuthBot.Core.csproj src/PriorAuthBot.Core/
COPY src/PriorAuthBot.Api/PriorAuthBot.Api.csproj src/PriorAuthBot.Api/
RUN dotnet restore src/PriorAuthBot.Api/PriorAuthBot.Api.csproj --runtime linux-x64

COPY src/PriorAuthBot.Core/ src/PriorAuthBot.Core/
COPY src/PriorAuthBot.Api/ src/PriorAuthBot.Api/
RUN dotnet publish src/PriorAuthBot.Api/PriorAuthBot.Api.csproj --configuration Release --runtime linux-x64 \
      --self-contained false --no-restore --output /app

# Chiseled: no shell, no package manager, and a non-root user, so a compromised process has very
# little to work with. The "extra" variant adds ICU and time zone data, which culture-aware string
# handling and date formatting need.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:cae5b962bfb786606def03d250c6a645cc98839095c89e806e4bd8978a68115b AS final
WORKDIR /app
COPY --from=build /app ./

# The sample corpus, catalog, and member directory the book uses. A real deployment reads its
# documents from the document management system instead; the paths are configuration.
COPY samples/documents/ /app/samples/documents/
COPY samples/catalog/policy-catalog.json /app/samples/catalog/
COPY samples/intake/members.json /app/samples/intake/

ARG REVISION=local
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1 \
    PriorAuthBot__Revision=${REVISION} \
    Ingestion__DocumentsPath=/app/samples/documents \
    Catalog__Path=/app/samples/catalog/policy-catalog.json \
    Intake__MembersPath=/app/samples/intake/members.json

LABEL org.opencontainers.image.source="https://github.com/DgreatGnazzini/mastering-ai-sharp-code" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.licenses="MIT"

# $APP_UID is the chiseled image's non-root user (1654). Container Apps runs it as is.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PriorAuthBot.Api.dll"]
