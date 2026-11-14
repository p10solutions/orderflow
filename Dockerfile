FROM node:22.23.4-bookworm-slim AS web
WORKDIR /workspace/web
COPY web/package.json web/package-lock.json ./
RUN --mount=type=secret,id=build_ca \
    if [ -f /run/secrets/build_ca ]; then NODE_EXTRA_CA_CERTS=/run/secrets/build_ca npm ci; else npm ci; fi
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /workspace
COPY global.json Directory.Build.props OrderFlow.slnx ./
COPY config/ config/
COPY src/ src/
COPY tests/ tests/
RUN --mount=type=secret,id=build_ca \
    if [ -f /run/secrets/build_ca ]; then \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/build_ca > /tmp/orderflow-build-ca.pem; \
      SSL_CERT_FILE=/tmp/orderflow-build-ca.pem dotnet restore OrderFlow.slnx --locked-mode; \
      result=$?; rm /tmp/orderflow-build-ca.pem; exit "$result"; \
    else dotnet restore OrderFlow.slnx --locked-mode; fi
COPY --from=web /workspace/src/OrderGenerator.Host/wwwroot/ src/OrderGenerator.Host/wwwroot/
RUN dotnet publish src/OrderGenerator.Host -c Release --no-restore -o /out/generator && dotnet publish src/OrderAccumulator.Host -c Release --no-restore -o /out/accumulator
RUN mkdir -p /out/data

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra AS accumulator
WORKDIR /app
COPY --from=build /out/accumulator/ ./
COPY --from=build --chown=1654:1654 /out/data/ /data/
USER $APP_UID
EXPOSE 5001
ENTRYPOINT ["dotnet", "OrderAccumulator.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra AS generator
WORKDIR /app
COPY --from=build /out/generator/ ./
COPY --from=build --chown=1654:1654 /out/data/ /data/
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderGenerator.dll"]
