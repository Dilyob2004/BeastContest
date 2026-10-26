FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json NuGet.Config ./
COPY src/BeastContest.Web/ src/BeastContest.Web/
RUN dotnet restore src/BeastContest.Web/BeastContest.Web.csproj --configfile NuGet.Config

RUN dotnet publish src/BeastContest.Web/BeastContest.Web.csproj -c Release --no-restore -o /app/publish
RUN test -s /app/publish/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/App_Data && chown -R app:app /app/App_Data
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BeastContest.Web.dll"]



