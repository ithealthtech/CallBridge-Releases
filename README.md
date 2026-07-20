# CallBridge Softphone v0.1.0

A dependency-free Windows desktop softphone prototype for Hive/Axion PBX workflows.

## Run

Double-click `run-softphone.cmd`. No installation, browser, port, Node.js, or npm is required.

## Included

- Dial pad and number entry
- Simulated outgoing call state
- Mute, hold, transfer, keypad, and hang-up controls
- Contacts, favorites, and presence
- Call history and missed-call indicators
- Voicemail and messaging navigation
- Device and SIP account settings
- Always-on-top and launch-at-startup preferences

## Telephony boundary

The current build provides the complete desktop interaction shell and simulated call state. Real registration, audio, incoming calls, DTMF, transfer, voicemail, and presence require a Hive PBX SIP/WebRTC adapter.
