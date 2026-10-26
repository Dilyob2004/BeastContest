FROM mcr.microsoft.com/dotnet/sdk:10.0
ENV DEBIAN_FRONTEND=noninteractive DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 DOTNET_EnableDiagnostics=0 DOTNET_GCHeapHardLimitPercent=50 DOTNET_PROCESSOR_COUNT=2 HOME=/tmp GOMAXPROCS=2 GOCACHE=/tmp/go-cache GOPATH=/tmp/go CGO_ENABLED=0 GOTOOLCHAIN=local GOPROXY=off GOSUMDB=off
RUN apt-get update && apt-get install -y --no-install-recommends g++ python3 openjdk-21-jdk-headless golang-go ghc nodejs && rm -rf /var/lib/apt/lists/* && mkdir /solution /opt/judge && chmod 755 /solution
COPY judge/prepare.py judge/pack.py judge/testlib.h judge/testlib.LICENSE /opt/judge/
RUN chmod 644 /opt/judge/testlib.h /opt/judge/testlib.LICENSE
RUN python3 /opt/judge/prepare.py
# .NET environment GC percentages are hexadecimal: 0x32 = 50 percent.
ENV DOTNET_GCHeapHardLimitPercent=32
USER 65534:65534
WORKDIR /solution
CMD ["true"]
