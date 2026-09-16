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
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Obhijog.Domain/Obhijog.Domain.csproj                 src/Obhijog.Domain/
COPY src/Obhijog.Infrastructure/Obhijog.Infrastructure.csproj src/Obhijog.Infrastructure/
COPY src/Obhijog.Api/Obhijog.Api.csproj                       src/Obhijog.Api/

# Restores the API project and what it transitively references — Infrastructure, then
# Domain — rather than the solution.
#
# It restored Obhijog.sln until M10, on the reasoning that the solution restore also
# covered the test project. That reasoning coupled this file to the solution's contents,
# and M10 broke it: adding functions/Obhijog.Functions made the restore fail with
# "The project file ... was not found", because the solution listed a project no COPY
# above brings in. The image publishes the API and nothing else, so it has no business
# knowing how many projects the solution has. Validating the whole solution is the CI
# gate's job, on the same SHA, where `dotnet restore` and `dotnet build` already run.
RUN dotnet restore src/Obhijog.Api/Obhijog.Api.csproj

COPY src/ src/

# Only the API is published.
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
