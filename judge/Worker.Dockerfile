FROM docker:28-cli AS cli
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json NuGet.Config ./
COPY src/ src/
RUN dotnet publish src/BeastContest.Judge/BeastContest.Judge.csproj -c Release -o /publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=build /publish .
ENTRYPOINT ["dotnet","BeastContest.Judge.dll"]
