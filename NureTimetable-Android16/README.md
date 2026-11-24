# Nure Timetable — Android 16 / Redmi Note 13 Pro

This standalone .NET MAUI project targets Android API 36 and ARM64.

## Current build scope

This first APK is a mobile shell around the official CIST timetable website. It provides back/forward/home/refresh navigation and an internet status message. It is not yet a full native port of every feature of the Xamarin app. Offline cache, timetable search, favorites, lesson timer and notes are not implemented yet.

The original Xamarin source depends on an uncommitted secrets.json for several keys, and CIST mobile API availability must be verified before a native API port can be completed.

## Build

The GitHub Actions workflow at the repository root builds a Release APK. It creates a temporary signing key on the runner for each build. Because that key is temporary, future updates may require uninstalling the previous build first.

Package id: ua.nure.timetable.mobile
Target: Android API 36
Minimum: Android API 24
ABI: arm64-v8a
