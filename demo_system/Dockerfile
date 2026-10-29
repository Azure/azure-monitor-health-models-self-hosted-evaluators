FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
WORKDIR /source
COPY src/Checkout.Demo/Checkout.Demo.csproj src/Checkout.Demo/
RUN dotnet restore src/Checkout.Demo/Checkout.Demo.csproj --source "$NUGET_SOURCE"
COPY src/Checkout.Demo/*.cs src/Checkout.Demo/
RUN dotnet publish src/Checkout.Demo/Checkout.Demo.csproj \
    --configuration Release --no-restore --no-self-contained --output /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
COPY --from=build --chown=1654:1654 /publish/ ./
COPY --chmod=755 container/*.sh /app/control/
USER 1654
ENTRYPOINT ["dotnet", "Checkout.Demo.dll"]
CMD ["api"]
