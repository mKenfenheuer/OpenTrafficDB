FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Shared/OpenTrafficDB.Shared.csproj", "Shared/"]
COPY ["AggregateWorker/OpenTrafficDB.AggregateWorker.csproj", "AggregateWorker/"]
RUN dotnet restore "AggregateWorker/OpenTrafficDB.AggregateWorker.csproj"
COPY Shared/ Shared/
COPY AggregateWorker/ AggregateWorker/
RUN dotnet build "AggregateWorker/OpenTrafficDB.AggregateWorker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AggregateWorker/OpenTrafficDB.AggregateWorker.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OpenTrafficDB.AggregateWorker.dll"]
