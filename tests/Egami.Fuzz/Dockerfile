# AFL++ with the .NET SDK and SharpFuzz, for fuzzing on any machine with Docker (see README.md).
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends afl++ && rm -rf /var/lib/apt/lists/*
RUN dotnet tool install --global SharpFuzz.CommandLine
ENV PATH="${PATH}:/root/.dotnet/tools" \
    AFL_SKIP_BIN_CHECK=1 AFL_NO_UI=1 AFL_SKIP_CPUFREQ=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1
WORKDIR /work
