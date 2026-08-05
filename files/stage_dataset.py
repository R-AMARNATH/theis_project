"""
stage_dataset.py

Copies the fixed dataset (dataset.csv + zone_lookup.csv, produced once by
generate_dataset.py) into a storage bucket/container in EVERY candidate
region, across AWS/Azure/GCP. Run this ONCE before the 84-cycle experiment
starts. The batch job VM in each region then always reads from same-region
storage, keeping the dataset-transfer latency measurement clean (in-region,
no cross-region egress).

Manifest format (manifest.json):
[
  {"cloud": "aws",   "region": "eu-west-1",     "bucket": "carbonaware-data-aws-eu-west-1"},
  {"cloud": "aws",   "region": "us-east-1",     "bucket": "carbonaware-data-aws-us-east-1"},
  {"cloud": "azure", "region": "westeurope",    "account": "carbonawestaging1", "container": "carbonaware-data"},
  {"cloud": "gcp",   "region": "europe-west1",  "project": "your-gcp-project",  "bucket": "carbonaware-data-gcp-europe-west1"}
]

Notes:
  - AWS: bucket name must be globally unique. Script creates the bucket in
    the given region if it doesn't already exist (requires s3:CreateBucket).
  - Azure: the storage ACCOUNT must already exist in the target region
    (storage accounts are provisioned, not just named) -- create it once via
    `az storage account create --location <region> ...`. The script creates
    the CONTAINER within it if missing.
  - GCP: script creates the bucket in the given region if missing (requires
    storage.buckets.create on the project).

Usage:
  python stage_dataset.py --manifest manifest.json --data-dir ./data
  python stage_dataset.py --manifest manifest.json --data-dir ./data --clouds aws,gcp
"""
import argparse
import json
import sys
import time
from pathlib import Path

FILES_TO_STAGE = ["dataset.csv", "zone_lookup.csv"]


def stage_aws(target: dict, data_dir: Path):
    import boto3
    from botocore.exceptions import ClientError

    region = target["region"]
    bucket = target["bucket"]
    s3 = boto3.client("s3", region_name=region)

    try:
        s3.head_bucket(Bucket=bucket)
    except ClientError:
        print(f"  [aws:{region}] creating bucket {bucket}")
        kwargs = {"Bucket": bucket}
        if region != "us-east-1":  # us-east-1 rejects LocationConstraint
            kwargs["CreateBucketConfiguration"] = {"LocationConstraint": region}
        s3.create_bucket(**kwargs)

    for fname in FILES_TO_STAGE:
        local = data_dir / fname
        t0 = time.time()
        s3.upload_file(str(local), bucket, fname)
        print(f"  [aws:{region}] uploaded {fname} in {time.time() - t0:.1f}s")


def stage_azure(target: dict, data_dir: Path):
    from azure.storage.blob import BlobServiceClient
    from azure.core.exceptions import ResourceExistsError
    import os

    account = target["account"]
    container = target["container"]
    region = target["region"]

    conn_str = os.environ.get(f"AZURE_STORAGE_CONNECTION_STRING_{account.upper()}") \
        or os.environ.get("AZURE_STORAGE_CONNECTION_STRING")
    if not conn_str:
        raise RuntimeError(
            f"Set AZURE_STORAGE_CONNECTION_STRING for account '{account}' "
            f"(storage account must already exist in region {region})"
        )

    svc = BlobServiceClient.from_connection_string(conn_str)
    try:
        svc.create_container(container)
        print(f"  [azure:{region}] created container {container}")
    except ResourceExistsError:
        pass

    for fname in FILES_TO_STAGE:
        local = data_dir / fname
        size_mb = local.stat().st_size / 1_048_576
        t0 = time.time()
        last_report = [0.0]

        def _progress(current, total):
            # azure-storage-blob calls this with bytes uploaded so far / total bytes
            elapsed = time.time() - t0
            if elapsed - last_report[0] >= 5 or current == total:
                mb_done = current / 1_048_576
                mb_total = total / 1_048_576
                rate = mb_done / elapsed if elapsed > 0 else 0
                print(f"    [azure:{region}] {fname}: {mb_done:.1f}/{mb_total:.1f} MB "
                      f"({rate:.1f} MB/s)")
                last_report[0] = elapsed

        print(f"  [azure:{region}] uploading {fname} ({size_mb:.1f} MB)...")
        with open(local, "rb") as f:
            svc.get_blob_client(container, fname).upload_blob(
                f, overwrite=True, max_concurrency=4, progress_hook=_progress
            )
        print(f"  [azure:{region}] uploaded {fname} in {time.time() - t0:.1f}s")


def stage_gcp(target: dict, data_dir: Path):
    from google.cloud import storage
    from google.api_core.exceptions import Conflict

    project = target["project"]
    region = target["region"]
    bucket_name = target["bucket"]

    client = storage.Client(project=project)
    bucket = client.bucket(bucket_name)
    if not bucket.exists():
        print(f"  [gcp:{region}] creating bucket {bucket_name}")
        client.create_bucket(bucket, location=region)

    for fname in FILES_TO_STAGE:
        local = data_dir / fname
        t0 = time.time()
        bucket.blob(fname).upload_from_filename(str(local))
        print(f"  [gcp:{region}] uploaded {fname} in {time.time() - t0:.1f}s")


STAGERS = {"aws": stage_aws, "azure": stage_azure, "gcp": stage_gcp}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--data-dir", default="./data")
    ap.add_argument("--clouds", default="aws,azure,gcp",
                     help="Comma-separated subset of clouds to stage, e.g. aws,gcp")
    args = ap.parse_args()

    data_dir = Path(args.data_dir)
    for fname in FILES_TO_STAGE:
        if not (data_dir / fname).exists():
            print(f"ERROR: {fname} not found in {data_dir}. "
                  f"Run generate_dataset.py first.", file=sys.stderr)
            sys.exit(1)

    with open(args.manifest) as f:
        targets = json.load(f)

    allowed_clouds = set(args.clouds.split(","))
    failures = []

    for target in targets:
        cloud = target["cloud"]
        if cloud not in allowed_clouds:
            continue
        stager = STAGERS.get(cloud)
        if stager is None:
            print(f"skipping unknown cloud '{cloud}'")
            continue
        try:
            stager(target, data_dir)
        except Exception as e:
            print(f"  FAILED [{cloud}:{target.get('region')}]: {e}", file=sys.stderr)
            failures.append((cloud, target.get("region"), str(e)))

    print()
    if failures:
        print(f"{len(failures)} region(s) failed to stage:")
        for cloud, region, err in failures:
            print(f"  - {cloud}:{region} -> {err}")
        sys.exit(1)
    else:
        print("All regions staged successfully.")


if __name__ == "__main__":
    main()
