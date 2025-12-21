FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Copy published SXMPlayer.Proxy output from CI-provided artifact directory
COPY .docker/app/ /app/

# Configure web listener (adjust if a different port is required)
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

# Run the app
ENTRYPOINT ["dotnet", "SXMPlayer.Proxy.dll"]
