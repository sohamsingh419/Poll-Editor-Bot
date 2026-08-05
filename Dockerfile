# Build stage
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

COPY PollEditorBot/PollEditorBot.csproj PollEditorBot/
RUN dotnet restore PollEditorBot/PollEditorBot.csproj

COPY PollEditorBot/ PollEditorBot/
RUN dotnet publish PollEditorBot/PollEditorBot.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:6.0
WORKDIR /app
COPY --from=build /app/publish .

RUN adduser --disabled-password --gecos "" appuser
USER appuser

# PORT is set by Render automatically; default 8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "PollEditorBot.dll"]
