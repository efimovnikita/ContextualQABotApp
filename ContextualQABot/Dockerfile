FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["ContextualQABot/ContextualQABot.csproj", "ContextualQABot/"]
RUN dotnet restore "ContextualQABot/ContextualQABot.csproj"
COPY . .
WORKDIR "/src/ContextualQABot"
RUN dotnet build "ContextualQABot.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ContextualQABot.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ContextualQABot.dll"]
