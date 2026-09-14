# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# O contexto contem os repositorios irmaos Veiculando e
# Veiculando.WhiteLabel.Api. Copiar os projetos primeiro preserva o cache restore.
COPY Veiculando.WhiteLabel.Api/Veiculando.WhiteLabel.Api.csproj Veiculando.WhiteLabel.Api/
COPY Veiculando/Veiculando.Data/Veiculando.Data.csproj Veiculando/Veiculando.Data/
COPY Veiculando/Veiculando.Domain/Veiculando.Domain.csproj Veiculando/Veiculando.Domain/
COPY Veiculando/Veiculando.Infra/Veiculando.Infra.csproj Veiculando/Veiculando.Infra/
COPY Veiculando/Veiculando.Infra.Security/Veiculando.Infra.Security.csproj Veiculando/Veiculando.Infra.Security/
COPY Veiculando/Veiculando.Shared/Veiculando.Shared.csproj Veiculando/Veiculando.Shared/
RUN dotnet restore Veiculando.WhiteLabel.Api/Veiculando.WhiteLabel.Api.csproj

COPY Veiculando/ Veiculando/
COPY Veiculando.WhiteLabel.Api/ Veiculando.WhiteLabel.Api/
RUN dotnet publish Veiculando.WhiteLabel.Api/Veiculando.WhiteLabel.Api.csproj \
    --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:8080/health || exit 1

USER app
ENTRYPOINT ["dotnet", "Veiculando.WhiteLabel.Api.dll"]
