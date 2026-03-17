FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Shared/OpenTrafficDB.Shared.csproj", "Shared/"]
COPY ["RoutingWorker/OpenTrafficDB.RoutingWorker.csproj", "RoutingWorker/"]
RUN dotnet restore "RoutingWorker/OpenTrafficDB.RoutingWorker.csproj"
COPY Shared/ Shared/
COPY RoutingWorker/ RoutingWorker/
RUN dotnet build "RoutingWorker/OpenTrafficDB.RoutingWorker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "RoutingWorker/OpenTrafficDB.RoutingWorker.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OpenTrafficDB.RoutingWorker.dll"]
