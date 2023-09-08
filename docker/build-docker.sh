#!/usr/bin/env bash
dotnet publish ../src/AkkaWebTemplate.App/AkkaWebTemplate.App.csproj --os linux --arch x64 -c Release -p:PublishProfile=DefaultContainer