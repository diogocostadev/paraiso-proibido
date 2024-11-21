FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ConsumidorRoboPhub/ConsumidorRoboPhub.csproj", "ConsumidorRoboPhub/"]
COPY ["paraiso.models/paraiso.models.csproj", "paraiso.models/"]
COPY ["RoboPhub/RoboPhub.csproj", "RoboPhub/"]
RUN dotnet restore "ConsumidorRoboPhub/ConsumidorRoboPhub.csproj"
COPY . .
WORKDIR "/src/ConsumidorRoboPhub"
RUN dotnet build "ConsumidorRoboPhub.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "ConsumidorRoboPhub.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ConsumidorRoboPhub.dll"]
