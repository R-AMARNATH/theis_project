#!/usr/bin/env python3
"""
resolve_vm_sizes.py

Pre-flight VM size resolution for the carbon-aware scheduler experiment.

For every (cloud, region) pair in files/manifest.json, this checks whether
the standardised experiment VM size (t3.medium / Standard_B2s / e2-medium)
is actually offered in that region, and if not, walks a fallback candidate
list -- closest spec match first (2 vCPU / 4 GiB) -- to find the first size
that IS offered. The result is written as a JSON list to
files/vm_size_resolution.json, which your GitHub Actions targets (or the
CarbonAware.Targets dispatchers) can read to pick `instanceType` / `vmsize`
/ `machineType` for workflow_dispatch, instead of guessing at launch time.

Real availability APIs used (not trial-and-error launches):
  AWS   -> ec2 describe-instance-type-offerings --location-type region
  Azure -> az vm list-skus --location <region> --size <size> --all
  GCP   -> gcloud compute machine-types describe <type> --zone=<region>-b

Requires the same CLIs/credentials already used by the three workflows:
  - aws CLI, configured (AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY)
  - az CLI, logged in (az login / az login --service-principal)
  - gcloud CLI, authenticated (gcloud auth activate-service-account)

Usage:
    python3 resolve_vm_sizes.py
    python3 resolve_vm_sizes.py --manifest files/manifest.json --out files/vm_size_resolution.json
    python3 resolve_vm_sizes.py --cloud aws --region eu-west-1
    python3 resolve_vm_sizes.py --cloud azure --region westeurope --target Standard_B2s

    # Every region the account can see for a cloud, not just manifest.json's:
    python3 resolve_vm_sizes.py --all-regions --cloud aws
    python3 resolve_vm_sizes.py --all-regions --cloud gcp --project carbonawarescheduler

    # Every region for all three clouds:
    python3 resolve_vm_sizes.py --all-regions --project carbonawarescheduler

Exit code is 0 only if every requested region resolved to *some* deployable
size (may not be the requested one). Exit code is 1 if any region has no
deployable candidate at all -- treat that as a hard failure before dispatching.
"""

import argparse
import json
import shutil
import subprocess
import sys
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

# Same fallback ladders already used in the three workflows, kept here as the
# single source of truth so the resolver and the workflows never drift apart.
CANDIDATES = {
    "aws": ["t3.medium", "t3a.medium", "t2.medium", "m5.large", "m5a.large", "m6i.large"],
    "azure": [
        "Standard_B2s", "Standard_B2ms", "Standard_D2s_v3", "Standard_D2s_v4",
        "Standard_D2s_v5", "Standard_DS1_v2", "Standard_F2s_v2", "Standard_D2_v3",
        "Standard_A2_v2",
    ],
    "gcp": ["e2-medium", "e2-standard-2", "n2-standard-2", "n1-standard-2", "e2-standard-4"],
}


def _candidates_for(cloud: str, target: str | None) -> list[str]:
    ladder = CANDIDATES[cloud]
    if target and target not in ladder:
        return [target] + ladder
    if target:
        # Move the requested size to the front without duplicating it.
        return [target] + [c for c in ladder if c != target]
    return list(ladder)


_AZ_LOCK = threading.Lock()


def _run(cmd: list[str]) -> subprocess.CompletedProcess:
    # On Windows, az/gcloud are .cmd batch wrappers -- subprocess.run() with a
    # bare name and no shell=True fails to launch those (FileNotFoundError),
    # even though the same call works fine for aws.exe. shutil.which() honours
    # PATHEXT and resolves to the real az.cmd/gcloud.cmd path, which fixes it
    # on Windows and is a harmless no-op on macOS/Linux.
    resolved = shutil.which(cmd[0])
    if resolved is None:
        class _Missing:
            returncode = 1
            stdout = ""
            stderr = f"'{cmd[0]}' not found on PATH -- is it installed and on PATH?"
        return _Missing()

    try:
        # az CLI is not safe for concurrent invocation: parallel az processes
        # race on the shared MSAL token cache file and deadlock rather than
        # erroring, which is why runs hung silently right as Azure calls
        # started. AWS and GCP CLIs don't have this issue, so only az calls
        # get serialized. The timeout is a backstop in case a CLI ever blocks
        # on an interactive prompt (e.g. expired login) instead of erroring.
        if cmd[0] == "az":
            with _AZ_LOCK:
                return subprocess.run([resolved] + cmd[1:], capture_output=True, text=True, timeout=120)
        return subprocess.run([resolved] + cmd[1:], capture_output=True, text=True, timeout=120)
    except subprocess.TimeoutExpired:
        class _TimedOut:
            returncode = 1
            stdout = ""
            stderr = (f"'{cmd[0]}' timed out after 120s -- likely stuck waiting for interactive "
                      f"login/consent. Try running 'az login' (or the equivalent) manually first.")
        return _TimedOut()


def resolve_aws(region: str, target: str | None) -> dict:
    candidates = _candidates_for("aws", target)
    proc = _run([
        "aws", "ec2", "describe-instance-type-offerings",
        "--location-type", "region",
        "--region", region,
        "--filters", f"Name=instance-type,Values={','.join(candidates)}",
        "--query", "InstanceTypeOfferings[].InstanceType",
        "--output", "json",
    ])
    if proc.returncode != 0:
        return _error("aws", region, target, candidates, proc.stderr.strip())

    try:
        offered = set(json.loads(proc.stdout))
    except json.JSONDecodeError:
        return _error("aws", region, target, candidates, f"unparseable CLI output: {proc.stdout[:200]}")

    for size in candidates:
        if size in offered:
            return _ok("aws", region, target, candidates, size)
    return _no_match("aws", region, target, candidates)


def resolve_azure(region: str, target: str | None) -> dict:
    candidates = _candidates_for("azure", target)
    tried = []
    for size in candidates:
        tried.append(size)
        # No --all here on purpose: --all makes az fetch restriction reasons
        # for every zone of every SKU, which is what made each call take
        # 60s+. Without --all, list-skus only returns SKUs that are actually
        # usable in this subscription/region -- restricted ones are simply
        # omitted -- which is exactly the availability check we need, and
        # it's a much lighter query.
        proc = _run([
            "az", "vm", "list-skus",
            "--location", region,
            "--size", size,
            "--query", f"[?name=='{size}'].name",
            "-o", "tsv",
        ])
        if proc.returncode != 0:
            return _error("azure", region, target, tried, proc.stderr.strip())
        if proc.stdout.strip():
            return _ok("azure", region, target, tried, size)
    return _no_match("azure", region, target, tried)


def resolve_gcp(region: str, target: str | None, project: str | None) -> dict:
    candidates = _candidates_for("gcp", target)
    zone = f"{region}-b"
    tried = []
    for size in candidates:
        tried.append(size)
        cmd = ["gcloud", "compute", "machine-types", "describe", size, "--zone", zone,
               "--format", "value(name)"]
        if project:
            cmd += ["--project", project]
        proc = _run(cmd)
        if proc.returncode == 0 and proc.stdout.strip():
            return _ok("gcp", region, target, tried, size)
    return _no_match("gcp", region, target, tried)


def _ok(cloud, region, target, tried, resolved) -> dict:
    return {
        "cloud": cloud,
        "region": region,
        "requested_size": target or tried[0],
        "resolved_size": resolved,
        "requested_available": resolved == (target or tried[0]),
        "candidates_tried": tried,
        "status": "ok",
    }


def _no_match(cloud, region, target, tried) -> dict:
    return {
        "cloud": cloud,
        "region": region,
        "requested_size": target or (tried[0] if tried else None),
        "resolved_size": None,
        "requested_available": False,
        "candidates_tried": tried,
        "status": "no_available_size",
    }


def _error(cloud, region, target, tried, message) -> dict:
    return {
        "cloud": cloud,
        "region": region,
        "requested_size": target or (tried[0] if tried else None),
        "resolved_size": None,
        "requested_available": False,
        "candidates_tried": tried,
        "status": "error",
        "error": message,
    }


def list_regions(cloud: str, project: str | None = None, include_disabled: bool = False) -> list[str]:
    """Enumerate regions for a cloud. By default only regions actually usable
    on this account (e.g. AWS "opt-in" regions like af-south-1 are excluded
    unless include_disabled=True, since querying them just returns AuthFailure)."""
    cloud = cloud.lower()
    if cloud == "aws":
        cmd = ["aws", "ec2", "describe-regions", "--query", "Regions[].RegionName", "--output", "json"]
        if include_disabled:
            cmd.insert(3, "--all-regions")
        proc = _run(cmd)
        if proc.returncode != 0:
            raise RuntimeError(f"failed to list AWS regions: {proc.stderr.strip()}")
        return sorted(json.loads(proc.stdout))

    if cloud == "azure":
        proc = _run([
            "az", "account", "list-locations",
            "--query", "[?metadata.regionType=='Physical'].name",
            "-o", "json",
        ])
        if proc.returncode != 0:
            raise RuntimeError(f"failed to list Azure locations: {proc.stderr.strip()}")
        return sorted(json.loads(proc.stdout))

    if cloud == "gcp":
        if not project:
            raise RuntimeError("--project is required to list GCP regions (no manifest project to fall back on)")
        proc = _run([
            "gcloud", "compute", "regions", "list",
            "--project", project,
            "--format", "value(name)",
        ])
        if proc.returncode != 0:
            raise RuntimeError(f"failed to list GCP regions: {proc.stderr.strip()}")
        return sorted(l for l in proc.stdout.splitlines() if l.strip())

    raise ValueError(f"unknown cloud: {cloud}")


def resolve_one(cloud: str, region: str, target: str | None, project: str | None = None) -> dict:
    cloud = cloud.lower()
    if cloud == "aws":
        return resolve_aws(region, target)
    if cloud == "azure":
        return resolve_azure(region, target)
    if cloud == "gcp":
        return resolve_gcp(region, target, project)
    raise ValueError(f"unknown cloud: {cloud}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", default="files/manifest.json", help="manifest.json to read cloud/region pairs from")
    ap.add_argument("--out", default="files/vm_size_resolution.json", help="where to write the resolved list")
    ap.add_argument("--cloud", choices=["aws", "azure", "gcp"], help="restrict to a single cloud (required with --region; optional with --all-regions)")
    ap.add_argument("--region", help="region to resolve (used with --cloud, ignored with --all-regions)")
    ap.add_argument("--target", help="preferred size to try first (defaults to the project standard for that cloud)")
    ap.add_argument("--all-regions", action="store_true",
                     help="ignore manifest.json and resolve every region the account can see, for --cloud "
                          "(or all three clouds if --cloud is omitted)")
    ap.add_argument("--project", help="GCP project id, needed for --all-regions (and for GCP entries without a manifest)")
    ap.add_argument("--include-disabled-regions", action="store_true",
                     help="also probe AWS opt-in regions you haven't enabled (af-south-1, ap-east-1, etc.) -- "
                          "these will always fail with AuthFailure until enabled in the AWS console/IAM, so "
                          "they're skipped by default")
    ap.add_argument("--workers", type=int, default=8,
                     help="parallel CLI calls to run at once (default 8). These are network-bound "
                          "az/aws/gcloud calls, not CPU work, so threads help a lot here -- raise "
                          "this if it's still slow, lower it if a CLI starts rate-limiting you")
    args = ap.parse_args()

    entries = []
    if args.all_regions:
        clouds = [args.cloud] if args.cloud else ["aws", "azure", "gcp"]
        for cloud in clouds:
            try:
                regions = list_regions(cloud, args.project, args.include_disabled_regions)
            except RuntimeError as exc:
                print(f"[FAIL] {cloud}: {exc}", file=sys.stderr)
                continue
            print(f"Found {len(regions)} {cloud} regions to check...")
            for region in regions:
                entries.append({"cloud": cloud, "region": region, "project": args.project})
    elif args.cloud:
        if not args.region:
            ap.error("--region is required when --cloud is given (or use --all-regions)")
        entries.append({"cloud": args.cloud, "region": args.region, "project": args.project})
    else:
        manifest_path = Path(args.manifest)
        if not manifest_path.exists():
            ap.error(f"manifest not found: {manifest_path}")
        data = json.loads(manifest_path.read_text())
        for row in data:
            entries.append({"cloud": row["cloud"], "region": row["region"], "project": row.get("project")})

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    def _save(partial_results: list[dict]) -> None:
        out_path.write_text(json.dumps(partial_results, indent=2) + "\n")

    # --target only makes sense when checking a single cloud; in multi-cloud
    # runs (manifest mode or --all-regions without --cloud) each cloud uses
    # its own project-standard size as the first candidate.
    target = args.target if args.cloud else None

    results = []
    any_unresolved = False
    print(f"Resolving {len(entries)} region(s) with {args.workers} parallel workers...\n")
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {
            pool.submit(resolve_one, e["cloud"], e["region"], target, e.get("project")): e
            for e in entries
        }
        try:
            for future in as_completed(futures):
                result = future.result()
                results.append(result)
                status_flag = "OK" if result["status"] == "ok" else "FAIL"
                note = result["resolved_size"] or result.get("error", "no size available")
                print(f"[{status_flag}] {result['cloud']:5s} {result['region']:16s} "
                      f"requested={result['requested_size']:20s} -> {note}")
                if result["status"] != "ok":
                    any_unresolved = True
                # Save after every completion, not just at the end -- if you Ctrl+C
                # or a CLI hangs, whatever finished so far is already on disk.
                _save(results)
        except KeyboardInterrupt:
            print(f"\nInterrupted -- {len(results)}/{len(entries)} results already saved to {out_path}", file=sys.stderr)
            pool.shutdown(wait=False, cancel_futures=True)
            return 130

    print(f"\nWrote {len(results)} entries to {out_path}")
    return 1 if any_unresolved else 0


if __name__ == "__main__":
    sys.exit(main())
