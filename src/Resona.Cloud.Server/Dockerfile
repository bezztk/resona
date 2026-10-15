FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Resona.Core/Resona.Core.csproj src/Resona.Core/
COPY src/Resona.Cloud.Server/Resona.Cloud.Server.csproj src/Resona.Cloud.Server/
RUN dotnet restore src/Resona.Cloud.Server/Resona.Cloud.Server.csproj
COPY src/Resona.Core/ src/Resona.Core/
COPY src/Resona.Cloud.Server/ src/Resona.Cloud.Server/
RUN dotnet publish src/Resona.Cloud.Server/Resona.Cloud.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Resona.Cloud.Server.dll"]
