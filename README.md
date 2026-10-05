# AvatarInterview

AvatarInterview is an interactive avatar interview system for VR and MR, built with Meta Quest and Unity.
It connects to a FastAPI backend powered by the Gemini API for speech recognition, interview responses, facial expressions, and speech synthesis.

## Features

- Transcribes microphone input with Gemini speech-to-text
- Generates interview responses using the conversation history
- Controls VRM avatar expressions based on response labels
- Plays generated responses with Gemini text-to-speech
- Places the avatar and handles environmental occlusion with Meta XR MR Utility Kit
- Supports configurable nonverbal behavior, nodding, gaze, and posture controls

## Project Structure

```text
Assets/           Unity scenes, scripts, and avatar assets
Packages/         Unity Package Manager settings and local packages
ProjectSettings/  Unity project settings
Server/           FastAPI backend for Gemini API integration
```

## Requirements

- Unity 2022.3.62f3
- Python 3.11 or later
- A Meta Quest and Meta XR-compatible environment
- A Gemini API key issued in Google AI Studio
- Git LFS

## Setup

### 1. Clone the Repository

```bash
git lfs install
git clone https://github.com/s260o/AvatarInterview.git
cd AvatarInterview
```

### 2. Configure the API Server

Create a Gemini API key from the API keys page in [Google AI Studio](https://aistudio.google.com/app/apikey).
Create or select a Google Cloud project when prompted, then copy the generated key.

```bash
cd Server
python -m venv .venv
```

On Windows PowerShell:

```powershell
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
Copy-Item .env.example .env
```

Replace the value in `Server/.env` with your Gemini API key:

```dotenv
GEMINI_API_KEY=your-gemini-api-key
```

Start the server:

```bash
python -m uvicorn main:app --host 0.0.0.0 --port 8001
```

Open `http://127.0.0.1:8001/` in a browser to use the basic test interface.

### 3. Open the Unity Project

Open this directory from Unity Hub, then load `Assets/Scenes/SampleScene.unity`.

When using a Quest device, set `Api Base Url` on `VoiceInterviewClient` to the LAN IP address of the computer running the API server.

## API Endpoints

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/` | Browser-based test interface |
| `POST` | `/api/stt` | Converts speech to text |
| `POST` | `/api/interview` | Generates an interview response and expression label |
| `POST` | `/api/tts` | Converts text to speech |
| `POST` | `/api/reset` | Clears the server-side conversation history |

## Security and Data Handling

- Never commit `.env` files or API keys.
- Review the Gemini API data-use terms before sending participant recordings. Free-tier requests may be used to improve Google products.
- Files under `Server/logs/` may contain conversation content or personal information.
- `UserData/`, `accounts.json`, `app_config.json`, test recordings, and runtime logs are excluded from Git.
- Before publishing or distributing the project, verify the license terms for all avatars, fonts, 3D models, and other assets.

## License

This repository includes third-party packages and assets. Follow the license terms supplied with each file and package.
