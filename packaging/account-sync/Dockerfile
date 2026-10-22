FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY app/ ./
ENV ASPNETCORE_URLS=http://+:8080 SELF_SNIP_DATA=/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Snip.Sync.Server.dll"]
