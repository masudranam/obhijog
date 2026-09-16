# The API image. SPEC.md §14 F15.
#
# Multi-stage and non-root, both for the reasons F15 gives rather than as ceremony:
# the SDK is ~800 MB of compiler and NuGet cache that has no business in a running
# container, and a process that cannot write to its own filesystem cannot be made to
# persist anything by whatever reaches it.

# ---------------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build
WORKDIR /src

# Restore before copying the source. Docker caches a layer by the files it was built
# from, so a change to a .cs file must not invalidate the restore — which is the
# difference between a ten-second rebuild and a two-minute one.
#
# Directory.Build.props and Directory.Packages.props come first because central
# package management means the restore genuinely cannot resolve a version without
# them, not merely that it would be slower.
COPY Directory.Build.props Directory.Packages.props Obhijog.sln ./
COPY src/Obhijog.Domain/Obhijog.Domain.csproj                 src/Obhijog.Domain/
COPY src/Obhijog.Infrastructure/Obhijog.Infrastructure.csproj src/Obhijog.Infrastructure/
COPY src/Obhijog.Api/Obhijog.Api.csproj                       src/Obhijog.Api/
COPY tests/Obhijog.Tests/Obhijog.Tests.csproj                 tests/Obhijog.Tests/

# The solution references the test project, so restoring the solution restores it too.
# Restoring the API project alone would skip it and leave `dotnet build` to discover
# the gap later.
RUN dotnet restore Obhijog.sln

COPY src/ src/

# Only the API is published; the test project is restored above but never built here.
# TreatWarningsAsErrors is on for every project (Directory.Build.props), so this stage
# fails on a warning — which is the intent: the image and the CI gate hold the same bar.
RUN dotnet publish src/Obhijog.Api/Obhijog.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /app

# ---------------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble AS runtime

# `app` (uid 1654) ships with the Microsoft .NET images. Using it rather than adding a
# user means the image works unchanged under a Kubernetes `runAsNonRoot` policy, which
# checks the numeric uid and rejects a container whose user resolves to 0.
USER app
WORKDIR /app

# Container Apps sets PORT and expects the app to listen on it; 8080 is the default
# for a non-root .NET 8+ image, which cannot bind 80. Kestrel reads ASPNETCORE_HTTP_PORTS.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=true \
    # The SLA sweeper is a hosted service and one instance must own it (§17). Scaling
    # the API out means setting this to 0 on every replica but one. It is spelled out
    # here so the decision is visible in the image rather than only in the spec.
    Sla__SweepIntervalSeconds=60

EXPOSE 8080

COPY --from=build --chown=app:app /app .

# No HEALTHCHECK instruction: Container Apps probes over HTTP from outside the
# container and ignores it, so one here would be a second definition of readiness that
# nothing reads. /health and /health/ready are wired to the Bicep probes instead.

ENTRYPOINT ["dotnet", "Obhijog.Api.dll"]
