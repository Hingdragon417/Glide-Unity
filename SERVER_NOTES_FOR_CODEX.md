# Server Notes For Codex

Use this when the lobby/dedicated server context gets fuzzy.

## Project Folders

- Main Unity project: `C:\Users\Hingdragon\My project (1)`
- Atomic/deploy repo copy: `C:\Users\Hingdragon\My project (1)\Flying-unity-game`
- GitHub repo: `https://github.com/Hingdragon417/Flying-unity-game`

## Atomic Server

- Server id: `740`
- Public TCP endpoint: `play.atomichost.xyz:25661`
- Atomic API base used successfully: `https://atomichost.xyz/api/client/v1`
- Restart endpoint:
  - `POST /servers/740/power`
  - JSON body: `{ "signal": "restart" }`
- Do not store the API key in files or commits. Ask the user for it again, or use a secure environment variable if one exists.

## Important Runtime Check

Probe the live TCP server with Node:

```powershell
node -e "const net=require('net'); const s=net.createConnection(25661,'play.atomichost.xyz'); let data=''; s.setTimeout(2500); s.on('data',d=>data+=d.toString('utf8')); s.on('timeout',()=>s.end()); s.on('close',()=>console.log(JSON.stringify(data))); s.on('error',e=>{console.error(e.message); process.exit(1);});"
```

Expected current protocol output starts like:

```text
welcome|id
server_protocol|2
listings_begin|count
...
listings_end
```

If the live server only returns `welcome|id`, the running binary is old.

## Key Lesson

Updating `Assets/Scripts/DedicatedTcpServer.cs` is not enough for Atomic if it runs the checked-in Linux build. The deployable build output must also be rebuilt and committed, especially:

- `glideServer_Data/Managed/Assembly-CSharp.dll`
- `glideServer_Data/boot.config`
- `glideServer_Data/globalgamemanagers`
- `glideServer_Data/globalgamemanagers.assets`
- `glideServer_Data/level0`
- `glideServer_Data/resources.assets`

After rebuilding, make sure `glideServer_Data/boot.config` still has:

```text
dedicatedServer-port=25661
```

Unity may reset this to `7777`; change it back before committing.

## Build Command

From `C:\Users\Hingdragon\My project (1)\Flying-unity-game`:

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.4.10f1\Editor\Unity.exe" -batchmode -quit -projectPath "C:\Users\Hingdragon\My project (1)\Flying-unity-game" -executeMethod BuildDedicatedServer.PerformBuild -logFile "C:\Users\Hingdragon\My project (1)\Flying-unity-game\dedicated-build.log"
```

If Unity exits quickly, wait for the matching Unity process if needed, then check the log for:

```text
Build Finished, Result: Success.
Dedicated server build completed: glideServer.x86_64
```

## Recent Server Features

- Server listings are stored in an in-memory static dictionary in `DedicatedTcpServer`.
- Clients can request listings with `listings_request`.
- Creating a lobby sends `create_listing|maxPlayers|lobbyName`.
- Joining a lobby from a row button sends `join_listing|listingId`.
- Server replies/broadcasts listing messages:
  - `server_protocol|2`
  - `listings_begin|count`
  - `listing|id|hostClientId|maxPlayers|currentPlayers|name`
  - `listing_created|...`
  - `listing_joined|...`
  - `join_failed|listingId|reason`
  - `listing_added|...`
  - `listing_removed|id`
  - `listings_end`
- State messages are only rebroadcast to clients in the same joined listing.
- Main menu loads `MainGame` only after `listing_created` or `listing_joined`.
- Dedicated server console command:
  - Type `stop` in the server console to safely shut down the TCP server and quit Unity.

## Last Known Good Push

- `71c4772 Serialize lobby server client writes`
