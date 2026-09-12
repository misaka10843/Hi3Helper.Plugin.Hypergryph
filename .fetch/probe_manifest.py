import hashlib, json, time, urllib.request, urllib.error

SALT = "DE7108E9B2842FD460F4777702727869"
GAME_TAG = "Arknights_EN"
LAUNCHER_VERSION = "1.8.1"
BASE = "https://api-launcher-en.yo-star.com"

def auth_header():
    ts = int(time.time())
    head = json.dumps({"game_tag": GAME_TAG, "time": ts, "version": LAUNCHER_VERSION}, separators=(",", ":"))
    sign = hashlib.md5((head + SALT).encode()).hexdigest()
    return '{"head":' + head + ',"sign":"' + sign + '"}'

version = "041.2.0"
file_path = "prod/ZIP_TEMP/Arknights_EN_TEMP/Arknights_EN-041.2.0-game.zip"
path = "api/launcher/game/config/json?version=%s&file_path=%s" % (version, urllib.parse.quote(file_path, safe=""))
req = urllib.request.Request(BASE + "/" + path, headers={"Authorization": auth_header()})
try:
    with urllib.request.urlopen(req, timeout=30) as r:
        body = json.loads(r.read().decode())
    print("HTTP", r.status)
    print("code:", body.get("code"), "msg:", body.get("msg"))
    url = body.get("data", {}).get("url")
    print("manifest url:", url)
    if url:
        with urllib.request.urlopen(url + ("&" if "?" in url else "?") + "nocache=1", timeout=60) as mr:
            manifest = json.loads(mr.read().decode())
        print("source:", manifest.get("source"))
        files = manifest.get("file", [])
        print("total files:", len(files))
        for f in files:
            if "space" in f.get("path", "") or "dump" in f.get("path", "") or "TQM64" in f.get("path", ""):
                print("MATCH:", f)
        # show a couple examples
        for f in files[:3]:
            print("EXAMPLE:", f)
except Exception as e:
    print("ERROR:", repr(e))