@echo off
rem Expose http://localhost:8001 as https://api.s260o.com via Cloudflare Tunnel.
rem Start the API server (uvicorn) in another terminal first. Stop with Ctrl+C.
cloudflared tunnel run --url http://localhost:8001 avatar-interview
