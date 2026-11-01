# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
ARG SKIP_TESTS=false
WORKDIR /src

COPY ["jetpack.sln", "./"]
COPY ["Jetpack.Api/Jetpack.Api.csproj", "Jetpack.Api/"]
COPY ["Jetpack.Tests/Jetpack.Tests.csproj", "Jetpack.Tests/"]
RUN dotnet restore "Jetpack.Tests/Jetpack.Tests.csproj"

COPY . .
# The test suite runs as part of the image build (--build-arg SKIP_TESTS=true
# to skip it).
RUN if [ "$SKIP_TESTS" != "true" ]; then \
      dotnet test "Jetpack.Tests/Jetpack.Tests.csproj" \
        -c $BUILD_CONFIGURATION --no-restore; \
    fi
RUN dotnet publish "Jetpack.Api/Jetpack.Api.csproj" -a $TARGETARCH \
      -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# File system storage lives in a volume owned by the unprivileged app user.
RUN mkdir -p /data && chown "$APP_UID" /data
ENV Storage__Path=/data \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
VOLUME ["/data"]
EXPOSE 8080 8443
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Jetpack.Api.dll"]
