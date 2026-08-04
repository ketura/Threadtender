FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ThreadTender/ThreadTender.csproj ThreadTender/
RUN dotnet restore ThreadTender/ThreadTender.csproj
COPY ThreadTender/ ThreadTender/
RUN dotnet publish ThreadTender/ThreadTender.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENV DATA_DIR=/data
VOLUME /data
ENTRYPOINT ["dotnet", "ThreadTender.dll"]
