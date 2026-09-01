FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY DoForYou.API.csproj .
RUN dotnet restore DoForYou.API.csproj
COPY . .
RUN dotnet publish DoForYou.API.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DoForYou.API.dll"]
