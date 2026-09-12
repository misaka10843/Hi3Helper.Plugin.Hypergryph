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

for path in ["api/launcher/base/config", "api/launcher/operations/resource",
             "api/launcher/social/media/resource", "api/launcher/game/config",
             "api/launcher/advanced/game/download/cdn"]:
    req = urllib.request.Request(BASE + "/" + path, headers={"Authorization": auth_header()})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            body = r.read().decode()
        print("=== " + path + " (HTTP " + str(r.status) + ") ===")
        print(body[:3000])
    except urllib.error.HTTPError as e:
        print("=== " + path + " (HTTP " + str(e.code) + ") ===")
        print(e.read().decode()[:1000])
    except Exception as e:
        print("=== " + path + " ERROR: " + repr(e))
    print()