# Use the official ASP.NET Core runtime image as the base image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Use the official .NET SDK image for building the application
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy the project file and restore dependencies
COPY ["Jetpack.Api/Jetpack.Api.csproj", "Jetpack.Api/"]
RUN dotnet restore "Jetpack.Api/Jetpack.Api.csproj"

# Copy the remaining source code and build the application
COPY . .
WORKDIR "/src/Jetpack.Api"
RUN dotnet build "./Jetpack.Api.csproj" -a $TARGETARCH -c $BUILD_CONFIGURATION -o /app/build

# Publish the application to a folder
FROM build AS publish
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Jetpack.Api.csproj" -a $TARGETARCH -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Create the final image with the published application
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Jetpack.Api.dll"]
