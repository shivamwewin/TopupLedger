FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/TopupLedger.Domain/ TopupLedger.Domain/
COPY src/TopupLedger.Application/ TopupLedger.Application/
COPY src/TopupLedger.Infrastructure/ TopupLedger.Infrastructure/
COPY src/TopupLedger.Api/ TopupLedger.Api/
RUN dotnet publish TopupLedger.Api/TopupLedger.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TopupLedger.Api.dll"]
