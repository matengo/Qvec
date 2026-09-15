# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0-noble AS build
WORKDIR /src

COPY Qvec.slnx ./
COPY Qvec.Api/Qvec.Api.csproj Qvec.Api/
COPY Qvec.Core/Qvec.Core.csproj Qvec.Core/
RUN dotnet restore Qvec.Api/Qvec.Api.csproj -r linux-x64

COPY Qvec.Api/ Qvec.Api/
COPY Qvec.Core/ Qvec.Core/
RUN dotnet publish Qvec.Api/Qvec.Api.csproj -c Release -r linux-x64 --self-contained true /p:PublishAot=true --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["./Qvec.Api"]