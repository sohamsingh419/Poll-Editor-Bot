# Build stage
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

COPY PollEditorBot/PollEditorBot.csproj PollEditorBot/
RUN dotnet restore PollEditorBot/PollEditorBot.csproj

COPY PollEditorBot/ PollEditorBot/
RUN dotnet publish PollEditorBot/PollEditorBot.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:6.0
WORKDIR /app
COPY --from=build /app/publish .

RUN adduser --disabled-password --gecos "" appuser
USER appuser

ENTRYPOINT ["dotnet", "PollEditorBot.dll"]
