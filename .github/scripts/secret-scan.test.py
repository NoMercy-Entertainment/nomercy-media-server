"""Exercise the pull-request secret scan with a public vector and a fake token."""

from pathlib import Path
import os
import random
import shutil
import string
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/secret-scan.yml"
CONFIG = ROOT / ".github/gitleaks.toml"


def fail(message: str) -> None:
    raise SystemExit(f"FAIL: {message}")


if not WORKFLOW.is_file():
    fail("pull-request secret-scan workflow is missing")
if not CONFIG.is_file():
    fail("Gitleaks allowlist is missing")

workflow = WORKFLOW.read_text(encoding="utf-8")
if "\non:\n  pull_request:\n" not in workflow or "paths:" in workflow:
    fail("secret scan must run on every pull request")
if any(
    required not in workflow
    for required in ("pull_request:", "fetch-depth: 0", "gitleaks git", "BASE_SHA..HEAD")
):
    fail("workflow must scan every pull request's commits")

scanner = shutil.which(os.environ.get("GITLEAKS_BIN", "gitleaks"))
if scanner is None:
    fail("Gitleaks is unavailable")


def scan(repo: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            scanner,
            "dir",
            "--config",
            str(CONFIG),
            "--no-banner",
            "--redact",
            str(repo),
        ],
        capture_output=True,
        text=True,
    )


with tempfile.TemporaryDirectory(prefix="nomercy-secret-scan-") as temporary:
    repo = Path(temporary)
    fixture = Path("tests/NoMercy.Tests.Notifications/Push/rfc8291-example.json")
    target = repo / fixture
    target.parent.mkdir(parents=True)
    shutil.copyfile(ROOT / fixture, target)
    result = scan(repo)
    if result.returncode != 0:
        fail("public RFC 8291 vectors were rejected: " + result.stderr.strip()[-500:])

    fake_token = "ghp_" + "".join(
        random.Random(188).choices(string.ascii_letters + string.digits, k=36)
    )
    (repo / "injected.txt").write_text(fake_token + "\n", encoding="utf-8")
    result = scan(repo)
    if result.returncode == 0:
        fail("fake GitHub token was accepted")
    if "leaks found" not in result.stderr:
        fail("scanner failed without detecting the fake token")

    (repo / "injected.txt").unlink()
    with target.open("a", encoding="utf-8") as fixture_file:
        fixture_file.write(f'\n"api_key": "{fake_token}"\n')
    result = scan(repo)
    if result.returncode == 0 or "leaks found" not in result.stderr:
        fail("fake token inside the RFC fixture was accepted")

def git(repo: Path, *args: str) -> str:
    return subprocess.run(
        [
            "git",
            "-c", "user.name=t",
            "-c", "user.email=t@example.invalid",
            "-c", "commit.gpgsign=false",
            *args,
        ],
        cwd=repo,
        capture_output=True,
        text=True,
        check=True,
    ).stdout.strip()


def scan_range(repo: Path, base: str) -> subprocess.CompletedProcess[str]:
    # The exact form the workflow runs: gitleaks git with a commit range.
    return subprocess.run(
        [
            scanner,
            "git",
            "--config",
            str(CONFIG),
            f"--log-opts={base}..HEAD",
            "--no-banner",
            "--redact",
            str(repo),
        ],
        capture_output=True,
        text=True,
    )


with tempfile.TemporaryDirectory(prefix="nomercy-secret-scan-git-") as temporary:
    repo = Path(temporary)
    git(repo, "init", "-q")
    (repo / "readme.txt").write_text("base\n", encoding="utf-8")
    git(repo, "add", ".")
    git(repo, "commit", "-q", "-m", "base")
    base = git(repo, "rev-parse", "HEAD")

    target = repo / fixture
    target.parent.mkdir(parents=True)
    shutil.copyfile(ROOT / fixture, target)
    git(repo, "add", ".")
    git(repo, "commit", "-q", "-m", "add public vectors")
    result = scan_range(repo, base)
    if result.returncode != 0:
        fail("range scan rejected the public RFC 8291 vectors: " + result.stderr.strip()[-500:])

    (repo / "injected.txt").write_text(fake_token + "\n", encoding="utf-8")
    git(repo, "add", ".")
    git(repo, "commit", "-q", "-m", "plant fake token")
    result = scan_range(repo, base)
    if result.returncode == 0 or "leaks found" not in result.stderr:
        fail("gitleaks git range scan accepted a planted fake token")

    # A later commit that deletes the token must still fail: the range holds the commit that added it.
    git(repo, "rm", "-q", "injected.txt")
    git(repo, "commit", "-q", "-m", "remove fake token")
    result = scan_range(repo, base)
    if result.returncode == 0 or "leaks found" not in result.stderr:
        fail("gitleaks git range scan missed a token removed in a later commit")

print("PASS: RFC 8291 vectors allowed; fake tokens rejected inside and outside fixture, and in gitleaks git range form")
