@echo off
REM Opens the Live Studio in Chrome with a synthetic camera and microphone.
REM Use this to exercise the full broadcast flow on a machine with no webcam.
REM A separate profile directory is used so your normal Chrome session is untouched.

start "" "C:\Program Files\Google\Chrome\Application\chrome.exe" ^
  --use-fake-device-for-media-stream ^
  --use-fake-ui-for-media-stream ^
  --autoplay-policy=no-user-gesture-required ^
  --user-data-dir="%TEMP%\livestream-fake-camera" ^
  http://localhost:3000/login
