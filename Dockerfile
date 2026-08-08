# KTD6: build+test+package only, no deploy automation. Multi-stage build for
# SupportForge.Api; SupportForge.Api.Tests is excluded from the runtime image.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY backend/*.sln ./backend/
COPY backend/SupportForge.Api/*.csproj ./backend/SupportForge.Api/
COPY backend/SupportForge.Core/*.csproj ./backend/SupportForge.Core/
COPY backend/SupportForge.Agents/*.csproj ./backend/SupportForge.Agents/
COPY backend/SupportForge.Ingestion/*.csproj ./backend/SupportForge.Ingestion/
COPY backend/SupportForge.VectorStore/*.csproj ./backend/SupportForge.VectorStore/
COPY backend/SupportForge.Common/*.csproj ./backend/SupportForge.Common/
COPY backend/SupportForge.Api.Tests/*.csproj ./backend/SupportForge.Api.Tests/
COPY backend/SupportForge.Evals/*.csproj ./backend/SupportForge.Evals/
RUN dotnet restore backend/SupportForge.Backend.sln

COPY backend/ ./backend/
RUN dotnet publish backend/SupportForge.Api/SupportForge.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "SupportForge.Api.dll"]
