# CallBridge Softphone v0.3.0

A dependency-free Windows desktop softphone prototype for Hive/Axion PBX workflows.

## Run

Double-click `run-softphone.cmd`. No installation, browser, port, Node.js, or npm is required.

## Included

- IT Health Technologies blue/teal visual system
- Windows-native Segoe MDL2 icons with no encoding corruption
- Dial pad and number entry
- Simulated outgoing call state
- Mute, hold, transfer, keypad, and hang-up controls
- Contacts, favorites, and presence
- Call history and missed-call indicators
- Voicemail and messaging navigation
- Device and SIP account settings
- Searchable contacts, call history, messages, and voicemail
- PBX dashboard and connector health
- Hive PBX, SIP, connector, and directory connection views
- Live `/health` and `/diagnostics` connector status
- Live `/events/recent` call history with automatic demo fallback
- Editable local API and extension configuration in `settings.json`
- Always-on-top and launch-at-startup preferences

## Telephony boundary

The current build provides the complete desktop interaction shell and simulated call state. Real registration, audio, incoming calls, DTMF, transfer, voicemail, and presence require a Hive PBX SIP/WebRTC adapter.
