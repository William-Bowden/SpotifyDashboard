
#!/usr/bin/env sh
set -eu

echo "Installing .NET 10..."
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0 --install-dir ./dotnet

echo "Locating project..."
PROJECT=$(find . -maxdepth 3 -name "*.csproj" -not -path "./dotnet/*" | head -n 1)

if [ -z "$PROJECT" ]; then
    echo "ERROR: No .csproj file found!"
    exit 1
fi

echo "Publishing $PROJECT..."
./dotnet/dotnet publish "$PROJECT" -c Release -o publish

echo "Preparing Cloudflare output..."
mkdir -p output/wwwroot

if [ -f publish/wwwroot/index.html ]; then
    cp -a publish/wwwroot/. output/wwwroot/
elif [ -f publish/index.html ]; then
    cp -a publish/. output/wwwroot/
else
    echo "ERROR: Could not locate published index.html"
    find publish -maxdepth 4 -type f | head -n 50
    exit 1
fi

echo "Verifying output..."
test -f output/wwwroot/index.html
echo "Build completed successfully!"

echo "=== OUTPUT DIRECTORY CONTENTS ==="
ls -la output/wwwroot

echo "=== INDEX.HTML PREVIEW ==="
head -n 20 output/wwwroot/index.html

echo "=== BUILD SCRIPT IN OUTPUT? ==="
find output/wwwroot -name "build.sh"
