# Handoff: even out Zoom participant loudness ("Sound Protection")

- **Date:** 2026-11-09
- **From:** a Claude Code session opened in `C:\Users\Ariel\source\PraklitAI`. That project is unrelated; Ariel called this "a non related question".
- **To:** the next session working in this folder.
- **Folder purpose:** inferred from the folder name only. Ariel has not described a project here. Confirm with Ariel before building anything.

## 1. The ask

Ariel, verbatim: "is there a way in zoon to force all participants to have same decibels? in group chats, one is silent another is screaming"

Reading: Ariel is a listener in Zoom group calls. Some participants are far too quiet and others far too loud. Ariel wants the volumes evened out, or at least the loud ones tamed.

## 2. Findings: Zoom itself

**Short answer: no.** The newest material found was a Feb 2026 Zoom dev-forum feature request.

- No host-side "normalize everyone's volume" setting exists.
- Listeners have no per-participant volume slider, only a master output level. Per-participant volume and host-side audio adjustment appear only as unmet feature requests on Zoom's dev forum and community.
- I found no official Zoom help page or release note on this. Everything is community and dev-forum posts, so a newer feature could exist. Check Zoom's release notes before declaring it impossible.

What Zoom does offer:

- **Per-user auto-gain:** Settings → Audio → "Automatically adjust microphone volume". Only the speaker's own client controls it. The host cannot force it, only ask. It reacts slowly (it averages over a short window), and some users report the box missing or greyed out.
- **Host mute controls:** Mute, Mute All, and unticking "Allow participants to unmute themselves". These are blunt tools.
- **Local recording option "record a separate audio file for each participant":** per-person tracks for post-production only, not live.
- **Zoom Rooms NDI feeds:** up to 3 feeds, each carrying one pinned participant's audio and video, which a mixer could level. One source calls this far from simple. Not explored.

Key constraint for any listener-side fix: my understanding is that the Zoom client plays all speakers as one mixed stream to the speaker device. This is consistent with per-person audio being reachable only via Zoom Rooms NDI or recordings. Anything done at the OS or device level therefore acts on the mix. It can tame loud peaks and lift quiet passages over time, but it cannot separate people who talk over each other.

## 3. Findings: this PC

Gathered from Ariel's two screenshots and one read-only PowerShell check.

- Windows 11 Pro, build 10.0.26200. Likely an HP machine (an HP Audio Control app is installed).
- Realtek driver: "Realtek High Definition Audio", provider Realtek Semiconductor Corp., version **6.0.9279.1**, INF `oem57.inf`.
- Store app installed: `RealtekSemiconductorCorp.HPAudioControl` v2.32.260.0 (HP Audio Control). **Not opened or inspected yet.**
- Other audio devices: NVIDIA High Definition Audio (`oem35.inf`, v1.4.5.6) for the monitors C24-40 and VA2405-FHD, and two "USB Audio Device" entries (Microsoft `wdma_usb.inf`).
- Classic Sound dialog, Playback tab: C24-40, VA2405-FHD, **Speakers** (Realtek), **Headphones** (Realtek). Headphones is the **Default Device**.
- Headphones Properties tabs: General, Levels, Advanced, Spatial sound. **There is no Enhancements tab.**
- Advanced tab: default format 2 channel, 24 bit, 48000 Hz (Studio Quality). Both Exclusive Mode boxes are ticked. "Enable audio enhancements" is ticked.
- New Settings page (System → Sound → Properties) for a Realtek endpoint (the icon looks like Speakers): output volume 32, "Audio enhancements" = Device Default Effects, Spatial sound = Off. The contents of the "Audio enhancements" dropdown were not checked. Ariel moved on to the classic dialog.

Conclusion: Windows' built-in **Loudness Equalization**, the obvious tool, is not exposed by this Realtek driver on the Realtek endpoints.

Caution: the screenshots show the Windows default device. Zoom has its own speaker selection (Zoom → Settings → Audio → Speaker), which may point at another device such as a USB headset. Not checked.

## 4. Options and status

| # | Option | Status | Notes |
|---|--------|--------|-------|
| 1 | Look in HP Audio Control for a loudness or leveling option | Not tried | Cheap. Low expectations. |
| 2 | Swap the driver to Microsoft's generic "High Definition Audio Device" so the Enhancements tab (with Loudness Equalization) returns | Not done. Recommended to Ariel; no decision yet | Steps below. The reports of this fix are blogs and forum posts, not official docs, and are unverified on this PC. |
| 3 | Software compressor or limiter on the output path: Equalizer APO plus a compressor VST (e.g. ReaComp), or Voicemeeter (has compressor controls; check which edition applies one to the output bus) | Not done. Ariel was offered written steps | No driver change and fully reversible, but more setup. |

Option 2 steps:

1. Device Manager → Sound, video and game controllers → Realtek High Definition Audio → right-click → Update driver → Browse my computer → Let me pick from a list.
2. Choose "High Definition Audio Device" (Microsoft) → Next.
3. Reopen the classic Sound dialog (Win+R → `mmsys.cpl`) → Playback → the device → Properties → Enhancements → tick Loudness Equalization.

Option 2 trade-offs:

- HP Audio Control will probably stop working, and Windows Update may put the Realtek driver back later.
- One report (a Dell owner) saw no generic entry in the list. If that happens here, this route is unavailable.
- To revert, repeat the steps and pick the Realtek entry (`oem57.inf` stays in the driver store), or use Roll Back Driver.

## 5. What was changed

Nothing. No settings, drivers or software were changed, and nothing was installed. The only actions were web searches and two read-only checks: `Get-AppxPackage -Name *Realtek*` and `Get-CimInstance Win32_PnPSignedDriver` filtered to class MEDIA.

## 6. Open questions for Ariel

1. Which option: try 1 first and then 2, or go straight to 3?
2. Which output device does Zoom use during calls (Realtek headphones jack, a USB audio device, or a monitor)? Any fix has to target that device.
3. Is the goal only Ariel's own listening, or a reusable tool (hence the folder name "Sound Protection")? A limiter that protects against sudden loud peaks is a different and bigger project than an equalizer for call volume.
4. Is Ariel comfortable changing the audio driver (option 2)?

## 7. Suggested next steps

1. Ask Ariel the open questions, especially 2 and 4.
2. Open HP Audio Control and record what it offers.
3. With Ariel's go-ahead, do option 2, or option 3 if Ariel prefers no driver change.
4. Test with two clips at very different levels, or Zoom's speaker test, with Loudness Equalization on and off. Confirm the gap between loud and quiet gets smaller and that Ariel finds it acceptable.
5. Suggest that the quiet and loud participants turn on Zoom's "Automatically adjust microphone volume". It is the only fix that acts at the source.

## 8. Sources

None is official Zoom or Realtek documentation.

Zoom:

- https://devforum.zoom.us/t/add-volume-controlling-unit-for-every-users/141710.md
- https://devforum.zoom.us/t/zoom-volume-normalization/124148.md
- https://devforum.zoom.us/t/host-administrated-participant-audio-adjustment/30525
- https://community.zoom.com/meetings-2/how-can-i-boost-reduce-volume-for-specific-participants-28109
- https://login.baycollege.edu/wiki/index.php/Zoom_automatic_adjust_volume (a college IT wiki on the auto-adjust setting)

Windows and Realtek:

- https://learn.microsoft.com/en-us/answers/questions/5490258/how-to-fix-enhancements-tab-disappearing-after-win
- https://itechtics.com/restore-enhancement-tab/
- https://www.kapilarya.com/fix-audio-enhancements-tab-missing-in-windows-11-10-8-1
- https://answers.microsoft.com/en-us/windows/forum/all/how-do-i-get-loudness-equalization-back/d3cf05a1-0fb7-4576-9f5d-ca32a4c85ec2
- Unread lead, possibly relevant to an HP machine: https://h30434.www3.hp.com/t5/AI-PCs/App-for-Loudness-Equalization-of-Realtek-Audio-Speakers/td-p/9549090

## 9. Notes for the receiving agent

- Windows 11. PowerShell is the primary shell, and Git Bash is also available.
- Do not change drivers or install system-wide audio software without Ariel's explicit go-ahead. Confirm first.
- This folder was empty when the handoff was written, and no project memory exists for it. Treat this file as the full context.
