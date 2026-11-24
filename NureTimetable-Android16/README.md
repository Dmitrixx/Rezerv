# Nure Timetable — Android 16 Edition

An independent .NET MAUI implementation for Android 16 / Redmi Note 13 Pro, based on the public feature description and available CIST interfaces in the original maxkoshevoi/NureTimetable repository.

## Features in this port

- Multiple saved group / teacher / room timetables.
- Search and add entities from CIST catalog pages, plus manual ID entry.
- Day, week and month timetable views.
- Local JSON schedule cache and offline viewing.
- Manual refresh and refresh-on-app-resume; 12-hour foreground update interval.
- Filters by lesson text and event type; lesson/teacher hiding preferences.
- Lesson notes and statistics (total, upcoming, next lesson, teachers, lecture/practice/lab counts).
- Timer until the next lesson / current break.
- Light, dark and system themes.
- Russian / Ukrainian / English UI language selector.
- ICS calendar export and share.
- DL NURE login link.
- Android ARM64 APK, API 36.

## CIST schedule source

The original app's P_API_EVEN_JSON mobile timetable endpoint is not assumed to be available. The port uses the older public CSV export endpoint for group and teacher timetables and tries the HTML entity catalog endpoints where the JSON catalog endpoints fail. The XLS export is used as an optional source of subject names / teacher metadata.

CIST may temporarily disable exports or change the page markup. In that case cached schedules remain viewable; first-time downloads or catalogue search can fail until the upstream service is restored. Room schedule export uses a legacy Aid_aud parameter and is best-effort.

## Build

Use .github/workflows/build-android.yml. The workflow creates a one-build signing key on the runner and uploads the signed APK as an Actions artifact. The key is not persisted, so installing a later build over an APK signed by a different temporary key may require uninstalling the old app first.

Package ID: ua.nure.timetable.mobile
Target Android API: 36
Minimum Android API: 24
ABI: arm64-v8a

The project is independent of the original developer's unpublished secrets.json; this port does not embed private Syncfusion or App Center keys.
