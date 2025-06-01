# 1. Start with an official .NET 8 runtime image (slim but with libc etc)
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS base

# 2. Install runtime dependencies: Tesseract, Leptonica, FFmpeg, yt-dlp, and others
RUN apt-get update && apt-get install -y --no-install-recommends \
    tesseract-ocr tesseract-ocr-por libtesseract-dev libleptonica-dev \
    ffmpeg python3 python3-pip \
    && pip install --no-cache-dir yt-dlp \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# 3. Add the libleptonica-1.82.0.so symlink (for Charles Weld wrapper compatibility)
RUN ln -sf /usr/lib/x86_64-linux-gnu/liblept.so.5 /usr/lib/libleptonica-1.82.0.so

# 4. Add Tesseract traineddata (Portuguese already pulled above, add more if needed)
# e.g. RUN apt-get install -y tesseract-ocr-eng

# 5. Set a writable directory for storage (optional, can be mapped as volume)
RUN mkdir -p /app_data && chmod 777 /app_data

# 6. Set working directory
WORKDIR /app

# 7. Copy published app from a build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "TCUWatcher.API.csproj"
RUN dotnet publish "TCUWatcher.API.csproj" -c Release -o /app/publish

# 8. Merge build with runtime
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080

# 9. Default command (override if needed)
ENTRYPOINT ["dotnet", "TCUWatcher.API.dll"]
