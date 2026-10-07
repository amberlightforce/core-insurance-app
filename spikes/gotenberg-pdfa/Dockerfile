# Builds the spike console with the .NET 10 SDK image (no local SDK needed).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PdfaSpike/PdfaSpike.csproj src/PdfaSpike/
RUN dotnet restore src/PdfaSpike/PdfaSpike.csproj
COPY src/ src/
RUN dotnet publish src/PdfaSpike/PdfaSpike.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
COPY fonts/ /app/fonts/
ENTRYPOINT ["dotnet", "PdfaSpike.dll"]
