# DvbHeadend

A small DVB-T/T2 TV server for a Raspberry Pi with USB tuners: a from-scratch
replacement for Tvheadend, written in C# on .NET 10. It talks to the kernel's
DVB API directly (no `dvbv5-zap`, no ffmpeg) and serves channels to anything
on the LAN.

- **Live TV over HTTP**: each channel is a plain MPEG-TS stream (`/stream/channel/101`); the
  Play button in the web UI downloads a one-line playlist that opens it in VLC
- **Tuner sharing**: a dual tuner serves any number of channels at once as long as
  they sit on at most two muxes
- **Looks like an HDHomeRun** to Jellyfin, Plex and Emby; also M3U + XMLTV for VLC/Kodi
- **Channel scan** with Freeview channel numbers (LCNs from the NIT)
- **Programme guide** from broadcast EIT, including the compressed text used on Freeview HD,
  refreshed in the background when a tuner is idle
- **Recordings** to disk (local or a NAS mount), scheduled from the guide
- **Web UI** on port 5004: channels as a list or a timeline grid guide, favourites, your own
  channel logos, recordings, live tuner status and per-mux signal history charts

## Install on the Pi

Needs 64-bit Raspberry Pi OS and the tuner showing up as `/dev/dvb/adapter0` and
`adapter1` (`ls /dev/dvb`). Copy this folder to the Pi, then:

```bash
sudo deploy/install.sh
```

That installs the ASP.NET Core 10 runtime to `/opt/dotnet`, the app to `/opt/dvbheadend`,
creates a `dvbheadend` service user in the `video` group, disables Tvheadend if it is
running (it locks the tuners), and starts the `dvbheadend` systemd service.

Then open `http://<pi>:5004`, go to **Setup** and press **Scan now**. The seven
Black Hill muxes are pre-loaded; edit them on the same page for any other transmitter.

Logs: `journalctl -u dvbheadend -f`

## Connecting clients

| Client | How |
|---|---|
| Jellyfin | Dashboard → Live TV → add tuner **HDHomeRun**, URL `http://<pi>:5004`. Guide: add **XMLTV**, `http://<pi>:5004/xmltv.xml` |
| Plex | Live TV & DVR → set up → enter the address manually: `<pi>:5004` |
| Kodi | PVR IPTV Simple Client → M3U `http://<pi>:5004/playlist.m3u` (the playlist points at the XMLTV guide itself) |
| VLC | Media → Open Network Stream → `http://<pi>:5004/playlist.m3u` |

## Settings

`/etc/dvbheadend/settings.json` overrides `appsettings.json` and survives upgrades.
Restart the service after editing.

| Key (under `Headend`) | Default | Meaning |
|---|---|---|
| `RecordingsDir` | `/var/lib/dvbheadend/recordings` | Point at a NAS mount to record to the NAS |
| `Adapters` | `[]` (all found) | Restrict to specific `/dev/dvb/adapterN` numbers |
| `TuneTimeoutSeconds` | 6 | How long to wait for signal lock |
| `IdleReleaseSeconds` | 30 | Keep a tuner on its mux this long after the last viewer leaves |
| `EpgGrabIntervalMinutes` | 120 | Background guide refresh; 0 turns it off |
| `SignalSampleSeconds` / `SignalHistoryHours` | 20 / 48 | How often each tuned mux's signal is sampled, and how long the history is kept; 0 seconds turns it off |
| `AutoScanIntervalDays` / `AutoScanHour` | 7 / 4 | Automatic channel rescan every N days at that local hour, only while no tuner is in use; 0 days turns it off |
| `RecordingPreRollSeconds` / `PostRollSeconds` | 60 / 180 | Padding around recordings |

Recording to a NAS: mount the share (e.g. `/mnt/recordings` via `/etc/fstab`), make it
writable by the `dvbheadend` user, and set `RecordingsDir`.

## Who gets the tuner

Requests carry a priority: guide refresh (1) < scan (3) < live viewing (5) < recording (10).
A request for a mux that is already tuned always shares it. If both tuners are busy on
other muxes, a higher-priority request takes the tuner from the lowest-priority one; an
equal-priority request gets HTTP 503.

## Known limits

- **No transcoding**: streams are passed through as broadcast (MPEG-2 SD, H.264 HD).
  Let Jellyfin/Plex transcode for clients that need it.
- **No authentication**: keep it on the home network.
- **No auto-discovery**: clients are pointed at the address by hand.
- DVB-T and DVB-T2 only (no satellite or cable), free-to-air only.

## Third-party data

`src/DvbHeadend/Ts/freesat_huffman.bin` holds the Freeview HD / Freesat Huffman code tables,
converted by `tools/make_huffman_tables.py` from tvheadend's `freesat_huffman.c`, which is
licensed GPL-3.0. Keep that in mind before redistributing this project.

## Building

```bash
./build.sh        # runs the tests, then writes ./publish
```

Needs the .NET 10 SDK. The output is portable (no native code), so it can be built on
the Pi or on any other machine and copied over.

### Running without a tuner

```bash
tools/make_test_mux.py /tmp/testdata      # needs ffmpeg; builds two fake muxes with NIT + EIT
tools/run_simulated.sh /tmp/testdata      # two simulated tuners on http://127.0.0.1:5004
```

## Layout

```
src/DvbHeadend/Linux     ioctl bindings: frontend (tuning, signal), demux/dvr (transport stream)
src/DvbHeadend/Ts        packets, section reassembly, PAT/PMT/SDT/NIT/EIT, DVB text
src/DvbHeadend/Tuners    ITuner: LinuxDvbTuner (real), FileTuner (simulated)
src/DvbHeadend/Core      TunerManager, MuxSession, ServiceStreamer, Scanner, EpgStore, recorder
src/DvbHeadend/Web       stream, HDHomeRun/M3U/XMLTV and REST endpoints
src/DvbHeadend/wwwroot   web UI
```
