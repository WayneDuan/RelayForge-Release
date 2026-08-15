FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY RelayForge.Panel.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:6365
EXPOSE 6365
ENTRYPOINT ["dotnet", "RelayForge.Panel.Api.dll"]
