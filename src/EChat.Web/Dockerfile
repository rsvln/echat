FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /repo
COPY ["src/EChat.Web/EChat.Web.csproj", "src/EChat.Web/"]
COPY ["src/EChat.Web/version.txt", "src/EChat.Web/"]
COPY ["src/EChat.Core/EChat.Core.csproj", "src/EChat.Core/"]
COPY ["src/EChat.Core/version.txt", "src/EChat.Core/"]
COPY ["src/EChat.UI/EChat.UI.csproj", "src/EChat.UI/"]
COPY ["src/EChat.UI/version.txt", "src/EChat.UI/"]
RUN dotnet restore "src/EChat.Web/EChat.Web.csproj"
COPY . .
WORKDIR "/repo/src/EChat.Web"
RUN dotnet publish "EChat.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data
ENTRYPOINT ["dotnet", "EChat.Web.dll"]
