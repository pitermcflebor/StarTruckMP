FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY ./StarTruckMP-server .
EXPOSE 7777/tcp
EXPOSE 7777/udp
ENTRYPOINT ["dotnet", "StarTruckMP.Server.dll"]