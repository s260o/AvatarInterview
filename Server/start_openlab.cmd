@echo off
rem Start the API server in OPEN LAB (demo) mode. Experiment features are disabled.
rem See OPEN_LAB.md. For the real experiment, start the server the normal way instead.
cd /d "%~dp0"
set OPEN_LAB_MODE=1
python -m uvicorn main:app --host 0.0.0.0 --port 8001
