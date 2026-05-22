FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Barbershop.sln ./
COPY Directory.Build.props ./
COPY src/Api.Barbershop/Api.Barbershop.csproj src/Api.Barbershop/
COPY src/Barbershop.Application/Barbershop.Application.csproj src/Barbershop.Application/
COPY src/Barbershop.Domain/Barbershop.Domain.csproj src/Barbershop.Domain/
COPY src/Barbershop.Infrastructure/Barbershop.Infrastructure.csproj src/Barbershop.Infrastructure/
COPY tests/Barbershop.Tests/Barbershop.Tests.csproj tests/Barbershop.Tests/

RUN dotnet restore Barbershop.sln

COPY . .
RUN dotnet publish src/Api.Barbershop/Api.Barbershop.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:5000
# Default environment is Production; override at runtime with -e ASPNETCORE_ENVIRONMENT=QA
ENV ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish .

RUN addgroup --system appgroup \
  && adduser --system --ingroup appgroup appuser \
  && chown -R appuser:appgroup /app

USER appuser

EXPOSE 5000

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
  CMD wget -qO- http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "Api.Barbershop.dll"]
