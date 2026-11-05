"""Scan a release build for personal or build-machine strings before publishing.

    python tools/scan_release.py [path-to-dBoost.exe] [extra needle ...]

Scans the exe's host part and every file inside its single-file bundle (unpacked here, because the
bundle is compressed and a plain byte search would see nothing inside it). Each needle is searched as
ASCII, UTF-8 and UTF-16, ignoring case.

Personal needles (this machine's user name, home folder, repo path and its top folder, the git e-mail,
Steam library and SteamID) must not appear anywhere. Build strings (obj folders, user and temp folders,
and any drive-letter path) must not appear in our own assemblies (dBoost.dll, dBoost.Core.dll).
Third-party files (the .NET runtime, WPF, and dBoost.Ffmpeg.dll, which is almost entirely the unmodified
ffmpeg.exe) are only counted for those. Exit code 1 on any hit that must not be there.
"""
import getpass
import os
import re
import struct
import subprocess
import sys
import zlib

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(REPO, "bin", "publish", "dBoost.exe")
OURS = ("dBoost.dll", "dBoost.Core.dll")
B = chr(92)  # backslash, spelled out so no escaping can mangle it
# The marker the .NET host writes into a single-file app (HostWriter.bundleSignature in dotnet/runtime).
BUNDLE_SIGNATURE = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")


def git(*args):
    try:
        return subprocess.run(["git", *args], cwd=REPO, capture_output=True, text=True).stdout.strip()
    except OSError:
        return ""


def personal_needles():
    drive, rest = os.path.splitdrive(REPO)
    top = drive + B + rest.strip(B + "/").replace("/", B).split(B)[0]
    email = git("config", "user.email")
    needles = [getpass.getuser(), os.path.expanduser("~"), REPO, top,
               ":" + B + "SteamLibrary", "7656119", "claude", "anthropic"]
    if email:
        needles += [email, email.split("@")[0]]
    return [n for n in dict.fromkeys(needles) if n]


BUILD_NEEDLES = [B + "obj" + B, "/obj/", ":" + B + "Users" + B, "AppData", "Temp" + B]

# A drive-letter path with two folders (drive, colon, backslash, folder, backslash, folder) in our own
# assemblies would be a leaked build path. With ContinuousIntegrationBuild the embedded debug info only
# names "dBoost.pdb".
_PART = "[A-Za-z0-9_ .-]{2,}"
DRIVE_PATH = re.compile("[A-Za-z]:" + re.escape(B) + _PART + re.escape(B) + _PART)

# Third-party text that can contain a needle without being about anyone: Brotli's built-in dictionary
# of common words (inside .NET's System.IO.Compression.Native) includes common first names. A hit
# whose surroundings are that dictionary's word run is not reported.
ALLOWED = [b"city of(this);"]


def find(low, data, needle):
    for enc in ("ascii", "utf-8", "utf-16le"):
        try:
            pattern = needle.lower().encode(enc)
        except UnicodeEncodeError:
            continue
        i = low.find(pattern)
        while i != -1:
            window = low[max(0, i - 80):i + 80]
            if not any(a in window for a in ALLOWED):
                return enc, data[max(0, i - 40):i + 80]
            i = low.find(pattern, i + 1)
    return None


def drive_paths(blob):
    """A drive-letter path as ASCII text or as UTF-16 text (both byte alignments), or None."""
    texts = [blob.decode("latin-1"),
             blob.decode("utf-16le", errors="replace"),
             blob[1:].decode("utf-16le", errors="replace")]
    for text in texts:
        m = DRIVE_PATH.search(text)
        if m:
            return m.group(0)
    return None


def bundle(data):
    """(host bytes without the bundle, [(path, body)]) for a .NET single-file app (bundle format 6+)."""
    sig = data.find(BUNDLE_SIGNATURE)
    if sig < 8:
        raise SystemExit("not a .NET single-file bundle")
    header = struct.unpack_from("<q", data, sig - 8)[0]
    major, _minor, count = struct.unpack_from("<IIi", data, header)
    pos = header + 12

    def string(p):
        length = shift = 0
        while True:
            b = data[p]
            p += 1
            length |= (b & 0x7F) << shift
            shift += 7
            if b < 0x80:
                return data[p:p + length].decode("utf-8"), p + length

    _bundle_id, pos = string(pos)
    if major >= 2:
        pos += 8 * 4 + 8
    entries, first = [], len(data)
    for _ in range(count):
        offset, size = struct.unpack_from("<qq", data, pos)
        pos += 16
        compressed = 0
        if major >= 6:
            compressed = struct.unpack_from("<q", data, pos)[0]
            pos += 8
        pos += 1
        path, pos = string(pos)
        raw = data[offset:offset + (compressed or size)]
        body = zlib.decompress(raw, -15) if compressed else raw
        if len(body) != size:
            raise SystemExit(f"bundle entry {path}: unpacked {len(body)} bytes, expected {size}")
        entries.append((path, body))
        first = min(first, offset)
    host = data[:first] + data[pos:]
    return host, entries


def main():
    args = sys.argv[1:]
    exe = args.pop(0) if args and args[0].lower().endswith(".exe") else DEFAULT_EXE
    personal = personal_needles() + args
    data = open(exe, "rb").read()
    host, entries = bundle(data)
    files = [("exe host and bundle header", host, False)]
    files += [("bundle/" + p, body, p in OURS) for p, body in entries]

    bad = third_party = 0
    for label, blob, ours in files:
        low = blob.lower()
        for needle in personal:
            hit = find(low, blob, needle)
            if hit:
                bad += 1
                print(f"HIT {label}: personal string {needle!r} ({hit[0]}) ...{hit[1]!r}")
        for needle in BUILD_NEEDLES:
            hit = find(low, blob, needle)
            if hit and ours:
                bad += 1
                print(f"HIT {label}: build string {needle!r} ({hit[0]}) ...{hit[1]!r}")
            elif hit:
                third_party += 1
        if ours:
            path = drive_paths(blob)
            if path:
                bad += 1
                print(f"HIT {label}: drive path {path!r}")
    print(f"scanned {exe}: {len(files)} parts, ours: {', '.join(f[0] for f in files if f[2])}")
    print(f"needles: {len(personal)} personal, {len(BUILD_NEEDLES)} build, plus drive paths in ours")
    if third_party:
        print(f"note: {third_party} build-string matches in third-party files (their own build paths, not ours)")
    print(f"{bad} hits")
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
