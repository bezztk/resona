# Resona

**Your music, your library, your decisions.**

Resona is a local-first music library for people who want to collect, understand, and curate their music instead of handing that job to a streaming service. The desktop application downloads new tracks through one local queue, while the self-hosted analyzer handles audio analysis. Windows, Linux, and Android can retain audio for offline playback.

Paste a YouTube video, playlist, mix, or radio link and Resona expands it into a persistent import queue. New tracks can be analyzed with Essentia-based models for genre suggestions, BPM, loudness, dynamics, and emotional character. Ratings, tags, languages, styles, collections, presets, and reusable views make even a large library manageable without taking control away from you.

Resona also includes a dedicated track editor, Discord Rich Presence, review workflows, artwork-driven visuals, and local database backups.

## How the system fits together

- **Resona Desktop** owns and edits the SQLite library, downloads tracks, and plays local audio.
- **Resona Analyzer** processes audio for the desktop app.
- **Resona Android** plays a portable local library without writing library metadata.

## Requirements

For the desktop application and analyzer you need:

- a Windows or Linux computer for Resona Desktop;
- `yt-dlp`, FFmpeg, and FFprobe for imports;
- a reachable Resona Analyzer instance for audio analysis.

The analyzer can run as a Docker service from `src/AnalysisServer`. Configure its address under **Settings → Connections**.

## Linux

Linux x64 releases are available as an AppImage. Playback uses the system `libvlc`; imports rely on `yt-dlp`, FFmpeg, FFprobe, and Node.js. Resona reports missing runtime dependencies in Settings and integrates with desktop media controls through MPRIS.

## Screenshots

### Your library at a glance

Browse, play, rate, and manage the collection from the central library view.

![Library view](docs/images/track-library.png)

### Make every track your own

Edit metadata, ratings, classifications, language, and analysis results in one focused view.

![Track editor](docs/images/edit.png)

### Find the exact mood

Combine ratings, genres, styles, tags, languages, visibility, and emotional character.

![Filter](docs/images/filter.png)

### Import an entire playlist from one URL

Paste a YouTube playlist, mix, or radio link, review the expanded track list, and send the selection into the persistent import queue.

![Playlist import](docs/images/playlist-import.png)

### Tune Resona to your environment

Configure playback, analysis, appearance, backups, integrations, and external tools.

![Settings](docs/images/settings.png)
