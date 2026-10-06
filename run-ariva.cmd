@echo off
rem Runs Ariva on this machine and shows every demo user's sign-in details; see run-ariva.ps1.
rem Double-click it, or: run-ariva.cmd -Demo (also starts the scripted demo evening).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-ariva.ps1" %*
