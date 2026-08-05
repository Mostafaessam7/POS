# syntax=docker/dockerfile:1
#
# Builds POS.Api into a container image. Multi-stage: the SDK image (large, has the
# compiler) only exists to produce a publish output; the runtime image (small, no
# compiler) is what actually ships and runs. Confusing the two is how a production
# image ends up multiple gigabytes larger than it needs to be.
#
# Build from the REPOSITORY ROOT, not this directory — the module layout means
# POS.Api.csproj's ProjectReferences reach up into src/Modules and src/Shared:
#
#   docker build -f src/Hosts/POS.Api/Dockerfile -t pos-api .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Central package management files first, so a change to application code alone
# does not invalidate the restore layer — restore is the slow, network-bound step.
COPY Directory.Build.props Directory.Packages.props ./

# The whole src/ tree, not just POS.Api's own folder: `dotnet restore` on a single
# project still needs every ProjectReference it transitively pulls in (every
# module's domain + infrastructure half, POS.Common, POS.SharedKernel, POS.Contracts)
# to be present on disk, or it fails immediately. tests/ is deliberately NOT copied
# — nothing test-only is reachable from POS.Api.csproj, and pulling test projects
# into the image context would only add unused weight and cache invalidation.
COPY src/ src/

RUN dotnet restore src/Hosts/POS.Api/POS.Api.csproj

RUN dotnet publish src/Hosts/POS.Api/POS.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# The base image ships a non-root "app" user (APP_UID) for exactly this — a
# container running its process as root is a needless privilege-escalation surface
# if the process is ever compromised.
USER $APP_UID

COPY --from=build /app/publish .

# Kestrel's own binding, not IIS/nginx port assumptions. 8080 (not 80) matches the
# aspnet base image's documented convention for the non-root user, which cannot bind
# privileged ports below 1024.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "POS.Api.dll"]
