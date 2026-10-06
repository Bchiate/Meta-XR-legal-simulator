#!/usr/bin/env python3
"""Minimal MCP-over-SSE client for the Meta XR Operator server (http://localhost:8720/sse).
Usage:
  scripts/xrop.py tools                      # list tools
  scripts/xrop.py call <tool> '<json-args>'  # call a tool
  scripts/xrop.py shot [out.png]             # capture composited image to file
Only works while an XR app with the Operator layer is running (Play Mode w/ simulator, or a dev build on device via adb forward).
"""
import json, sys, threading, queue, urllib.request, urllib.parse, base64, time

BASE = "http://localhost:8720"
SSE = BASE + "/sse"

class Client:
    def __init__(self, timeout=60):
        self.q = queue.Queue(); self.endpoint = None; self.timeout = timeout; self._id = 0
        self.resp = urllib.request.urlopen(SSE, timeout=timeout)
        threading.Thread(target=self._reader, daemon=True).start()
        t0 = time.time()
        while self.endpoint is None:
            if time.time() - t0 > timeout: raise TimeoutError("no endpoint event from SSE")
            time.sleep(0.05)

    def _reader(self):
        event, data = None, []
        for raw in self.resp:
            line = raw.decode("utf-8", "replace").rstrip("\r\n")
            if line == "":
                if event == "endpoint": self.endpoint = urllib.parse.urljoin(BASE, "".join(data))
                elif data:
                    try: self.q.put(json.loads("".join(data)))
                    except json.JSONDecodeError: pass
                event, data = None, []
            elif line.startswith("event:"): event = line[6:].strip()
            elif line.startswith("data:"): data.append(line[5:].strip())

    def request(self, method, params=None):
        self._id += 1; rid = self._id
        body = json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}}).encode()
        req = urllib.request.Request(self.endpoint, data=body, headers={"Content-Type": "application/json"})
        urllib.request.urlopen(req, timeout=self.timeout).read()
        t0 = time.time()
        while True:
            try: msg = self.q.get(timeout=1)
            except queue.Empty:
                if time.time() - t0 > self.timeout: raise TimeoutError(method)
                continue
            if isinstance(msg, dict) and msg.get("id") == rid: return msg

    def notify(self, method, params=None):
        body = json.dumps({"jsonrpc": "2.0", "method": method, "params": params or {}}).encode()
        req = urllib.request.Request(self.endpoint, data=body, headers={"Content-Type": "application/json"})
        urllib.request.urlopen(req, timeout=self.timeout).read()

    def init(self):
        r = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "his-xrop", "version": "0.1"}})
        self.notify("notifications/initialized"); return r

def main():
    if len(sys.argv) < 2: print(__doc__); return 1
    cmd = sys.argv[1]
    c = Client(); c.init()
    if cmd == "tools":
        r = c.request("tools/list")
        for t in r.get("result", {}).get("tools", []):
            print(f"- {t['name']}: {(t.get('description') or '')[:140].replace(chr(10),' ')}")
        return 0
    if cmd == "call":
        args = json.loads(sys.argv[3]) if len(sys.argv) > 3 else {}
        r = c.request("tools/call", {"name": sys.argv[2], "arguments": args})
        out = r.get("result", r)
        for item in out.get("content", []) if isinstance(out, dict) else []:
            if item.get("type") == "text": print(item["text"])
            elif item.get("type") == "image":
                p = f"Builds/xrop-{sys.argv[2]}.png"; open(p, "wb").write(base64.b64decode(item["data"])); print(f"[image saved] {p}")
        if "error" in r: print("ERROR:", json.dumps(r["error"]))
        return 0
    if cmd == "shot":
        out = sys.argv[2] if len(sys.argv) > 2 else "Builds/xrop-view.png"
        r = c.request("tools/call", {"name": "openxr_capture_composited_image", "arguments": {}})
        for item in r.get("result", {}).get("content", []):
            if item.get("type") == "image": open(out, "wb").write(base64.b64decode(item["data"])); print(f"[image saved] {out}")
            elif item.get("type") == "text": print(item["text"][:500])
        if "error" in r: print("ERROR:", json.dumps(r["error"]))
        return 0
    print(__doc__); return 1

if __name__ == "__main__": sys.exit(main())
