FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Cotacoes.Web.csproj ./
RUN dotnet restore Cotacoes.Web.csproj

COPY . ./
RUN dotnet publish Cotacoes.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

CMD ["sh", "-c", "dotnet Cotacoes.Web.dll --urls http://0.0.0.0:${PORT:-8080}"]
