FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /source
COPY global.json Directory.Build.props nuget.config ./
COPY src/ ./src/
RUN --mount=type=bind,source=_build/restore,target=/restore \
    if ls /restore/*.nupkg >/dev/null 2>&1; then \
        dotnet publish src/Host/Host.csproj --configuration Release --output /publish --no-self-contained -p:UseAppHost=false --source /restore -p:NuGetAudit=false; \
    else \
        dotnet publish src/Host/Host.csproj --configuration Release --output /publish --no-self-contained -p:UseAppHost=false; \
    fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build --chown=1654:1654 /publish/ /app/host/
COPY --chown=1654:1654 _image/packages/ /app/packages/
ENV SHE_PACKAGES_DIR=/app/packages
USER 1654
ENTRYPOINT ["/usr/share/dotnet/dotnet", "/app/host/SelfHostedEvaluators.Host.dll"]
CMD ["run"]
