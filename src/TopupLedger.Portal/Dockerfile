FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/TopupLedger.Domain/ TopupLedger.Domain/
COPY src/TopupLedger.Application/ TopupLedger.Application/
COPY src/TopupLedger.Infrastructure/ TopupLedger.Infrastructure/
COPY src/TopupLedger.Portal/ TopupLedger.Portal/
RUN dotnet publish TopupLedger.Portal/TopupLedger.Portal.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8081
EXPOSE 8081
ENTRYPOINT ["dotnet", "TopupLedger.Portal.dll"]
