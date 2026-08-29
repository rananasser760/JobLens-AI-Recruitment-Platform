FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/JobLens.Api/JobLens.Api.csproj src/JobLens.Api/
COPY src/JobLens.Application/JobLens.Application.csproj src/JobLens.Application/
COPY src/JobLens.Domain/JobLens.Domain.csproj src/JobLens.Domain/
COPY src/JobLens.Infrastructure/JobLens.Infrastructure.csproj src/JobLens.Infrastructure/
RUN dotnet restore src/JobLens.Api/JobLens.Api.csproj

COPY . .
RUN dotnet publish src/JobLens.Api/JobLens.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=5245
EXPOSE 5245

ENTRYPOINT ["dotnet", "JobLens.Api.dll"]