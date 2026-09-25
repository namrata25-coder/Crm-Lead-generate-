FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY ["CrmLeadManagement.csproj", "./"]
RUN dotnet restore "CrmLeadManagement.csproj"

# Copy everything else and publish
COPY . .
RUN dotnet publish "CrmLeadManagement.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false


# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "CrmLeadManagement.dll"]