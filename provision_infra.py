#!/usr/bin/env python3
"""
provision_infra.py

Creates every storage resource (S3 bucket, Azure Storage Account + container,
GCS bucket) listed in files/manifest.json, for regions that don't already
have one. Run this once after expanding manifest.json to the full 91-region
list -- the workflows will fail at "Resolve bucket/account from manifest"
for any region whose real storage doesn't exist yet, no matter what the
JSON says.

Also prints the exact `AZURE_STORAGE_CONNECTION_STRING_<ACCOUNT>` repo
secret name + value for every Azure account it creates (or that already
exists), since those secrets can't be created via CLI/API -- you still have
to paste each one into GitHub manually (Settings -> Secrets and variables ->
Actions). With 32 Azure regions that's 32 secrets; there's no way around
that part being manual, GitHub doesn't have a bulk-secret-set API.

Requires: aws CLI, az CLI, gcloud CLI, all authenticated (same creds as the
three deploy workflows).

Usage:
    python3 provision_infra.py                       # create everything missing
    python3 provision_infra.py --cloud azure          # just Azure
    python3 provision_infra.py --dry-run              # show what would be created, do nothing
    python3 provision_infra.py --resource-group carbonaware-rg  # Azure resource group (created if missing)
"""

import argparse
import base64
import json
import os
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

try:
    import nacl.encoding
    import nacl.public
    HAVE_NACL = True
except ImportError:
    HAVE_NACL = False


def run(cmd: list[str]) -> subprocess.CompletedProcess:
    # On Windows, az/gcloud are .cmd batch wrappers -- subprocess.run() with a
    # bare name and no shell=True fails to launch those, even though the same
    # call works fine for aws.exe (which is why AWS ran cleanly above but
    # Azure crashed with FileNotFoundError). shutil.which() resolves the real
    # az.cmd/gcloud.cmd path, fixing this on Windows and no-op elsewhere.
    resolved = shutil.which(cmd[0])
    if resolved is None:
        class _Missing:
            returncode = 1
            stdout = ""
            stderr = f"'{cmd[0]}' not found on PATH -- is it installed and on PATH?"
        return _Missing()
    return subprocess.run([resolved] + cmd[1:], capture_output=True, text=True)


def _github_api(method: str, url: str, token: str, body: dict | None = None) -> dict:
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "Content-Type": "application/json",
    })
    with urllib.request.urlopen(req, timeout=20) as resp:
        return json.loads(resp.read().decode()) if resp.length != 0 else {}


def get_repo_public_key(owner: str, repo: str, token: str) -> tuple[str, str]:
    url = f"https://api.github.com/repos/{owner}/{repo}/actions/secrets/public-key"
    data = _github_api("GET", url, token)
    return data["key_id"], data["key"]


def set_github_secret(owner: str, repo: str, token: str, key_id: str, public_key_b64: str,
                       secret_name: str, secret_value: str) -> None:
    public_key = nacl.public.PublicKey(public_key_b64.encode(), nacl.encoding.Base64Encoder())
    sealed_box = nacl.public.SealedBox(public_key)
    encrypted = sealed_box.encrypt(secret_value.encode())
    encrypted_b64 = base64.b64encode(encrypted).decode()

    url = f"https://api.github.com/repos/{owner}/{repo}/actions/secrets/{secret_name}"
    req = urllib.request.Request(url, data=json.dumps({
        "encrypted_value": encrypted_b64,
        "key_id": key_id,
    }).encode(), method="PUT", headers={
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "Content-Type": "application/json",
    })
    with urllib.request.urlopen(req, timeout=20) as resp:
        resp.read()  # 201 Created (new) or 204 No Content (updated existing) -- both fine


def provision_aws(entries: list[dict], dry_run: bool) -> list[str]:
    failures = []
    for e in entries:
        bucket, region = e["bucket"], e["region"]
        check = run(["aws", "s3api", "head-bucket", "--bucket", bucket, "--region", region])
        if check.returncode == 0:
            print(f"[SKIP] aws  {region:20s} {bucket} (already exists)")
            continue
        if dry_run:
            print(f"[DRY]  aws  {region:20s} would create {bucket}")
            continue
        # us-east-1 is the one region where --create-bucket-configuration must be omitted
        if region == "us-east-1":
            cmd = ["aws", "s3api", "create-bucket", "--bucket", bucket, "--region", region]
        else:
            cmd = ["aws", "s3api", "create-bucket", "--bucket", bucket, "--region", region,
                   "--create-bucket-configuration", f"LocationConstraint={region}"]
        result = run(cmd)
        if result.returncode != 0:
            print(f"[FAIL] aws  {region:20s} {result.stderr.strip()[:150]}", file=sys.stderr)
            failures.append(f"aws/{region}")
        else:
            print(f"[OK]   aws  {region:20s} created {bucket}")
    return failures


def provision_azure(entries: list[dict], dry_run: bool, resource_group: str,
                     github_token: str | None, github_owner: str, github_repo: str) -> list[str]:
    failures = []
    if not dry_run:
        rg_check = run(["az", "group", "show", "--name", resource_group])
        if rg_check.returncode != 0:
            print(f"Creating resource group {resource_group}...")
            # Use the first region as the RG's own location; individual storage
            # accounts below are still created in their own specific regions.
            run(["az", "group", "create", "--name", resource_group, "--location", entries[0]["region"]])

    secrets_to_set = []
    for e in entries:
        account, region, container = e["account"], e["region"], e["container"]
        check = run(["az", "storage", "account", "show", "--name", account, "--resource-group", resource_group])
        if check.returncode == 0:
            print(f"[SKIP] azure {region:20s} {account} (already exists)")
        elif dry_run:
            print(f"[DRY]  azure {region:20s} would create {account}")
            continue
        else:
            result = run(["az", "storage", "account", "create", "--name", account,
                          "--resource-group", resource_group, "--location", region, "--sku", "Standard_LRS"])
            if result.returncode != 0:
                print(f"[FAIL] azure {region:20s} {result.stderr.strip()[:150]}", file=sys.stderr)
                failures.append(f"azure/{region}")
                continue
            print(f"[OK]   azure {region:20s} created {account}")

        if dry_run:
            continue

        conn = run(["az", "storage", "account", "show-connection-string", "--name", account,
                    "--resource-group", resource_group, "--query", "connectionString", "-o", "tsv"])
        conn_str = conn.stdout.strip()
        if conn_str:
            run(["az", "storage", "container", "create", "--name", container,
                 "--connection-string", conn_str])
            secret_name = f"AZURE_STORAGE_CONNECTION_STRING_{account.upper()}"
            secrets_to_set.append((secret_name, conn_str))

    if not secrets_to_set:
        return failures

    if github_token and HAVE_NACL:
        print(f"\n=== Setting {len(secrets_to_set)} GitHub repo secrets via API ===")
        try:
            key_id, public_key_b64 = get_repo_public_key(github_owner, github_repo, github_token)
        except (urllib.error.URLError, KeyError) as exc:
            print(f"[FAIL] Couldn't fetch repo public key: {exc}", file=sys.stderr)
            print("Falling back to printing secrets for manual entry:\n")
            for name, value in secrets_to_set:
                print(f"{name} = {value}")
            failures.append("github-secrets/public-key")
            return failures

        for name, value in secrets_to_set:
            try:
                set_github_secret(github_owner, github_repo, github_token, key_id, public_key_b64, name, value)
                print(f"[OK]   secret {name}")
            except urllib.error.URLError as exc:
                print(f"[FAIL] secret {name}: {exc}", file=sys.stderr)
                failures.append(f"github-secret/{name}")
    else:
        reason = "no --github-token given" if not github_token else "PyNaCl not installed (pip install pynacl)"
        print(f"\n=== {len(secrets_to_set)} GitHub repo secrets need to be set manually ({reason}) ===")
        print("(Settings -> Secrets and variables -> Actions -> New repository secret)\n")
        for name, value in secrets_to_set:
            print(f"{name} = {value}")

    return failures


def provision_gcp(entries: list[dict], dry_run: bool) -> list[str]:
    failures = []
    for e in entries:
        bucket, region, project = e["bucket"], e["region"], e["project"]
        check = run(["gsutil", "ls", "-b", f"gs://{bucket}"])
        if check.returncode == 0:
            print(f"[SKIP] gcp  {region:20s} {bucket} (already exists)")
            continue
        if dry_run:
            print(f"[DRY]  gcp  {region:20s} would create {bucket}")
            continue
        result = run(["gsutil", "mb", "-p", project, "-l", region, f"gs://{bucket}"])
        if result.returncode != 0:
            print(f"[FAIL] gcp  {region:20s} {result.stderr.strip()[:150]}", file=sys.stderr)
            failures.append(f"gcp/{region}")
        else:
            print(f"[OK]   gcp  {region:20s} created {bucket}")
    return failures


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", default="files/manifest.json")
    ap.add_argument("--cloud", choices=["aws", "azure", "gcp"], help="only provision one cloud")
    ap.add_argument("--dry-run", action="store_true", help="show what would be created, do nothing")
    ap.add_argument("--resource-group", default="carbonaware-rg", help="Azure resource group (created if missing)")
    ap.add_argument("--github-token", default=os.environ.get("GITHUB_TOKEN"),
                     help="GitHub PAT with 'repo' scope (or set GITHUB_TOKEN env var). "
                          "When given, Azure connection-string secrets are set on the repo "
                          "automatically via the GitHub API instead of just being printed.")
    ap.add_argument("--github-owner", default="R-AMARNATH")
    ap.add_argument("--github-repo", default="theis_project")
    args = ap.parse_args()

    manifest_path = Path(args.manifest)
    if not manifest_path.exists():
        ap.error(f"manifest not found: {manifest_path}")
    manifest = json.loads(manifest_path.read_text())

    clouds = [args.cloud] if args.cloud else ["aws", "azure", "gcp"]
    all_failures = []

    if "aws" in clouds:
        aws_entries = [e for e in manifest if e["cloud"] == "aws"]
        print(f"=== AWS: {len(aws_entries)} buckets ===")
        all_failures += provision_aws(aws_entries, args.dry_run)

    if "azure" in clouds:
        azure_entries = [e for e in manifest if e["cloud"] == "azure"]
        print(f"\n=== Azure: {len(azure_entries)} storage accounts ===")
        all_failures += provision_azure(azure_entries, args.dry_run, args.resource_group,
                                         args.github_token, args.github_owner, args.github_repo)

    if "gcp" in clouds:
        gcp_entries = [e for e in manifest if e["cloud"] == "gcp"]
        print(f"\n=== GCP: {len(gcp_entries)} buckets ===")
        all_failures += provision_gcp(gcp_entries, args.dry_run)

    if all_failures:
        print(f"\n[WARN] {len(all_failures)} resources failed to provision: {all_failures}", file=sys.stderr)
        return 1

    print("\nDone.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
