#!/usr/bin/env python3
"""Guard (NX-OT-02): deployable configuration must never enable DevAuthentication or run as Development.

Scans everything that ships or deploys: infra/azure, the Dockerfile, .github/workflows, and the non-Development
appsettings files. Local-only files (infra/local, tests/e2e stacks, *.Development.json, launchSettings.json) are exempt.
Fails (exit 1) on any hit. Usage: python3 scripts/check-no-dev-config.py [repo-root]
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()

SCAN_DIRS = ["infra/azure", ".github/workflows"]
SCAN_FILES = ["Dockerfile"]
EXTRA_GLOBS = ["src/**/appsettings*.json"]
EXEMPT_NAMES = re.compile(r"\.Development\.json$|launchSettings\.json$")
SKIP_PARTS = {"bin", "obj", "node_modules"}
SELF = {"check-no-dev-config.py"}

# env var / setting assigned to Development, in YAML, bicep, JSON, Dockerfile (ENV X=Y / ENV X Y) forms.
ENV_DEV = re.compile(
    r"""(ASPNETCORE_ENVIRONMENT|DOTNET_ENVIRONMENT|\bEnvironment)['"]?\s*[:=,]?\s*[,]?\s*(value\s*:\s*)?['"]?Development\b""",
    re.IGNORECASE,
)
ENV_DEV_DOCKER = re.compile(r"^\s*ENV\s+(ASPNETCORE_ENVIRONMENT|DOTNET_ENVIRONMENT)[=\s]+['\"]?Development\b", re.IGNORECASE)
DEVAUTH_ENABLED = re.compile(r"""DevAuthentication(__|:|['"]?\s*:\s*\{[^}]*)\s*['"]?Enabled['"]?(\s*[:=,]|\s+value\s*:)?\s*(value\s*:\s*)?['"]?true""", re.IGNORECASE | re.DOTALL)


def files():
    seen = set()
    for d in SCAN_DIRS:
        base = ROOT / d
        if base.is_dir():
            for p in base.rglob("*"):
                if p.is_file():
                    seen.add(p)
    for f in SCAN_FILES:
        if (ROOT / f).is_file():
            seen.add(ROOT / f)
    for g in EXTRA_GLOBS:
        seen.update(p for p in ROOT.glob(g) if p.is_file())
    for p in sorted(seen):
        if EXEMPT_NAMES.search(p.name) or SKIP_PARTS & set(p.parts) or p.name in SELF:
            continue
        yield p


failures = []
for path in files():
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        continue
    rel = path.relative_to(ROOT).as_posix()
    for n, line in enumerate(text.splitlines(), 1):
        if ENV_DEV.search(line) or ENV_DEV_DOCKER.search(line):
            failures.append(f"{rel}:{n}: runs as Development: {line.strip()}")
    for m in DEVAUTH_ENABLED.finditer(text):
        n = text.count("\n", 0, m.start()) + 1
        failures.append(f"{rel}:{n}: DevAuthentication enabled")
    # Any mention of DevAuthentication in a bicep file at all is suspicious.
    if path.suffix in (".bicep", ".bicepparam") and re.search("DevAuthentication", text, re.IGNORECASE):
        failures.append(f"{rel}: references DevAuthentication (not allowed in bicep)")

if failures:
    print("Non-local configuration must not enable DevAuthentication or ASPNETCORE_ENVIRONMENT=Development:", file=sys.stderr)
    for f in failures:
        print("  " + f, file=sys.stderr)
    sys.exit(1)
print("OK: no deployable config enables DevAuthentication or Development.")
