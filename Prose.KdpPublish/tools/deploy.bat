@echo off
rem KdpPublish redeploy convenience launcher.
rem Shuts down any running instance, clears build cache, rebuilds, publishes to
rem C:\Apps\KdpPublish\, and launches it. Same process IdiotProof uses
rem (tools\publish-all.ps1) to always have a fresh, independent deployed copy.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1" -Launch %*
