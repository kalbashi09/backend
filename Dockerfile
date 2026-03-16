
# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
RUN apt-get update && apt-get install -y libgssapi-krb5-2
WORKDIR /app

# Look inside the NEW folder name for the project file
COPY backend/*.csproj ./
RUN dotnet restore

# Look inside the NEW folder name for the source code
COPY backend/ ./
RUN dotnet publish -c Release -o out

# Stage 2: Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build-env /app/out .

# IMPORTANT: The DLL name is usually the same as the .csproj filename
# If your file is backend.csproj, this is correct:
ENTRYPOINT ["dotnet", "backend.dll"]

