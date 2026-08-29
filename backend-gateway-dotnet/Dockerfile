FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore dependencies
COPY ["src/JobLens.Api/JobLens.Api.csproj", "JobLens.Api/"]
COPY ["src/JobLens.Application/JobLens.Application.csproj", "JobLens.Application/"]
COPY ["src/JobLens.Domain/JobLens.Domain.csproj", "JobLens.Domain/"]
COPY ["src/JobLens.Infrastructure/JobLens.Infrastructure.csproj", "JobLens.Infrastructure/"]
RUN dotnet restore "JobLens.Api/JobLens.Api.csproj"
# Build and publish
COPY src/ .
WORKDIR "/src/JobLens.Api"
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "JobLens.Api.dll"]