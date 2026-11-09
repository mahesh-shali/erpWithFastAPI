FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Erp.Api/Erp.Api.csproj src/Erp.Api/
RUN dotnet restore src/Erp.Api/Erp.Api.csproj
COPY src/ src/
RUN dotnet publish src/Erp.Api/Erp.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_TieredPGO=1
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Erp.Api.dll"]
